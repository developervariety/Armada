# Style rules

1. Every public module, class, function and method has a docstring.
2. Every public function and method has type hints on its parameters and its return value.
3. Private attributes are named with a leading underscore (`self._items`).
4. Public functions check their arguments and raise `TypeError` / `ValueError`.
5. No bare `except:`; catch the specific exception.
6. No `print()` in library code under `inventory/`.
