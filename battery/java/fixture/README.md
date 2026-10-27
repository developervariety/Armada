# Inventory fixture

A small Java library used by the Armada fixture battery. It needs only a JDK (17 or later); there is no Maven or Gradle
build and nothing is downloaded.

- Build and test: `javac -encoding UTF-8 -d build -sourcepath . tests/TestRunner.java`, then
  `java -cp build tests.TestRunner` (exit code 0 when every registered test passes)
- List registered tests: `java -cp build tests.TestRunner --list`

`javac` compiles every class the test runner reaches through `-sourcepath .`, so a test class is compiled and run only
when it is registered in `tests/TestRegistry.java`. Tests are static methods; `tests/Check.java` has the assertions.
`config/legacy-codes.properties` is UTF-8 with a BOM and CRLF line endings on purpose. Follow `STYLE.md`.
