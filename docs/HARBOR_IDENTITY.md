# Harbor identity and session core

The Harbor session core is a disabled-by-default building block for a future
transport. It does not inspect HTTP headers, trust a tenant header, open a
socket, or launch a process.

The normal authentication service must first produce a verified `AuthContext`.
Enabled use also requires an injected `IHarborRunnerOwnerResolver` backed by
durable runner enrollment. The resolver owns the runner to principal binding;
the live session registry cannot create or transfer that binding. Registration
accepts the context only when it is authenticated, contains a tenant, user, and
authentication method, and matches the enrolled owner. Unknown runners and
different users, tenants, authentication methods, or credentials are rejected,
including after a prior session disconnects.

Each accepted registration receives a new `HarborRunnerSession` generation. A
stale session cannot disconnect its replacement. A transport must keep the
session lease and present it for every request, response, and disconnect.

Typed pending responses are registered with `TryRegisterPending<T>`. The
registry issues each correlation identifier from the connection generation and
a monotonic sequence. The registry records the runner and generation with the request. A response is
accepted only when its session object and generation are current. Repeated
correlation identifiers, responses after completion, and responses from an old
generation are rejected. Reconnecting cancels old pending tasks. The bounded
replay cache is only an additional duplicate filter; identifier non-reuse does
not depend on retaining an unbounded history.

Enable the registry only from an explicit future Harbor transport configuration:

```csharp
HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(
    enabled: true,
    ownerResolver: durableRunnerOwnerResolver);
bool accepted = registry.TryRegister(runnerId, verifiedAuth, out HarborRunnerSession? session, out string reason);
```

The transport integration must continue to use the application's verified
authentication service and must not replace it with a non-empty access-key or
caller-supplied tenant header check.
