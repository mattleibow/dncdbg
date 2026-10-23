partial class Program
{
    public static int WaitDuringEval()
    {
        System.Diagnostics.Debugger.Log(0, "", "evaluation-entered");
        for (int i = 0; i < 100; i++)
            Thread.Sleep(100);
        return 1;
    }
}
