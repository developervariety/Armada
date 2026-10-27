# Inventory fixture

A small Go module (package `inventory`) used by the Armada fixture battery. It has no module dependencies; nothing
is downloaded.

- Build: `go build ./...`
- Tests: `go test -count=1 .` (exit code 0 when every registered test passes)
- List registered tests: `go test -count=1 . -args -list`

Tests are unexported functions `func name(t *testing.T)` in `*_test.go` files. A test only runs when it is registered
in `registry_test.go`. Follow `STYLE.md`.
