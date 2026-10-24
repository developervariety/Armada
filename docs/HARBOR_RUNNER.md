# Headless Harbor runner

`Armada.Harbor` connects to the Admiral as an enrolled runner. It runs the resolved
agent command on its own host and returns process identity, ordered output and
the observed exit code through the authenticated Harbor link. It uses no GUI.

The runner supports process-only mission routes. Dock creation, Git operations,
checks and landing remain on the Admiral for those routes. It advertises only
its configured runtime commands. It does not advertise checkout, file or Git
operations.

## Build and configure

Build or publish `src/Armada.Harbor/Armada.Harbor.csproj` for `net10.0`:

```sh
dotnet publish src/Armada.Harbor/Armada.Harbor.csproj -c Release -f net10.0 -o harbor-runner
dotnet harbor-runner/Armada.Harbor.dll --config /path/to/private/runner.json
```

The host needs the selected agent CLIs and their own provider sign-in. The
Admiral does not forward its provider credentials. Each runtime key must match
the command name in the Admiral's resolved launch plan. Its value is an
absolute executable path on the runner. The runner passes the resolved argument
list through `ProcessStartInfo.ArgumentList`; it does not build a shell command
or rebuild the runtime's arguments.

Create the settings file in a private user directory. On Unix, use `umask 077`
when creating it and mode `600` for the file. On Windows, restrict its directory
and file access to the runner account and trusted administrators. Do not commit
the populated file.

```json
{
  "runnerId": "hbr_example",
  "name": "Build runner",
  "linkUri": "wss://admiral.example/harbor/link",
  "credential": "replace-with-enrolled-bearer-credential",
  "maxConcurrentJobs": 4,
  "heartbeatIntervalSeconds": 10,
  "reconnectDelaySeconds": 2,
  "supervisor": {
    "stateDirectory": "/var/lib/harbor/state",
    "workingRoots": ["/work"],
    "commands": {
      "claude": "/usr/local/bin/claude",
      "codex": "/usr/local/bin/codex"
    },
    "maxSpoolBytes": 16777216,
    "maxRetainedJobs": 64
  }
}
```

The credential is the persistent bearer credential named by the runner's
enrollment. The link uses the `Authorization` header. The URL cannot contain
credentials, a query or a fragment. Remote links require `wss`; `ws` is accepted
only for loopback development. The configuration loader rejects symbolic links
in its path and files that grant access outside the runner account and trusted
system administrators.

Enable and enroll the runner through the existing
[Harbor operator controls](HARBOR_PROTOCOL.md#opt-in-operation). Starting this
console does not enable the Admiral's Harbor listener or create an enrollment.

## Job ownership and recovery

One console owns a state directory at a time. Each accepted job starts an
independent supervisor process. The supervisor owns the agent process and its
stdout and stderr readers. Closing the console closes the link; it leaves
these supervisors and agents running.

The supervisor writes a private launch intent before starting the agent. It
stores the exact job and launch key, supervisor and agent process identifiers,
UTC process start times, output cursor and terminal state. On Linux, it also
retains the kernel process start ticks and boot identifier for local identity
checks across console restarts. It does not store
the launch prompt, arguments, environment or link credential. The console sends
the launch envelope to the supervisor once through stdin. It removes inherited
Admiral and Harbor credential variables and aliases of its link credential
before starting that process.

The supervisor flushes each output chunk before advancing its saved sequence.
It saves the observed exit code only after both output streams have finished.
A stop request must match the job, launch key and recorded process identity.
The supervisor waits for the requested grace period, then kills the remaining
process tree and observes its exit.

After reconnect, the console offers saved job proofs to the Admiral. A live
proof requires the exact supervisor and child process identities. Linux checks
the saved kernel start ticks and boot identifier; other supported hosts check
the process start time. A terminal
proof requires a saved confirmed stop and a known exit code. The Admiral names
the output sequence to replay. The console sends that output before the exit;
it never starts another agent for a retained job.

Missing or corrupt identity, a dead unconfirmed supervisor, incomplete output,
and an unknown exit code cannot produce a safe resume proof. The job remains
fenced. A launch that fails before it records a child process sends a named
error and also remains fenced on the Admiral. An administrator must verify
the old writer and use the exact-revision stop-confirmation route described in
the [protocol](HARBOR_PROTOCOL.md). Neither timeout nor a missing process alone
proves a safe replacement.

## State and output limits

The state directory and its files are private. Symbolic links and reparse
points in state or working-directory paths are refused. A requested working
directory must exist under a configured working root. Only configured runtime
executables and the shared allowed launch environment can run.

`maxSpoolBytes` bounds each job's retained output. An output limit records a
named gap and stops the child; it does not report a complete successful replay.
`maxRetainedJobs` bounds the number of job records, including terminal jobs and
incomplete admission records. A full journal refuses new jobs. The product of
these two limits cannot exceed one GiB. Unpublished staging records left by a
failed admission are removed under the exclusive admission lock. Published job
records remain until manual cleanup. The state directory stores its spool and
job-count limits. Later consoles and supervisors must use the same limits. To
change them, account for and archive all old jobs, then select a new private
state directory.

Recovery covers console and Admiral process restarts. It does not guarantee
that a host power failure preserves the most recent filesystem writes. Missing
proof after a host failure keeps the job fenced.

Published job state and output are not removed automatically. Archive and verify completed
job directories before a manual cleanup. Keep every pending, running or
uncertain job directory until its writer and Admiral result are accounted for.
