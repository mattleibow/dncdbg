# Debugging in Docker and WSL

Run **both `dncdbg-dap` and the managed target in the same Linux environment**.
The host IDE is only a DAP client. Its transport starts the Linux adapter over
stdio using `docker exec -i` or `wsl.exe --exec`; no debugger network port is
necessary.

**A Windows or macOS adapter cannot attach across the OS boundary to a Linux
process in Docker/WSL.** Host PIDs, container PIDs, runtime libraries and native
debugging APIs are not interchangeable. `sourceFileMap` only maps source paths;
it does not make cross-OS attach possible. A sidecar adapter in another container,
cross-architecture emulation, and attaching from the Docker VM host are not
covered by this integration.

## Docker prerequisites

- A reachable Docker CLI/daemon running **Linux containers**, native x64 or
  arm64. Use `docker info`, not the host machine architecture, to choose the RID.
  Windows containers and other architectures are not covered.
- The SDK selected by [`global.json`](../global.json) for publishing. Use a
  glibc-based Linux runtime image, such as the .NET 10 Ubuntu image below.
  Alpine/musl is not covered.
- Matching adapter, target, runtime and native debugging-library architectures.
  Keep the entire adapter publish directory, including native assets and
  licenses; copying only the apphost is insufficient.
- Permission to use Docker and to debug inside the container. Docker access is
  security-sensitive. For this isolated development fixture we add `SYS_PTRACE`
  and disable the container's seccomp profile. Do not use `--privileged`, disable
  host ptrace policy, or apply these settings to production containers.

On macOS, start your existing Colima VM (or Docker Desktop) first:

```sh
colima start
docker info --format '{{.OSType}}/{{.Architecture}}'
```

The test never starts/stops a daemon or changes Docker contexts.

## Automated end-to-end test

From the repository root:

```sh
dotnet run --project tests/DncDbg.DockerTests -- --require-docker
```

The runner queries the daemon architecture and publishes the adapter and the
unoptimized portable-PDB fixture for `linux-x64` or `linux-arm64`. It builds a
unique image from [`Dockerfile`](../tests/DncDbg.DockerTests/Dockerfile), starts
an isolated container with no network, then starts the adapter using
`docker exec -i`. A real DAP session verifies:

1. Deferred launch and configuration.
2. A source-breakpoint stop at the exact fixture line.
3. `sourceFileMap` translation from a deterministic PDB prefix to a
   container-local source path, with `sourceReference: 0`.
4. Source retrieval from the container, without a host bind mount.
5. The local `answer` (`int`, `42`) and evaluation of `answer + 1` (`43`).
6. Continue, stdout delivery, a zero exit code, termination and adapter shutdown.

No sleeps or timing-based breakpoint synchronization are used. Commands and the
DAP session have deadlines. The runner removes its own container, tagged image
and `build/docker-tests/<unique-id>` publish context in `finally`, including on
assertion failures. Cleanup failures fail the test. Shared runtime base images
and Docker build caches are deliberately retained; there is no daemon-wide
prune. Hard termination of the runner cannot execute `finally`; Linux CI has
an additional bounded, run-label-scoped `always()` cleanup step.

Without `--require-docker`, missing/unreachable Docker or an unsupported daemon
platform prints `SKIP` and exits zero. Once preflight succeeds, any publish,
image, protocol, assertion or cleanup failure fails the test. CI always uses
`--require-docker`: lack of Docker is a failure, never a silently skipped gate.
The test needs NuGet and Microsoft Container Registry access during setup;
the debug session itself has no network access. The base image tracks the
serviced `10.0-noble` tag.

## Manual Docker setup

This uses the same small fixture so the paths below are reproducible. Replace
the fixture publish/source copy with your application for real debugging.
Run from the repository root in a POSIX shell:

```sh
case "$(docker info --format '{{.OSType}}/{{.Architecture}}')" in
  linux/x86_64|linux/amd64) rid=linux-x64; platform=linux/amd64 ;;
  linux/aarch64|linux/arm64) rid=linux-arm64; platform=linux/arm64 ;;
  *) echo "A native Linux x64/arm64 Docker daemon is required" >&2; exit 1 ;;
esac
dotnet publish src/DncDbg.Dap -c Debug -r "$rid" --self-contained false \
  -o build/docker-demo/adapter
dotnet publish tests/DncDbg.DockerTests/fixture/DockerTarget.csproj \
  -c Debug -r "$rid" --self-contained false -o build/docker-demo/app \
  "-p:PathMap=$PWD/tests/DncDbg.DockerTests/fixture=/_/dncdbg-docker"
cp tests/DncDbg.DockerTests/fixture/Program.cs build/docker-demo/Program.cs
docker build --platform "$platform" -f tests/DncDbg.DockerTests/Dockerfile \
  -t dncdbg-demo build/docker-demo
docker run --rm --detach --name dncdbg-demo --platform "$platform" \
  --network none --cap-add SYS_PTRACE --security-opt seccomp=unconfined \
  dncdbg-demo sleep infinity
```

Configure your DAP client's **adapter executable** as `docker`, with these
arguments (an executable descriptor, not DNCDbg launch arguments):

```json
{
  "command": "docker",
  "args": ["exec", "-i", "dncdbg-demo", "/opt/dncdbg/dncdbg-dap"]
}
```

The equivalent process command is:

```sh
docker exec -i dncdbg-demo /opt/dncdbg/dncdbg-dap
```

Keep stdin open with `-i`. **Do not add `-t`**: a pseudo-TTY can corrupt DAP
framing and mix diagnostics with stdout. Do not use a shell startup banner or
write non-DAP output to the adapter's stdout. DNCDbg does not ship an IDE
extension; adapter registration/transport configuration belongs to your client,
not the adapter. Existing IDE-specific `pipeTransport` support is client-owned.

After DAP `initialize`, send a launch request with Linux paths:

```json
{
  "program": "/workspace/app/DockerTarget.dll",
  "cwd": "/workspace/app",
  "stopAtEntry": false,
  "sourceFileMap": {
    "/_/dncdbg-docker": "/workspace/source"
  }
}
```

Set a source breakpoint using `/workspace/source/Program.cs`, then send
`configurationDone`. A real target can instead be attached using `processId`
from **inside the container**; run the adapter there under an identity allowed
to debug that process, and do not disable .NET diagnostics for the target.
The automated Docker test proves **launch**, not attach.

### Source paths and host editors

The map direction is **PDB document prefix → adapter-side filesystem prefix**.
Here the fixture PDB contains `/_/dncdbg-docker/Program.cs`, while the source
exists at `/workspace/source/Program.cs`. Breakpoint paths map back to PDB paths;
stack frames and source requests map to the Linux source tree.

`program`, `cwd` and source paths are resolved by the Linux adapter, not by the
host. A Windows `C:\...` path is not a Linux path. For host editor navigation,
use your client's remote/container workspace or its source-path translation
support, or make the source available at a shared path in both environments.
The container-local path returned by this test does not by itself prove a host
IDE will open the correct file. Do not confuse client-side editor path mapping
with adapter-side `sourceFileMap`.

Remove the manual fixture when finished:

```sh
docker container rm --force dncdbg-demo
docker image rm dncdbg-demo
rm -rf build/docker-demo
```

## WSL

Use the Linux .NET SDK/runtime **inside the chosen distribution**. From the
repository root in WSL, publish with its matching Linux RID:

```sh
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) echo "Unsupported architecture" >&2; exit 1 ;;
esac
dotnet publish src/DncDbg.Dap -c Release -r "$rid" --self-contained false \
  -o "bin/$rid"
```

Keep the adapter, target, symbols and source in that distribution.
A VS Code Remote WSL window or another WSL-side DAP client can launch
`/home/you/dncdbg/bin/linux-x64/dncdbg-dap` directly.

For a Windows-host DAP client, the adapter executable can instead be `wsl.exe`
with arguments such as:

```json
{
  "command": "wsl.exe",
  "args": [
    "--distribution", "Ubuntu", "--exec",
    "/home/you/dncdbg/bin/linux-x64/dncdbg-dap"
  ]
}
```

Use Linux paths in launch/attach requests, Linux PIDs obtained in that same
distribution, and an adapter-side `sourceFileMap` if the PDB's build paths
differ. Apply the same host-editor path considerations as Docker. Do not launch
the Windows `dncdbg-dap.exe` and pass it a WSL PID or a `\\wsl$` program path:
that is unsupported cross-OS debugging, not a transport.

The stdio WSL recipe is documented but is not exercised by the Docker/Linux CI
test. Distribution security policies, WSL versions and individual IDE transports
still need validation in your environment.
