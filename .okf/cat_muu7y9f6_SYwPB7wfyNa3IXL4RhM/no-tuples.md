---
type: "Project"
title: "C#: no tuples; define a class or use out parameters"
description: "Never return or pass (string, int) or ValueTuple; create a small result class (one per file) such as LandingTestEntitiesResult, or use out parameters."
timestamp: "2026-10-05T05:42:30.9312400Z"
created: "2026-10-05T05:42:30.7420350Z"
slug: "no-tuples"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["one-class-per-file", "no-var-keyword"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): no tuples, named or unnamed, and no ValueTuple/Tuple<>. Return a dedicated class instead (the repo has many: PrerequisiteResult, TestEntitiesResult, FleetListResult, ProcessOutcome, RuntimeExitInfo, MissionDispatchResult) or use out parameters (TryGet patterns like RouteAuthorizationRegistry.TryGetByTemplate(method, template, out AuthorizationRequirement? declared)). Each new result class goes in its own file with XML docs. Enforced by review.
