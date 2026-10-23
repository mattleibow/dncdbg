using Microsoft.CodeAnalysis.CSharp;
using DncDbg.Engine.Symbols;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private sealed partial class Evaluator
    {
        private static string RewritePseudoVariables(string expression)
        {
            var tokens = SyntaxFactory.ParseTokens(expression).ToArray();
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Text == "$" && i + 1 < tokens.Length)
                {
                    string? replacement = tokens[i + 1].ValueText switch
                    {
                        "exception" => ExceptionVariable,
                        "ReturnValue" => ReturnValueVariable,
                        "pid" => ProcessIdVariable,
                        "tid" => ThreadIdVariable,
                        _ => null,
                    };
                    if (replacement != null)
                    {
                        result.Append(tokens[i].LeadingTrivia).Append(replacement).Append(tokens[++i].TrailingTrivia);
                        continue;
                    }
                }
                result.Append(tokens[i].ToFullString());
            }
            return result.ToString();
        }

        private sealed record Conversion(TypeOperand Type, MethodDescription Method);

        private Conversion? FindImplicitConversion(Operand operand, string target)
        {
            string? source = ClrTypeOf(operand);
            if (source == null || source == target)
                return null;
            foreach (string name in new[] { source, target }.Distinct())
            {
                if (ResolveQualifiedType(name) is not { } type)
                    continue;
                MethodDescription? method = type.Metadata.GetMethods(type.Token, "op_Implicit").FirstOrDefault(m =>
                    m.ReturnType == target && m.ParameterTypes.Length == 1
                    && (m.ParameterTypes[0] == source || IsWidening(source, m.ParameterTypes[0])));
                if (method != null)
                    return new Conversion(type, method);
            }
            return null;
        }

        private Operand ApplyConversion(Operand operand, Conversion conversion) =>
            TryCall(null, conversion.Type, "op_Implicit", [operand], isPropertyAccess: true, onlyToken: conversion.Method.Token)
                ?? throw new DebuggerException("The implicit conversion could not be evaluated.");

        private Operand? TryImplicitHost(Operand operand, string? target)
        {
            if (target == null || FindImplicitConversion(operand, target) is not { } conversion)
                return null;
            return ApplyConversion(operand, conversion);
        }

        private Operand? TryUserOperator(SyntaxKind kind, List<Operand> arguments)
        {
            string? method = kind switch
            {
                SyntaxKind.AddExpression => "op_Addition",
                SyntaxKind.SubtractExpression => "op_Subtraction",
                SyntaxKind.MultiplyExpression => "op_Multiply",
                SyntaxKind.DivideExpression => "op_Division",
                SyntaxKind.ModuloExpression => "op_Modulus",
                SyntaxKind.EqualsExpression => "op_Equality",
                SyntaxKind.NotEqualsExpression => "op_Inequality",
                SyntaxKind.LessThanExpression => "op_LessThan",
                SyntaxKind.LessThanOrEqualExpression => "op_LessThanOrEqual",
                SyntaxKind.GreaterThanExpression => "op_GreaterThan",
                SyntaxKind.GreaterThanOrEqualExpression => "op_GreaterThanOrEqual",
                SyntaxKind.BitwiseAndExpression => "op_BitwiseAnd",
                SyntaxKind.BitwiseOrExpression => "op_BitwiseOr",
                SyntaxKind.ExclusiveOrExpression => "op_ExclusiveOr",
                SyntaxKind.LeftShiftExpression => "op_LeftShift",
                SyntaxKind.RightShiftExpression => "op_RightShift",
                SyntaxKind.UnaryMinusExpression => "op_UnaryNegation",
                SyntaxKind.UnaryPlusExpression => "op_UnaryPlus",
                SyntaxKind.LogicalNotExpression => "op_LogicalNot",
                SyntaxKind.BitwiseNotExpression => "op_OnesComplement",
                _ => null,
            };
            if (method == null)
                return null;
            foreach (string? name in arguments.Select(ClrTypeOf).Distinct())
            {
                if (name == null || name.StartsWith("System.", StringComparison.Ordinal)
                    || ResolveQualifiedType(name) is not { } type)
                    continue;
                if (TryCall(null, type, method, arguments, isPropertyAccess: true) is { } result)
                    return result;
            }
            return null;
        }

        private string ConcatenationText(Operand operand)
        {
            operand = TryImplicitHost(operand, "System.String") ?? operand;
            return TryToHost(operand, out object? value, out _)
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
                : ToVariable("", operand).Value.Trim('{', '}');
        }

        private bool IsStringOperand(Operand operand) => ClrTypeOf(operand) == "System.String"
            || operand is RemoteOperand remote && remote.Getter() is { } value && _values.GetTypeName(value) == "string";

        private bool CanCoalesce(Operand left, Operand right)
        {
            bool IsReference(Operand operand) => operand switch
            {
                HostOperand host => host.Value == null || !host.Value.GetType().IsValueType,
                RemoteOperand remote when remote.Getter() is { } value =>
                    value.Type is ClrDebug.CorElementType.Class or ClrDebug.CorElementType.Object or ClrDebug.CorElementType.String
                        or ClrDebug.CorElementType.SZArray or ClrDebug.CorElementType.Array,
                _ => false,
            };
            if (!IsReference(left) || !IsReference(right))
                return false;
            if (left is HostOperand { Value: null, DeclaredType: null } || right is HostOperand { Value: null, DeclaredType: null })
                return true;
            string leftType = OperandType(left), rightType = OperandType(right);
            if (leftType == rightType || leftType == "object" || rightType == "object")
                return true;
            return left is RemoteOperand leftRemote && right is RemoteOperand rightRemote
                && leftRemote.Getter() is { } l && rightRemote.Getter() is { } r
                && (_values.GetTypeLevels(l).Any(t => _values.TypeName(t.Type) == rightType)
                    || _values.GetTypeLevels(r).Any(t => _values.TypeName(t.Type) == leftType));
        }

        private string OperandType(Operand operand) =>
            operand is VoidOperand ? "void" : ToVariable("", operand).Type ?? "null";

        private DebuggerException UnaryOperatorError(string op, Operand operand) =>
            new($"Operator '{op}' cannot be applied to operand of type '{OperandType(operand)}'");

        private DebuggerException BinaryOperatorError(string op, Operand left, Operand right) =>
            new($"Operator '{op}' cannot be applied to operands of type '{OperandType(left)}' and '{OperandType(right)}'");
    }
}
