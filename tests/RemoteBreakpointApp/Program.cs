using System.Runtime.CompilerServices;

int result = Scenario(1);
File.WriteAllText(args[0], result.ToString(System.Globalization.CultureInfo.InvariantCulture));

[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
static int Scenario(int input)
{
    int answer = input + 41;
    return answer; // REMOTE_BREAKPOINT
}
