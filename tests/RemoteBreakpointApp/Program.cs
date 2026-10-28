using System.Runtime.CompilerServices;

int result = Scenario(1);
using (FileStream output = File.Create(args[0]))
{
    // Existence does not mean the result is committed; exercise the reader's exit synchronization.
    Thread.Sleep(150);
    output.Write(System.Text.Encoding.UTF8.GetBytes(result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
static int Scenario(int input)
{
    int answer = input + 41;
    return answer; // REMOTE_BREAKPOINT
}
