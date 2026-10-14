# Synthetic Activity for Screenshots

`src/Armada.LoadGenerator` is a .NET 8/10 CLI that writes randomized historical activity through Armada's database drivers or a global-admin REST endpoint. Preview is the default. No captain processes, git operations, inference calls, or unread signals are launched.

## Covered charts

| Surface | Generated records |
|---|---|
| Dashboard mission history; TUI/mobile mission activity | Complete/failed missions with varied runtimes and timestamps |
| Activity → API Requests | Requests with varied routes, methods, status codes, durations and sizes |
| Activity → All Activity / Events / Signals | Linked mission events and read synthetic signals |
| Activity → Token Usage | Harbor/vessel-scoped model usage, supplementing existing synthetic tokens |
| Configuration → Harbors → Charts | Jobs, slot utilization, launch speed, link state, round-trip latency and tokens |

Select the generated **Screenshot (synthetic)** Harbor or vessel for filtered charts. The Harbor is disabled and has no real runner. Missions are terminal, titles start `[Synthetic]`, API principals/auth methods identify synthetic requests, and event/signal payloads contain `synthetic: true`. IDs have `_syn_` markers. Existing records are preserved.

## CLI

Run `dotnet run --project src/Armada.LoadGenerator --framework net10.0 -- --help`.

```sh
# Preview and preserve a profile outside the repository.
dotnet run --project src/Armada.LoadGenerator -f net10.0 -- \
  --profile-file /tmp/screenshot-activity.json --days 42 --density 3 --entropy 1.3

# Apply that exact profile through the Admiral (global admin session required).
dotnet run --project src/Armada.LoadGenerator -f net10.0 -- \
  --profile-file /tmp/screenshot-activity.json --api-url http://127.0.0.1:7890 \
  --session-file /external/session.json --retention-days 60 --apply

# Write directly to an existing initialized database.
dotnet run --project src/Armada.LoadGenerator -f net10.0 -- \
  --profile-file /tmp/screenshot-activity.json --db-type Sqlite \
  --db-file /external/armada.db --tenant-id default --user-id default --apply
```

Server providers accept `--db-type Mysql|Postgresql|SqlServer`, `--db-host`, `--db-port`, `--db-name`, `--db-user`, and `--db-password-env ENV_NAME`. Passwords are read from the named environment variable; session files and credentials must stay outside the repository. Database mode does not initialize or migrate schemas. For histories longer than the server's retention period, configure `DataRetentionDays` and `RequestHistoryRetentionDays` in its settings file. API mode accepts `--retention-days 60` to raise those periods live without shortening longer/unlimited retention. The default server retention is 30 days, which would otherwise remove older screenshot rows.

`--density` scales mean volume (80 requests and eight missions per day at density 1, before weekend/random variation). `--entropy` controls daily count variation (0 uniform, 1 default, 2 strongest). `--seed` allows reproducibility; otherwise the seed is randomly generated. `--days` accepts 1–90, density 0.1–5, entropy 0–2; excessive profiles are rejected by a 50,000-record planning budget. Every profile supplies at least twelve requests and terminal missions spread across the final two hours. Keep `ToUtc` close to execution time for current screenshot charts. The CLI default ends 30 seconds before the local clock to tolerate small server clock differences; explicit `--to-utc` values are preserved.

Profile files preserve batch, seed and exact UTC window; rerunning the same profile/owner skips existing IDs and resumes partial runs. Changing a profile produces a separate dataset. Receipts are saved next to the profile as `.receipt.json`. Back up before direct database operations; this tool has no purge mode.

## Execution checklist

- [x] Implement provider-neutral generator, CLI, admin API, and historical filter decoding.
- [x] Add generation/replay/ownership/window/auth tests.
- [x] Complete .NET 8/10 generation, auth, replay, SDK, encoded-window and live-retention checks; four providers pass (12 cases each).
- [x] Publish updated `armada-server:v1.1.0` image (including retention support).
- [x] Update the screenshot deployment backend container.
- [x] Generate 42 days with density 3 and entropy 1.3; verify mission/API/recent/Harbor summaries live.
- [x] Record live counts and verify chart summaries; operator can now collect screenshots.

Validation: .NET 10 affected suites passed (57 initial chart/provider/API cases; 31 generation/retention/parity cases; 11 final SDK/retention cases). .NET 8 generation/retention tests passed (nine cases). Solution and dashboard builds passed without warnings/errors. Live deployment verified; retention set to 60 days.

## Live screenshot dataset

Generated on 2026-10-14 UTC: 12,295 API requests, 1,226 terminal missions, 1,226 events, 324 read signals, 1,226 Harbor jobs, 1,226 additional token records, 1,008 link samples and 126 link events. A fixed-profile replay reported zero new writes and 18,660 existing records. The original 5,742 synthetic token records remain available.

Verification found 871 synthetic missions in the last 30 days and 75 API requests / 19 missions in the latest two hours. The screenshot Harbor has three runtime series, nonempty latency and link-state charts, scoped tokens, and a week peak of six occupied slots; its synthetic capacity is eight slots and routing remains disabled.

Harbor: `hbr_syn_8e6704abcd36ff64b0aa`; vessel: `vsl_syn_8e6704abcd36ff64b0aa`. Select **Screenshot (synthetic) 8e6704abcd36ff64b0aa** for filtered screenshots. Profile, session and receipts are stored outside the repository.
