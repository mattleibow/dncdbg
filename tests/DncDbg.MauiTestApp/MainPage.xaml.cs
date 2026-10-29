using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace DncDbg.MauiTestApp;

public partial class MainPage : ContentPage
{
	int count = 0;
	readonly List<AssemblyLoadContext> reloadContexts = [];

	public MainPage()
	{
		InitializeComponent();
#if DEBUG
		RuntimeStatus.Text = TargetAgentStartup.Status;
		ReloadControls.IsVisible = true;
#else
		RuntimeStatus.Text = "Application build without target debugging assets.";
#endif
#if DNCDBG_TEST_FAULTS
		WatchdogScenario.IsVisible = true;
#endif
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
		numbers = Enumerable.Range(0, 150).ToArray();
		numbers[0] = input;
		numbers[1] = input + 41;
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

	private void OnDeepClicked(object? sender, EventArgs e)
	{
		ResultStatus.Text = $"Deep result: {DeepScenario(149)}";
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static int DeepScenario(int depth)
	{
		if (depth > 0) return DeepScenario(depth - 1);
		Array shifted = Array.CreateInstance(typeof(int), new[] { 150 }, new[] { 5 });
		shifted.SetValue(55, 5);
		GC.KeepAlive(shifted); // MOBILE_DEEP_BREAKPOINT: 150 recursive frames and nonzero array bounds.
		return (int)shifted.GetValue(5)!;
	}

	private void OnWatchdogClicked(object? sender, EventArgs e)
	{
#if DNCDBG_TEST_FAULTS
		TargetAgentStartup.StallOwner();
#endif
	}

	private void OnReloadLoadClicked(object? sender, EventArgs e)
	{
		using Stream assembly = typeof(MainPage).Assembly.GetManifestResourceStream("DncDbg.ReloadFixture.dll")!;
		var context = new AssemblyLoadContext("mobile-reload-" + Guid.NewGuid(), isCollectible: true);
		context.LoadFromStream(assembly);
		reloadContexts.Add(context);
		ReloadStatus.Text = $"Contexts: {reloadContexts.Count}";
	}

	private void OnReloadRunClicked(object? sender, EventArgs e)
	{
		int result = 0;
		foreach (var context in reloadContexts)
			result += (int)context.Assemblies.Single().GetType("ReloadScenario")!.GetMethod("Run")!.Invoke(null, [1])!;
		ReloadStatus.Text = $"Reload result: {result} from {reloadContexts.Count}";
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private WeakReference UnloadFirstContext()
	{
		var context = reloadContexts[0];
		reloadContexts.RemoveAt(0);
		var weak = new WeakReference(context);
		context.Unload();
		return weak;
	}

	private void OnReloadUnloadClicked(object? sender, EventArgs e)
	{
		WeakReference context = UnloadFirstContext();
		for (int i = 0; i < 50 && context.IsAlive; ++i)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
		ReloadStatus.Text = $"Contexts: {reloadContexts.Count}; collected: {!context.IsAlive}";
	}

	private sealed class ScenarioObject
	{
		public int Number;
		public string Message = "";
		public float SmallFloat = 1e-10f;
		public double SmallDouble = 1e-10;
		public double PreciseDouble = 1.23456789012345;
		public double NegativeZero = -0.0;
		public double NotANumber = double.NaN;
		public double PositiveInfinity = double.PositiveInfinity;
		public double NegativeInfinity = double.NegativeInfinity;
	}
}
