using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace DncDbg.Engine.Symbols;

internal sealed record ScopeImport(ImportDefinitionKind Kind, string? Alias, string Target);

internal sealed partial class ModuleMetadata
{
    public IReadOnlyList<ScopeImport> GetImports(int methodToken, int offset)
    {
        if (_pdb == null)
            return [];
        var result = new List<ScopeImport>();
        var visited = new HashSet<ImportScopeHandle>();
        foreach (LocalScopeHandle local in _pdb.GetLocalScopes((MethodDefinitionHandle)MetadataTokens.Handle(methodToken)))
        {
            LocalScope localScope = _pdb.GetLocalScope(local);
            if (offset < localScope.StartOffset || offset >= localScope.EndOffset)
                continue;
            for (ImportScopeHandle handle = localScope.ImportScope; !handle.IsNil && visited.Add(handle);)
            {
                ImportScope scope = _pdb.GetImportScope(handle);
                foreach (ImportDefinition import in scope.GetImports())
                {
                    if (import.Kind is ImportDefinitionKind.ImportNamespace or ImportDefinitionKind.AliasNamespace
                        or ImportDefinitionKind.ImportAssemblyNamespace or ImportDefinitionKind.AliasAssemblyNamespace)
                        result.Add(new ScopeImport(import.Kind, import.Kind is ImportDefinitionKind.AliasNamespace or ImportDefinitionKind.AliasAssemblyNamespace
                            ? Read(import.Alias) : null, Read(import.TargetNamespace)));
                    else if (import.Kind is ImportDefinitionKind.ImportType or ImportDefinitionKind.AliasType)
                    {
                        string? target = import.TargetType.Kind == HandleKind.TypeSpecification
                            ? _md.GetTypeSpecification((TypeSpecificationHandle)import.TargetType).DecodeSignature(SignatureNames.Instance, this)
                            : GetTypeHandleName(import.TargetType);
                        if (target != null)
                            result.Add(new ScopeImport(import.Kind, import.Kind == ImportDefinitionKind.AliasType ? Read(import.Alias) : null, target));
                    }
                }
                handle = scope.Parent;
            }
        }
        return result;

        string Read(BlobHandle handle) => Encoding.UTF8.GetString(_pdb.GetBlobBytes(handle));
    }

    public bool IsHoistedLocalInScope(int methodToken, int offset, int index)
    {
        if (_pdb == null)
            return true;
        foreach (CustomDebugInformationHandle handle in _pdb.GetCustomDebugInformation(MetadataTokens.EntityHandle(methodToken)))
        {
            CustomDebugInformation info = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(info.Kind) != new Guid("6da9a61e-f8c7-4874-be62-68bc5630df71"))
                continue;
            BlobReader reader = _pdb.GetBlobReader(info.Value);
            if (index < 0 || index >= reader.Length / 8)
                return true;
            reader.Offset = index * 8;
            uint start = reader.ReadUInt32(), length = reader.ReadUInt32();
            return offset >= start && (uint)offset - start < length;
        }
        return true;
    }

    public string? GetLocalType(int methodToken, int offset, string name)
    {
        var local = GetLocals(methodToken, offset).Where(l => l.Name == name).Select(l => (int?)l.Index).FirstOrDefault();
        if (local == null)
            return null;
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        if (method.RelativeVirtualAddress == 0)
            return null;
        StandaloneSignatureHandle signature = _pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature;
        if (signature.IsNil)
            return null;
        var types = _md.GetStandaloneSignature(signature).DecodeLocalSignature(SignatureNames.Instance, this);
        return local.Value < types.Length ? types[local.Value] : null;
    }
}
