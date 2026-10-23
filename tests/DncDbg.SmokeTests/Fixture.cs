partial class Program
{
    public static int WaitDuringEval()
    {
        System.Diagnostics.Debugger.Log(0, "", "evaluation-entered");
        for (int i = 0; i < 100; i++)
            Thread.Sleep(100);
        return 1;
    }

    internal static class DeferredInitializer
    {
        private static readonly int Value;

        static DeferredInitializer()
        {
            Value = 42;
        }

        public static int Read() => Value;
    }
}
