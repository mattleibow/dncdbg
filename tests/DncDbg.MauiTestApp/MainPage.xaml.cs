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
		int answer = input + 41;
		return answer; // MOBILE_BREAKPOINT: inspect input/answer, evaluate answer + 1, then continue.
	}
}
