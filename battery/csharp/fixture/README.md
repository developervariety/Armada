# Inventory fixture

A small C# solution used by the Armada fixture battery. It has no package dependencies.

- Build: `dotnet build Inventory.sln`
- Tests: `dotnet run --project tests/Inventory.Tests` (exit code 0 when every registered test passes)
- List registered tests: `dotnet run --project tests/Inventory.Tests -- --list`

Tests are plain static methods. A test only runs when it is registered in `tests/Inventory.Tests/TestRegistry.cs`.
Follow `STYLE.md`.
