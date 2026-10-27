# Inventory fixture

A small Ruby (2.6 or later) library used by the Armada fixture battery. Its tests use minitest, which ships with
Ruby; there is no Gemfile or Bundler and nothing is installed.

- Tests: `ruby tests/run.rb` (exit code 0 when every registered test passes)
- List registered tests: `ruby tests/run.rb --list`

Tests are `test_*` methods of `Minitest::Test` classes in `tests/*_test.rb`. A test only runs when it is registered
in `tests/registry.rb` (and its file is required there); `ruby tests/run.rb` runs the registered tests, nothing else.
`lib/inventory/legacy_codes.rb` is UTF-8 with a BOM and CRLF line endings on purpose. Follow `STYLE.md`.
