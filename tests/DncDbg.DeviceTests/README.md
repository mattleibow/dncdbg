# Generic Linux device proof

```sh
dotnet run --project tests/DncDbg.DeviceTests -c Release -- --require-docker \
  --output build/validation/device-UNIQUE
```

Docker must be running; absence is a failure, never a skip. The Linux daemon's
native x64/arm64 architecture selects the matching explicit target bundle.
The adapter remains outside the container. A real separate Linux target is
deployed/started/observed/stopped through the public generic command and SSH
providers, not the Android provider or a fake backend.

The proof reuses the native-host debugger assertions for breakpoints, actual
inspection, safe evaluation, steps, exceptions, pause, restart, stale handles,
detach and exact cleanup. It also runs an ordinary console `Main`, attached
natural exit codes 0/7, route death while running/stopped, target death, terminate
and reconnect. The SSH route uses real strict host-key-pinned authentication,
SFTP deployment and built-in encrypted `ssh -W`. Generated private client/host
keys stay local and are excluded from uploaded evidence.

The SDK base image is digest-pinned; runtime archives are SHA-512 pinned and
native JSON headers SHA-256 pinned. The bundle records every deployed file hash
and validates its trusted inventory before starting. Logs record build/package
provenance; a changed required private ABI image fails closed.

Each run owns a uniquely labelled image/container and independent workspace.
The controller/SSH account is unprivileged and maps to the host runner UID, so
owner-only diagnostic directories remain readable without relaxing permissions.
Guest PIDs are never signalled on the host. Cleanup verifies Docker labels and
adapter command identity before acting; unrelated resources are preserved.
The product target worker removes credentials and deployments on actual target
exit, including after detach. Emergency removal of leftover credentials fails
the gate rather than masking an ownership leak.

Recover an interrupted run with the same output directory:

```sh
dotnet run --project tests/DncDbg.DeviceTests -c Release --no-build -- \
  --output build/validation/device-UNIQUE --cleanup
```

Required hosted Linux x64 CI is in `remote.yml`; local Linux arm64 evidence uses
the same entrypoint. This proves a representative external device, not physical
Android/Apple hardware. See [vendor provisioning](../../docs/remote-devices.md).
