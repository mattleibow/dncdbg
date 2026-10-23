using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DncDbg.Engine.Symbols;

internal sealed partial class ModuleMetadata
{
    public IReadOnlyList<(int Type, int Method, int Offset)> GetStaticConstructorReturns()
    {
        var result = new List<(int, int, int)>();
        foreach (TypeDefinitionHandle typeHandle in _md.TypeDefinitions)
            foreach (MethodDefinitionHandle methodHandle in _md.GetTypeDefinition(typeHandle).GetMethods())
            {
                MethodDefinition method = _md.GetMethodDefinition(methodHandle);
                if (_md.GetString(method.Name) != ".cctor" || method.RelativeVirtualAddress == 0)
                    continue;
                byte[]? il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is { Length: > 0 } && il[^1] == 0x2a)
                    result.Add((MetadataTokens.GetToken(typeHandle), MetadataTokens.GetToken(methodHandle), il.Length - 1));
            }
        return result;
    }
}
