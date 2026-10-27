using System;

internal static class Program
{
    private static int Main()
    {
        int answer = 42;
        Console.WriteLine($"docker-answer={answer}"); // BREAKPOINT
        return answer == 42 ? 0 : 1;
    }
}
