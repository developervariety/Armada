# Style rules

1. No `var`: declare every local variable with its explicit type.
2. Every public class, constructor and method has a `/** ... */` Javadoc comment.
3. Fields are private and named in camelCase (`items`), with no prefix or underscore.
4. Public methods check their arguments and throw `NullPointerException` / `IllegalArgumentException`.
5. Compare strings with `equals`, never with `==` or `!=` (null checks use `== null` and `!= null`).
6. No `System.out` or `System.err` in library code under `inventory/`.
