---
type: "Reference"
title: "Vessel: a registered git repository (vsl_) and its key settings"
description: "Vessel (vsl_) = one git repo: RepoUrl, LocalPath (bare clone), WorkingDirectory (user checkout), context/style guide, landing and safety policies."
timestamp: "2026-10-05T05:28:54.0066540Z"
created: "2026-10-05T05:28:53.4960210Z"
slug: "term-vessel"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-fleet", "term-dock", "term-landing-mode", "arch-definition-of-done-and-autoland"]
version: "1"
salience: "0.5"
---
Term: Vessel (src/Armada.Core/Models/Vessel.cs, prefix vsl_). A git repository registered with Armada; belongs to one Fleet (FleetId).

Paths: RepoUrl; LocalPath = Armada's bare clone (default ReposDirectory/<name>.git; Armada may delete it); WorkingDirectory = the user's own checkout (LocalMerge target, Workspace, fleet Command actions, health evaluation). Imported vessels set WorkingDirectory and never LocalPath so deleting a vessel never touches the clone; a file:// RepoUrl on add_vessel also records the local clone as WorkingDirectory.
Context: ProjectContext, StyleGuide, EnableModelContext + ModelContext (agent-maintained notes, "Build/Refine Context").
Policy fields: DefaultBranch, DefaultPipelineId, LandingMode, BranchCleanupPolicy, AllowConcurrentMissions (default serialized per vessel), AutoApprove (nullable override of captain auto-approve, migration v76), RequirePassingChecksToLand, ProtectedBranchPatterns, SecretScanEnabled, ProtectedPathPatterns, PrivateIdentifierDenylist, AutoLandEnabled + AutoLandMaxFiles/Lines + AutoLandPathAllow/DenyGlobs, DefinitionOfDoneEnabled + Build/TestCommand + timeout, Release/HotfixBranchPrefix, RequirePullRequestForProtectedBranches, RequireMergeQueueForReleaseBranches, PreferredHarborId, RequiredCapabilities, GitHubTokenOverride (write-only; reads expose hasGitHubTokenOverride).
Example: `armada vessel add my-api https://github.com/you/my-api`.
