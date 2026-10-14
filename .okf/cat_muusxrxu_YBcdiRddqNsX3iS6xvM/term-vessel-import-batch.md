---
type: "Reference"
title: "Vessel import batch (vib_), items (vii_), fleet recommendations (vfr_)"
description: "A VesselImportBatch records one discover-then-import run of local repos: candidates with status New/AlreadyOnboarded/Worktree/ArmadaManaged/NotFound/NotGit/AccessDenied, item outcomes, and optional captain fleet recommendations."
timestamp: "2026-10-05T05:31:44.4665570Z"
created: "2026-10-05T05:31:44.0626370Z"
slug: "term-vessel-import-batch"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-vessel-import-and-categorization", "term-job"]
version: "1"
salience: "0.5"
---
Term: Vessel import batch (src/Armada.Core/Models/VesselImportBatch.cs vib_; VesselImportItem vii_; VesselImportFleetRecommendation vfr_).

Batch Status (VesselImportBatchStatusEnum): Discovering (background discovery running), Discovered, Importing, Completed, CompletedWithFailures, Failed. Candidate status (VesselImportCandidateStatusEnum): New, AlreadyOnboarded (matches an existing vessel's WorkingDirectory or normalized RepoUrl; ExistingVesselId set), Worktree (.git is a file; not preselected), ArmadaManaged (Armada's own ReposDirectory/DocksDirectory/DataDirectory), NotFound, NotGit, AccessDenied. Item Outcome (VesselImportOutcomeEnum): Pending, Created, SkippedExisting, SkippedNotSelected, Failed, with OutcomeReason codes (VesselAlreadyExists, NotSelected, NotImportable, PathMissing, CreateFailed, Cancelled). CategorizationStatus (VesselImportCategorizationStatusEnum): None, Pending, Running, Completed, Failed, Applied.
Imported vessels get RepoUrl = origin URL (or local path), WorkingDirectory = discovered path, LocalPath unset. Error detail codes (VesselImportCodes): InvalidRequest, HarborNotSupported, PathNotAllowed (403), DirectoryNotFound, BatchNotFound, BatchBusy (409). Finished batches pruned after Retention.ImportBatchRetentionDays (90). See [[arch-vessel-import-and-categorization]].
