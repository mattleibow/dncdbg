using ClrDebug;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    public byte[] ReadMemory(ulong address, int count)
    {
        lock (_lock)
        {
            RequireStopped();
            if (count is < 0 or > 16 * 1024 * 1024)
                throw new DebuggerException("Memory count must be between 0 and 16777216.");
            return count == 0 ? [] : RequireProcess().ReadMemory(new CORDB_ADDRESS(address), count);
        }
    }

    public unsafe int WriteMemory(ulong address, byte[] data)
    {
        lock (_lock)
        {
            RequireStopped();
            fixed (byte* buffer = data)
                return RequireProcess().WriteMemory(new CORDB_ADDRESS(address), data.Length, (nint)buffer);
        }
    }
}
