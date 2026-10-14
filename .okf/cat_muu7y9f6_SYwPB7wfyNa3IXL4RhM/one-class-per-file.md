---
type: "Project"
title: "C#: one class per file, file name matches the class"
description: "Each class, enum, or interface lives in its own file named after it (FleetListResult.cs, ApprovalKindEnum.cs); no nested helper classes bundled into another file."
timestamp: "2026-10-05T05:42:47.4740540Z"
created: "2026-10-05T05:42:47.2514640Z"
slug: "one-class-per-file"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["region-blocks", "no-tuples"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md File Organization): one class per file and the filename matches the class name. This is why the repo has many tiny files (for example src/Test.Shared/Infrastructure has McpRpcError.cs, McpContentItem.cs, ParityEntry.cs separately, and Armada.Tui has OpsAction.cs, OpsTile.cs ...). Enums use the Enum suffix and also get their own file. Exception: generic and non-generic pairs are still separate files. Enforced by review.
