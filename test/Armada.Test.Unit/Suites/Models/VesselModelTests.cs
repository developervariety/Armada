namespace Armada.Test.Unit.Suites.Models
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class VesselModelTests : TestSuite
    {
        public override string Name => "Vessel Model";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Vessel DefaultConstructor GeneratesIdWithPrefix", () =>
            {
                Vessel vessel = new Vessel();
                AssertStartsWith(Constants.VesselIdPrefix, vessel.Id);
            });

            await RunTest("Vessel NameRepoConstructor SetsProperties", () =>
            {
                Vessel vessel = new Vessel("MyRepo", "https://github.com/user/repo");
                AssertEqual("MyRepo", vessel.Name);
                AssertEqual("https://github.com/user/repo", vessel.RepoUrl);
            });

            await RunTest("Vessel DefaultValues AreCorrect", () =>
            {
                Vessel vessel = new Vessel();
                AssertEqual("My Vessel", vessel.Name);
                AssertEqual("main", vessel.DefaultBranch);
                AssertTrue(vessel.Active);
                AssertNull(vessel.FleetId);
                AssertNull(vessel.LocalPath);
            });

            await RunTest("Vessel SetName Null Throws", () =>
            {
                Vessel vessel = new Vessel();
                AssertThrows<ArgumentNullException>(() => vessel.Name = null!);
            });

            await RunTest("Vessel SetRepoUrl Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.RepoUrl = "";
                AssertEqual("", vessel.RepoUrl);
                vessel.RepoUrl = null;
                AssertNull(vessel.RepoUrl);
            });

            await RunTest("Vessel Serialization RoundTrip", () =>
            {
                Vessel vessel = new Vessel("TestVessel", "https://github.com/test/repo");
                vessel.FleetId = "flt_test";
                vessel.DefaultBranch = "develop";

                string json = JsonSerializer.Serialize(vessel);
                Vessel deserialized = JsonSerializer.Deserialize<Vessel>(json)!;

                AssertEqual(vessel.Id, deserialized.Id);
                AssertEqual(vessel.Name, deserialized.Name);
                AssertEqual(vessel.RepoUrl, deserialized.RepoUrl);
                AssertEqual(vessel.FleetId, deserialized.FleetId);
                AssertEqual(vessel.DefaultBranch, deserialized.DefaultBranch);
            });

            await RunTest("Vessel UniqueIds AcrossInstances", () =>
            {
                Vessel v1 = new Vessel();
                Vessel v2 = new Vessel();
                AssertNotEqual(v1.Id, v2.Id);
            });
        }
    }
}
