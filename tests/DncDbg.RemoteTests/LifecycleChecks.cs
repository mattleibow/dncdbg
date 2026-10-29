using DncDbg.Dap;
using DncDbg.Remote;

internal static class LifecycleChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        var failures = new List<Exception>();
        await Case("completed detach upgrades to terminating cleanup", async (owner, target, tunnel) =>
        {
            await owner.CloseAsync(false);
            await Task.WhenAll(owner.CloseAsync(true), owner.CloseAsync(true), owner.CloseAsync(false));
            check(target.Detaches == 1 && target.Stops == 1 && tunnel.Disposals == 1,
                "late terminate upgrades detached target once without redisposing the transport");
        });
        await Case("terminate is never downgraded", async (owner, target, tunnel) =>
        {
            await owner.CloseAsync(true);
            await Task.WhenAll(owner.CloseAsync(false), owner.CloseAsync(true));
            check(target.Detaches == 0 && target.Stops == 1 && tunnel.Disposals == 1,
                "terminating close is monotonic and exact-once");
        });
        await Case("terminate racing detach settlement", async (owner, target, tunnel) =>
        {
            target.DetachGate = new();
            Task detach = owner.CloseAsync(false);
            await target.DetachStarted.Task.WaitAsync(token);
            Task terminate = owner.CloseAsync(true);
            target.DetachGate.SetResult();
            await Task.WhenAll(detach, terminate).WaitAsync(token);
            check(target.Detaches == 1 && target.Stops == 1 && tunnel.Disposals == 1,
                "terminate queued during detach settlement forces cleanup exactly once");
        });
        await Case("observer failure forces owned cleanup", async (owner, target, tunnel) =>
        {
            target.Observe = _ => Task.FromException<int?>(new IOException("owned observer failed"));
            await Reject(owner.CompleteExitAsync(0, token), typeof(IOException));
            check(target.Stops == 1 && tunnel.Disposals == 1,
                "natural-exit observation failure upgrades the earlier nonterminating close");
        });
        await Case("incorrect exit notification fails closed", async (owner, target, tunnel) =>
        {
            target.Observe = _ => Task.FromResult<int?>(7);
            await Reject(owner.CompleteExitAsync(0, token), typeof(InvalidDataException));
            check(target.Stops == 1 && tunnel.Disposals == 1,
                "reported and observed exit-code disagreement cannot publish a successful exit");
        });
        await Case("early exit with hung observer and cancellation", async (owner, target, tunnel) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            target.Observe = async observationToken => await new TaskCompletionSource<int?>().Task.WaitAsync(observationToken);
            Task settling = owner.CompleteExitAsync(0, cancel.Token);
            await target.ObservationStarted.Task.WaitAsync(token);
            cancel.Cancel();
            await Reject(settling, typeof(OperationCanceledException));
            check(target.Stops == 1 && tunnel.Disposals == 1,
                "canceled hung natural-exit settlement still forces independent owned cleanup");
        });
        await Case("terminate cancels concurrent exit observation", async (owner, target, tunnel) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            target.Observe = async observationToken => await new TaskCompletionSource<int?>().Task.WaitAsync(observationToken);
            Task settling = owner.CompleteExitAsync(0, cancel.Token);
            try
            {
                await target.ObservationStarted.Task.WaitAsync(token);
                await owner.CloseAsync(true).WaitAsync(TimeSpan.FromSeconds(2), token);
                await Reject(settling.WaitAsync(TimeSpan.FromSeconds(2), token), typeof(OperationCanceledException));
                check(target.Stops == 1 && tunnel.Disposals == 1,
                    "terminate interrupts settlement independently without duplicate target stop/dispose");
            }
            finally { cancel.Cancel(); try { await settling; } catch (OperationCanceledException) { } }
        });
        await Case("acquisition cancellation still permits escalation", async (owner, target, tunnel) =>
        {
            target.BlockAcquisition = true;
            Task connect = owner.ConnectAsync(token);
            await target.AcquisitionStarted.Task.WaitAsync(token);
            await owner.CloseAsync(false);
            await Reject(connect, typeof(OperationCanceledException));
            await owner.CloseAsync(true);
            check(target.Detaches == 1 && target.Stops == 1 && tunnel.Disposals == 1,
                "canceled partial acquisition retains upgradeable cleanup ownership");
        });
        await Case("failed detach still permits terminate", async (owner, target, tunnel) =>
        {
            target.FailDetach = true;
            await Reject(owner.CloseAsync(false), typeof(AggregateException));
            try { await owner.CloseAsync(true); }
            catch (AggregateException) { }
            check(target.Stops == 1 && tunnel.Disposals == 1,
                "detach cleanup error is retained but cannot suppress later owned stop");
        });
        await Case("successful observed completion remains nonterminating", async (owner, target, tunnel) =>
        {
            target.Observe = _ => Task.FromResult<int?>(7);
            int? code = await owner.CompleteExitAsync(7, token);
            check(code == 7 && target.Detaches == 1 && target.Stops == 0 && tunnel.Disposals == 1,
                "actual natural completion preserves its exact exit code without killing the target");
        });
        if (failures.Count != 0) throw new AggregateException("Lifecycle regressions failed.", failures);

        async Task Case(string name, Func<RemoteLifecycle, Target, Tunnel, Task> body)
        {
            var target = new Target();
            var tunnel = new Tunnel();
            var owner = new RemoteLifecycle(target, tunnel, 1,
                Path.Combine(Environment.CurrentDirectory, "build/validation/lifecycle-contracts"));
            try { await body(owner, target, tunnel); }
            catch (Exception error) { Console.Error.WriteLine($"FAIL: {name}: {error.Message}"); failures.Add(error); }
        }

        static async Task Reject(Task operation, Type type)
        {
            try { await operation; }
            catch (Exception error) when (type.IsAssignableFrom(error.GetType())) { return; }
            throw new InvalidOperationException("Expected " + type.Name + ".");
        }
    }

    private sealed class Target : IRemoteTargetProvider
    {
        public string Name => "lifecycle-fixture";
        public string ExpectedPlatform => "LinuxNativeHost";
        public string ExpectedArchitecture => "X64";
        public string? ArtifactsPath => null;
        public int Detaches, Stops;
        public bool BlockAcquisition, FailDetach;
        public TaskCompletionSource AcquisitionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObservationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DetachStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DetachGate { get; set; }
        public Func<CancellationToken, Task<int?>> Observe { get; set; } = _ => Task.FromResult<int?>(0);
        public async Task AcquireAsync(string session, CancellationToken token)
        {
            AcquisitionStarted.SetResult();
            if (BlockAcquisition) await new TaskCompletionSource().Task.WaitAsync(token);
        }
        public Task<string?> ReadDescriptorAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public async Task CloseAsync(bool terminate, CancellationToken token)
        {
            if (terminate) { Interlocked.Increment(ref Stops); return; }
            Interlocked.Increment(ref Detaches);
            DetachStarted.TrySetResult();
            if (DetachGate != null) await DetachGate.Task.WaitAsync(token);
            if (FailDetach) throw new IOException("detach failed");
        }
        public Task<int?> WaitForExitAsync(CancellationToken token)
        {
            ObservationStarted.TrySetResult();
            return Observe(token);
        }
    }

    private sealed class Tunnel : ITunnelProvider
    {
        public int Disposals;
        public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token) => Task.FromResult(target);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
