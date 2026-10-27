# Style rules

1. Every PHP file starts with `<?php` followed by `declare(strict_types=1);`.
2. Every class, public method and function has a `/** ... */` docblock.
3. Properties are private and named in camelCase (`$items`); expose them through methods.
4. Public methods check their arguments and throw `InvalidArgumentException` / `LengthException`.
5. Strict comparison only: `===` and `!==`, never `==` or `!=`.
6. No `echo`, `print` or `var_dump` in library code under `src/`.
