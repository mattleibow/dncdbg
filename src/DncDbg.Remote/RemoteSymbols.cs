using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace DncDbg.Remote;

/// <summary>Host portable-PDB catalog. Only tokens, MVIDs and source-checked sequence points cross the wire.</summary>
public sealed class RemoteSymbols
{
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    public RemoteModuleSymbols[] Modules { get; }
    public RemoteSymbols(string assetsPath)
    {
        Modules = Directory.EnumerateFiles(assetsPath, "*.dll").Order(StringComparer.Ordinal)
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb"))).Take(257).Select(Read).ToArray();
        if (Modules.Length is 0 or > 256)
            throw new InvalidDataException("mobile.assetsPath must contain 1–256 matching PE/portable-PDB pairs.");
    }

    public RemoteBreakpoints Bind(string source, int[] lines)
    {
        string full = Path.GetFullPath(source);
        var locations = new List<RemoteManagedLocation>();
        foreach (int line in lines)
        {
            var matches = Modules.SelectMany(module => module.Methods.SelectMany(method =>
                method.Points.Where(point => Path.GetFullPath(point.Source) == full && point.Line == line)
                    .Select(point => (module, method, point)))).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"No unique portable-PDB sequence point for {full}:{line}.");
            var (module, method, point) = matches[0];
            byte[] content = File.ReadAllBytes(full);
            byte[] hash = point.Algorithm switch
            {
                "SHA256" => SHA256.HashData(content),
                "SHA1" => SHA1.HashData(content),
                _ => throw new InvalidDataException("Unsupported portable-PDB source checksum."),
            };
            if (Convert.ToHexString(hash) != point.Checksum)
                throw new InvalidDataException($"The source checksum does not match the target portable PDB: {full}");
            locations.Add(new(module.Mvid, method.Token, point.Offset, method.Name,
                method.Variables.Where(v => point.Offset >= v.StartOffset && point.Offset < v.EndOffset).ToArray()));
        }
        return new(full, lines, Locations: locations.ToArray());
    }

    public IEnumerable<RemoteConfiguration> ConfigurationChunks()
    {
        bool append = false;
        foreach (RemoteModuleSymbols module in Modules)
        {
            foreach (RemoteTypeSymbols[] types in module.Types.Chunk(64))
            {
                yield return new([module with { Types = types, Methods = [] }], append);
                append = true;
            }
            foreach (RemoteMethodSymbols method in module.Methods)
            {
                var fragments = method.Points.Length == 0 ? new[] { Array.Empty<RemoteSequencePoint>() } : method.Points.Chunk(32);
                foreach (RemoteSequencePoint[] points in fragments)
                {
                    yield return new([module with { Types = [], Methods = [method with { Points = points }] }], append);
                    append = true;
                }
            }
        }
    }

    private static RemoteModuleSymbols Read(string path)
    {
        using FileStream assembly = File.OpenRead(path), pdbFile = File.OpenRead(Path.ChangeExtension(path, ".pdb"));
        using var pe = new PEReader(assembly);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbFile);
        MetadataReader metadata = pe.GetMetadataReader(), pdb = provider.GetMetadataReader();
        DebugDirectoryEntry entry = pe.ReadDebugDirectory().Single(x => x.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData cv = pe.ReadCodeViewDebugDirectoryData(entry);
        var id = pdb.DebugMetadataHeader!.Id;
        if (id.Length != 20 || cv.Age != 1 || cv.Guid != new Guid(id.AsSpan(0, 16)) ||
            entry.Stamp != BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(16, 4)))
            throw new InvalidDataException($"Portable PDB identity mismatch for {path}.");
        var methods = new List<RemoteMethodSymbols>();
        var types = new List<RemoteTypeSymbols>();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            types.Add(new(MetadataTokens.GetToken(typeHandle), typeName,
                type.GetFields().Where(h => (metadata.GetFieldDefinition(h).Attributes & FieldAttributes.Static) == 0)
                    .Select(h => new RemoteFieldSymbols(MetadataTokens.GetToken(h), metadata.GetString(metadata.GetFieldDefinition(h).Name))).ToArray()));
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                MethodDebugInformation info = pdb.GetMethodDebugInformation(methodHandle);
                var points = new List<RemoteSequencePoint>();
                foreach (SequencePoint point in info.GetSequencePoints().Where(p => !p.IsHidden))
                {
                    Document document = pdb.GetDocument(point.Document.IsNil ? info.Document : point.Document);
                    Guid algorithm = pdb.GetGuid(document.HashAlgorithm);
                    points.Add(new(point.Offset, pdb.GetString(document.Name), point.StartLine,
                        Convert.ToHexString(pdb.GetBlobBytes(document.Hash)), algorithm == Sha256 ? "SHA256" : algorithm == Sha1 ? "SHA1" : "unsupported"));
                }
                var variables = new List<RemoteManagedVariable>();
                bool instance = (method.Attributes & MethodAttributes.Static) == 0;
                if (instance) variables.Add(new("this", 0, true));
                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    Parameter parameter = metadata.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber > 0)
                        variables.Add(new(metadata.GetString(parameter.Name), parameter.SequenceNumber - 1 + (instance ? 1 : 0), true));
                }
                foreach (LocalScopeHandle scopeHandle in pdb.GetLocalScopes(methodHandle))
                {
                    LocalScope scope = pdb.GetLocalScope(scopeHandle);
                    foreach (LocalVariableHandle localHandle in scope.GetLocalVariables())
                    {
                        LocalVariable local = pdb.GetLocalVariable(localHandle);
                        if ((local.Attributes & LocalVariableAttributes.DebuggerHidden) == 0)
                            variables.Add(new(pdb.GetString(local.Name), local.Index, false, scope.StartOffset, scope.EndOffset));
                    }
                }
                methods.Add(new(MetadataTokens.GetToken(methodHandle), typeName + "." + metadata.GetString(method.Name),
                    points.ToArray(), variables.ToArray()));
            }
        }
        return new(Convert.ToHexString(metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToByteArray()),
            Path.GetFileNameWithoutExtension(path), methods.ToArray(), types.ToArray());
    }
}
