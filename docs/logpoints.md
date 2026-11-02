# Logpoints

Source logpoints send output to the Debug Console without stopping execution.
Set `logMessage` on a source breakpoint; expressions inside `{...}` are evaluated
when it is hit. Conditions and parsed hit-count filters still gate the message.

These case-sensitive special tokens are expanded only inside expression parts:

| Token | Value |
| --- | --- |
| `$FILEPOS` | Current source file path and breakpoint line, as a string |
| `$ADDRESS` | Current breakpoint native address, as a `0x`-prefixed string |
| `$TID` | Current CLR thread ID, equivalent to `$tid` |
| `$PID` | Current target process ID, equivalent to `$pid` |
| `$HITCOUNT` | This breakpoint's unsigned hit count after condition filtering and increment |

For example:

```text
At {$FILEPOS,nq}, process {$PID}, thread {$TID}, hit {$HITCOUNT}
```

String-valued token expressions use quoted, escaped display text by default;
`,nq` removes the quotes. Existing [format specifiers](evaluation_format_specifiers.md)
can be used. Tokens also work in compound expressions such as
`{$HITCOUNT + 10}` and `{$PID == $pid}`.

Expansion is ordinal and left-to-right, with no word-boundary requirement:
`{$HITCOUNT$HITCOUNT}` becomes the adjacent decimal digits (`11` on the first
hit). Substituted values are not scanned again. Outside braces, `$PID` and the
other tokens remain literal text. Malformed expressions preserve the existing
logpoint error rendering rather than stopping or silently dropping the message.

File paths and address strings are inserted using C# verbatim literal syntax.
The managed evaluator supports `@"..."` with doubled quotes and literal
backslashes; the evaluated value also drops optional `u8`/`U8` suffixes,
matching the tested native debugger contract. If native-address inspection
fails, an engine diagnostic is logged and the native-compatible zero address
is displayed; no address is guessed.

Hit counts retain the existing source-breakpoint replacement semantics and
reset on restart. This is managed source-logpoint functionality; the separate
remote built-assets contract still rejects conditions, hit counts and logpoints.
