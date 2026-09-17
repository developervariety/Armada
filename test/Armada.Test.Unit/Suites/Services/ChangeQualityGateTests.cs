namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the change_quality orchestrator gate: a diff with a routable (deterministically-backed)
    /// weakness files exactly one Triaged objective through the router; a clean diff files none; and the
    /// row is auto-dispatch OFF by construction of the router. Runs with the adapter absent to isolate the
    /// deterministic routing.
    /// </summary>
    public class ChangeQualityGateTests : TestSuite
    {
        public override string Name => "Change Quality Gate";

        protected override async Task RunTestsAsync()
        {
            await RunTest("A violating diff files exactly one Triaged row", async () =>
            {
                RecordingRouter router = new RecordingRouter();
                CancellationToken none = CancellationToken.None;
                // A project-wide NoWarn is a Slop FAIL -> a deterministic core_rule MustFix -> routable.
                string diff = "diff --git a/src/A.csproj b/src/A.csproj\n--- a/src/A.csproj\n+++ b/src/A.csproj\n@@ -1,1 +1,2 @@\n <Project>\n+  <PropertyGroup><NoWarn>CS1591</NoWarn></PropertyGroup>\n";

                ChangeQualityGateResult result = await ChangeQualityGate.ReviewAndRouteAsync(
                    diff, "vsl_x", "msn_reviewed", false, adapter: null, router: router, logging: null, token: none).ConfigureAwait(false);

                AssertEqual(1, router.CreateCount, "exactly one Triaged row is filed for a violating diff");
                AssertNotNull(result.CreatedObjectiveId);
                AssertEqual(1, result.Verdict.RoutableWeaknesses.Count);
                AssertEqual(ChangeQualityDimensions.CoreRule, result.Verdict.RoutableWeaknesses[0].Dimension);
                AssertEqual("vsl_x", router.LastRequest!.FollowUp.VesselId, "the row carries the vessel");
            });

            await RunTest("A clean diff files no row", async () =>
            {
                RecordingRouter router = new RecordingRouter();
                CancellationToken none = CancellationToken.None;
                string diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,2 @@\n public class A { }\n+// a harmless comment\n";

                ChangeQualityGateResult result = await ChangeQualityGate.ReviewAndRouteAsync(
                    diff, "vsl_x", "msn_reviewed", false, adapter: null, router: router, logging: null, token: none).ConfigureAwait(false);

                AssertEqual(0, router.CreateCount, "a clean diff files nothing");
                AssertNull(result.CreatedObjectiveId);
            });
        }

        private sealed class RecordingRouter : IFollowUpRouter
        {
            public int CreateCount { get; private set; }

            public FollowUpRouteRequest? LastRequest { get; private set; }

            public Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token)
            {
                CreateCount++;
                LastRequest = request;
                return Task.FromResult<string?>("obj_fake_" + CreateCount);
            }

            public Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
                => Task.FromResult<IReadOnlyList<FollowUpDuplicateCandidate>>(new List<FollowUpDuplicateCandidate>());
            public Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
            public Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token) => Task.CompletedTask;
            public Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
        }
    }
}
