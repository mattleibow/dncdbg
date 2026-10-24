using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace DncDbg.Dap.Native;

/// <summary>A single-reader process pipe whose readiness wait can be stopped before disposing the stream.</summary>
internal sealed class CancellablePipeInput(Stream input, CancellationToken cancellation) : Stream
{
    private readonly SafeHandle _handle = input switch
    {
        FileStream file => file.SafeFileHandle,
        PipeStream pipe => pipe.SafePipeHandle,
        _ => throw new NotSupportedException("Native adapter input requires a process pipe handle."),
    };

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
            return 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                if (!PeekNamedPipe(_handle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is 109 or 232)
                        return 0;
                    throw new IOException("Cannot inspect native adapter pipe readiness.", new Win32Exception(error));
                }
                if (available > 0)
                    return input.Read(buffer, offset, (int)Math.Min(count, available));
                cancellation.WaitHandle.WaitOne(20);
            }
            else
            {
                var descriptor = new PollDescriptor { File = _handle.DangerousGetHandle().ToInt32(), Events = 1 };
                int ready = Poll(ref descriptor, 1, 50);
                if (ready > 0)
                    return input.Read(buffer, offset, count);
                if (ready < 0 && Marshal.GetLastPInvokeError() != 4)
                    throw new IOException("Cannot wait for native adapter pipe readiness.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            input.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int File;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle pipe, IntPtr buffer, uint size, IntPtr read,
        out uint available, IntPtr remaining);
}
