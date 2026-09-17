---
topic: "Available Features And Active Policy"
summary: "Which optional features this deployment enables, the setting that gates each, and the fallback when one is off."
read_when: "Before depending on a feature such as code indexing, autonomous recovery, or a landing mode."
applies_to: orchestrator
tier: leaf
---
# Available Features And Active Policy

> Template. Copy to `03-available-features-and-policy.md` in this folder and fill it in for your
> deployment. The filled copy is operator-local and is not committed.

Cover at least:

- A table: feature, setting key, state in this deployment, fallback when off.
- Owner policy that constrains a feature (for example which vessels may be indexed).
- How to read the live value instead of trusting this table.
