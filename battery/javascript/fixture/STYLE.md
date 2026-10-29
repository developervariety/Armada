# Style rules

1. No `var`: declare every variable with `const`, or `let` when it is reassigned.
2. Every exported function, class and method has a `/** ... */` JSDoc comment.
3. Private state is named with a leading underscore (`_items`).
4. Public functions check their arguments and throw `TypeError` / `RangeError`.
5. Strict equality only: `===` and `!==`, never `==` or `!=`.
6. No `console.log` in library code under `src/`.
