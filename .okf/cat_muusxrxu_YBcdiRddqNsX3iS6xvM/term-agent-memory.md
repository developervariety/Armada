---
type: "Reference"
title: "Agent memory (mem_): durable episodic/semantic/procedural knowledge"
description: "Memory (mem_) stores Episodic/Semantic/Procedural knowledge distilled by the Recorder persona, with provenance, tags, salience, and TenantWide/UserSpecific scope; searched by substring, ordered by salience."
timestamp: "2026-10-05T05:32:45.4814440Z"
created: "2026-10-05T05:32:45.1289480Z"
slug: "term-agent-memory"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-persona", "term-pipeline"]
version: "1"
salience: "0.5"
---
Term: Memory (src/Armada.Core/Models/Memory.cs, prefix mem_; MemoryService). Durable agent memory that survives across sessions; the persistent counterpart to a captain's transient context and to a vessel's ModelContext.

Fields: Scope (TenantWide/UserSpecific), Type (MemoryTypeEnum Episodic = what happened, Semantic = standalone fact, Procedural = how-to), Topic, Key, Summary, content, Salience (double; recall ordered by it), confidence/usage counters, provenance SourceKind (MemorySourceKindEnum Voyage, Mission, Vessel, Conversation, Manual, Other) with SourceVoyageId/SourceMissionId/SourceVesselId/SourceDetail, VesselId, Tags.
Written mainly by the Recorder persona (final, non-gating stage of Recorded/FullPipeline) which reconciles new knowledge against existing memories; every working persona is told to call search_memory first. Search is case-insensitive substring over content, topic, and tags, applied in memory over the caller-visible set (no vectors). Interfaces: MCP search_memory, get_memory, create_memory, update_memory, delete_memory; REST /api/v1/memories; dashboard Configuration > Memory. Not to be confused with this NotDory scope.
