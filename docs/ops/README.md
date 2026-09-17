# Operator Guide Chapters

The chapters of the operator guide ([`../armada-ops.md`](../armada-ops.md)) are
operator-local. Each deployment keeps its own filled copies in this folder, and
git ignores them, because a filled chapter names real hosts, vessels, accounts,
and policy.

The tracked files here are templates:

- `NN-<chapter>.example.md` — one template per chapter, with the retrieval
  front-matter (`topic`, `summary`, `read_when`, `applies_to`, `tier`) the
  context index reads.
- `INDEX.example.json` — an example of the generated chapter index.

To start a deployment's guide, copy each template to the same name without
`.example` and fill it in. The Admiral reads the chapters from the docs root
(`ARMADA_DOCS_ROOT`, or the `docs` folder that holds `armada-ops.md`).

Product reference that every deployment shares is tracked outside this folder:
[`../MCP_TOOL_CATALOG.md`](../MCP_TOOL_CATALOG.md),
[`../TYPED_DECISIONS.md`](../TYPED_DECISIONS.md), and
[`../USAGE_ROUTING.md`](../USAGE_ROUTING.md).
