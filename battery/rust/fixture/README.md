# Inventory fixture

A small Rust library crate (`inventory`) used by the Armada fixture battery. It has no crate dependencies; nothing is
downloaded.

- Build: `cargo build --offline`
- Tests: `cargo test --offline` (exit code 0 when every registered test passes)
- List registered tests: `cargo test --offline -- --list`

Tests are plain `fn name()` functions in `tests/*_tests.rs` run by the dependency-free runner `tests/run.rs`. A test
only runs when it is registered in `tests/registry.rs`. Follow `STYLE.md`.
