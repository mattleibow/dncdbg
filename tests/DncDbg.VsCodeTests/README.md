# VS Code integration tests

These tests use Microsoft's official
[`@vscode/test-cli`](https://www.npmjs.com/package/@vscode/test-cli) and
[`@vscode/test-electron`](https://www.npmjs.com/package/@vscode/test-electron)
packages to launch an isolated VS Code Extension Development Host.

The test extension contributes a `dncdbg` debug type and registers:

- a `DebugAdapterDescriptorFactory` that launches `dncdbg-dap`;
- a `DebugAdapterTrackerFactory` that observes DAP messages;
- C# breakpoint support for the fixture workspace.

The source-navigation test builds and debugs `fixture/ConsoleApp.csproj`, stops
at a source breakpoint, and verifies all of the following:

- the top stack frame has `sourceReference: 0` for the local source;
- the frame path resolves to the fixture's real `Program.cs`;
- the active VS Code editor uses a `file:` URI;
- VS Code did not open a duplicate virtual `Program.cs` document.

## Run locally

Publish the adapter from the repository root:

```sh
dotnet publish src/DncDbg.Dap -c Release -o bin
npm ci --prefix tests/DncDbg.VsCodeTests
npm test --prefix tests/DncDbg.VsCodeTests
```

The first run downloads VS Code Insiders into the ignored `.vscode-test`
directory. Set these environment variables to override the defaults:

| Variable | Purpose |
|---|---|
| `DNCDBG_DAP_PATH` | Absolute path to the `dncdbg-dap` apphost |
| `DNCDBG_VSCODE_VERSION` | VS Code version used by the test runner; defaults to `insiders` |
| `DNCDBG_VSCODE_TEST_TEMP` | Short directory for VS Code user data and extensions |

On Linux CI, run the test under `xvfb-run` because VS Code Desktop requires a
display server.
