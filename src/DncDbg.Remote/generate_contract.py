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
           "inline void ValidateRequest(int operation, const Json& body) {",
           '    if (!body.is_object()) throw std::runtime_error("MalformedPayload");', "    switch (operation) {"]
for _, number, request, _ in schema["operations"]:
    native += [f"        case {number}:"]
    for field in schema["records"][request]:
        if " = " in field:
            continue
        kind, name = field.split()
        if kind.endswith("?"):
            continue
        check = "is_array" if kind.endswith("[]") else {
            "string": "is_string", "int": "is_number_integer", "long": "is_number_integer",
            "bool": "is_boolean"}.get(kind, "is_object")
        native += [f'            if (!body.at("{name}").{check}()) throw std::runtime_error("MalformedPayload");']
    native += ["            break;"]
native += ["        default: break;", "    }", "}", "}", ""]
outputs = {root / "RemoteContract.g.cs": "\n".join(cs),
           root.parent / "DncDbg.Remote.Target/RemoteContract.g.h": "\n".join(native)}
for path, text in outputs.items():
    if args.check:
        if not path.exists() or path.read_text() != text:
            raise SystemExit(f"Generated contract is stale: {path.name}")
    else:
        path.write_text(text)
