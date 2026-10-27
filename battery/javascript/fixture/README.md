# Inventory fixture

A small JavaScript (Node.js, CommonJS) library used by the Armada fixture battery. It has no package dependencies;
nothing needs to be installed.

- Tests: `node tests/run.js` (exit code 0 when every registered test passes); `npm test` runs the same command
- List registered tests: `node tests/run.js --list`

Tests are plain functions exported from `tests/*Tests.js`. A test only runs when it is registered in
`tests/registry.js`. Follow `STYLE.md`.
