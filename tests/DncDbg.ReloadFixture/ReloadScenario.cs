using System.Runtime.CompilerServices;

public static class ReloadScenario
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int Run(int input)
    {
        int answer = input + 41;
        return answer; // MOBILE_RELOAD_BREAKPOINT: the same PE loaded in independent collectible contexts.
    }
}
