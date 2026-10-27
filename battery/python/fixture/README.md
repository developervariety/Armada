# Inventory fixture

A small Python package (`inventory`) used by the Armada fixture battery. It uses only the standard library; nothing
needs to be installed with pip.

- Tests: `python3 -B -m tests.run` from the repository root (exit code 0 when every registered test passes)
- List registered tests: `python3 -B -m tests.run --list`

Tests are `unittest.TestCase` methods in `tests/test_*.py`. A test only runs when it is registered in
`tests/registry.py`. Follow `STYLE.md`.
