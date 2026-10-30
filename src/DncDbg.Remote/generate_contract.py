#!/usr/bin/env python3
"""Generate closed typed wire models, clients and native request guards. --check is CI-safe."""
import argparse
import json
from pathlib import Path

root = Path(__file__).resolve().parent
schema = json.loads((root / "contract.json").read_text())
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--check", action="store_true")
args = parser.parse_args()
cs = ["// Generated from contract.json by generate_contract.py. Do not edit.", "#nullable enable",
      "using System.Text.Json.Serialization;", "namespace DncDbg.Remote;", "",
      "public enum RemoteOperation", "{"]
cs += [f"    {name} = {number}," for name, number, _, _ in schema["operations"]]
cs += ["}", "", f"public static class RemoteContract {{ public const int ModelVersion = {schema['modelVersion']}; }}", ""]
for name, fields in schema["records"].items():
    cs.append(f"public sealed record {name}({', '.join(fields)});" if fields else f"public sealed record {name};")
cs += ["", "[JsonSerializable(typeof(RemoteEnvelope))]", "[JsonSerializable(typeof(PrivateBootstrapIdentity))]"]
for name in schema["records"]:
    cs += [f"[JsonSerializable(typeof({name}))]", f"[JsonSerializable(typeof({name}[]))]"]
cs += ["public partial class RemoteJson : JsonSerializerContext;", ""]
cs += ["public interface IRemoteDebugModel : IAsyncDisposable", "{", "    RemoteCapabilities Capabilities { get; }"]
for name, _, request, response in schema["operations"]:
    cs += [f"    Task<{response}> Request{name}Async({request} request, CancellationToken token);"]
cs += ["}", "", "public sealed partial class RemoteDebugClient : IRemoteDebugModel", "{"]
for name, _, request, response in schema["operations"]:
    cs += [f"    public Task<{response}> Request{name}Async({request} request, CancellationToken token) =>",
           f"        CallAsync<{request}, {response}>(RemoteOperation.{name}, request, token);"]
for name, _, request, response in schema["operations"][9:]:
    cs += [f"    public Task<{response}> {name}Async({request} request, CancellationToken token) =>",
           f"        Request{name}Async(request, token);"]
cs += ["}", ""]
native = ["// Generated from contract.json by generate_contract.py. Do not edit.",
          "#pragma once", '#include "Wire.h"', "namespace dncdbg {", "enum class Operation {"]
native += [f"    {name} = {number}," for name, number, _, _ in schema["operations"]]
native += ["};", f"constexpr int ModelVersion = {schema['modelVersion']};",
           "inline bool ContractInteger(const Json& value, int64_t minimum, int64_t maximum) {",
           "    if (!value.is_number_integer()) return false;",
           "    if (value.is_number_unsigned()) { uint64_t number = value.get<uint64_t>();",
           "        return number <= static_cast<uint64_t>(maximum) && (minimum <= 0 || number >= static_cast<uint64_t>(minimum)); }",
           "    int64_t number = value.get<int64_t>(); return number >= minimum && number <= maximum;",
           "}",
           "inline void ValidateEnvelope(const Json& body) {",
           '    if (!body.is_object() || !ContractInteger(body.at("Version"), 0, 2147483647LL) ||',
           '        !ContractInteger(body.at("Operation"), 0, 2147483647LL) ||',
           '        !ContractInteger(body.at("Sequence"), 1, 9223372036854775807LL)) throw std::runtime_error("MalformedPayload");',
           "}"]
native += [f"inline void Validate_{name}(const Json& body);" for name in schema["records"]]


def native_check(kind, value, indent):
    kind = kind.removesuffix("?")
    if kind.endswith("[]"):
        return [indent + f'if (!{value}.is_array()) throw std::runtime_error("MalformedPayload");',
                indent + f"for (const auto& element : {value}) {{",
                *native_check(kind[:-2], "element", indent + "    "), indent + "}"]
    if kind in schema["records"]:
        return [indent + f"Validate_{kind}({value});"]
    predicate = {
        "int": f"ContractInteger({value}, -2147483648LL, 2147483647LL)",
        "long": f"ContractInteger({value}, (-9223372036854775807LL - 1), 9223372036854775807LL)",
        "string": f"{value}.is_string()", "bool": f"{value}.is_boolean()",
    }.get(kind, f"{value}.is_object()")
    return [indent + f'if (!({predicate})) throw std::runtime_error("MalformedPayload");']


for name, fields in schema["records"].items():
    native += [f"inline void Validate_{name}(const Json& body) {{",
               '    if (!body.is_object()) throw std::runtime_error("MalformedPayload");']
    for field in fields:
        kind, name = field.split(" = ")[0].split()
        optional = " = " in field or kind.endswith("?")
        nullable = kind.endswith("?")
        value = f'body.at("{name}")'
        if optional:
            condition = f'body.contains("{name}")' + (f" && !{value}.is_null()" if nullable else "")
            native += [f"    if ({condition}) {{", *native_check(kind, value, "        "), "    }"]
        else:
            native += native_check(kind, value, "    ")
    native += ["}"]
native += ["inline void ValidateRequest(int operation, const Json& body) {", "    switch (operation) {"]
for _, number, request, _ in schema["operations"]:
    native += [f"        case {number}: Validate_{request}(body); break;"]
native += ["        default: break;", "    }", "}", "}", ""]
outputs = {root / "RemoteContract.g.cs": "\n".join(cs),
           root.parent / "DncDbg.Remote.Target/RemoteContract.g.h": "\n".join(native)}
for path, text in outputs.items():
    if args.check:
        if not path.exists() or path.read_text() != text:
            raise SystemExit(f"Generated contract is stale: {path.name}")
    else:
        path.write_text(text)
