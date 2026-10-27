# Exception conditions

The `all`, `user-unhandled`, and `unhandled` filters accept exception type
conditions in DAP `setExceptionBreakpoints` requests. Conditions are separated
by commas or ASCII whitespace.

Names match case-sensitively against the full exception type name, not its base
types. A trailing `*` marks a prefix match: `System.InvalidOperation*` also
matches `System.InvalidOperationException`. A lone `*` matches every type.
Only the final `*` is a wildcard; any earlier `*`, and `?`, are literal
characters. These are prefix conditions, not general glob patterns or regular
expressions. For example, `System.*Exception` and `System.**` do not match
`System.InvalidOperationException`.

Prefix matching also applies to exclusions. Each token beginning with `!`
excludes that token's type or prefix. Matching exclusions override all positive
and unconditional entries for the same filter, including entries in different
`filterOptions`, regardless of their order. Negation does not enable other
types. A lone `!` is ignored; an empty condition, including one containing only
ignored tokens, covers every type.

| Condition | Result for `all` |
| --- | --- |
| `System.NullReferenceException` | Break only on that exact type. |
| `System.NullReferenceException, System.InvalidOperationException` | Break on either type. |
| `*, !System.NullReferenceException` | Break on every type except the excluded type. |
| `System.*, !System.NullReferenceException` | Break on the `System.` prefix except the excluded type. |
| `System.*, !System.Null*` | Break on the `System.` prefix except types starting with `System.Null`. |
| `!*` | Ignore every type for this filter. |
| `System.*, !*` | Ignore every type; the exclusion takes priority. |
| `!System.Null*` | Exclude that prefix without enabling any other type. |

The `filters` and `filterOptions` arrays are additive. For example, an `all`
entry in `filters` plus a positive condition in `filterOptions` still covers
every type. A matching exclusion for `all` still wins over that unconditional
entry.

## Unhandled exceptions

Unhandled exceptions stop by default, even when the `unhandled` filter is
omitted or its positive conditions do not match. Only a matching exclusion on
`unhandled` suppresses this final stop. Exclusions on `all` or `user-unhandled`
do not suppress it.

For example, `all` with `*` and `unhandled` with
`!System.InvalidOperation*` stops when an invalid-operation exception is
thrown, but does not stop again if that exception is unhandled. To suppress
both stops, apply the exclusion to both filters. `!*` on `unhandled` suppresses
every final unhandled stop; it does not prevent the target process from
terminating because of an uncaught exception.

## Mapping VsDbg conditions

[VS Code exception conditions](https://code.visualstudio.com/docs/csharp/debugging#_exception-conditions)
use different negation rules: a leading `!` makes the entire VsDbg list an
exclusion list. DNCDbg negates each token separately. To preserve an exclusion
list's meaning for `all`, add `*` and negate every excluded type.

| VsDbg condition | DNCDbg condition |
| --- | --- |
| `System.NullReferenceException` | `System.NullReferenceException` |
| `System.NullReferenceException, System.InvalidOperationException` | `System.NullReferenceException, System.InvalidOperationException` |
| `!System.Threading.Tasks.TaskCanceledException` | `*, !System.Threading.Tasks.TaskCanceledException` |
| `!System.Threading.Tasks.TaskCanceledException, System.NotImplementedException` | `*, !System.Threading.Tasks.TaskCanceledException, !System.NotImplementedException` |
