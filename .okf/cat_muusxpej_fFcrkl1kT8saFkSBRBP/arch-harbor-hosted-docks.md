---
type: "Project"
title: "Harbor-hosted mission docks (split mode, branch work/harbor-docks, 2026-10-08)"
description: "Missions routed to a Harbor get their dock on the Harbor host; Admiral picks Harbor before provisioning via Resolve; all later dock ops run on the Harbor through DockHostResolver."
timestamp: "2026-10-08T16:52:29.7968270Z"
created: "2026-10-08T16:52:26.5991420Z"
slug: "arch-harbor-hosted-docks"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-harbor-split-mode", "arch-harbor-link-protocol"]
version: "1"
salience: "0.5"
---
Design (built on branch work/harbor-docks, not yet merged as of 2026-10-08):
- Placement: MissionService.ResolveDockPlacementAsync (set to AgentLifecycleHandler.ResolveDockPlacementAsync) runs before dock provisioning. Router (CaptainLaunchRouter.DecideAsync + HarborRoutingRequest.ExcludedHarborIds) picks a Harbor; Admiral sends HarborDockRequest Resolve; a Harbor that cannot serve is excluded and the next tried. None: wait if requireHarborForLaunch, else Admiral dock. A branch whose latest dock was Harbor-hosted pins to that Harbor (FindBranchHarborAsync). Harbors without the 'harbor-docks' capability keep the old Admiral-dock + launch probe behavior.
- Harbor side: Core/Services/HarborDockManager (+HarborRepositoryLocator, GitRemoteUrl). Mapping (Harbor settings Repositories vessel->path) > discovery under RepositoryRoots by remote URL > bare clone in ReposDirectory. Docks at DocksDirectory/<vessel>/<mission>. Checkout flow only fetches origin, git branch --no-track, git worktree add; never touches user's tree/index/HEAD/config. File ops restricted to docks folder; AddGitExclude writes to the shared .git/info/exclude.
- Protocol additive: dock/dockResult, file/fileResult, git.timeoutMs, gitResult.timedOut.
- Admiral side: Dock.RepositoryPath / Dock.CheckoutPath (migration 80). DockHostResolver.IsHarborDock(dock) = HarborId && RepositoryPath set. DockHost gives remote GitService (GitService(logging, IHostCommandExecutor)), RemoteHostCommandExecutor, HarborDockFileSystem. Used by DockService (ProvisionOnHarborAsync, reclaim), MissionService (instructions, anchors, DoD via EvaluateOnHostAsync with /bin/sh -lc), CaptainService recovery, MissionLandingHandler (landing into dock.CheckoutPath; PR via gh on Harbor).
- Harbor UI: RepositoriesSettingsView tab "Repositories" in ManagementWindow (coordinator asked for a self-contained view; Settings UI will be reorganized).
- Tests: Services.GitRemoteUrl, Services.HarborDockManager, Services.HarborDockLifecycle (real git + in-process Harbors, E2E).
