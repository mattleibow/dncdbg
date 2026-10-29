using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

FileStream? retained = null;
TcpClient? control = null;
try
{
    if (args is [_, "--detach-io", var port])
    {
        retained = File.Create(args[0] + ".retained");
        retained.Write("before\n"u8);
        retained.Flush(flushToDisk: true);
        control = new TcpClient();
        control.Connect(IPAddress.Loopback, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture));
        using var hello = new StreamWriter(control.GetStream(), new UTF8Encoding(false), leaveOpen: true);
        hello.WriteLine(JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, RetainedLength = retained.Length,
            RetainedHandle = retained.SafeFileHandle.DangerousGetHandle().ToInt64(),
        }));
        hello.Flush();
    }

    int result = Scenario(1);
    if (control != null)
    {
        using var release = new StreamReader(control.GetStream(), Encoding.UTF8, leaveOpen: true);
        if (release.ReadLine() != "released") throw new IOException("Debugger ownership teardown was not acknowledged.");
        retained!.Write("after\n"u8);
        retained.Flush(flushToDisk: true);
        retained.Dispose();
        retained = null;
    }
    string staging = args[0] + ".pending";
    using (FileStream output = File.Create(staging))
    {
        output.Write(Encoding.UTF8.GetBytes(result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        output.Flush(flushToDisk: true);
    }
    File.Move(staging, args[0], overwrite: true);
}
finally
{
    retained?.Dispose();
    control?.Dispose();
}

[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
static int Scenario(int input)
{
    int answer = input + 41;
    return answer; // REMOTE_BREAKPOINT
}
