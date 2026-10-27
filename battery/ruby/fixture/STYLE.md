# Style rules

1. Every Ruby file starts with the magic comment `# frozen_string_literal: true`.
2. Every public class, module and method has a comment directly above it saying what it does.
3. Methods and variables are named in snake_case (`total_units`), never camelCase.
4. Public methods check their arguments and raise `TypeError` / `ArgumentError`.
5. Never `rescue Exception`; rescue the specific error class.
6. No `puts`, `print` or `p` in library code under `lib/`.
