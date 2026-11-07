using ClrDebug;
using DncDbg.Engine.Launch;

internal static class ResumeHandleChecks
{
    public static int Run()
    {
        int checks = 0;
        IntPtr handle = new(0x1234);
        foreach (HRESULT resumeStatus in new[] { HRESULT.S_OK, HRESULT.E_FAIL })
            foreach (HRESULT closeStatus in new[] { HRESULT.S_OK, HRESULT.E_INVALIDARG })
            {
                var calls = new List<string>();
                Exception? error = null;
                try
                {
                    ProcessLauncher.ResumeProcessAndCloseHandle(handle, Resume, Close);
                }
                catch (DebugException caught)
                {
                    error = caught;
                }
                Check(calls.SequenceEqual(["resume", "close"]), "resume and close each run exactly once, in order");
                HRESULT expected = resumeStatus != HRESULT.S_OK ? resumeStatus : closeStatus;
                Check(expected == HRESULT.S_OK ? error == null : error?.HResult == (int)expected,
                    "the original resume HRESULT wins; otherwise the close failure is reported");

                HRESULT Resume(IntPtr value)
                {
                    Check(value == handle, "resume receives the owned handle");
                    calls.Add("resume");
                    return resumeStatus;
                }

                HRESULT Close(IntPtr value)
                {
                    Check(value == handle, "close receives the owned handle");
                    calls.Add("close");
                    return closeStatus;
                }
            }

        var original = new InvalidOperationException("resume delegate failed");
        int closes = 0;
        try
        {
            ProcessLauncher.ResumeProcessAndCloseHandle(handle, _ => throw original, value =>
            {
                Check(value == handle, "exception cleanup receives the owned handle");
                closes++;
                return HRESULT.S_OK;
            });
            throw new InvalidOperationException("Resume unexpectedly succeeded.");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, original))
        {
            Check(closes == 1, "an exception from resume still closes exactly once and is propagated unchanged");
        }
        return checks;

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Resume handle check failed: " + name);
            checks++;
        }
    }
}
