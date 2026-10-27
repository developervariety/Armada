Grade the plan from 0 to 3 on each line; the evaluator records the scores and a one-line reason for each.

1. Slicing: each mission is a bounded, independently verifiable slice (parser, import into Stock, summary, tests),
   not one mission for everything and not trivial fragments.
2. Grounding: the missions name the real files and types of this fixture (`stock.go`, `item.go`, `Item`,
   `Stock.Add`, `errors.go`, `registry_test.go`) instead of invented ones.
3. Acceptance: every mission states how it is checked (registered tests that run with `go test -count=1 .`),
   including the bad-line and merge cases.
4. Dependencies: the order between missions is stated where one needs another (parser before import).
5. Scope: nothing outside the request (no database, no UI, no module dependencies).
