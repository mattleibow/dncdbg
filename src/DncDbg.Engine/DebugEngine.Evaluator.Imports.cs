using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.CSharp;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private sealed partial class Evaluator
    {
        private IReadOnlyList<Symbols.ScopeImport>? _imports;
        private IReadOnlyList<Symbols.ScopeImport> Imports => _imports ??= engine.GetILFrame(frame) is { } il
            ? engine.GetMetadata(il.Function.Module)?.GetImports((int)il.Function.Token.Value, il.IP.pnOffset) ?? [] : [];

        private IEnumerable<TypeOperand> ImportedTypes() => Imports.Where(i => i.Kind == ImportDefinitionKind.ImportType)
            .Select(i => ResolveType(SyntaxFactory.ParseTypeName(Symbols.ModuleMetadata.DisplayType(i.Target))));

        private string ExpandNamespaceAlias(string name)
        {
            foreach (var import in Imports.Where(i => i.Alias != null && i.Kind != ImportDefinitionKind.AliasType))
                if (name.StartsWith(import.Alias + ".", StringComparison.Ordinal))
                    return import.Target + name[import.Alias!.Length..];
            return name;
        }

        private string? DeclaredLocalType(string name)
        {
            if (engine.GetILFrame(frame) is not { } il || engine.GetMetadata(il.Function.Module) is not { } metadata)
                return null;
            return metadata.GetLocalType((int)il.Function.Token.Value, il.IP.pnOffset, name);
        }
    }
}
