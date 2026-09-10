namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the tenant-local, transitive vessel lane map.
    /// </summary>
    public class VesselLaneMapTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Vessel Lane Map";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("One-way name and identifier references form a transitive lane", () =>
            {
                Vessel alpha = CreateVessel("vsl_alpha", "Alpha", "tenant-a");
                Vessel beta = CreateVessel("vsl_beta", "Beta", "tenant-a");
                Vessel gamma = CreateVessel("vsl_gamma", "Gamma", "tenant-a");
                alpha.SiblingRepos = SerializeSibling("Beta", true);
                beta.SiblingRepos = SerializeSibling(gamma.Id, true);

                VesselLaneMap map = VesselLaneMap.Build(new[] { alpha, beta, gamma });

                AssertMembers(map, alpha.Id, alpha.Id, beta.Id, gamma.Id);
                AssertMembers(map, beta.Id, alpha.Id, beta.Id, gamma.Id);
                AssertMembers(map, gamma.Id, alpha.Id, beta.Id, gamma.Id);
            }).ConfigureAwait(false);

            await RunTest("Read-only sibling references do not join lanes", () =>
            {
                Vessel consumer = CreateVessel("vsl_consumer", "Consumer", "tenant-a");
                Vessel source = CreateVessel("vsl_source", "Source", "tenant-a");
                consumer.SiblingRepos = SerializeSibling(source.Id, false);

                VesselLaneMap map = VesselLaneMap.Build(new[] { consumer, source });

                AssertMembers(map, consumer.Id, consumer.Id);
                AssertMembers(map, source.Id, source.Id);
            }).ConfigureAwait(false);

            await RunTest("Same-name vessels in other tenants cannot join the lane", () =>
            {
                Vessel consumerA = CreateVessel("vsl_consumer_a", "Consumer", "tenant-a");
                Vessel sharedA = CreateVessel("vsl_shared_a", "Shared", "tenant-a");
                Vessel sharedB = CreateVessel("vsl_shared_b", "Shared", "tenant-b");
                consumerA.SiblingRepos = SerializeSibling("Shared", true);

                VesselLaneMap map = VesselLaneMap.Build(new[] { sharedB, consumerA, sharedA });

                AssertMembers(map, consumerA.Id, consumerA.Id, sharedA.Id);
                AssertMembers(map, sharedA.Id, consumerA.Id, sharedA.Id);
                AssertMembers(map, sharedB.Id, sharedB.Id);
            }).ConfigureAwait(false);

            await RunTest("Lane membership is stable across input order and every member lookup", () =>
            {
                Vessel alpha = CreateVessel("vsl_alpha", "Alpha", "tenant-a");
                Vessel beta = CreateVessel("vsl_beta", "Beta", "tenant-a");
                Vessel gamma = CreateVessel("vsl_gamma", "Gamma", "tenant-a");
                alpha.SiblingRepos = SerializeSibling(beta.Id, true);
                gamma.SiblingRepos = SerializeSibling(beta.Id, true);

                VesselLaneMap forward = VesselLaneMap.Build(new[] { alpha, beta, gamma });
                VesselLaneMap reverse = VesselLaneMap.Build(new[] { gamma, beta, alpha });
                string expected = JoinMembers(alpha.Id, beta.Id, gamma.Id);

                AssertEqual(expected, JoinMembers(forward.MembersFor(alpha.Id)), "forward membership");
                AssertEqual(expected, JoinMembers(forward.MembersFor(gamma.Id)), "same-lane lookup");
                AssertEqual(expected, JoinMembers(reverse.MembersFor(beta.Id)), "reverse input membership");
                AssertEqual("vsl_unknown", JoinMembers(reverse.MembersFor("vsl_unknown")), "unknown vessel membership");
            }).ConfigureAwait(false);
        }

        private static Vessel CreateVessel(string id, string name, string tenantId)
        {
            Vessel vessel = new Vessel(name, "https://example.test/" + name + ".git");
            vessel.Id = id;
            vessel.TenantId = tenantId;
            return vessel;
        }

        private static string SerializeSibling(string vesselRef, bool buildParticipant)
        {
            List<SiblingRepo> siblings = new List<SiblingRepo>
            {
                new SiblingRepo
                {
                    VesselRef = vesselRef,
                    RelativePath = "../Sibling",
                    BuildParticipant = buildParticipant
                }
            };
            return JsonSerializer.Serialize(siblings);
        }

        private void AssertMembers(VesselLaneMap map, string vesselId, params string[] expected)
        {
            AssertEqual(JoinMembers(expected), JoinMembers(map.MembersFor(vesselId)), "lane members for " + vesselId);
        }

        private static string JoinMembers(IEnumerable<string> members)
        {
            return String.Join(",", members.OrderBy(item => item, StringComparer.Ordinal));
        }

        private static string JoinMembers(params string[] members)
        {
            return JoinMembers((IEnumerable<string>)members);
        }
    }
}
