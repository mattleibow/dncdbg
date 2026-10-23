namespace DncDbg.Engine;

public sealed record BreakpointLocationInfo(int Line, int Column, int EndLine, int EndColumn);

public sealed partial class DebugEngine
{
    public IReadOnlyList<BreakpointLocationInfo> GetBreakpointLocations(string path, int line, int column, int endLine, int endColumn)
    {
        lock (_lock)
            return _modules.Values.Where(m => m.Metadata != null)
                .SelectMany(m => m.Metadata!.GetBreakpointLocations(ToPdbPath(path), line, column, endLine, endColumn))
                .Select(p => new BreakpointLocationInfo(p.Line, p.Column, p.EndLine, p.EndColumn))
                .Distinct().OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();
    }
}
