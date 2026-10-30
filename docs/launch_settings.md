# Launch settings profiles

Ordinary desktop `launch` requests can use `Properties/launchSettings.json`
under the request's `cwd`. With no `cwd`, discovery is relative to the debugger's
current directory. `launchSettingsFilePath` selects another file; relative file
paths are resolved against request `cwd`, or the debugger directory when absent.

`launchSettingsProfile` selects a profile by its exact, case-sensitive name.
When omitted, the first profile in file order whose `commandName` is exactly
`Project` is selected. Explicit `null` or `""` disables profile processing
entirely, including file lookup. A named profile must use `Project` or
`Executable`; both launch the selected managed assembly or executable through
the normal debugger launch path. Neither builds a project implicitly.

JSON comments are supported; trailing commas are not. Unknown profile fields
are ignored. Non-string environment values are ignored with a diagnostic.

| Profile field | Behavior |
| --- | --- |
| `executablePath` | Supplies `program` when the request's program is missing or empty. Relative paths use the directory containing the settings file. |
| `workingDirectory` | Supplies `cwd` when request `cwd` is missing or empty. Relative paths use the settings-file directory. |
| `commandLineArgs` | Supplies arguments when the request's parsed `args` list is empty. |
| `environmentVariables` | Adds string-valued target environment entries; request `env` wins per variable. |

Request `args` accepts either a string or an array of strings. A string and
profile `commandLineArgs` use the same splitter: ASCII whitespace separates
arguments, double quotes group text, and `\"` represents a literal quote.
Missing, null, empty-array, empty-string and whitespace-only request arguments
allow profile arguments. A nonempty array takes precedence. The splitter is
not a shell: it performs no variable expansion, globbing or command execution.
Existing process-launch restrictions still apply after splitting; empty target
arguments and Unix arguments containing spaces followed by a trailing
backslash are omitted. Literal-quote splitting has separate native/managed
parser coverage; this is not a new guarantee that every quote round-trips
through native dbgshim's command-line encoding.

Profile environment entries configure the target only. They do not change
debugger settings such as the console relay or inner-exception walk limit;
put those in request `env` or the debugger process environment.
For ordinary launches, `applicationUrl`, `launchBrowser`, `sslPort` and
`httpPort` are ignored.

## Errors and restart

Without an explicit file or profile, missing, unreadable, malformed or
unsuitable settings are skipped with diagnostics. Explicit selection reports
a launch error for these conditions, missing names and unsupported command
names. A missing target after profile resolution is an error. Bad request
types, including non-string array elements in `args`, are rejected.

A bare `restart` reuses the already resolved launch, even if the settings file
has since changed. `restart.arguments` containing an updated launch
configuration resolves its selected profile again.

## Example

```json
{
  "profiles": {
    "Tool": {
      "commandName": "Executable",
      "executablePath": "../bin/Debug/net10.0/Tool.dll",
      "commandLineArgs": "one \"two words\"",
      "workingDirectory": "..",
      "environmentVariables": { "TOOL_MODE": "development" }
    }
  }
}
```

```json
{
  "request": "launch",
  "launchSettingsFilePath": "/workspace/Properties/launchSettings.json",
  "launchSettingsProfile": "Tool",
  "env": { "TOOL_MODE": "test" }
}
```

Here both relative profile paths start at `/workspace/Properties`, and the
target receives `TOOL_MODE=test`.

## Existing managed project-launch extension

The managed adapter additionally accepts `project`, `build`, `configuration`
and `framework`. Project output discovery/build and ReadyToRun handling are
preserved. When `project` is supplied without `cwd`, default settings discovery
uses the project directory. This existing managed extension also maps the
profile's `applicationUrl` to `ASPNETCORE_URLS`, below profile environment
entries and request `env` in precedence. It is not part of upstream's ordinary
`program` launch contract. Without any supplied working directory, the managed
adapter retains its existing project/program-directory fallback.

Remote launch retains its separate built-assets contract and rejects desktop
profile, build and other incompatible launch options; profiles are not applied
to remote deployment or transport configuration.
