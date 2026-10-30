using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using DncDbg.Remote;
using DncDbg.Testing;

internal static class MobileBreakpointProof
{
    public static async Task RunAsync(string descriptor, string source, string assembly, string pdbPath,
        string fixtureDescriptor, bool disconnect, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        int line = File.ReadAllLines(source).Select((text, index) => (text, index))
            .Single(x => x.text.Contains("// MOBILE_BREAKPOINT", StringComparison.Ordinal)).index + 1;
        RemoteManagedLocation location = ReadLocation(source, line, assembly, pdbPath);
        RemoteEndpoint endpoint = JsonSerializer.Deserialize(File.ReadAllText(descriptor), RemoteJson.Default.RemoteEndpoint)!;
        await using RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(endpoint, token);
        Require(client.Capabilities.ManagedDebugging && client.Capabilities.Bootstrap == PrivateBootstrapPolicy.Bootstrap &&
            client.Capabilities.SupportedBuild is { } supported &&
            PrivateBootstrapPolicy.Validate(supported, ["DllMain", "CoreCLRCreateCordbObject3"]) == null, "exact pinned private capability");
        Console.WriteLine(JsonSerializer.Serialize(client.Capabilities, RemoteJson.Default.RemoteCapabilities));
        FixtureEndpoint fixture = JsonSerializer.Deserialize(File.ReadAllText(fixtureDescriptor), FixtureJson.Default.FixtureEndpoint)!;
        Require(fixture.SessionId == endpoint.SessionId && fixture.ProcessId == endpoint.ProcessId,
            "fixture control belongs to the exact debugger process/generation");
        Require(!fixture.BrokerRequired, "independent debugger fixture has no registered DevFlow transport");
        var control = new FixtureControlClient(fixture);
        await client.SetBreakpointsAsync(new(source, [line], location), token);
        Require(await control.CommandAsync("reset", token) == new FixtureState(0, "Count: 0", "Result: not run"),
            "app-local deterministic reset needs no UI automation broker");
        await client.StartAsync(token);
        Task<RemoteStop> waiting = client.WaitForStopAsync(token);
        Task<FixtureState> run = control.CommandAsync("run", token);
        RemoteStop stop = await waiting;
        Require(stop.Reason == "breakpoint", "real target-native managed breakpoint callback");
        RemoteFrame[] stack = await client.StackAsync(stop.Id, token);
        Require(stack.Length > 0 && stack[0].Name == location.Method && stack[0].Source == source &&
            stack[0].Line == line, "exact MOBILE_BREAKPOINT stack/source/line");
        string input = (await client.EvaluateAsync(stack[0].Handle, "input", token)).Value;
        string answer = (await client.EvaluateAsync(stack[0].Handle, "answer", token)).Value;
        string expression = (await client.EvaluateAsync(stack[0].Handle, "answer + 1", token)).Value;
        Require(input == "1" && answer == "42" && expression == "43", "target ICorDebug argument/local/int32 expression: 1/42/43");
        Console.WriteLine(JsonSerializer.Serialize(new { Stop = stop, Stack = stack, Input = input, Answer = answer, Expression = expression }));
        if (disconnect)
            await client.DisposeAsync();
        else
        {
            await client.ReleaseAsync([stack[0].Handle], token);
            await Invalid(() => client.EvaluateAsync(stack[0].Handle, "answer", token));
            await client.ContinueAsync(stop.Id, token);
            await Invalid(() => client.ContinueAsync(stop.Id, token));
            await Invalid(() => client.EvaluateAsync(stack[^1].Handle, "answer", token));
            await client.DisposeAsync();
        }
        Require(await run.WaitAsync(token) == new FixtureState(1, "Count: 1", "Result: 42"), "continued fixture state is exactly 1/42");
        Require(await control.CommandAsync("reset", token) == new FixtureState(0, "Count: 0", "Result: not run"),
            "detached app-local reset remains usable");
        Require(await control.CommandAsync("run", token) == new FixtureState(1, "Count: 1", "Result: 42"),
            "detached scenario still executes normally without the debugger or broker");
        Console.WriteLine(disconnect ? "PASS: mobile disconnect relinquishes the callback stop; scenario completed normally"
            : "PASS: mobile continue completed normally; duplicate continue and stale handles rejected");
        async Task Invalid(Func<Task> action)
        {
            try { await action(); }
            catch (RemoteRequestException error) when (error.Code == "InvalidState") { return; }
            throw new InvalidOperationException("Expected stale native stop/frame rejection.");
        }
    }

    private static RemoteManagedLocation ReadLocation(string source, int line, string assemblyPath, string pdbPath)
    {
        using FileStream assembly = File.OpenRead(assemblyPath), pdbFile = File.OpenRead(pdbPath);
        using var pe = new PEReader(assembly);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbFile);
        MetadataReader metadata = pe.GetMetadataReader(), pdb = provider.GetMetadataReader();
        DebugDirectoryEntry codeView = pe.ReadDebugDirectory().Single(x => x.Type == DebugDirectoryEntryType.CodeView);
        Require(pe.ReadCodeViewDebugDirectoryData(codeView).Guid ==
            new Guid(pdb.DebugMetadataHeader!.Id.AsSpan(0, 16)), "portable PDB matches exact target assembly");
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name) != "ManagedScenario") continue;
                MethodDebugInformation info = pdb.GetMethodDebugInformation(methodHandle);
                SequencePoint point = info.GetSequencePoints().Single(p => p.StartLine == line && !p.IsHidden);
                Document document = pdb.GetDocument(point.Document.IsNil ? info.Document : point.Document);
                Require(Path.GetFullPath(pdb.GetString(document.Name)) == source &&
                    pdb.GetBlobBytes(document.Hash).SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),
                    "portable PDB exact source/line/checksum");
                var variables = new List<RemoteManagedVariable>();
                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    Parameter parameter = metadata.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber > 0)
                        variables.Add(new(metadata.GetString(parameter.Name), parameter.SequenceNumber - 1, true));
                }
                foreach (LocalScopeHandle scopeHandle in pdb.GetLocalScopes(methodHandle))
                {
                    LocalScope scope = pdb.GetLocalScope(scopeHandle);
                    if (point.Offset < scope.StartOffset || point.Offset >= scope.EndOffset) continue;
                    foreach (LocalVariableHandle localHandle in scope.GetLocalVariables())
                    {
                        LocalVariable local = pdb.GetLocalVariable(localHandle);
                        variables.Add(new(pdb.GetString(local.Name), local.Index, false));
                    }
                }
                return new(Convert.ToHexString(metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToByteArray()),
                    MetadataTokens.GetToken(methodHandle), point.Offset,
                    metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) + "." + metadata.GetString(method.Name),
                    variables.ToArray());
            }
        }
        throw new InvalidOperationException("No matching portable-PDB mobile scenario.");
    }
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
