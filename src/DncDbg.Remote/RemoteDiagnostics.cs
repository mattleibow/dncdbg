namespace DncDbg.Remote;

public static class RemoteDiagnostics
{
    public static Action<string> Safe(Action<string>? log) => message =>
    {
        try { log?.Invoke(message); }
        catch (Exception error)
        {
            try { Console.Error.WriteLine("Remote diagnostic writer failed: " + error.GetType().Name); }
            catch (Exception) { } // Neither failed diagnostic sink may change ownership or the primary failure.
        }
    };
}
