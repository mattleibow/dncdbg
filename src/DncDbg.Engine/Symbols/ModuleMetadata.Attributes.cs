using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DncDbg.Engine.Symbols;

internal enum BrowsableState
{
    Never = 0,
    Collapsed = 2,
    RootHidden = 3,
}

// Debugger-related custom attributes, IL call sites and source documents.
internal sealed partial class ModuleMetadata
{
    public bool IsHiddenMethod(int methodToken)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        return FindAttribute(method.GetCustomAttributes(), "System.Diagnostics.DebuggerHiddenAttribute") != null
            || FindAttribute(_md.GetTypeDefinition(method.GetDeclaringType()).GetCustomAttributes(), "System.Diagnostics.DebuggerHiddenAttribute") != null;
    }

    public bool IsStepThroughMethod(int methodToken)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        string[] attributes = ["System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerStepThroughAttribute"];
        return FindAttribute(method.GetCustomAttributes(), attributes) != null
            || FindAttribute(_md.GetTypeDefinition(method.GetDeclaringType()).GetCustomAttributes(), attributes) != null;
    }

    public bool IsThreadStaticField(int fieldToken) => FindAttribute(
        _md.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.Handle(fieldToken)).GetCustomAttributes(), "System.ThreadStaticAttribute") != null;

    public bool PropagateAsyncAttribute(int methodToken, bool includeNonUserCode)
    {
        // Roslyn adds DebuggerStepThrough to async stubs; it alone must not hide their MoveNext bodies.
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        string[] direct = includeNonUserCode
            ? ["System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerNonUserCodeAttribute"]
            : ["System.Diagnostics.DebuggerHiddenAttribute"];
        string[] typeAttributes = includeNonUserCode ? s_nonUserCodeAttributes
            : ["System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerStepThroughAttribute"];
        return FindAttribute(method.GetCustomAttributes(), direct) != null
            || FindAttribute(_md.GetTypeDefinition(method.GetDeclaringType()).GetCustomAttributes(), typeAttributes) != null;
    }
    private const string DebuggerDisplayAttribute = "System.Diagnostics.DebuggerDisplayAttribute";
    private const string DebuggerBrowsableAttribute = "System.Diagnostics.DebuggerBrowsableAttribute";
    private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private static readonly string[] s_nonUserCodeAttributes =
    [
        "System.Diagnostics.DebuggerHiddenAttribute",
        "System.Diagnostics.DebuggerStepThroughAttribute",
        "System.Diagnostics.DebuggerNonUserCodeAttribute",
    ];

    private readonly Dictionary<int, string?> _debuggerDisplayCache = [];

    // ---------------------------------------------------------------- attributes

    private string GetAttributeTypeName(CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                EntityHandle parent = _md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference type = _md.GetTypeReference((TypeReferenceHandle)parent);
                    return _md.GetString(type.Namespace) + "." + _md.GetString(type.Name);
                }
                return parent.Kind == HandleKind.TypeDefinition ? GetTypeName((TypeDefinitionHandle)parent) : "";
            }
            case HandleKind.MethodDefinition:
                return GetTypeName(_md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
            default:
                return "";
        }
    }

    private CustomAttribute? FindAttribute(CustomAttributeHandleCollection attributes, params string[] names)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = _md.GetCustomAttribute(handle);
            if (names.Contains(GetAttributeTypeName(attribute)))
                return attribute;
        }
        return null;
    }

    /// <summary>The format string of [DebuggerDisplay] on the type itself (not inherited), or null.</summary>
    public string? GetDebuggerDisplay(int typeDefToken)
    {
        lock (_sync)
        {
            if (_debuggerDisplayCache.TryGetValue(typeDefToken, out string? cached))
                return cached;

            string? result = null;
            TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
            if (FindAttribute(type.GetCustomAttributes(), DebuggerDisplayAttribute) is { } attribute)
            {
                // blob: prolog (0x0001), then the constructor's single string argument
                BlobReader reader = _md.GetBlobReader(attribute.Value);
                if (reader.Length > 2 && reader.ReadUInt16() == 1)
                    result = reader.ReadSerializedString();
            }
            if (result == null && _md.IsAssembly)
            {
                string typeName = GetTypeName(typeDefToken);
                foreach (CustomAttributeHandle handle in _md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    CustomAttribute assemblyAttribute = _md.GetCustomAttribute(handle);
                    if (GetAttributeTypeName(assemblyAttribute) != DebuggerDisplayAttribute)
                        continue;
                    BlobReader reader = _md.GetBlobReader(assemblyAttribute.Value);
                    if (reader.ReadUInt16() != 1)
                        continue;
                    string? template = reader.ReadSerializedString();
                    int namedCount = reader.ReadUInt16();
                    for (int i = 0; i < namedCount; i++)
                    {
                        reader.ReadByte();
                        byte kind = reader.ReadByte();
                        if (kind is not (0x0e or 0x50))
                            break;
                        string? name = reader.ReadSerializedString();
                        string? value = reader.ReadSerializedString();
                        if (name is "Target" or "TargetTypeName" && value?.Split(',')[0].Replace('+', '.') == typeName)
                            result = template;
                    }
                }
            }
            return _debuggerDisplayCache[typeDefToken] = result;
        }
    }

    public Dictionary<string, string> GetMemberDebuggerDisplays(int typeToken)
    {
        var result = new Dictionary<string, string>();
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeToken));
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(handle);
            Add(_md.GetString(field.Name), field.GetCustomAttributes());
        }
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = _md.GetPropertyDefinition(handle);
            Add(_md.GetString(property.Name), property.GetCustomAttributes());
        }
        return result;

        void Add(string name, CustomAttributeHandleCollection attributes)
        {
            if (FindAttribute(attributes, DebuggerDisplayAttribute) is not { } attribute)
                return;
            BlobReader reader = _md.GetBlobReader(attribute.Value);
            if (reader.ReadUInt16() == 1 && reader.ReadSerializedString() is { } template)
                result[name] = template;
        }
    }

    private BrowsableState? GetBrowsableState(CustomAttributeHandleCollection attributes)
    {
        if (FindAttribute(attributes, DebuggerBrowsableAttribute) is not { } attribute)
            return null;
        BlobReader reader = _md.GetBlobReader(attribute.Value);
        return reader.Length >= 6 && reader.ReadUInt16() == 1 ? (BrowsableState)reader.ReadInt32() : null;
    }

    /// <summary>How the members of a type want to be shown: name -> state, for members that say anything at all.</summary>
    public Dictionary<string, BrowsableState> GetBrowsableStates(int typeDefToken)
    {
        var result = new Dictionary<string, BrowsableState>();
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(handle);
            if (GetBrowsableState(field.GetCustomAttributes()) is { } state)
                result[_md.GetString(field.Name)] = state;
        }
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = _md.GetPropertyDefinition(handle);
            string name = _md.GetString(property.Name);
            if (GetBrowsableState(property.GetCustomAttributes()) is { } state)
            {
                result[name] = state;
            }
            else if (FindAttribute(property.GetCustomAttributes(), CompilerGeneratedAttribute) != null
                     || (!property.GetAccessors().Getter.IsNil
                         && FindAttribute(_md.GetMethodDefinition(property.GetAccessors().Getter).GetCustomAttributes(), CompilerGeneratedAttribute) != null
                         && !HasBackingField(type, name)))
            {
                // synthesized members such as a record's EqualityContract are noise
                result[name] = BrowsableState.Never;
            }
        }
        return result;
    }

    // Auto-properties have compiler generated accessors too, but those are what the user wrote.
    private bool HasBackingField(TypeDefinition type, string propertyName)
    {
        string backingField = $"<{propertyName}>k__BackingField";
        foreach (FieldDefinitionHandle handle in type.GetFields())
            if (_md.GetString(_md.GetFieldDefinition(handle).Name) == backingField)
                return true;
        return false;
    }

    /// <summary>Methods marked [DebuggerHidden], [DebuggerStepThrough] or [DebuggerNonUserCode] (directly or through their type).</summary>
    public List<int> GetNonUserCodeMethods(bool includeNonUserCode = true)
    {
        var result = new List<int>();
        string[] attributes = includeNonUserCode ? s_nonUserCodeAttributes
            : ["System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerStepThroughAttribute"];
        foreach (TypeDefinitionHandle typeHandle in _md.TypeDefinitions)
        {
            TypeDefinition type = _md.GetTypeDefinition(typeHandle);
            bool wholeType = FindAttribute(type.GetCustomAttributes(), attributes) != null;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (wholeType || FindAttribute(_md.GetMethodDefinition(methodHandle).GetCustomAttributes(), attributes) != null)
                    result.Add(MetadataTokens.GetToken(methodHandle));
            }
        }
        return result;
    }

    /// <summary>Property accessors and user defined operators: what "step filtering" skips.</summary>
    public bool IsPropertyOrOperator(int methodToken)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        if ((method.Attributes & MethodAttributes.SpecialName) == 0)
            return false;
        string name = _md.GetString(method.Name);
        return name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("op_", StringComparison.Ordinal);
    }

    public bool DeclaresMethod(int typeDefToken, string name, int parameterCount) =>
        GetMethods(typeDefToken, name).Any(m => !m.IsStatic && m.ParameterTypes.Length == parameterCount);

    // ---------------------------------------------------------------- documents

    public List<string> GetDocuments()
    {
        var result = new List<string>();
        if (_pdb == null)
            return result;
        foreach (DocumentHandle handle in _pdb.Documents)
            result.Add(_pdb.GetString(_pdb.GetDocument(handle).Name));
        return result;
    }

    // ---------------------------------------------------------------- IL

    private static readonly Dictionary<ushort, OperandType> s_operandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value), o => o.OperandType);

    /// <summary>IL offsets of the call instructions of a method (call, callvirt, calli).</summary>
    public List<int> GetCallSites(int methodToken)
    {
        var result = new List<int>();
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        if (method.RelativeVirtualAddress == 0)
            return result;

        byte[]? il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il == null)
            return result;

        int offset = 0;
        while (offset < il.Length)
        {
            int start = offset;
            ushort opcode = il[offset++];
            if (opcode == 0xFE && offset < il.Length)
                opcode = (ushort)(0xFE00 | il[offset++]);
            if (!s_operandTypes.TryGetValue(opcode, out OperandType operand))
                break; // unknown instruction: better no answer than a wrong one

            if (opcode is 0x28 or 0x29 or 0x6F)
                result.Add(start);

            if (operand == OperandType.InlineSwitch)
            {
                if (offset + 4 > il.Length)
                    break;
                offset += 4 + 4 * BitConverter.ToInt32(il, offset);
                continue;
            }
            offset += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                _ => 4,
            };
        }
        return result;
    }
}
