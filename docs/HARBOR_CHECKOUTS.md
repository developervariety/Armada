# Harbor-owned checkouts

A Harbor mission route can select `checkoutMode: "HarborOwned"`. The default is
`ProcessOnly`: the Admiral owns the repository and docks, and Harbor runs the
agent. Existing routes keep that behavior.

In owned mode, the selected Harbor owns the repository, agent checkout, file
operations, Git commands, verification and landing checkout. The Admiral stores
a versioned dock binding and sends typed operations through the authenticated
link. It does not open the remote path or run a local replacement. Disabling
Harbor, losing the runner, or changing its enrollment leaves a named hold.

## Configure both ends

First configure and enroll the [headless runner](HARBOR_RUNNER.md). On its host,
prepare a bare repository and an existing primary working checkout. The bare
repository's `origin` must match the vessel's repository URL. The primary
checkout must have the same origin. Create a separate checkout parent directory.
All configured paths must be absolute and must contain no symbolic links or
reparse points. Repository setup remains an administrator action; the RPC has
no arbitrary clone, executable, command-line or working-directory field.

Add an explicit grant to the private runner configuration:

```json
{
  "repositoryGrants": [{
    "id": "grant_example",
    "vesselId": "vsl_example",
    "tenantId": "tenant_example",
    "userId": "user_example",
    "repositoryPath": "/work/repository.git",
    "checkoutRoot": "/work/checkouts",
    "workingDirectory": "/work/primary",
    "repositoryUrl": "https://example.invalid/team/repository.git",
    "verificationCommands": {
      "check-build": "dotnet build",
      "check-unit-test": "dotnet test",
      "dod-build": "dotnet build",
      "dod-unit-test": "dotnet test"
    }
  }]
}
```

Keep the grant's owner equal to the enrolled runner owner and vessel owner.
The mission owner must also match. A captain route takes precedence over a
vessel route. On the Admiral, select the mode explicitly:

```json
{
  "harbor": {
    "enabled": true,
    "captainMcpBaseUrl": "https://admiral.example/mcp",
    "missionRoutes": [{
      "vesselId": "vsl_example",
      "runnerId": "hbr_example",
      "checkoutMode": "HarborOwned",
      "repositoryGrantId": "grant_example"
    }]
  }
}
```

`captainMcpBaseUrl` must reach the Admiral's authenticated MCP endpoint from the
runner. It accepts HTTPS, or HTTP only for loopback development. With runtime
MCP configuration enabled, a missing endpoint or unresolved mission credential
holds launch. Config files reference the mission token by environment variable;
they do not contain its value. The private launch envelope supplies that scoped
token to the child. Provider login remains local to Harbor. The Admiral retains
final replies from authenticated output, without sending its local log path.

## Identity and recovery

Provisioning saves one stable attempt before it creates a checkout. Its binding
pins the runner, enrollment generation, owner, repository fingerprint, checkout
ID, branch and full base commit. A retry uses that saved identity. It does not
resolve a moving branch again. Malformed stored bindings cannot become local
docks. The runner checks its durable checkout record and linked Git metadata.

Writes require a stable operation key and expected full HEAD. Replay returns the
saved result; a changed payload with the same key is refused. An interrupted
mutation remains uncertain. Live or uncertain job writers block mutations,
verification and removal. Read-only inspection can remain available. Console
and Admiral restart use the same [fenced job adoption](HARBOR_PROTOCOL.md),
without starting a replacement writer.

Reclamation saves a lifecycle intent before it snapshots or removes a checkout.
It retains the source dock identity while queued landing needs it. Later cleanup
uses the saved receipt and compare-and-swap ref deletion. An unavailable owner
cannot trigger local cleanup or an unverified branch deletion.

## Verification and landing

Verification selects a fixed command key from the grant. Its exact command hash
must match the Admiral's configured workflow command. Check keys use
`check-build`, `check-lint`, `check-unit-test` and the corresponding Check type.
Definition-of-done keys are `dod-build` and `dod-unit-test`; the merge queue uses
`merge-queue-test`. Profile reruns use `check-rerun-unit-test`,
`check-rerun-integration-test` or `dod-rerun-unit-test`. Commands, provider login
and host environment remain administrator-owned. Remote requests cannot supply
raw command text or arbitrary environment variables.

Reserve verification output space with `supervisor.maxVerificationSpoolBytes`.
The default is zero and refuses verification with
`workspace_verification_storage_not_configured`. Job reservations plus this
reserve cannot exceed one GiB. For example, 48 retained jobs at 16 MiB each leave
256 MiB for verification. All three limits are pinned to a state directory;
use a new private state directory after accounting for old history to change
limits. Do not delete retained output to make room without first archiving and
verifying it.

Output previews are bounded. Authenticated chunk reads verify the retained full
stdout and stderr lengths and hashes. Truncation and uncertain process stop
remain explicit. Existing Check and definition-of-done result rules still apply.

Landing uses a separate owned integration checkout, the fork's candidate checks,
target drift handling and compare-and-swap publication. The grant's primary
`workingDirectory` is the landing target; the agent checkout is a separate path.
Public mission diffs read the bound source checkout. Unsupported required sibling
roots, consumer verification or workflow environment transfer return named
holds; they cannot be omitted to produce a passing result. Admiral-local branch
maintenance also refuses owned repositories until an explicit checkout-scoped
operation is available.
