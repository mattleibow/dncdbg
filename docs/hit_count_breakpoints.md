# Hit count breakpoints

Related to [SetBreakpoints](dap_status.md#setbreakpointsrequest-setbreakpoints)
and [SetFunctionBreakpoints](dap_status.md#setfunctionbreakpointsrequest-setfunctionbreakpoints),
which use `hitCondition` on [SourceBreakpoint](dap_status.md#sourcebreakpoint)
and [FunctionBreakpoint](dap_status.md#functionbreakpoint).

Source and function breakpoints accept a `hitCondition` string in their DAP
requests. Conditions are parsed when the request is received, not evaluated as
C# expressions in the target.

| Syntax | Stop when the count is |
| --- | --- |
| `<N` | Less than N |
| `<=N` | Less than or equal to N |
| `==N` | Equal to N |
| `>N` | Greater than N |
| `N` or `>=N` | Greater than or equal to N |
| `%N` | An exact multiple of N |

Examples: `>5` stops after the first five hits, `<3` stops on the first two,
`7` means `>=7`, and `%2` stops on every other counted hit.

N is an unsigned decimal integer from 0 through 4294967295. Zero is accepted
except for `%0`, which is invalid. The first counted hit is 1, so `==0` and
`<0` never stop, while `0` and `>0` stop at every counted hit.

ASCII spaces are removed before parsing, including between operator characters
and digits: ` > = 2 ` means `>=2`, and `1 0` means `10`. An empty string or
spaces alone means no hit condition. Tabs, newlines, non-ASCII whitespace,
signs, suffixes, overflow, lone `=`, and C# expressions such as `1+3` or
`GetLimit()` are rejected. Invalid input fails the whole breakpoint request
before replacing existing breakpoints or saved restart requests. It is not
removed later with an evaluation diagnostic.

The regular `condition` expression is checked first. A false condition does
not increment the hit count. A true condition increments the count, then the
hit condition decides whether to stop or emit a logpoint message. A source
logpoint that passes its hit condition emits output without stopping.

Resending or changing a breakpoint at the same source path/line/column, or
the same function request name, preserves its ID and count. Changing the
numeric threshold does not reset it. Removing the breakpoint and adding it
again starts a new count. Counts use unsigned 32-bit arithmetic.
Breakpoint requests can also be changed after target exit for the next restart;
they do not synchronize or bind against the dead process. A rejected request
does not replace the configuration saved for restart.

This replaces the historical native `hitCount > expression` behavior. Bare
`5` now stops on hit 5 and subsequent hits, not only hits after 5. Conditions
are no longer a route to calling target methods or evaluating arithmetic.
Existing expression-based hit-condition tests were replaced by request-time
rejection coverage and the upstream source/function operator fixture; normal
breakpoint `condition` expressions remain supported.
