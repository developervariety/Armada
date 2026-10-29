namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="HarborRouter"/>: dock-affinity pinning (including the offline-owner stall),
    /// capability filtering, capacity limits, preferred-Harbor honoring, and least-loaded selection. Pure,
    /// deterministic selection logic; positive cases assert the chosen Harbor, negative cases assert the
    /// typed no-eligible reasons.
    /// </summary>
    public sealed class HarborRouterSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the HarborRouter suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("affinity_pins_to_owning_harbor", "Dock affinity pins to the owning Harbor", TestTags.Positive, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude"), Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { ExistingHarborId = "hbr_b", RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, Zero, request);
                AssertTrue(decision.Success, "Expected an affinity pin to succeed.");
                AssertEqual("hbr_b", decision.HarborId);
                AssertTrue(decision.PinnedByAffinity, "Expected the decision to be pinned by affinity.");
            }));

            cases.Add(Case("affinity_offline_owner_stalls", "Dock affinity stalls when the owner is offline", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude"), Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { ExistingHarborId = "hbr_b" };

                // hbr_b is registered but not connected.
                HarborRoutingDecision decision = router.Select(harbors, id => id == "hbr_a", Zero, request);
                AssertTrue(!decision.Success, "Expected the stall to fail selection.");
                AssertTrue(decision.Reason != null && decision.Reason.Contains("hbr_b"), "Expected the reason to name the offline owner.");
            }));

            cases.Add(Case("affinity_owner_without_runtime_is_refused", "Dock affinity does not pin a launch to a Harbor that lacks the captain's runtime", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude"), Cap("hbr_b", "B", 4, "codex") };
                HarborRoutingRequest request = new HarborRoutingRequest { ExistingHarborId = "hbr_b", RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, Zero, request);
                AssertFalse(decision.Success, "an incapable owner is not chosen");
                AssertContains("hbr_b", decision.Reason ?? String.Empty);
                AssertContains("claude", decision.Reason ?? String.Empty, "the reason names the missing runtime");
            }));

            cases.Add(Case("affinity_disabled_owner_is_refused", "Dock affinity does not pin a launch to a disabled Harbor", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                Harbor disabled = Cap("hbr_b", "B", 4, "claude");
                disabled.Enabled = false;
                HarborRoutingDecision decision = router.Select(new List<Harbor> { disabled }, All, Zero, new HarborRoutingRequest { ExistingHarborId = "hbr_b", RequestedRuntime = "claude" });
                AssertFalse(decision.Success);
                AssertContains("disabled", decision.Reason ?? String.Empty);
            }));

            cases.Add(Case("capability_filter_excludes_incapable", "Capability filter excludes Harbors lacking the runtime", TestTags.Positive, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "codex"), Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, Zero, request);
                AssertTrue(decision.Success, "Expected a capable Harbor to be selected.");
                AssertEqual("hbr_b", decision.HarborId);
            }));

            cases.Add(Case("preferred_harbor_honored_when_eligible", "Preferred Harbor is honored when eligible", TestTags.Positive, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude"), Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude", PreferredHarborId = "hbr_a" };

                // hbr_a is busier, but preference wins among eligible.
                HarborRoutingDecision decision = router.Select(harbors, All, id => id == "hbr_a" ? 2 : 0, request);
                AssertTrue(decision.Success, "Expected selection to succeed.");
                AssertEqual("hbr_a", decision.HarborId);
            }));

            cases.Add(Case("least_loaded_selected", "Least-loaded eligible Harbor is selected", TestTags.Positive, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude"), Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, id => id == "hbr_a" ? 3 : 1, request);
                AssertTrue(decision.Success, "Expected selection to succeed.");
                AssertEqual("hbr_b", decision.HarborId);
            }));

            cases.Add(Case("capacity_full_reports_reason", "All-at-capacity reports a clear reason", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 2, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, _ => 2, request);
                AssertTrue(!decision.Success, "Expected capacity-full to fail selection.");
                AssertTrue(decision.Reason != null && decision.Reason.Contains("capacity"), "Expected a capacity reason.");
                AssertTrue(decision.Reason!.Contains("A (hbr_a): 2/2 slots in use"), "Name the full Harbor and its slot usage.");
                AssertTrue(decision.Reason.Contains("MaxConcurrentJobs"), "Explain how to increase capacity.");
                AssertTrue(decision.Reason.Contains("retry interactive requests"), "Interactive requests are not automatically queued.");
                AssertTrue(decision.CapableHarborsAtCapacity, "capacity, not capability, is what blocks it, so the work should wait");

                HarborRoutingDecision incapable = router.Select(new List<Harbor> { Cap("hbr_b", "B", 2, "codex") }, All, _ => 0, request);
                AssertFalse(incapable.CapableHarborsAtCapacity, "a Harbor without the runtime is not a capacity wait");
            }));

            cases.Add(Case("missing_capability_named_in_reason", "The no-eligible reason names the capability that is missing, not the runtime the Harbor has", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "ApiEndpoint") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "ApiEndpoint", RequiredCapabilities = new List<string> { "api-inference-context-v1" } };

                HarborRoutingDecision decision = router.Select(harbors, All, Zero, request);
                AssertFalse(decision.Success);
                AssertContains("api-inference-context-v1", decision.Reason ?? String.Empty);
                AssertContains("update the Harbor", decision.Reason ?? String.Empty);
                AssertFalse((decision.Reason ?? String.Empty).Contains("ApiEndpoint"), "the runtime is present and must not be blamed");
            }));

            cases.Add(Case("no_connected_reports_reason", "No connected Harbor reports a clear reason", TestTags.Negative, () =>
            {
                HarborRouter router = new HarborRouter();
                List<Harbor> harbors = new List<Harbor> { Cap("hbr_a", "A", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, _ => false, Zero, request);
                AssertTrue(!decision.Success, "Expected no-connected to fail selection.");
                AssertTrue(decision.Reason != null && decision.Reason.Contains("connected"), "Expected a connectivity reason.");
            }));

            cases.Add(Case("disabled_harbor_excluded", "Disabled Harbor is excluded from routing", TestTags.Positive, () =>
            {
                HarborRouter router = new HarborRouter();
                Harbor disabled = Cap("hbr_a", "A", 4, "claude");
                disabled.Enabled = false;
                List<Harbor> harbors = new List<Harbor> { disabled, Cap("hbr_b", "B", 4, "claude") };
                HarborRoutingRequest request = new HarborRoutingRequest { RequestedRuntime = "claude" };

                HarborRoutingDecision decision = router.Select(harbors, All, Zero, request);
                AssertTrue(decision.Success, "Expected the enabled Harbor to be selected.");
                AssertEqual("hbr_b", decision.HarborId);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.HarborRouter",
                displayName: "Harbor Router",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Harbor Cap(string id, string name, int capacity, string runtime)
        {
            Harbor harbor = new Harbor { Id = id, Name = name, MaxConcurrentJobs = capacity, Enabled = true };
            harbor.Capabilities = new List<HarborCapability> { new HarborCapability { Name = runtime, Available = true } };
            return harbor;
        }

        private static bool All(string id) => true;

        private static int Zero(string id) => 0;

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.HarborRouter",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
