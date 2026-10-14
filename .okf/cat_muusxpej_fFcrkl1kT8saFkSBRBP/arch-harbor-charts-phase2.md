---
type: "Project"
title: "Harbor charts phase 2: Harbor app and TUI (branch work/harbor-charts, 2026-10-08)"
description: "Chart models in Armada.Core.Metrics.Charts, fetch in Armada.Client.Metrics (HarborMetricsFeed/Refresher), Harbor Status Activity tab + Overview Last 24 hours card, TUI HarborsScreen Activity panel (r range, u token usage), TokenUsageScreen ?harborId= with x to clear."
timestamp: "2026-10-08T22:16:53.4655370Z"
created: "2026-10-08T22:16:50.2901020Z"
slug: "arch-harbor-charts-phase2"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
version: "1"
salience: "0.5"
---
Phase 2 of per-Harbor charts. Pure, testable chart layer in src/Armada.Core/Metrics/Charts (HarborChartMapper, BucketChartModel, StatusStripModel, SparklineModel, ChartGeometry, ChartFormat; color roles ChartColorEnum mapped to theme resources). Fetch in src/Armada.Client/Metrics: HarborMetricsFeed classifies 401/403/404 (probes GET /harbors/{id}: exists -> AdmiralOutdated, else NotRegistered)/offline (transport, 502-504)/failed; HarborMetricsRefresher with injectable delay (tests use a SemaphoreSlim gate) and CompletedRuns counter for deterministic waits. Harbor app: ChartControlBase/BucketChartControl/StackedBarChart/LineChart/Sparkline/StatusStrip/ChartLegend drawn with DrawingContext; HarborActivityView (Status > Activity) and HarborDayStripView (Overview). TUI: HarborMetricsPanel under the grid via EntityListScreen.DetailPanel hook. Tests: Metrics.HarborCharts, Client.HarborMetricsFeed, Tui.HarborMetrics; fixture Test.Shared/Infrastructure/HarborMetricsFixture.
Lesson: FocusScope.Move used to return false when Tab wrapped onto the only child (shell at 80 cols has just the screen), so Tab stuck on a screen's last stop; fixed by re-entering the container via FocusEdge. FocusAudit at 80x24 catches it only when the stuck stop is not stop 0.
