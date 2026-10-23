namespace DncDbg.Protocol;

public sealed class BreakpointLocationsArguments
{
    public Source Source { get; set; } = new();
    public int Line { get; set; }
    public int? Column { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
}

public sealed record BreakpointLocation(int Line, int Column, int EndLine, int EndColumn);
public sealed record BreakpointLocationsResponseBody(BreakpointLocation[] Breakpoints);
