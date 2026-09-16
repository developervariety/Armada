# Upstream Parity Standpoint

Baseline record so future upstream-parity passes start here instead of re-deriving from the merge base.

## Assessed
- Our fork tip at assessment: `ae0431ad1` (origin/main).
- Upstream tip assessed: `upstream/main` `d92e1dce6` (jchristn/Armada), 2026-09-16.
- Merge base: `e9e3021fac`.
- Method: `git log origin/main..upstream/main` (327 upstream-only commits) classified per feature against our tree.

## Conclusion
Our fork is a divergent SUPERSET of upstream, not behind it. We keep parity by re-implementing upstream features, not by merging. A blanket `git merge upstream/main` produced ~2,028 conflicting files and would collide with — and in places regress — deliberate fork choices. Do not merge upstream/main.

## Already have (do not re-port)
Recorder + native memory + memories REST; Linter persona; ApiEndpoint MCP-in-chat; per-user scope/auth + caller attribution; OpenCode runtime; Audit/Research mission modes; definition-of-done gate; no-op completion rejection; autonomous recovery; papercuts; model-tier routing; the entire dashboard (multi-tenant scoping, Ask Armada, captain-map, token-usage, memory surface, stickiness, setup wizard, jobs — several more developed than upstream); plumbing scripts (framework arg, insecure flag, factory-reset, install-mcp, publish); telemetry (Radiant); ARMADA_DATA_DIR data-directory env override (accepted as ARMADA_DATA_DIRECTORY and the ARMADA_DATA_DIR alias).

## Deliberate divergences (keep ours; a merge would regress these)
- Linter is seeded into the code-producing pipelines (Tested, ReferencePortingTested, ProductDevelopment), NOT FullPipeline: FullPipeline stays the minimal review shape. Upstream wired its Linter into FullPipeline instead.
- "Harbor" is a name collision: ours is a distributed-runner enrollment/job protocol (server-side); upstream's is a desktop rebuild supervisor. Different concepts.
- Self-deploy is a headless supervised cutover (SelfDeployPreflight/Build/DatabaseBackup/ReleasePrune/RestartRecord, rehearse-self-deploy-cutover.sh), not upstream's SlotManager A/B slots.
- ModelEndpoint uses hand-rolled per-provider HTTP contracts, not upstream's PolyPrompt clients.

## Genuinely new upstream items and disposition (as of this baseline)
- Cloud model-endpoint providers (Azure OpenAI / Vertex AI / Bedrock, upstream 65630266a via PolyPrompt): NOT ported (owner decision 2026-09-16). Real gap if those providers are wanted later; needs adopting PolyPrompt or hand-rolling three provider wire+auth contracts.
- In-place Restart Server (upstream b9cb70b62): PORTED, adapted for Docker (graceful stop + restart policy) rather than the native process-relaunch.
- ARMADA_DATA_DIR (upstream 87f214461): already present; no port needed.
- Armada.Publisher installer/packaging + release workflow (upstream d92e1dce6): NOT ported — targets native installers; our fork ships Docker + self-deploy.
- Harbor Avalonia desktop runner app (src/Armada.Harbor/): NOT ported — our Harbor is server-side and arguably ahead.
- SlotManager A/B-slot rebuild + dashboard Rebuild button: NOT ported — targets native single-box; our deploy is headless supervised Docker cutover.

## Next pass
Start the next upstream-parity pass from `upstream/main` `d92e1dce6`: assess `d92e1dce6..upstream/main`. Everything up to `d92e1dce6` is covered by this baseline.
