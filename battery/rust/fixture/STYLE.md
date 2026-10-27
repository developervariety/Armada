# Style rules

1. Every `pub` item has a `///` doc comment.
2. Functions and variables are `snake_case`; types are `CamelCase`.
3. No `unwrap()` or `expect()` in library code under `src/`; return a `Result`.
4. Public functions check their arguments and return `InventoryError`.
5. Code is formatted with `rustfmt`.
6. No `println!` / `eprintln!` in library code under `src/`.
