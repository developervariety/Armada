# Fixture Battery

A battery run gives one captain a pack of small repository tasks and grades the result. Each run answers one
question: how does this captain do on these tasks, today?

A run targets one captain and one pack. For each task the Admiral:

1. Resets the pack's fixture repository to the pack's fixture files plus the task's setup files.
2. Dispatches a voyage on the pack's fixture vessel. The voyage uses the `WorkerOnly` pipeline, landing mode `None`,
   and `IsEvaluation` true, so no battery work lands and no evaluation mission counts as production work.
3. Waits for the mission to finish or for the task timeout (60 minutes unless the task sets `timeoutMinutes`).
4. Waits for mission completion handling to release the captain, then grades an isolated checkout of the recorded result commit on the Admiral host. Hidden grading files do not change the captain's dock. The grading checkout is removed after grading.

Evaluation missions wait for their requested captain when it is busy or unavailable; they do not use another captain from the fallback tier. The normal eligibility and usage gates still apply. Captain wait time is included in `MissionDurationMs`, so use the mission's start and completion times when comparing execution time.

The fixture vessel is named `battery-<pack id>` and is created on request. Fixture repositories live under the
`battery` folder of the data directory.

## Pack format

A pack is a folder under `battery/` with a `battery-pack.json` manifest. The manifest is JSON; comments and
trailing commas are allowed. A pack that breaks the format is listed with `Valid` false and its errors, and it
cannot run.

| Field | Meaning |
|---|---|
| `schemaVersion` | Must be `1` |
| `id` | Lowercase letters, digits and dashes; the pack folder name |
| `name`, `domain`, `description` | Labels |
| `version` | Integer, 1 or more; raise it when a change makes old grades incomparable |
| `fixture.path` | Folder, relative to the pack, that holds the fixture repository files |
| `fixture.defaultBranch` | Branch of the fixture repository (default `main`) |
| `toolchain[]` | Tools the Admiral host needs: `name`, `executable`, `arguments` (a version probe), `hint` |
| `tasks[]` | The tasks, described below |

Each task has:

| Field | Meaning |
|---|---|
| `id`, `title`, `difficulty` | Labels; `id` is unique in the pack |
| `prompt` | The mission description the captain receives |
| `setup` | Optional folder of files laid over the fixture before the task |
| `toolchain` | Optional names from the pack toolchain; default is every pack entry |
| `timeoutMinutes` | Optional, 1 to 1440 |
| `grader` | How the worktree is judged |

Every path in a manifest is relative to the pack and stays inside it.

### Graders

| `grader.type` | Fields | Passes when |
|---|---|---|
| `command` | `commands[]`, optional `overlay` | Every command exits with its `expectedExitCode` (default 0) |
| `byteComparison` | `files[]` of `path` and `expected` | Every worktree file is byte-identical to its expected file in the pack |
| `mutation` | `commands[]`, `mutants[]`, optional `overlay` | The commands pass on the worktree and fail with each mutant applied |

`overlay` is a folder of files copied into the worktree before grading, so the captain cannot see the grading tests.
A command has `executable`, `arguments` and `timeoutSeconds` (default 600). A mutant has `id`, `file`, `find` and
`replace`; the first occurrence of `find` is replaced, the commands run, and the file is restored.

## Grades

| Grade | Meaning |
|---|---|
| `Passed` | The grader's condition held. `Score` is 1. |
| `Failed` | The grader ran and the condition did not hold, or the mission failed, was cancelled or timed out after the captain started. `Score` is the share of commands run, files matched or mutants killed. |
| `CouldNotRun` | The task could not be judged: a toolchain tool is missing on the Admiral host, the fixture could not be reset, the result commit is unavailable or the dock is Harbor-owned, another captain ran the mission, or the captain never took it. |

A missing tool never grades `Failed`. After one task finds a tool missing, later tasks that need it are not
dispatched.

A run is `Running`, `Completed`, `Failed` (the run itself stopped on an error) or `Interrupted` (the Admiral stopped
while it ran). The run record holds the run id, captain, pack and version, each task's grade, score, reason, timings,
voyage and mission, and the evidence behind the grade. The attempts behind a grade are the mission's attempt
provenance records: `GET /api/v1/missions/{id}/attempts`.

## Running a battery

All routes need a global administrator.

```bash
curl -X POST http://localhost:7890/api/v1/battery/runs \
  -H "Content-Type: application/json" \
  -d '{"CaptainId": "cpt_example", "PackId": "csharp", "CreateFixtureVessel": true}'
curl http://localhost:7890/api/v1/battery/runs/bat_example
```

A run dispatches real missions and spends captain tokens. A captain has one run at a time. The MCP tools
`armada_battery_packs`, `armada_battery_runs` and `armada_battery_run` read packs and runs; no MCP tool starts a run.
The dashboard Battery page lists runs and shows each run's tasks.

The shipped packs are `csharp` (needs the .NET SDK) and `javascript` (needs Node.js). Both need `git` on the Admiral
host's `PATH`.

When the installation's definition-of-done gate requires workflow commands, configure a workflow profile for the fixture vessel before collecting comparative results. For `battery-csharp`, use `dotnet build Inventory.sln -c Release` and `dotnet run --project tests/Inventory.Tests -c Release --no-build --no-restore`. A missing profile is an infrastructure failure before grading; retain that attempt separately from model-quality results.
