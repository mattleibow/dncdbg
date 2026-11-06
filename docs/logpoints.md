# Logpoints

Source logpoints send output to the Debug Console without stopping execution.
Set `logMessage` on a source breakpoint; expressions inside `{...}` are evaluated
when it is hit. Conditions and parsed hit-count filters still gate the message.

These case-sensitive special tokens are expanded only inside expression parts:

| Token | Value |
| --- | --- |
| `$FILEPOS` | Current source file path and breakpoint line, as a string |
| `$FUNCTION` | Current method name, including its declaring type and parameters, as a string |
| `$ADDRESS` | Current breakpoint native address, as a `0x`-prefixed string |
| `$TID` | Current CLR thread ID, equivalent to `$tid` |
| `$PID` | Current target process ID, equivalent to `$pid` |
| `$TNAME` | Current thread name, as a string |
| `$CALLER` | Immediate caller's method name, including its declaring type and parameters, as a string |
| `$CALLSTACK` | Newline-separated physical call stack, starting with the current frame, as a string |
| `$HITCOUNT` | This breakpoint's unsigned hit count after condition filtering and increment |

For example:

```text
At {$FILEPOS,nq}, process {$PID}, thread {$TID}, hit {$HITCOUNT}
```

String-valued expressions use quoted, escaped display text by default;
`,nq` removes the quotes but retains escaped newlines, quotes and backslashes.
This applies equally to tokens, string literals, locals and compound expressions.
Literal text outside expression braces is not string-value escaped.
Existing [format specifiers](evaluation_format_specifiers.md)
can be used. Tokens also work in compound expressions such as
`{$HITCOUNT + 10}` and `{$PID == $pid}`.

Expansion is ordinal and left-to-right, with no word-boundary requirement:
`{$HITCOUNT$HITCOUNT}` becomes the adjacent decimal digits (`11` on the first
hit). Substituted values are not scanned again. Outside braces, `$PID` and the
other tokens remain literal text. Malformed expressions preserve the existing
logpoint error rendering rather than stopping or silently dropping the message.

Method, caller, stack and thread names are inspected only when their tokens occur in an
expression, once per hit. Method names reuse the user-facing metadata display,
including concrete generic arguments and the original async method rather than
its generated `MoveNext`. If method metadata is unavailable, the debugger logs
a diagnostic and displays `[Unnamed managed method in optimized code]`.
Caller names use the immediate CLR caller frame, not a filtered user-code or
logical async stack walk. A caller in an async state machine uses its original
method name; a missing or unresolvable caller frame displays `[Unknown Frame]`.
An existing caller without usable method metadata uses the unnamed-method
placeholder above.
Call stacks use physical CLR frames with the native stack display's async kickoff
and builder-frame filtering. Just My Code collapses consecutive non-user frames
into one `[External Code]` entry. With Just My Code disabled, physical framework
frames remain visible. Logical async continuations and `[Async Call Stack]`
labels from the debugger's extended stack view are not appended. An unavailable
or empty stack logs a diagnostic and displays `[Unknown Stack]`.
After an exception's first pass finds no catch, foreign frames from a
cross-thread rethrow precede the physical stack with an `[Exception] ` prefix.
They are not added while an exception has a catch handler.

`DNCDBG_STACKTRACE_LIMIT` bounds CALLSTACK to 250 eligible physical/foreign frames
by default, before Just My Code collapse. Async and internal-frame filtering
happens first. An additional frame beyond the limit adds a final
`[Truncated Stack Trace]`; an exact-limit stack has no marker. Zero displays
only that marker for a nonempty stack. The setting accepts a complete unsigned
32-bit decimal value. Invalid values emit a diagnostic and use 250. Each
launch/attach/restart resets to the cached debugger-process value before
applying request environment overrides. This setting applies to CALLSTACK;
the extended managed DAP stack view is unchanged.
Thread names are read without calling a target property getter. An unnamed
launch main thread is `Main Thread`; other unnamed or unknown threads use
`<No name>`. Explicit thread names and subsequent renames are preserved.
Tokens embedded in a substituted thread name remain literal, not expanded.
Logpoint string formatting does not change ordinary DAP evaluation or
`DebuggerDisplay` formatting.

File paths and address strings are inserted using C# verbatim literal syntax.
The managed evaluator supports `@"..."` with doubled quotes and literal
backslashes; the evaluated value also drops optional `u8`/`U8` suffixes,
matching the tested native debugger contract. If native-address inspection
fails, an engine diagnostic is logged and the native-compatible zero address
is displayed; no address is guessed.

Hit counts retain the existing source-breakpoint replacement semantics and
reset on restart. This is managed source-logpoint functionality; the separate
remote built-assets contract still rejects conditions, hit counts and logpoints.
