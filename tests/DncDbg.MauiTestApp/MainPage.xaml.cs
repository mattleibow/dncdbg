using System.Runtime.CompilerServices;

namespace DncDbg.MauiTestApp;

public partial class MainPage : ContentPage
{
	int count = 0;

	public MainPage()
	{
		InitializeComponent();
		RuntimeStatus.Text = ExperimentalStartup.Status;
	}

	private void OnCounterClicked(object? sender, EventArgs e)
	{
		count++;

		int result = ManagedScenario(count);
		CounterStatus.Text = $"Count: {count}";
		ResultStatus.Text = $"Result: {result}";
	}

	private void OnResetClicked(object? sender, EventArgs e)
	{
		count = 0;
		CounterStatus.Text = "Count: 0";
		ResultStatus.Text = "Result: not run";
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static int ManagedScenario(int input)
	{
		int answer = PrepareScenario(input, out string message, out int[] numbers, out ScenarioObject sample);
		return FinishScenario(answer, message, numbers, sample); // MOBILE_BREAKPOINT: inspect input/answer, evaluate answer + 1, then continue.
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static int PrepareScenario(int input, out string message, out int[] numbers, out ScenarioObject sample)
	{
		message = "mobile \ud83d\ude80";
		numbers = [input, input + 41];
		sample = new ScenarioObject { Number = input + 41, Message = message };
		return input + 41;
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static int FinishScenario(int answer, string message, int[] numbers, ScenarioObject sample)
	{
		GC.KeepAlive(message);
		GC.KeepAlive(numbers);
		GC.KeepAlive(sample);
		return answer;
	}

	private void OnExceptionClicked(object? sender, EventArgs e)
	{
		try { ThrowScenario(); }
		catch (InvalidOperationException) { ResultStatus.Text = "Exception handled"; }
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static void ThrowScenario() => throw new InvalidOperationException("MOBILE_EXCEPTION");

	private sealed class ScenarioObject
	{
		public int Number;
		public string Message = "";
	}
}
