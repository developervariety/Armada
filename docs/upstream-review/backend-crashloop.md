# Generic crash-loop protection

The fork already routes provider quota, credit, authentication, account spend
cap and safeguard failures through `CaptainQuarantineService`. Generic runtime
crashes now use the same service after repeated distinct process-exit failures
for one captain inside the configured `CrashLoopDetection.WindowMinutes`.

The tracker is in memory by design. A restart clears the recent crash counter,
while persisted quarantine state remains authoritative and is restored by the
existing quarantine sweep. A repeated process-exit notification with the same
process, captain and mission identity is ignored. Clean exits, interruption
and OOM exits, provider failures, test/build failures and Definition of Done
failures do not count as generic crash-loop evidence.

Each captain history is limited to the configured time window and 256 failure
identifiers. The configured threshold is capped at 256. The tracker retains at
most 1,024 recently active captain histories. Generation tokens prevent a
concurrent failure from being erased by a completed hold operation.

The process-exit tracker excludes the three structured DoD/test failure reason
prefixes and provider safeguard signals. It does not claim to classify every
possible test or build log.

Crash-loop holds use an atomic database predicate. An indefinite hold or a
later timed hold cannot be shortened. A busy captain cannot lose its work. The
manual quarantine operation keeps its existing overwrite behavior. The
operation records applied and refused holds through the quarantine service log.

`CaptainHealthMonitor` was removed. `AdmiralService` is the production crash
loop authority.
