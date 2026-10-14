---
type: "Reference"
title: "Entity ID prefixes (PrettyId)"
description: "IDs are PrettyId strings with type prefixes: flt_, vsl_, cpt_, msn_, vyg_, dck_, sig_, art_, mrg_, plus obj_, job_, rbd_, tku_, mep_, hbr_, mem_, vib_, vii_, vfr_, fac_, far_, fat_, vhl_, vhf_, vdp_, vho_, ath_ and more in Constants."
timestamp: "2026-10-05T05:43:21.4697980Z"
created: "2026-10-05T05:43:21.1138770Z"
slug: "id-prefixes"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-sentinel-strings-rule-3", "owner-libraries-usage"]
version: "1"
salience: "0.5"
---
CLAUDE.md lists the core prefixes: flt_ fleet, vsl_ vessel, cpt_ captain, msn_ mission, vyg_ voyage, dck_ dock, sig_ signal, art_ artifact; AGENTS.md adds mrg_ merge queue entry. src/Armada.Core/Constants.cs defines more (generated with Constants.IdGenerator, a PrettyId.IdGenerator): obj_ objective, job_ job, rbd_ rebuild, tku_ token usage, mep_ model endpoint, hbr_ Harbor, mem_ memory, vib_ vessel import batch, vii_ import item, vfr_ fleet recommendation, fac_ fleet action, far_ fleet action run, fat_ run target, vhl_ vessel health, vhf_ health finding, vdp_ vessel dependency, vho_ health override, ath_ Ask thread, and others. Other fixed ids: tenant "default", system tenant ten_system, API key ak_. New entity types get a new prefix constant there. Do not decide behavior from id prefixes (fragility rule 3 removed "local-" id prefixes as sentinels); the TUI palette opening an entity by typed id is navigation, not classification.
