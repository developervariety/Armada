# Inventory fixture

A small PHP (8.1 or later) library used by the Armada fixture battery. It needs only the PHP CLI; there is no Composer
and nothing is downloaded.

- Tests: `php -d zend.assertions=1 tests/run.php` (exit code 0 when every registered test passes)
- List registered tests: `php tests/run.php --list`

Tests are public static methods of the classes in `tests/` and check results with plain `assert()`; `Expect::throws`
checks exceptions. The runner refuses to run (exit code 3) when `zend.assertions` is not 1, because disabled
assertions would make every test pass. A test only runs when it is registered in `tests/Registry.php`. Classes are
loaded by `tests/bootstrap.php`: `Inventory\X` from `src/X.php`, `Inventory\Tests\X` from `tests/X.php`.
`config/legacy_codes.ini` is UTF-8 with a BOM and CRLF line endings on purpose. Follow `STYLE.md`.
