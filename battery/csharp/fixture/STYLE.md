# Style rules

1. No `var`: declare every local with its explicit type.
2. Every public type and member has a `/// <summary>` XML doc comment.
3. Private fields are named `_PascalCase`.
4. Public methods check their arguments and throw `ArgumentNullException` / `ArgumentOutOfRangeException`.
5. Braces on their own line; one class per file, and the file name matches the class name.
6. No `Console.WriteLine` in library code under `src/`.
