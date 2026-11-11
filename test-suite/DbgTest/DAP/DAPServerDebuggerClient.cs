using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DbgTestCore.DAP
{
public class DAPServerDebuggerClient : DAPLocalDebuggerClient
{
    public DAPServerDebuggerClient(string address, int port)
        : this(ConnectAsync(address, port).GetAwaiter().GetResult())
    {
    }

    DAPServerDebuggerClient(TcpClient client)
        : base(new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true),
               new StreamReader(client.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
    {
        m_tcpClient = client;
    }

    public override void Close()
    {
        try
        {
            base.Close();
        }
        finally
        {
            m_tcpClient.Dispose();
        }
    }

    static async Task<TcpClient> ConnectAsync(string address, int port)
    {
        using var timeout = new CancellationTokenSource(5000);
        try
        {
            while (true)
            {
                var client = new TcpClient { NoDelay = true };
                try
                {
                    await client.ConnectAsync(address, port, timeout.Token);
                    return client;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    client.Dispose();
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The debugger did not listen on {address}:{port} within 5000 ms.", ex);
        }
    }

    readonly TcpClient m_tcpClient;
}
}
