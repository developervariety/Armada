# Style rules

1. Every exported type, function, method and constant has a doc comment that starts with its name.
2. Names are MixedCaps or mixedCaps; no underscores in Go names.
3. Library code returns errors; it never panics.
4. Exported functions check their arguments and return the sentinel errors from `errors.go` (wrapped with `%w`).
5. Code is formatted with `gofmt`.
6. No `fmt.Print*` / `log.Print*` in library code.
