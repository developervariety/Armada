---
type: "Project"
title: "Fragility rule 3: no sentinel strings for types or errors"
description: "No \"EXCEPTION \", \"none\", \"! \" prefixes, \"local-\" id prefixes, or display labels used as keys; use enums, flags, or nullable typed fields."
timestamp: "2026-10-05T05:45:01.4767810Z"
created: "2026-10-05T05:45:01.0538250Z"
slug: "no-sentinel-strings-rule-3"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-substring-classification-rule-2", "enum-naming-jsonconverter", "test-assertions-structured-rule-7"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 3. Examples found by the 2026-10-04 scan: a test converted exceptions to the string "EXCEPTION " and then skipped them (a false pass); "none" meaning absent; "! " prefixes marking errors; "local-" id prefixes meaning not-persisted; display labels used as dictionary keys. Replace with enums, boolean flags, nullable typed fields, or dedicated result classes. Remaining known sentinel (deferred): ArchitectHandoffMarker in mission descriptions. TUI/dashboard R4 extends this to action menu keys and status badges (typed enums instead of label text).
