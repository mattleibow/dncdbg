using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModuleMetadata = DncDbg.Engine.Symbols.ModuleMetadata;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private static readonly string[] s_implicitNamespaces =
        ["System", "System.Collections.Generic", "System.Linq", "System.IO", "System.Threading", "System.Threading.Tasks", "System.Text"];

    private readonly Dictionary<string, (LoadedModule Module, int Token)?> _typeCache = [];

    /// <summary>Finds a type loaded in an AppDomain by full name, preferring user code over libraries.</summary>
    private (LoadedModule Module, int Token)? FindType(string fullName, int appDomainId)
    {
        string key = appDomainId + "|" + fullName;
        if (_typeCache.TryGetValue(key, out var cached))
            return cached;
        (LoadedModule, int)? result = null;
        foreach (LoadedModule module in _modules.Values.Where(m => m.Key.AppDomainId == appDomainId)
                     .OrderByDescending(m => m.Metadata?.HasSymbols == true).ThenBy(m => m.Id))
        {
            if (module.Metadata?.FindType(fullName) is { } token)
            {
                result = (module, token);
                break;
            }
        }
        return _typeCache[key] = result;
    }

    /// <summary>
    /// Interprets C# expressions against a stack frame. Syntax comes from Roslyn; semantics are a pragmatic subset:
    /// host-side arithmetic on values read from the debuggee, and func-evals for properties, indexers and methods.
    /// </summary>
    /// <param name="self">
    /// When set, names are resolved against this object instead of the frame's locals ([DebuggerDisplay] expressions).
    /// </param>
    private sealed partial class Evaluator(DebugEngine engine, FrameRef frame, bool allowCalls, Func<CorDebugValue?>? self = null)
    {
        private abstract record Operand;
        private sealed record VoidOperand : Operand;

        /// <param name="EnumClass">Set when <paramref name="Value"/> is the underlying value of an enum.</param>
        private sealed record HostOperand(object? Value, CorDebugClass? EnumClass = null, string? DeclaredType = null, bool IsConstant = false) : Operand;

        /// <param name="IsLocation">The getter returns a storage location (variable, field, element) that can be assigned to.</param>
        /// <param name="TupleNames">Names the source gives to the elements of a tuple-typed local (Item1, Item2, ... in the type).</param>
        private sealed record RemoteOperand(Func<CorDebugValue?> Getter, bool IsLocation = false, string?[]? TupleNames = null) : Operand;

        private sealed record TypeOperand(LoadedModule Module, int Token, IReadOnlyList<TypeOperand>? TypeArguments = null) : Operand
        {
            public ModuleMetadata Metadata => Module.Metadata!;
            public CorDebugClass Class => Module.Module.GetClassFromToken(new mdTypeDef(Token));
        }

        private sealed record NamespaceOperand(string Name) : Operand;

        private sealed record MethodLevel(CorDebugModule Module, ModuleMetadata Metadata, int Token, CorDebugType? Type);

        private const string ExceptionVariable = "__debugger_exception";
        private const string ReturnValueVariable = "__debugger_returnvalue";
        private const string ProcessIdVariable = "__debugger_pid";
        private const string ThreadIdVariable = "__debugger_tid";

        private readonly ValueInspector _values = engine._values;
        private readonly InspectionContext _context = new(frame.ThreadId, frame);
        private List<(string Name, Func<CorDebugValue?> Getter)>? _locals;
        private Operand? _conditionalReceiver;
        private List<CorDebugType>? _explicitTypeArguments;
        private Dictionary<string, string?[]>? _tupleNames;
        private bool _checked;

        private Dictionary<string, string?[]> TupleNames => _tupleNames ??= self != null ? [] : engine.GetLocalTupleElementNames(frame);

        private List<(string Name, Func<CorDebugValue?> Getter)> Locals =>
            _locals ??= self != null ? [("this", self)] : engine.GetLocalRefs(frame);

        // ---------------------------------------------------------------- entry points

        public VariableInfo EvaluateToVariable(string expression, DisplayOptions? baseOptions = null)
        {
            (string code, DisplayOptions options) = SplitFormatSpecifiers(expression, baseOptions ?? DisplayOptions.Default);
            if (!options.AllowCalls)
                return ToVariable(code, Run(code), options);
            bool previous = engine.AllowImplicitFuncEval;
            engine.AllowImplicitFuncEval = true;
            try
            {
                return ForceEvaluation(ToVariable(code, Run(code), options));
            }
            finally
            {
                engine.AllowImplicitFuncEval = previous;
            }
        }

        private VariableInfo ForceEvaluation(VariableInfo value)
        {
            if (value.ChildrenProvider is not { } provider)
                return value;
            return value with
            {
                ChildrenProvider = (start, count) =>
                {
                    bool previous = engine.AllowImplicitFuncEval;
                    engine.AllowImplicitFuncEval = true;
                    try { return provider(start, count).Select(ForceEvaluation).ToList(); }
                    finally { engine.AllowImplicitFuncEval = previous; }
                },
            };
        }

        /// <summary>Evaluates to a host value (enums as their underlying value); throws for non-primitive objects.</summary>
        public object? EvaluateToHost(string expression) => ToHost(Run(expression));

        /// <summary>Display text of the value; <paramref name="quoteStrings"/> is what ",nq" would turn off anyway.</summary>
        public string EvaluateToText(string expression, bool quoteStrings)
        {
            (string code, DisplayOptions options) = SplitFormatSpecifiers(expression, new DisplayOptions(NoQuotes: !quoteStrings));
            return ToVariable(expression, Run(code), options).Value;
        }

        // "expression,h" / ",nq" / ",raw" / ",d": the debugger's format specifiers, which are not C#
        private static (string Code, DisplayOptions Options) SplitFormatSpecifiers(string expression, DisplayOptions options)
        {
            while (true)
            {
                int comma = LastTopLevelComma(expression);
                if (comma < 0)
                    return (expression, options);
                string specifier = expression[(comma + 1)..].Trim();
                if (specifier.Length is > 0 and < 10 && specifier.All(char.IsAsciiDigit))
                {
                    // "array,5": the first five elements
                    options = options with { ElementLimit = int.Parse(specifier, System.Globalization.CultureInfo.InvariantCulture) };
                    expression = expression[..comma];
                    continue;
                }
                if (specifier.Length == 0 || !specifier.All(char.IsAsciiLetter))
                    return (expression, options);

                options = specifier switch
                {
                    "h" or "x" or "X" => options with { Hex = true },
                    "d" => options with { Hex = false },
                    "nq" => options with { NoQuotes = true },
                    "raw" => options with { Raw = true },
                    "hidden" => options with { Hidden = true },
                    "ac" => options with { AllowCalls = true },
                    _ => throw new DebuggerException($"Unknown format specifier '{specifier}'. Supported: h, d, nq, raw and a number of elements."),
                };
                expression = expression[..comma];
            }
        }

        private static int LastTopLevelComma(string text)
        {
            int depth = 0, last = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '"' or '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\')
                            i++;
                }
                else if (c is '(' or '[' or '{')
                    depth++;
                else if (c is ')' or ']' or '}')
                    depth--;
                else if (c == ',' && depth <= 0)
                    last = i;
            }
            return last;
        }

        public void Assign(Func<CorDebugValue?> location, string expression, string? targetName = null)
        {
            try
            {
                AssignCore(location, Run(expression), targetName == null ? null : DeclaredLocalType(targetName));
            }
            catch (EvalFailedException e)
            {
                throw new DebuggerException(e.Message);
            }
        }

        private Operand Run(string expression)
        {
            // "$exception" is a debugger pseudo variable, not C#
            ExpressionSyntax syntax = SyntaxFactory.ParseExpression(RewritePseudoVariables(expression));
            Diagnostic? error = syntax.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (error != null)
            {
                engine.Log?.Invoke($"Invalid evaluation syntax: {expression}: {error}");
                throw new DebuggerException(error.Id == "CS1012" ? "Failed to parse character." : "Expression has malformed or incomplete syntax.");
            }
            try
            {
                return Eval(syntax);
            }
            catch (EvalFailedException e)
            {
                throw new DebuggerException(e.Message);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e)
            {
                throw new DebuggerException(e.Message);
            }
            catch (Exception e) when (e is ArithmeticException or InvalidCastException or FormatException or COMException)
            {
                throw new DebuggerException(e is COMException ? ErrorText.Describe(e) : e.GetType().Name + ": " + e.Message);
            }
        }

        private VariableInfo ToVariable(string name, Operand operand, DisplayOptions? options = null)
        {
            switch (operand)
            {
                case VoidOperand:
                    return new VariableInfo { Name = name, Value = "Expression has been evaluated and has no value", Type = "void" };
                case HostOperand { EnumClass: { } enumClass } host when options?.Hex != true:
                    return _values.DescribeHost(name, host.Value, _values.FormatEnum(enumClass, ToBits(host.Value!)))
                        with
                    { Type = EnumTypeName(enumClass) };
                case HostOperand host:
                    {
                        VariableInfo result = _values.DescribeHost(name, host.Value, null, options);
                        return host.DeclaredType is { } declared ? result with { Type = ModuleMetadata.DisplayType(declared) } : result;
                    }
                case RemoteOperand remote:
                    {
                        VariableInfo result = _values.Describe(name, remote.Getter, name, _context, options);
                        if (result.Value.StartsWith("<error:", StringComparison.Ordinal))
                            throw new DebuggerException(result.Value[8..^1]);
                        if (!remote.IsLocation)
                            result = result with { Location = null, MemoryReference = "0x0000000000000000" };
                        return result;
                    }
                case TypeOperand type:
                    throw new DebuggerException($"'{type.Metadata.GetTypeName(type.Token)}' is a type, which is not valid in the given context.");
                case LambdaOperand lambda:
                    throw new DebuggerException($"The lambda '{lambda.Text}' has no value by itself: pass it to a LINQ method, e.g. list.Where({lambda.Text}).");
                default:
                    throw new DebuggerException($"The name '{((NamespaceOperand)operand).Name}' does not exist in the current context.");
            }
        }

        // ---------------------------------------------------------------- syntax walk

        private Operand Eval(ExpressionSyntax node)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax p:
                    return Eval(p.Expression);
                case LiteralExpressionSyntax literal:
                    return new HostOperand(literal.Token.Value, IsConstant: true);
                case ThisExpressionSyntax:
                    return Identifier("this");
                case IdentifierNameSyntax id:
                    return Identifier(id.Identifier.ValueText);
                case PredefinedTypeSyntax predefined:
                    return ResolveType(predefined.ToString()) ?? throw new DebuggerException($"Type '{predefined}' not found.");
                case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Name: GenericNameSyntax genericMember } genericAccess:
                    {
                        Operand owner = Eval(genericAccess.Expression);
                        string prefix = owner switch
                        {
                            NamespaceOperand ns => ns.Name + ".",
                            TypeOperand outer => outer.Metadata.GetTypeName(outer.Token) + ".",
                            _ => throw new DebuggerException("Generic members are not supported in expressions."),
                        };
                        string name = prefix + genericMember.Identifier.ValueText + "`" + genericMember.TypeArgumentList.Arguments.Count;
                        TypeOperand definition = ResolveQualifiedType(name) ?? throw new DebuggerException($"Type '{genericAccess}' not found.");
                        return definition with { TypeArguments = (owner is TypeOperand { TypeArguments: { } outerArguments } ? outerArguments : [])
                            .Concat(genericMember.TypeArgumentList.Arguments.Select(ResolveType)).ToList() };
                    }
                case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member:
                    return Member(Eval(member.Expression), SimpleName(member.Name));
                case MemberBindingExpressionSyntax binding:
                    return Member(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), SimpleName(binding.Name));
                case ElementBindingExpressionSyntax binding:
                    return ElementAccess(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), binding.ArgumentList);
                case ConditionalAccessExpressionSyntax conditional:
                    return ConditionalAccess(conditional);
                case InvocationExpressionSyntax invocation:
                    return Invoke(invocation);
                case ElementAccessExpressionSyntax element:
                    return ElementAccess(Eval(element.Expression), element.ArgumentList);
                case PrefixUnaryExpressionSyntax unary when unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression):
                    return Unary(unary);
                case CastExpressionSyntax cast:
                    return Cast(Eval(cast.Expression), cast.Type);
                case ConditionalExpressionSyntax ternary:
                    return ToHost(Eval(ternary.Condition)) is true ? Eval(ternary.WhenTrue) : Eval(ternary.WhenFalse);
                case BinaryExpressionSyntax binary:
                    return Binary(binary);
                case AnonymousFunctionExpressionSyntax lambda:
                    return Lambda(lambda);
                default:
                    return EvalExtended(node) ?? throw new DebuggerException($"Expressions of kind '{node.Kind()}' are not supported.");
            }
        }

        private static string SimpleName(SimpleNameSyntax name) => name is IdentifierNameSyntax
            ? name.Identifier.ValueText
            : throw new DebuggerException("Generic names are not supported in expressions.");

        private Operand Identifier(string name)
        {
            if (LambdaParameter(name) is { } parameter)
                return parameter;
            foreach ((string localName, Func<CorDebugValue?> getter) in Locals)
                if (localName == name)
                    return new RemoteOperand(getter, IsLocation: true, TupleNames.GetValueOrDefault(name));

            if (self == null && engine.GetILFrame(frame) is { } ilFrame
                && engine.GetMetadata(ilFrame.Function.Module)?.GetLocalConstants((int)ilFrame.Function.Token.Value, ilFrame.IP.pnOffset)
                    .FirstOrDefault(c => c.Name == name) is { Name: not null } constant)
                return new HostOperand(constant.Value, DeclaredType: constant.Type, IsConstant: true);
            if (name == ProcessIdVariable)
                return new HostOperand((uint)engine._processId);
            if (name == ThreadIdVariable)
                return new HostOperand((uint)frame.ThreadId);
            if (name == "this")
                throw new DebuggerException("Keyword 'this' is not available in the current context.");
            if (name == ExceptionVariable)
            {
                Func<CorDebugValue?> exception = () => engine.RequireThread(frame.ThreadId).CurrentException;
                // no exception: null on some platforms, S_FALSE turned into an exception on others
                if (TryGet(exception) is not { } current || ValueInspector.Unwrap(current, out bool noException) == null || noException)
                    throw new DebuggerException("No exception is being processed on this thread.");
                return new RemoteOperand(engine.Stabilize(exception));
            }
            if (name == ReturnValueVariable)
            {
                if (engine._returnValue is not { } returned || returned.ThreadId != frame.ThreadId)
                    throw new DebuggerException("$ReturnValue is only available right after a method has returned.");
                return returned.Getter != null ? new RemoteOperand(returned.Getter) : new HostOperand(returned.HostValue, returned.EnumClass);
            }

            // members of "this", then static members of the type the code belongs to
            if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } thisGetter }
                && TryMember(new RemoteOperand(thisGetter), name) is { } instanceMember)
            {
                return instanceMember;
            }
            foreach (TypeOperand enclosing in EnclosingTypes())
                if (TryMember(enclosing, name) is { } staticMember)
                    return staticMember;
            foreach (TypeOperand imported in ImportedTypes())
                if (TryMember(imported, name) is { } importedMember)
                    return importedMember;
            if (Imports.FirstOrDefault(i => i.Alias == name) is { } alias)
                return alias.Kind == System.Reflection.Metadata.ImportDefinitionKind.AliasType
                    ? ResolveType(SyntaxFactory.ParseTypeName(ModuleMetadata.DisplayType(alias.Target))) : new NamespaceOperand(alias.Target);

            return (Operand?)ResolveType(name) ?? new NamespaceOperand(name);
        }

        private TypeOperand? EnclosingType()
        {
            if (self != null)
            {
                CorDebugValue? target = self() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                ValueInspector.TypeLevel? level = target == null ? null : _values.GetTypeLevels(target).FirstOrDefault();
                return level != null && engine._modules.TryGetValue(level.Type.Class.Module, out LoadedModule? owner)
                    ? new TypeOperand(owner, level.Token)
                    : null;
            }

            CorDebugFunction function = engine.RequireILFrame(frame).Function;
            if (!engine._modules.TryGetValue(function.Module, out LoadedModule? module) || module.Metadata == null)
                return null;
            int declaring = module.Metadata.GetDeclaringType((int)function.Token.Value);
            int userType = module.Metadata.GetEnclosingUserType(declaring);
            int count = module.Metadata.GetTypeGenericParameters(userType).Length;
            if (count == 0)
                return new TypeOperand(module, userType);
            CorDebugILFrame il = engine.RequireILFrame(frame);
            CorDebugType[] actual = il.TypeParameters;
            if (!module.Metadata.GetMethodParameters((int)function.Token.Value).IsStatic
                && ValueInspector.Unwrap(il.GetArgument(0), out _) is { } instance
                && ValueInspector.TypeArgumentsOf(instance.ExactType) is { Length: > 0 } fromInstance)
                actual = fromInstance;
            List<TypeOperand?> arguments = actual.Take(count).Select(TypeOperandOf).ToList();
            return new TypeOperand(module, userType, arguments.Count == count && arguments.All(a => a != null)
                ? arguments.Select(a => a!).ToList() : null);
        }

        private TypeOperand? ResolveType(string name)
        {
            name = ExpandNamespaceAlias(name);
            if (Imports.FirstOrDefault(i => i.Alias == name && i.Kind == System.Reflection.Metadata.ImportDefinitionKind.AliasType) is { } alias)
                return ResolveType(SyntaxFactory.ParseTypeName(ModuleMetadata.DisplayType(alias.Target)));
            string? keyword = name switch
            {
                "bool" => "System.Boolean",
                "byte" => "System.Byte",
                "sbyte" => "System.SByte",
                "char" => "System.Char",
                "short" => "System.Int16",
                "ushort" => "System.UInt16",
                "int" => "System.Int32",
                "uint" => "System.UInt32",
                "long" => "System.Int64",
                "ulong" => "System.UInt64",
                "float" => "System.Single",
                "double" => "System.Double",
                "decimal" => "System.Decimal",
                "string" => "System.String",
                "object" => "System.Object",
                "nint" => "System.IntPtr",
                "nuint" => "System.UIntPtr",
                _ => null,
            };

            var candidates = new List<string> { keyword ?? name };
            if (keyword == null && EnclosingType() is { } enclosing)
            {
                foreach (TypeOperand scope in EnclosingTypes())
                    candidates.Add(scope.Metadata.GetTypeName(scope.Token) + "." + name);
                string ns = enclosing.Metadata.GetTypeNamespace(enclosing.Token);
                while (ns.Length > 0)
                {
                    candidates.Add(ns + "." + name);
                    int dot = ns.LastIndexOf('.');
                    ns = dot < 0 ? "" : ns[..dot];
                }
                candidates.AddRange(s_implicitNamespaces.Select(n => n + "." + name));
                candidates.AddRange(Imports.Where(i => i.Alias == null && i.Kind is System.Reflection.Metadata.ImportDefinitionKind.ImportNamespace
                    or System.Reflection.Metadata.ImportDefinitionKind.ImportAssemblyNamespace).Select(i => i.Target + "." + name));
            }

            foreach (string candidate in candidates)
                if (FindType(candidate) is { } found)
                    return new TypeOperand(found.Module, found.Token);
            return keyword == null && self == null ? ResolveTypeParameter(name) : null;
        }

        private IEnumerable<TypeOperand> EnclosingTypes()
        {
            for (TypeOperand? scope = EnclosingType(); scope != null;)
            {
                yield return scope;
                scope = scope.Metadata.GetParentType(scope.Token) is { } parent
                    ? new TypeOperand(scope.Module, parent) : null;
            }
        }

        /// <summary>"T" inside generic code: the type argument of the instantiation the frame is running.</summary>
        private TypeOperand? ResolveTypeParameter(string name)
        {
            CorDebugILFrame? ilFrame = engine.GetILFrame(frame);
            if (ilFrame == null)
                return null;
            CorDebugFunction function = ilFrame.Function;
            if (engine.GetMetadata(function.Module) is not { } metadata)
                return null;

            // type parameters of the declaring type come first, those of the method after them
            int methodToken = (int)function.Token.Value;
            string[] ofType = metadata.GetTypeGenericParameters(metadata.GetDeclaringType(methodToken));
            string[] ofMethod = metadata.GetMethodGenericParameters(methodToken);
            int index = Array.LastIndexOf(ofType.Concat(ofMethod).ToArray(), name);
            if (index < 0)
                return null;

            CorDebugType[] actual = ilFrame.TypeParameters;
            if (index < actual.Length && TypeOperandOf(actual[index]) is { } exact && exact.Metadata.GetTypeName(exact.Token) != "System.__Canon")
                return exact;

            // Code shared between reference types does not know its own type argument. A parameter declared as "T"
            // holds an instance of it, which is the next best thing (and exact for sealed types such as string).
            if (index >= ofType.Length)
            {
                string variable = "!!" + (index - ofType.Length);
                MethodDescription? description = metadata.GetMethods(metadata.GetDeclaringType(methodToken), metadata.GetSimpleMethodName(methodToken))
                    .FirstOrDefault(m => m.Token == methodToken);
                int parameter = description == null ? -1 : Array.IndexOf(description.ParameterTypes, variable);
                if (parameter >= 0)
                {
                    int argumentIndex = description!.IsStatic ? parameter : parameter + 1;
                    CorDebugValue? argument = ValueInspector.Unwrap(ilFrame.GetArgument(argumentIndex), out bool isNull);
                    if (!isNull && argument != null && TypeOperandOf(argument.ExactType) is { } fromValue)
                        return fromValue;
                }
            }
            throw new DebuggerException($"The type argument '{name}' is not known here: the code is shared between reference types.");
        }

        private TypeOperand? TypeOperandOf(CorDebugType type)
        {
            if (type.Type is CorElementType.Class or CorElementType.ValueType)
            {
                CorDebugClass cls = type.Class;
                if (!engine._modules.TryGetValue(cls.Module, out LoadedModule? module) || module.Metadata == null)
                    return null;
                List<TypeOperand?> arguments = ValueInspector.TypeArgumentsOf(type).Select(TypeOperandOf).ToList();
                return arguments.Contains(null)
                    ? null
                    : new TypeOperand(module, (int)cls.Token.Value, arguments.Count == 0 ? null : arguments.Select(a => a!).ToList());
            }
            if (type.Type is CorElementType.SZArray or CorElementType.Array or CorElementType.Ptr or CorElementType.ByRef or CorElementType.FnPtr)
                return null;
            return ResolveQualifiedType(_values.ClrTypeName(type));
        }

        private TypeOperand ResolveType(TypeSyntax syntax) =>
            ResolveGenericType(syntax)
            ?? ResolveType(syntax.ToString().Replace(" ", ""))
            ?? throw new DebuggerException($"The type or namespace name '{syntax}' couldn't be found");

        // ---------------------------------------------------------------- member access

        private Operand Member(Operand target, string name) =>
            TryMember(target, name) ?? throw new DebuggerException(target switch
            {
                NamespaceOperand ns => $"The name '{ns.Name}.{name}' does not exist in the current context.",
                TypeOperand type => $"'{type.Metadata.GetTypeName(type.Token)}' does not contain a definition for '{name}'.",
                _ => $"'{name}' is not a member of the value.",
            });

        private Operand? TryMember(Operand target, string name)
        {
            switch (target)
            {
                case NamespaceOperand ns:
                    if (ns.Name.Count(c => c == '.') > 8)
                        return null;
                    return (Operand?)ResolveQualifiedType(ns.Name + "." + name) ?? new NamespaceOperand(ns.Name + "." + name);

                case TypeOperand type:
                    return StaticMember(type, name);

                case HostOperand { Value: null }:
                    throw new DebuggerException("NullReferenceException: the value is null.");

                case HostOperand host:
                    {
                        // properties of strings and primitives are pure, so they can be read in the debugger itself
                        System.Reflection.PropertyInfo? property = host.Value!.GetType().GetProperty(name, Type.EmptyTypes);
                        return property == null ? null : new HostOperand(property.GetValue(host.Value));
                    }

                default:
                    return InstanceMember((RemoteOperand)target, name);
            }
        }

        private TypeOperand? ResolveQualifiedType(string fullName) =>
            FindType(fullName) is { } found ? new TypeOperand(found.Module, found.Token) : null;

        private Operand? StaticMember(TypeOperand type, string name)
        {
            ModuleMetadata metadata = type.Metadata;
            foreach (FieldDescription field in metadata.GetFields(type.Token))
            {
                if (field.Name != name || (!field.IsStatic && !field.IsLiteral))
                    continue;
                if (field.IsLiteral)
                {
                    object? constant = metadata.GetConstant(field.Token, out _);
                    return new HostOperand(constant, metadata.IsEnum(type.Token) ? type.Class : null, metadata.GetFieldType(field.Token), IsConstant: true);
                }
                int token = field.Token;
                EnsureTypeInitialized(type);
                if (metadata.IsThreadStaticField(token))
                {
                    try { _ = StaticFieldValue(type, token); }
                    catch (DebuggerException)
                    {
                        TypeSignature signature = TypeSignature.Parse(metadata.GetFieldType(token)).Substitute(
                            (type.TypeArguments ?? []).Select(t => ClosedSignature(ToCorDebugType(t))).ToList());
                        if (signature.ArrayRank > 0)
                            return new HostOperand(null, DeclaredType: signature.ToString());
                        CorDebugType fieldType = BuildType(signature);
                        if (fieldType.Type == CorElementType.ValueType)
                        {
                            CorDebugType[] arguments = ValueInspector.TypeArgumentsOf(fieldType);
                            CorDebugValue? boxed = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(
                                fieldType.Class.Raw, arguments.Length, arguments.Select(a => a.Raw).ToArray()));
                            Func<CorDebugValue?> stable = engine.Stabilize(() => boxed);
                            return new RemoteOperand(() => stable() is { } value ? ValueInspector.Unwrap(value, out _) : null);
                        }
                        return new RemoteOperand(() => engine.RequireThread(frame.ThreadId).CreateEval().CreateValueForType(fieldType.Raw));
                    }
                }
                return new RemoteOperand(() => StaticFieldValue(type, token), IsLocation: true);
            }

            if (metadata.GetProperties(type.Token).Any(p => p.Name == name && p.IsStatic))
                return Call(null, type, "get_" + name, [], isPropertyAccess: true);

            // nested type
            if (ResolveQualifiedType(metadata.GetTypeName(type.Token) + "." + name) is { } nested)
                return nested;
            return metadata.GetBaseTypeName(type.Token) is { } baseName && ResolveQualifiedType(baseName) is { } baseType
                ? StaticMember(baseType, name) : null;
        }

        private CorDebugValue StaticFieldValue(TypeOperand type, int fieldToken)
        {
            ICorDebugFrame? rawFrame = engine.GetILFrame(frame)?.Raw;
            if (type.TypeArguments is { Count: > 0 })
                return ToCorDebugType(type).GetStaticFieldValue(new mdFieldDef(fieldToken), rawFrame!);
            CorDebugClass cls = type.Class;
            var attempts = new Func<CorDebugValue>[]
            {
                () => cls.GetStaticFieldValue(fieldToken, rawFrame!),
                () => cls.GetStaticFieldValue(fieldToken, null!),
                () => cls.GetParameterizedType(CorElementType.Class, 0, []).GetStaticFieldValue(new mdFieldDef(fieldToken), rawFrame!),
                () => cls.GetParameterizedType(CorElementType.ValueType, 0, []).GetStaticFieldValue(new mdFieldDef(fieldToken), rawFrame!),
            };
            Exception? first = null;
            foreach (Func<CorDebugValue> attempt in attempts)
            {
                try
                {
                    return attempt();
                }
                catch (Exception e)
                {
                    engine.Log?.Invoke("GetStaticFieldValue attempt failed: " + e.Message);
                    first ??= e;
                }
            }
            throw new DebuggerException("The static field is not available: " + first!.Message);
        }

        private void EnsureTypeInitialized(TypeOperand type)
        {
            string key = $"{type.Module.Key}:{AssemblyQualifiedName(type)}";
            if (!allowCalls || engine._initializedTypes.Contains(key) || type.Metadata.GetMethods(type.Token, ".cctor").Count == 0)
                return;
            if (!engine.AllowImplicitFuncEval)
            {
                if (engine._completedInitializers.Contains((type.Module.Key, type.Token))
                    && type.Metadata.GetTypeGenericParameters(type.Token).Length == 0)
                    return;
                throw new DebuggerException("Implicit function evaluation is turned off by the user.");
            }
            CorDebugType[] arguments = type.TypeArguments?.Select(ToCorDebugType).ToArray() ?? [];
            CorDebugValue? instance = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(
                type.Class.Raw, arguments.Length, arguments.Select(t => t.Raw).ToArray()));
            engine._initializedTypes.Add(key);
            if (!type.Metadata.IsValueType(type.Token) && instance != null && ResolveQualifiedType("System.GC") is { } gc)
            {
                Func<CorDebugValue?> stable = engine.Stabilize(() => instance);
                Call(null, gc, "SuppressFinalize", [new RemoteOperand(stable)], isPropertyAccess: true);
            }
        }

        private Operand? InstanceMember(RemoteOperand target, string name)
        {
            CorDebugValue value = target.Getter() ?? throw new DebuggerException("The value is not available.");
            CorDebugValue? unwrapped = ValueInspector.Unwrap(value, out bool isNull);
            if (isNull || unwrapped == null)
                throw new DebuggerException("NullReferenceException: the value is null.");

            // (int Id, string Name) tuple: "tuple.Name" is Item2. The eighth element and beyond live in Rest.
            if (target.TupleNames is { } elementNames && Array.IndexOf(elementNames, name) is >= 0 and < 7 and int element)
                name = "Item" + (element + 1);

            switch (unwrapped.Type)
            {
                case CorElementType.SZArray or CorElementType.Array when name is "Length":
                    return new HostOperand(unwrapped.As<CorDebugArrayValue>().Count);
                case CorElementType.SZArray or CorElementType.Array when name is "Rank":
                    return new HostOperand(unwrapped.As<CorDebugArrayValue>().Rank);
                case CorElementType.String when name is "Length":
                    return new HostOperand(unwrapped.As<CorDebugStringValue>().Length);
            }

            if (unwrapped.Type == CorElementType.ValueType && ValueInspector.IsSpan(_values.GetTypeName(unwrapped)) && name is "Length" or "IsEmpty")
            {
                int length = _values.GetSpanLength(target.Getter);
                return new HostOperand(name == "Length" ? length : length == 0);
            }

            // Nullable<T> has no metadata-level properties worth a func-eval
            if (unwrapped.Type == CorElementType.ValueType && _values.GetTypeName(unwrapped).StartsWith("System.Nullable<", StringComparison.Ordinal))
            {
                bool hasValue = ValueInspector.ReadPrimitive(_values.GetFieldByName(unwrapped, "hasValue")!) is true;
                if (name == "HasValue")
                    return new HostOperand(hasValue);
                if (name == "Value")
                {
                    if (!hasValue)
                        throw new DebuggerException("InvalidOperationException: Nullable object must have a value.");
                    return new RemoteOperand(() => _values.GetFieldByName(target.Getter()!, "value"));
                }
            }

            // fields (auto-property backing fields count: reading them is cheaper and safer than calling the getter)
            List<ValueInspector.TypeLevel> levels = _values.GetTypeLevels(unwrapped);
            foreach (ValueInspector.TypeLevel level in levels)
            {
                foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
                {
                    if (field.IsStatic || field.IsLiteral || (field.Name != name && field.Name != $"<{name}>k__BackingField"))
                        continue;
                    CorDebugClass cls = level.Type.Class;
                    int token = field.Token;
                    return new RemoteOperand(() =>
                    {
                        CorDebugValue? owner = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                        return owner?.As<CorDebugObjectValue>().GetFieldValue(cls.Raw, new mdFieldDef(token));
                    }, IsLocation: true);
                }
            }

            foreach (MethodLevel level in MethodLevels(unwrapped))
            {
                PropertyDescription? property = level.Metadata.GetProperties(level.Token).FirstOrDefault(p => p.Name == name && !p.IsIndexer);
                if (property == null || property.IsStatic)
                    continue;
                return Call(target, null, "get_" + name, [], isPropertyAccess: true);
            }

            return null;
        }

        private LoadedModule ModuleOf(MethodLevel level) => engine._modules[level.Module];

        private Operand ConditionalAccess(ConditionalAccessExpressionSyntax node)
        {
            Operand receiver = Eval(node.Expression);
            if (IsNull(receiver))
                return receiver;

            Operand? saved = _conditionalReceiver;
            _conditionalReceiver = receiver;
            try
            {
                return Eval(node.WhenNotNull);
            }
            finally
            {
                _conditionalReceiver = saved;
            }
        }

        private bool IsNull(Operand operand)
        {
            switch (operand)
            {
                case HostOperand host:
                    return host.Value == null;
                case RemoteOperand remote:
                    {
                        CorDebugValue? value = remote.Getter();
                        if (value == null)
                            return true;
                        ValueInspector.Unwrap(value, out bool isNull);
                        if (isNull)
                            return true;
                        // Nullable<T> without a value
                        return _values.TryReadHostValue(value, out object? host) && host == null;
                    }
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- element access

        private Operand ElementAccess(Operand target, BracketedArgumentListSyntax argumentList)
        {
            List<Operand> indices = argumentList.Arguments.Select(a => Eval(a.Expression)).ToList();
            if (target is HostOperand { Value: string text })
                return new HostOperand(text[Convert.ToInt32(ToHost(indices.Single()))]);
            if (target is not RemoteOperand remote)
                throw new DebuggerException("Cannot apply indexing to this expression.");

            CorDebugValue? unwrapped = remote.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                throw new DebuggerException("0x80070057: NullReferenceException: the value is null.");

            if (unwrapped.Type == CorElementType.ValueType && ValueInspector.IsSpan(_values.GetTypeName(unwrapped)))
            {
                // the indexer returns "ref T", which a func-eval cannot hand back
                int index = Convert.ToInt32(ToHost(indices.Single()));
                if (index < 0 || index >= _values.GetSpanLength(remote.Getter))
                    throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the span.");
                SpanElement element = _values.GetSpanElements(remote.Getter, _context, index, 1).Single();
                return element.IsHost ? new HostOperand(element.HostValue) : new RemoteOperand(engine.Stabilize(element.Getter!));
            }

            if (unwrapped.Type is CorElementType.SZArray or CorElementType.Array)
            {
                var array = unwrapped.As<CorDebugArrayValue>();
                int rank = array.Rank;
                int[] dims = array.GetDimensions(rank);
                int[] position = indices.Select(i => Convert.ToInt32(ToHost(i))).ToArray();
                if (position.Length != rank)
                    throw new DebuggerException($"Wrong number of indices inside []; expected {rank}.");
                for (int d = 0; d < rank; d++)
                    if (position[d] < 0 || position[d] >= dims[d])
                        throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the array.");
                return new RemoteOperand(() =>
                {
                    CorDebugValue? owner = remote.Getter() is { } a ? ValueInspector.Unwrap(a, out _) : null;
                    return owner?.As<CorDebugArrayValue>().GetElement(rank, position);
                }, IsLocation: true);
            }

            if (unwrapped.Type == CorElementType.String)
            {
                string s = _values.ReadString(unwrapped)!;
                int index = Convert.ToInt32(ToHost(indices.Single()));
                if (index < 0 || index >= s.Length)
                    throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the string.");
                return new HostOperand(s[index]);
            }

            return Call(remote, null, "get_Item", indices, isPropertyAccess: true);
        }

        // ---------------------------------------------------------------- invocation

        private Operand Invoke(InvocationExpressionSyntax node)
        {
            if (!engine.AllowImplicitFuncEval)
                throw new DebuggerException("Implicit function evaluation is turned off by the user.");
            if (node.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && node.ArgumentList.Arguments.Count == 1)
                return new HostOperand(NameOf(node.ArgumentList.Arguments[0].Expression));
            if (!allowCalls)
                throw new DebuggerException("Method calls are not evaluated in this context.");
            List<Operand> arguments = node.ArgumentList.Arguments.Select(a => Eval(a.Expression)).ToList();
            if (arguments.Any(a => a is LambdaOperand))
                return InvokeWithLambda(node, arguments);
            if (node.Expression is GenericNameSyntax unqualified)
            {
                _explicitTypeArguments = unqualified.TypeArgumentList.Arguments.Select(t => ToCorDebugType(ResolveType(t))).ToList();
                try
                {
                    if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } selfGetter }
                        && TryCall(new RemoteOperand(selfGetter), null, unqualified.Identifier.ValueText, arguments, false) is { } instanceCall)
                        return instanceCall;
                    return EnclosingType() is { } type ? Call(null, type, unqualified.Identifier.ValueText, arguments, false)
                        : throw new DebuggerException("No enclosing type.");
                }
                finally { _explicitTypeArguments = null; }
            }

            // Method<T>(...): the type arguments are given instead of inferred
            if (node.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax genericMethod } genericCall)
            {
                _explicitTypeArguments = genericMethod.TypeArgumentList.Arguments.Select(t => ToCorDebugType(ResolveType(t))).ToList();
                try
                {
                    return CallOn(Eval(genericCall.Expression), genericMethod.Identifier.ValueText, arguments);
                }
                finally
                {
                    _explicitTypeArguments = null;
                }
            }

            switch (node.Expression)
            {
                case IdentifierNameSyntax id:
                    {
                        string name = id.Identifier.ValueText;
                        if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } thisGetter }
                            && TryCall(new RemoteOperand(thisGetter), null, name, arguments, false) is { } viaThis)
                        {
                            return viaThis;
                        }
                        if (EnclosingType() is { } enclosing && TryCall(null, enclosing, name, arguments, false) is { } viaType)
                            return viaType;
                        foreach (TypeOperand imported in ImportedTypes())
                            if (TryCall(null, imported, name, arguments, false) is { } viaImport)
                                return viaImport;
                        // not a method: a delegate in a local, a field or a property
                        if (Identifier(name) is RemoteOperand callable)
                            return CallOn(callable, "Invoke", arguments);
                        throw new DebuggerException($"The name '{name}' does not exist in the current context.");
                    }
                case MemberAccessExpressionSyntax member:
                    return CallOn(Eval(member.Expression), SimpleName(member.Name), arguments);
                case MemberBindingExpressionSyntax binding:
                    return CallOn(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), SimpleName(binding.Name), arguments);
                default:
                    throw new DebuggerException("Only methods can be invoked.");
            }
        }

        private Operand CallOn(Operand target, string name, List<Operand> arguments) => target switch
        {
            TypeOperand type => Call(null, type, name, arguments, false),
            NamespaceOperand ns => throw new DebuggerException($"The name '{ns.Name}' does not exist in the current context."),
            HostOperand { Value: null } => throw new DebuggerException("NullReferenceException: the value is null."),
            HostOperand { Value: string } host => CallInstanceOrExtension(new RemoteOperand(Materialize(host)), name, arguments),
            HostOperand host => CallOnHostOrExtension(host, host.Value, name, arguments),
            RemoteOperand remote when IsPrimitive(remote) => CallOnHostOrExtension(remote, ToHost(remote), name, arguments),
            _ => CallInstanceOrExtension((RemoteOperand)target, name, arguments),
        };

        private Operand CallInstanceOrExtension(RemoteOperand target, string name, List<Operand> arguments) =>
            TryCall(target, null, name, arguments, isPropertyAccess: false)
            ?? TryCallExtension(target, name, arguments)
            ?? TryCall(target, null, name, arguments, isPropertyAccess: false, explicitImplementations: true)
            ?? throw new DebuggerException($"0x80070057: No method or extension method '{name}' takes {arguments.Count} argument(s) of these types.");

        private Operand CallOnHostOrExtension(Operand target, object? value, string name, List<Operand> arguments)
        {
            try
            {
                return CallOnHost(value, name, arguments);
            }
            catch (DebuggerException e) when (e.Message.StartsWith("No overload", StringComparison.Ordinal) || e.Message.StartsWith("The operation is only", StringComparison.Ordinal))
            {
                return TryCallExtension(target, name, arguments)
                    ?? throw new DebuggerException($"0x80070057: No method or extension method '{name}' takes {arguments.Count} argument(s) of these types.");
            }
        }

        private static bool IsPrimitive(RemoteOperand operand) =>
            operand.Getter() is { } value && ValueInspector.Unwrap(value, out _) is { } unwrapped && ValueInspector.ReadPrimitive(unwrapped) != null;

        // ICorDebug cannot call instance methods on primitives that are not boxed; their methods are pure, so the
        // debugger's own runtime gives the same answer.
        private Operand CallOnHost(object? value, string name, List<Operand> arguments)
        {
            if (value == null)
                throw new DebuggerException("NullReferenceException: the value is null.");
            object?[] hostArguments = arguments.Select(ToHost).ToArray();
            try
            {
                return new HostOperand(value.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, value, hostArguments, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (MissingMethodException)
            {
                throw new DebuggerException($"No overload of '{name}' takes {arguments.Count} argument(s) of these types.");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                throw new DebuggerException(e.InnerException.GetType().Name + ": " + e.InnerException.Message);
            }
        }

        private Operand Call(RemoteOperand? target, TypeOperand? type, string name, List<Operand> arguments, bool isPropertyAccess)
        {
            if (!allowCalls && !isPropertyAccess)
                throw new DebuggerException("Method calls are not evaluated in this context.");
            return TryCall(target, type, name, arguments, isPropertyAccess)
                ?? (target == null || name == "get_Item" ? null : TryCall(target, type, name, arguments, isPropertyAccess, explicitImplementations: true))
                ?? throw new DebuggerException($"0x80070057: No overload of '{name}' takes {arguments.Count} argument(s) of these types.");
        }

        /// <summary>Base-type chain to search members in, including for primitives, strings and arrays.</summary>
        private List<MethodLevel> MethodLevels(CorDebugValue unwrapped)
        {
            List<MethodLevel> result = _values.GetTypeLevels(unwrapped)
                .Select(l => new MethodLevel(l.Type.Class.Module, l.Metadata, l.Token, l.Type)).ToList();
            if (result.Count > 0)
                return result;

            string clrName = unwrapped.Type is CorElementType.SZArray or CorElementType.Array ? "System.Array" : _values.ClrTypeName(unwrapped.ExactType);
            string[] chain = unwrapped.Type is CorElementType.String or CorElementType.SZArray or CorElementType.Array
                ? [clrName, "System.Object"]
                : [clrName, "System.ValueType", "System.Object"];
            foreach (string typeName in chain)
                if (FindType(typeName) is { } found)
                    result.Add(new MethodLevel(found.Module.Module, found.Module.Metadata!, found.Token, null));
            return result;
        }

        /// <param name="explicitImplementations">
        /// Look at explicit interface implementations ("IList.Contains") instead of the ordinary methods. C# only sees
        /// those through the interface, so they come last: after instance and extension methods.
        /// </param>
        private Operand? TryCall(RemoteOperand? target, TypeOperand? type, string name, List<Operand> arguments, bool isPropertyAccess,
            bool explicitImplementations = false, int? onlyToken = null)
        {
            List<MethodLevel> levels;
            if (target != null)
            {
                CorDebugValue? self = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) ?? v : null;
                if (self == null)
                    throw new DebuggerException("NullReferenceException: the value is null.");
                levels = MethodLevels(self);
            }
            else
            {
                levels = [new MethodLevel(type!.Module.Module, type.Metadata, type.Token, null)];
            }

            // overload resolution by argument count and a simple type score; the most derived type wins
            (MethodLevel Level, MethodDescription Method, Dictionary<int, TypeSignature> Bindings, List<Operand> Arguments)? best = null;
            foreach (MethodLevel level in levels)
            {
                int bestScore = 0;
                // "Interface.Method" implementations are private and only reachable by their mangled name
                List<MethodDescription> candidates = explicitImplementations
                    ? level.Metadata.GetExplicitImplementations(level.Token, name)
                    : level.Metadata.GetMethods(level.Token, name);
                foreach (MethodDescription candidate in candidates)
                {
                    if (isPropertyAccess && name == "get_Item"
                        && !level.Metadata.GetProperties(level.Token).Any(p => p.GetterToken == candidate.Token && p.IsIndexer))
                        continue;
                    MethodDescription method = CloseParameters(candidate, level.Type, target == null ? type : null);
                    if (_explicitTypeArguments != null && method.GenericParameterCount != _explicitTypeArguments.Count)
                        continue;
                    if (onlyToken != null && method.Token != onlyToken)
                        continue;
                    if (method.IsStatic != (target == null) || method.ParameterTypes.Length < arguments.Count)
                        continue;
                    // parameters left out are filled in with their default values, where they have one
                    List<Operand>? complete = method.ParameterTypes.Length == arguments.Count ? arguments : WithDefaults(level.Metadata, method, arguments);
                    if (complete == null)
                        continue;
                    int score = 1 + ScoreByUnification(method, complete, complete.Select(ClrTypeOf).ToList(), out Dictionary<int, TypeSignature> bindings);
                    // a method that takes exactly these arguments beats one that needs defaults
                    score = score <= 0 ? score : score * 2 + (ReferenceEquals(complete, arguments) ? 1 : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (level, method, bindings, complete);
                    }
                }
                if (best != null)
                    break;
            }
            if (best == null)
                return null;

            (MethodLevel chosenLevel, MethodDescription chosen, Dictionary<int, TypeSignature> inferred, arguments) = best.Value;
            arguments = arguments.Select((argument, i) => FindImplicitConversion(argument, chosen.ParameterTypes[i]) is { } conversion
                ? ApplyConversion(argument, conversion) : argument).ToList();
            arguments = arguments.Select((argument, i) =>
            {
                string? sourceType = ClrTypeOf(argument);
                string parameter = chosen.ParameterTypes[i];
                if (sourceType != null && sourceType != parameter && IsWidening(sourceType, parameter)
                    && TryToHost(argument, out object? value, out _))
                    return (Operand)new HostOperand(Convert.ChangeType(value, Type.GetType(parameter)!, System.Globalization.CultureInfo.InvariantCulture));
                return argument;
            }).ToList();

            // Anything that runs code (string creation) comes first: it invalidates plain ICorDebug values.
            // A method of System.Enum, System.ValueType or System.Object wants a boxed "this".
            Func<CorDebugValue?>? boxedThis = null;
            if (target != null && chosenLevel.Metadata.GetTypeName(chosenLevel.Token) is "System.Enum" or "System.ValueType" or "System.Object"
                && target.Getter() is { } selfValue && selfValue is not CorDebugReferenceValue
                && _values.TryReadHostValue(selfValue, out object? selfHost) && selfHost is ValueInspector.EnumBits selfBits)
            {
                boxedThis = BoxEnum(new HostOperand(selfBits.Underlying, selfBits.Class));
            }

            var argumentGetters = new Func<CorDebugValue?>[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                argumentGetters[i] = arguments[i] switch
                {
                    HostOperand { Value: decimal value } => MaterializeDecimal(value),
                    HostOperand { Value: string } host => Materialize(host),
                    HostOperand { EnumClass: not null, Value: not null } host => BoxEnum(host),
                    // A primitive for an "object" parameter has to be boxed by the caller. (For a generic "T value" it must
                    // not be: the func-eval takes the raw value there and misreads a box.)
                    HostOperand { Value: not null } host when chosen.ParameterTypes[i] == "System.Object" => MaterializeBoxed(host.Value),
                    RemoteOperand remote when chosen.ParameterTypes[i] == "System.Object" && IsPrimitive(remote) => MaterializeBoxed(ToHost(remote)!),
                    RemoteOperand remote => remote.Getter,
                    HostOperand host => null!, // created right before the call
                    _ => throw new DebuggerException("Types and namespaces cannot be passed as arguments."),
                };
            }

            var values = new List<CorDebugValue>();
            CorDebugValue? thisValue = null;
            if (target != null)
            {
                CorDebugValue raw = target.Getter()!;
                CorDebugValue unwrappedSelf = ValueInspector.Unwrap(raw, out _) ?? raw;
                bool isValueType = unwrappedSelf.Type is not (CorElementType.Class or CorElementType.Object or CorElementType.String
                    or CorElementType.SZArray or CorElementType.Array);
                thisValue = boxedThis?.Invoke() ?? (isValueType ? unwrappedSelf : raw);
                values.Add(thisValue);
            }
            for (int i = 0; i < arguments.Count; i++)
            {
                CorDebugValue? value = argumentGetters[i] != null
                    ? argumentGetters[i]()
                    : CreatePrimitive(ConvertForParameter(((HostOperand)arguments[i]).Value, chosen.ParameterTypes[i]));
                // an enum made by the debugger is a box: right for "Enum" and "object", the value itself for "Perm" and "T"
                if (value != null && arguments[i] is HostOperand { EnumClass: not null }
                    && chosen.ParameterTypes[i] is not ("System.Enum" or "System.ValueType" or "System.Object"))
                {
                    value = ValueInspector.Unwrap(value, out _);
                }
                values.Add(value ?? throw new DebuggerException($"Argument {i + 1} is not available."));
            }

            // class type arguments come from the exact type of "this", method type arguments are inferred from the arguments
            var typeArguments = new List<CorDebugType>();
            if (target != null)
            {
                CorDebugValue currentValue = target.Getter()!;
                MethodLevel current = MethodLevels(ValueInspector.Unwrap(currentValue, out _) ?? currentValue)
                    .First(l => l.Token == chosenLevel.Token && ReferenceEquals(l.Metadata, chosenLevel.Metadata));
                if (current.Type != null)
                    typeArguments.AddRange(ValueInspector.TypeArgumentsOf(current.Type));
            }
            else if (type!.TypeArguments is { Count: > 0 } classArguments)
            {
                typeArguments.AddRange(classArguments.Select(ToCorDebugType));
            }

            if (_explicitTypeArguments is { } given && chosen.GenericParameterCount > 0)
            {
                if (given.Count != chosen.GenericParameterCount)
                    throw new DebuggerException($"Method '{name}' takes {chosen.GenericParameterCount} type argument(s).");
                typeArguments.AddRange(given);
            }
            for (int g = 0; g < (_explicitTypeArguments == null ? chosen.GenericParameterCount : 0); g++)
            {
                // inferred while matching the arguments against the parameter types (IEnumerable<T> <- List<int>)
                if (!inferred.TryGetValue(g, out TypeSignature? typeArgument))
                    throw new DebuggerException($"0x80070057: The type arguments for method '{name}' cannot be inferred.");
                typeArguments.Add(BuildType(typeArgument));
            }

            CorDebugFunction function = chosenLevel.Module.GetFunctionFromToken(new mdMethodDef(chosen.Token));
            CorDebugValue? result = ((IEvalHost)engine).CallFunction(frame.ThreadId, function, typeArguments.ToArray(), values.ToArray());
            return FromResult(result);
        }

        private Operand FromResult(CorDebugValue? result)
        {
            if (result == null)
                return new VoidOperand();
            if (_values.TryReadHostValue(result, out object? host))
            {
                return host is ValueInspector.EnumBits bits
                    ? new HostOperand(bits.Underlying, bits.Class)
                    : new HostOperand(host);
            }

            return new RemoteOperand(engine.Stabilize(() => result));
        }

        private List<Operand>? WithDefaults(ModuleMetadata metadata, MethodDescription method, List<Operand> arguments)
        {
            Dictionary<int, object?> defaults = metadata.GetParameterDefaults(method.Token);
            var complete = new List<Operand>(arguments);
            for (int i = arguments.Count; i < method.ParameterTypes.Length; i++)
            {
                if (!defaults.TryGetValue(i, out object? value))
                    return null;
                // the constant of an enum parameter is a number: it has to become that enum again
                TypeOperand? parameterType = value == null || value is string ? null : ResolveQualifiedType(method.ParameterTypes[i]);
                complete.Add(parameterType != null && parameterType.Metadata.IsEnum(parameterType.Token)
                    ? new HostOperand(value, parameterType.Class)
                    : new HostOperand(value));
            }
            return complete;
        }

        private string? EnumTypeName(CorDebugClass enumClass) => engine.GetMetadata(enumClass.Module)?.GetTypeName((int)enumClass.Token.Value);

        private string? ClrTypeOf(Operand operand) => operand switch
        {
            VoidOperand => "System.Void",
            HostOperand { EnumClass: { } enumClass, Value: not null } => EnumTypeName(enumClass),
            HostOperand host => host.Value?.GetType().FullName,
            RemoteOperand remote => remote.Getter() is { } v ? _values.GetClrTypeName(v) : null,
            _ => null,
        };

        private static readonly HashSet<string> s_numericTypes =
        [
            "System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
            "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Char",
        ];

        // 0 disqualifies the overload
        private int Score(MethodDescription method, List<Operand> arguments, List<string?> argumentTypes)
        {
            int total = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                string parameter = method.ParameterTypes[i];
                string? argument = argumentTypes[i];
                bool parameterIsValueType = s_numericTypes.Contains(parameter) || parameter is "System.Boolean" or "System.Decimal";
                int score;
                if (parameter.StartsWith("ref ", StringComparison.Ordinal) || parameter.EndsWith('*'))
                    score = 0;
                else if (argument == null)
                    score = parameterIsValueType ? 0 : 3;
                else if (argument == parameter)
                    score = 6;
                else if (parameter.StartsWith('!'))
                    score = 4;
                else if (s_numericTypes.Contains(parameter) && s_numericTypes.Contains(argument) && arguments[i] is HostOperand)
                    score = IsWidening(argument, parameter) ? 3 : 0;
                else if (parameter == "System.Object")
                    score = 1;
                else if (parameterIsValueType || s_numericTypes.Contains(argument) || argument is "System.Boolean" or "System.String")
                    score = 0;
                else
                    score = 2; // some other reference type: base class or interface, trust the user
                if (score == 0)
                    return -1;
                total += score;
            }
            return total;
        }

        private static bool IsWidening(string from, string to)
        {
            string[] targets = from switch
            {
                "System.SByte" => ["System.Int16", "System.Int32", "System.Int64", "System.Single", "System.Double", "System.Decimal"],
                "System.Byte" => ["System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal"],
                "System.Int16" => ["System.Int32", "System.Int64", "System.Single", "System.Double", "System.Decimal"],
                "System.UInt16" => ["System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal"],
                "System.Char" => ["System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal"],
                "System.Int32" => ["System.Int64", "System.Single", "System.Double", "System.Decimal"],
                "System.UInt32" => ["System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal"],
                "System.Int64" or "System.UInt64" => ["System.Single", "System.Double", "System.Decimal"],
                "System.Single" => ["System.Double"],
                _ => [],
            };
            return targets.Contains(to);
        }

        private static object? ConvertForParameter(object? value, string parameterType)
        {
            if (value == null || !s_numericTypes.Contains(parameterType) || value is bool)
                return value;
            Type? type = Type.GetType(parameterType);
            return type == null || type == value.GetType() ? value : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- creating values in the debuggee

        private Func<CorDebugValue?> Materialize(HostOperand host)
        {
            if (host.Value is string text)
            {
                CorDebugValue? created = engine.RunEval(frame.ThreadId, eval => eval.NewString(text));
                return engine.Stabilize(() => created);
            }
            object? value = host.Value;
            return () => CreatePrimitive(value);
        }

        private Func<CorDebugValue?> MaterializeDecimal(decimal value)
        {
            TypeOperand type = ResolveQualifiedType("System.Decimal") ?? throw new DebuggerException("System.Decimal is not loaded.");
            CorDebugValue? box = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(type.Class.Raw, 0, []));
            Func<CorDebugValue?> stable = engine.Stabilize(() => box);
            int[] bits = decimal.GetBits(value);
            if (_values.GetFieldByName(stable()!, "_flags") != null)
            {
                AssignCore(() => _values.GetFieldByName(stable()!, "_flags"), new HostOperand(bits[3]));
                AssignCore(() => _values.GetFieldByName(stable()!, "_hi32"), new HostOperand((uint)bits[2]));
                AssignCore(() => _values.GetFieldByName(stable()!, "_lo64"), new HostOperand((uint)bits[0] | ((ulong)(uint)bits[1] << 32)));
            }
            else
            {
                foreach (var (field, part) in new[] { ("lo", bits[0]), ("mid", bits[1]), ("hi", bits[2]), ("flags", bits[3]) })
                    AssignCore(() => _values.GetFieldByName(stable()!, field), new HostOperand(part));
            }
            return () => ValueInspector.Unwrap(stable()!, out _);
        }

        /// <summary>An enum value of the debugger's making, as a boxed object in the debuggee.</summary>
        private Func<CorDebugValue?> BoxEnum(HostOperand host)
        {
            CorDebugClass cls = host.EnumClass!;
            CorDebugValue? box = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(cls.Raw, 0, []));
            Func<CorDebugValue?> boxed = engine.Stabilize(() => box);
            AssignCore(() => _values.GetFieldByName(boxed()!, "value__"), new HostOperand(host.Value));
            return boxed;
        }

        private Func<CorDebugValue?> MaterializeBoxed(object value)
        {
            TypeOperand type = ResolveQualifiedType(value.GetType().FullName!) ?? throw new DebuggerException($"Type '{value.GetType()}' not found.");
            CorDebugClass cls = type.Class;
            CorDebugValue? box = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(cls.Raw, 0, []));
            Func<CorDebugValue?> boxed = engine.Stabilize(() => box);

            // the box holds a zeroed System.Int32-style struct whose only field is the raw value
            CorDebugValue slot = _values.GetFieldByName(boxed()!, "m_value") ?? throw new DebuggerException("Unexpected layout of a boxed primitive.");
            CorDebugValue template = CreatePrimitive(value);
            var bytes = new byte[template.Size];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                template.As<CorDebugGenericValue>().GetValue(pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }
            ValueInspector.WriteBytes(slot, bytes);
            return boxed;
        }

        private CorDebugValue CreatePrimitive(object? value)
        {
            CorDebugEval eval = engine.RequireThread(frame.ThreadId).CreateEval();
            if (value == null)
                return eval.CreateValue(CorElementType.Class, null!);

            (CorElementType type, byte[] bytes) = value switch
            {
                bool v => (CorElementType.Boolean, new[] { (byte)(v ? 1 : 0) }),
                char v => (CorElementType.Char, BitConverter.GetBytes(v)),
                sbyte v => (CorElementType.I1, new[] { (byte)v }),
                byte v => (CorElementType.U1, new[] { v }),
                short v => (CorElementType.I2, BitConverter.GetBytes(v)),
                ushort v => (CorElementType.U2, BitConverter.GetBytes(v)),
                int v => (CorElementType.I4, BitConverter.GetBytes(v)),
                uint v => (CorElementType.U4, BitConverter.GetBytes(v)),
                long v => (CorElementType.I8, BitConverter.GetBytes(v)),
                ulong v => (CorElementType.U8, BitConverter.GetBytes(v)),
                float v => (CorElementType.R4, BitConverter.GetBytes(v)),
                double v => (CorElementType.R8, BitConverter.GetBytes(v)),
                _ => throw new DebuggerException($"Values of type '{value.GetType().Name}' cannot be passed to the debuggee."),
            };
            CorDebugValue created = eval.CreateValue(type, null!);
            ValueInspector.WriteBytes(created, bytes);
            return created;
        }

        // ---------------------------------------------------------------- operators

        private bool TryToHost(Operand operand, out object? value, out CorDebugClass? enumClass)
        {
            value = null;
            enumClass = null;
            switch (operand)
            {
                case VoidOperand:
                    return false;
                case HostOperand host:
                    value = host.Value;
                    enumClass = host.EnumClass;
                    return true;
                case RemoteOperand remote:
                    {
                        CorDebugValue? raw = remote.Getter();
                        if (raw == null)
                            throw new DebuggerException("The value is not available.");
                        if (!_values.TryReadHostValue(raw, out value))
                            return false;
                        if (value is ValueInspector.EnumBits bits)
                        {
                            value = bits.Underlying;
                            enumClass = bits.Class;
                        }
                        return true;
                    }
                default:
                    ToVariable("", operand); // throws the "is a type/namespace" error
                    return false;
            }
        }

        private object? ToHost(Operand operand) => TryToHost(operand, out object? value, out _)
            ? value
            : throw new DebuggerException("The operation is only supported for primitive values and strings.");

        private static ulong ToBits(object value) => value is ulong u ? u : unchecked((ulong)Convert.ToInt64(value));

        private Operand Unary(PrefixUnaryExpressionSyntax node)
        {
            if (node.Kind() == SyntaxKind.PointerIndirectionExpression)
            {
                if (Eval(node.Operand) is not RemoteOperand pointer || pointer.Getter() is not CorDebugReferenceValue { Type: CorElementType.Ptr } address)
                    throw new DebuggerException("The * operator can only be applied to a pointer.");
                if (address.IsNull)
                    throw new DebuggerException("NullReferenceException: the pointer is null.");
                return new RemoteOperand(() => ((CorDebugReferenceValue)pointer.Getter()!).Dereference(), IsLocation: true);
            }

            Operand value = Eval(node.Operand);
            if (TryUserOperator(node.Kind(), [value]) is { } overloaded)
                return overloaded;
            if (!TryToHost(value, out object? host, out CorDebugClass? enumClass)
                || enumClass != null && node.Kind() != SyntaxKind.BitwiseNotExpression)
                throw UnaryOperatorError(node.OperatorToken.Text, value);
            dynamic? operand = host;
            try
            {
                return new HostOperand(node.Kind() switch
                {
                    SyntaxKind.UnaryMinusExpression => _checked ? checked(-operand) : -operand,
                    SyntaxKind.UnaryPlusExpression => +operand,
                    SyntaxKind.LogicalNotExpression => !operand,
                    SyntaxKind.BitwiseNotExpression => ~operand,
                    _ => throw new DebuggerException($"Operator '{node.OperatorToken.Text}' is not supported."),
                }, enumClass, IsConstant: value is HostOperand { IsConstant: true });
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw UnaryOperatorError(node.OperatorToken.Text, value);
            }
        }

        private Operand Binary(BinaryExpressionSyntax node)
        {
            switch (node.Kind())
            {
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    {
                        Operand left = Eval(node.Left);
                        Operand right = Eval(node.Right);
                        if (ToHost(left) is not bool condition)
                            throw OperandType(left) == "decimal" ? BinaryOperatorError(node.OperatorToken.Text, left, right)
                                : new DebuggerException($"Cannot implicitly convert type '{OperandType(left)}' to 'bool'");
                        return ToHost(right) is bool result ? new HostOperand(node.Kind() == SyntaxKind.LogicalAndExpression
                            ? condition && result : condition || result)
                            : throw BinaryOperatorError(node.OperatorToken.Text, left, right);
                    }
                case SyntaxKind.CoalesceExpression:
                    {
                        Operand left = Eval(node.Left);
                        Operand right = Eval(node.Right);
                        if (!CanCoalesce(left, right))
                            throw new DebuggerException($"error CS0019: Operator ?? cannot be applied to operands of type '{OperandType(left)}' and '{OperandType(right)}'");
                        return IsNull(left) ? right : left;
                    }
                case SyntaxKind.IsExpression:
                    return new HostOperand(IsInstanceOf(Eval(node.Left), ResolveType((TypeSyntax)node.Right)));
                case SyntaxKind.AsExpression:
                    {
                        Operand left = Eval(node.Left);
                        return IsInstanceOf(left, ResolveType((TypeSyntax)node.Right)) ? left : new HostOperand(null);
                    }
            }

            return BinaryOperation(node.Kind(), Eval(node.Left), Eval(node.Right), node.OperatorToken.Text);
        }

        private Operand BinaryOperation(SyntaxKind kind, Operand leftOperand, Operand rightOperand, string operatorText)
        {
            if (TryUserOperator(kind, [leftOperand, rightOperand]) is { } overloaded)
                return overloaded;
            if (kind == SyntaxKind.AddExpression && (IsStringOperand(leftOperand) || IsStringOperand(rightOperand)))
                return new HostOperand(ConcatenationText(leftOperand) + ConcatenationText(rightOperand));
            if (leftOperand is VoidOperand || rightOperand is VoidOperand)
                throw BinaryOperatorError(operatorText, leftOperand, rightOperand);
            bool leftIsHost = TryToHost(leftOperand, out object? l, out _);
            bool rightIsHost = TryToHost(rightOperand, out object? r, out _);
            if (!leftIsHost && TryImplicitHost(leftOperand, ClrTypeOf(rightOperand)) is { } leftConverted)
                leftIsHost = TryToHost(leftConverted, out l, out _);
            if (!rightIsHost && TryImplicitHost(rightOperand, ClrTypeOf(leftOperand)) is { } rightConverted)
                rightIsHost = TryToHost(rightConverted, out r, out _);
            if (!leftIsHost || !rightIsHost)
            {
                // objects only support reference comparison
                if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
                    throw BinaryOperatorError(operatorText, leftOperand, rightOperand);
                bool same = AddressOf(leftOperand, leftIsHost, l) == AddressOf(rightOperand, rightIsHost, r);
                return new HostOperand(kind == SyntaxKind.EqualsExpression ? same : !same);
            }

            dynamic? a = l, b = r;
            if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
                or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                && (l is ulong && r is sbyte or short or int or long || r is ulong && l is sbyte or short or int or long))
                throw new DebuggerException($"Operator '{operatorText}' is ambiguous on operands of type '{OperandType(leftOperand)}' and '{OperandType(rightOperand)}'");
            try
            {
                return new HostOperand(kind switch
                {
                    SyntaxKind.AddExpression => _checked ? checked(a + b) : a + b,
                    SyntaxKind.SubtractExpression => _checked ? checked(a - b) : a - b,
                    SyntaxKind.MultiplyExpression => _checked ? checked(a * b) : a * b,
                    SyntaxKind.DivideExpression => a / b,
                    SyntaxKind.ModuloExpression => a % b,
                    SyntaxKind.EqualsExpression => a == b,
                    SyntaxKind.NotEqualsExpression => a != b,
                    SyntaxKind.LessThanExpression => a < b,
                    SyntaxKind.LessThanOrEqualExpression => a <= b,
                    SyntaxKind.GreaterThanExpression => a > b,
                    SyntaxKind.GreaterThanOrEqualExpression => a >= b,
                    SyntaxKind.BitwiseAndExpression => a & b,
                    SyntaxKind.BitwiseOrExpression => a | b,
                    SyntaxKind.ExclusiveOrExpression => a ^ b,
                    SyntaxKind.LeftShiftExpression => a << b,
                    SyntaxKind.RightShiftExpression => a >> b,
                    SyntaxKind.UnsignedRightShiftExpression => UnsignedShift(l, r, leftOperand, rightOperand),
                    _ => throw new DebuggerException($"Operator '{operatorText}' is not supported."),
                });
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw BinaryOperatorError(operatorText, leftOperand, rightOperand);
            }
        }

        private static ulong AddressOf(Operand operand, bool isHost, object? hostValue)
        {
            if (isHost)
                return hostValue == null ? 0UL : throw new DebuggerException("Objects can only be compared with other objects or null.");
            CorDebugValue value = ((RemoteOperand)operand).Getter()!;
            if (value is CorDebugReferenceValue reference)
                return reference.IsNull ? 0 : reference.Value.Value;
            return value.Address.Value;
        }

        private bool IsInstanceOf(Operand operand, TypeOperand type)
        {
            if (operand is not RemoteOperand remote)
                throw new DebuggerException("Type tests are only supported for objects of the debuggee.");
            CorDebugValue? unwrapped = remote.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                return false;
            List<MethodLevel> levels = MethodLevels(unwrapped);
            if (levels.Any(l => l.Token == type.Token && ReferenceEquals(l.Metadata, type.Metadata)))
                return true;
            string wanted = type.Metadata.GetTypeName(type.Token);
            return levels.Any(l => l.Metadata.GetInterfaceNames(l.Token).Contains(wanted));
        }

        private Operand Cast(Operand operand, TypeSyntax typeSyntax)
        {
            if (typeSyntax is PredefinedTypeSyntax predefined && predefined.Keyword.Kind() is not (SyntaxKind.ObjectKeyword or SyntaxKind.StringKeyword))
            {
                dynamic? value = ToHost(operand) ?? throw new DebuggerException("NullReferenceException: the value is null.");
                return new HostOperand(predefined.Keyword.Kind() switch
                {
                    SyntaxKind.BoolKeyword => (bool)value,
                    SyntaxKind.ByteKeyword => (byte)value,
                    SyntaxKind.SByteKeyword => (sbyte)value,
                    SyntaxKind.ShortKeyword => (short)value,
                    SyntaxKind.UShortKeyword => (ushort)value,
                    SyntaxKind.IntKeyword => (int)value,
                    SyntaxKind.UIntKeyword => (uint)value,
                    SyntaxKind.LongKeyword => (long)value,
                    SyntaxKind.ULongKeyword => (ulong)value,
                    SyntaxKind.FloatKeyword => (float)value,
                    SyntaxKind.DoubleKeyword => (double)value,
                    SyntaxKind.DecimalKeyword => (decimal)value,
                    SyntaxKind.CharKeyword => (char)value,
                    _ => throw new DebuggerException($"Casts to '{predefined}' are not supported."),
                });
            }

            TypeOperand type = ResolveType(typeSyntax);
            if (type.Metadata.IsEnum(type.Token))
                return new HostOperand(ToHost(operand), type.Class);
            if (IsNull(operand))
                return operand;
            if (operand is RemoteOperand && IsInstanceOf(operand, type))
                return operand;
            throw new DebuggerException($"InvalidCastException: the value is not a '{type.Metadata.GetTypeName(type.Token)}'.");
        }

        // ---------------------------------------------------------------- assignment

        private void AssignCore(Func<CorDebugValue?> location, Operand source, string? declaredType = null)
        {
            CorDebugValue target = location() ?? throw new DebuggerException("The variable is not available.");
            string targetName = _values.GetClrTypeName(target) ?? _values.ClrTypeName(target.ExactType);
            if (FindImplicitConversion(source, targetName) is { } conversion)
            {
                source = ApplyConversion(source, conversion);
                target = location() ?? throw new DebuggerException("The variable is not available.");
            }
            bool targetIsReference = target is CorDebugReferenceValue && target.Type is not (CorElementType.Ptr or CorElementType.FnPtr or CorElementType.ByRef);

            if (targetIsReference)
            {
                if (declaredType != null && declaredType != "System.Object" && !IsNull(source)
                    && ResolveType(SyntaxFactory.ParseTypeName(ModuleMetadata.DisplayType(declaredType))) is { } expected
                    && source is RemoteOperand && !IsInstanceOf(source, expected))
                    throw new DebuggerException("The value cannot be converted to the type of the variable.");
                if (declaredType is not (null or "System.Object" or "System.String") && source is HostOperand { Value: string })
                    throw new DebuggerException("The value cannot be converted to the type of the variable.");
                string? targetType = target.Type == CorElementType.String ? "System.String" : null;
                if (source is HostOperand { Value: string } && target.Type != CorElementType.Object
                    && _values.ClrTypeName(target.ExactType) is not ("System.String" or "System.Object"))
                    throw new DebuggerException("The value cannot be converted to the type of the variable.");
                Func<CorDebugValue?>? sourceGetter = source switch
                {
                    HostOperand { Value: null } => null,
                    HostOperand { Value: string } host => Materialize(host), // runs code: "target" is stale from here on
                    RemoteOperand remote => remote.Getter,
                    _ => throw new DebuggerException("The value cannot be converted to the type of the variable."),
                };
                if (targetType == "System.String" && source is not HostOperand && ClrTypeOf(source) is not (null or "System.String"))
                    throw new DebuggerException("The value cannot be converted to 'string'.");

                ulong address = 0;
                if (sourceGetter != null)
                {
                    if (sourceGetter() is not CorDebugReferenceValue sourceReference)
                        throw new DebuggerException("The value cannot be converted to the type of the variable.");
                    address = sourceReference.IsNull ? 0 : sourceReference.Value.Value;
                }
                ((CorDebugReferenceValue)location()!).Value = new CORDB_ADDRESS(address);
                return;
            }

            if (target.Type == CorElementType.ValueType && _values.GetTypeName(target).StartsWith("System.Nullable<", StringComparison.Ordinal))
            {
                // Nullable<T> is a flag and a value: null clears the flag, anything else goes into the value
                bool isNull = IsNull(source);
                if (!isNull)
                    AssignCore(() => _values.GetFieldByName(location()!, "value"), source);
                else if (_values.GetFieldByName(location()!, "value") is { } stored)
                    ClearValue(stored);
                ValueInspector.WriteBytes(_values.GetFieldByName(location()!, "hasValue")!, [(byte)(isNull ? 0 : 1)]);
                return;
            }
            if (target.Type == CorElementType.ValueType && _values.GetFieldByName(target, "m_value") is { Type: not CorElementType.ValueType })
            {
                AssignCore(() => _values.GetFieldByName(location()!, "m_value"), source);
                return;
            }

            if (target.Type == CorElementType.ValueType && source is RemoteOperand structSource && !TryToHost(source, out _, out _))
            {
                CopyStruct(location()!, structSource.Getter() ?? throw new DebuggerException("The value is not available."));
                return;
            }

            if (target.Type == CorElementType.ValueType && _values.GetTypeName(target) == "decimal")
            {
                if (ToHost(source) is not decimal number)
                    throw new DebuggerException("The value cannot be converted to the type of the variable.");
                Func<CorDebugValue?> converted = MaterializeDecimal(number);
                CopyStruct(location()!, converted()!);
                return;
            }

            object? value = TryToHost(source, out object? hostValue, out _)
                ? hostValue
                : throw new DebuggerException("The value cannot be converted to the type of the variable.");
            if (value == null)
                throw new DebuggerException("Cannot assign null to a value type.");

            CorDebugValue slot = location()!;
            if (slot.Type == CorElementType.ValueType && _values.GetFieldByName(slot, "value__") is { } enumSlot)
                slot = enumSlot;

            byte[] bytes = slot.Type switch
            {
                CorElementType.Boolean => value is bool b ? [(byte)(b ? 1 : 0)] : throw Mismatch(),
                CorElementType.Char => value is char c ? BitConverter.GetBytes(c) : throw Mismatch(),
                CorElementType.I1 => [(byte)Number<sbyte>()],
                CorElementType.U1 => [Number<byte>()],
                CorElementType.I2 => BitConverter.GetBytes(Number<short>()),
                CorElementType.U2 => BitConverter.GetBytes(Number<ushort>()),
                CorElementType.I4 => BitConverter.GetBytes(Number<int>()),
                CorElementType.U4 => BitConverter.GetBytes(Number<uint>()),
                CorElementType.I8 => BitConverter.GetBytes(Number<long>()),
                CorElementType.U8 => BitConverter.GetBytes(Number<ulong>()),
                CorElementType.R4 => BitConverter.GetBytes(Number<float>()),
                CorElementType.R8 => BitConverter.GetBytes(Number<double>()),
                _ => throw new DebuggerException("Variables of this type cannot be modified."),
            };
            ValueInspector.WriteBytes(slot, bytes);

            T Number<T>() where T : struct
            {
                string fromType = value.GetType().FullName!, toType = typeof(T).FullName!;
                bool constantConversion = source is HostOperand { IsConstant: true, Value: int }
                    && typeof(T) is var destination && (destination == typeof(sbyte) || destination == typeof(byte)
                        || destination == typeof(short) || destination == typeof(ushort) || destination == typeof(uint) || destination == typeof(ulong))
                    || source is HostOperand { IsConstant: true, Value: long } && typeof(T) == typeof(ulong);
                if (fromType != toType && !IsWidening(fromType, toType) && !constantConversion)
                    throw Mismatch();
                try
                {
                    return (T)Convert.ChangeType(value is char character ? (int)character : value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException)
                {
                    throw new DebuggerException("The value does not fit the type of the variable.");
                }
            }

            DebuggerException Mismatch() => new("The value cannot be converted to the type of the variable.");
        }

        /// <summary>
        /// Field by field rather than as a block of memory: references inside the struct have to go through the
        /// debugging interface, which knows about the garbage collector's bookkeeping.
        /// </summary>
        private void CopyStruct(CorDebugValue target, CorDebugValue source, int depth = 0)
        {
            CorDebugValue? from = ValueInspector.Unwrap(source, out bool isNull);
            if (isNull || from == null)
                throw new DebuggerException("Cannot assign null to a value type.");
            if (from.Type != CorElementType.ValueType || depth > 16 || _values.GetTypeName(from) != _values.GetTypeName(target))
                throw new DebuggerException("The value cannot be converted to the type of the variable.");

            List<(string Name, CorDebugValue? Value)> targetFields = _values.EnumerateFields(target).ToList();
            List<(string Name, CorDebugValue? Value)> sourceFields = _values.EnumerateFields(from).ToList();
            for (int i = 0; i < targetFields.Count; i++)
            {
                if (targetFields[i].Value is not { } to || i >= sourceFields.Count || sourceFields[i].Value is not { } value)
                    throw new DebuggerException("The value cannot be copied: a field is not available.");
                if (to is CorDebugReferenceValue reference && to.Type is not (CorElementType.Ptr or CorElementType.FnPtr))
                    reference.Value = value is CorDebugReferenceValue { IsNull: false } r ? r.Value : new CORDB_ADDRESS(0);
                else if (to.Type == CorElementType.ValueType)
                    CopyStruct(to, value, depth + 1);
                else
                    ValueInspector.WriteBytes(to, ValueInspector.ReadRawBytes(value));
            }
        }

        private void ClearValue(CorDebugValue value)
        {
            if (value is CorDebugReferenceValue reference)
            {
                reference.Value = new CORDB_ADDRESS(0);
                return;
            }
            if (value.Type == CorElementType.ValueType)
            {
                foreach (var (_, field) in _values.EnumerateFields(value))
                    if (field != null)
                        ClearValue(field);
                return;
            }
            ValueInspector.WriteBytes(value, new byte[value.Size]);
        }
    }
}
