# Token Usage Heatmaps

## Progress

- [x] Create `feature/token-usage-heatmap` and preserve existing monitoring charts.
- [x] Add shared UTC daily aggregation, gap filling, five intensity levels, and input/output/cached/total selection.
- [x] Dashboard: Activity → Token Usage and Configuration → Harbors → Charts.
- [x] TUI: Activity → Token Usage calendar and `u` inspector; Harbor `u` opens its filtered calendar.
- [x] Mobile: Activity → Token Usage and Harbor Charts, with horizontal calendar scrolling.
- [x] Add admin-only, bounded, idempotent synthetic usage import and authenticated generator.
- [x] Update SDKs, API documentation, and translated UI strings.
- [x] Visually verify desktop/tablet/phone, light/dark themes, German, keyboard navigation, and existing charts.
- [x] Complete final backend, mobile, and database-provider checks; record results below.
- [x] Build and publish server/dashboard/proxy images as `v1.1.0` and `latest`; republish server with SQL Server timestamp fix.
- [x] Screenshot deployment exposes the updated import endpoint.
- [x] Import expanded historical dataset on the screenshot deployment and verify daily/recent summaries.

## Behavior and Implementation

The calendar uses an independent 365-day daily summary (`bucketMinutes=1440`); web and mobile also offer 31/90-day ranges. UTC dates align across surfaces. Color represents the selected metric relative to the window's peak. Selecting a day exposes exact counts. Query failures display an error rather than zero activity. Harbor views preserve Harbor filtering.

Shared helpers: `src/Armada.Dashboard/src/lib/tokenHeatmap.ts`. Web: `components/activity/TokenActivityHeatmap.tsx`. Mobile: `screens/activity/TokenActivityHeatmap.tsx`. TUI: `Widgets/TokenActivityHeatmap.cs`, `Screens/Activity/TokenActivityDialog.cs`, and `TokenUsageScreen.cs`.

## Screenshot Data Procedure

Run `python3 scripts/common/seed-token-usage.py --help`. Preview first; applying requires `--apply --manifest /absolute/path/outside/repository.json` and a session file outside the repository. Never save passwords/tokens in repository files.

The default 42-day generator varies weekdays, idle days, bursts, model mix, hours, tokens, and cache ratios. It writes usage records without inference or mission execution. The API tags every row `source=synthetic`, `estimated=true`, and `sourceId=synthetic:<batchId>`. Cache reads are included in input, so total is input + output. Preserve the external manifest for exact idempotent retries with `--resume`. Synthetic rows have no real Harbor/captain/vessel attribution. Removal uses the existing filtered-delete API with the exact synthetic source ID after confirmation.

## Validation Record

Dashboard: 758 tests passed; production build passed. Browser QA passed at desktop, tablet, and phone sizes. Isolated API dataset: 1,830 records over 42 days (not yet imported on the screenshot deployment). Mobile: 1,203 tests passed, lint/type checks passed. Focused .NET 8/10 import/API/database/TUI checks passed; SQLite/PostgreSQL/MySQL/SQL Server parity passed (8 tests each). Full .NET 10 run: 4,500 passed, nine upgrade-baseline cases skipped, one stale parity-count assertion failed. After updating the count/manifests, all 25 focused parity/SDK/import/database/TUI cases passed; no unresolved failures. Live import completed: 5,742 randomized records (three times the original 1,914), in 23 batches over the historical 42-day period. Recent activity was added throughout the past two hours; verification found 64 records across all 13 overlapping ten-minute buckets. All 5,742 records are visible in the daily summary. Manifests, credentials, and receipts remain outside the repository.
