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

            await RunTest("Vessel ProjectContext DefaultsToNull", () =>
            {
                Vessel vessel = new Vessel();
                AssertNull(vessel.ProjectContext);
            });

            await RunTest("Vessel StyleGuide DefaultsToNull", () =>
            {
                Vessel vessel = new Vessel();
                AssertNull(vessel.StyleGuide);
            });

            await RunTest("Vessel ProjectContext SetAndGet", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ProjectContext = "A .NET 8 microservice with Redis caching.";
                AssertEqual("A .NET 8 microservice with Redis caching.", vessel.ProjectContext);
            });

            await RunTest("Vessel StyleGuide SetAndGet", () =>
            {
                Vessel vessel = new Vessel();
                vessel.StyleGuide = "Use async/await everywhere. No blocking calls.";
                AssertEqual("Use async/await everywhere. No blocking calls.", vessel.StyleGuide);
            });

            await RunTest("Vessel ProjectContext Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ProjectContext = "Some context";
                vessel.ProjectContext = null;
                AssertNull(vessel.ProjectContext);
            });

            await RunTest("Vessel StyleGuide Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.StyleGuide = "Some style";
                vessel.StyleGuide = null;
                AssertNull(vessel.StyleGuide);
            });

            await RunTest("Vessel Serialization RoundTrip WithProjectContextAndStyleGuide", () =>
            {
                Vessel vessel = new Vessel("ContextVessel", "https://github.com/test/repo");
                vessel.ProjectContext = "Multi-line\nproject context\nwith details.";
                vessel.StyleGuide = "Follow C# coding conventions.\nUse var when type is obvious.";

                string json = JsonSerializer.Serialize(vessel);
                Vessel deserialized = JsonSerializer.Deserialize<Vessel>(json)!;

                AssertEqual(vessel.Id, deserialized.Id);
                AssertEqual(vessel.Name, deserialized.Name);
                AssertEqual(vessel.ProjectContext, deserialized.ProjectContext);
                AssertEqual(vessel.StyleGuide, deserialized.StyleGuide);
            });

            await RunTest("Vessel Serialization RoundTrip WithNullProjectContextAndStyleGuide", () =>
            {
                Vessel vessel = new Vessel("NullContextVessel", "https://github.com/test/repo");

                string json = JsonSerializer.Serialize(vessel);
                Vessel deserialized = JsonSerializer.Deserialize<Vessel>(json)!;

                AssertNull(deserialized.ProjectContext);
                AssertNull(deserialized.StyleGuide);
            });

            await RunTest("Vessel Serialization JsonContainsProjectContextAndStyleGuide", () =>
            {
                Vessel vessel = new Vessel("JsonFieldVessel", "https://github.com/test/repo");
                vessel.ProjectContext = "test context";
                vessel.StyleGuide = "test style";

                string json = JsonSerializer.Serialize(vessel);
                AssertContains("ProjectContext", json);
                AssertContains("test context", json);
                AssertContains("StyleGuide", json);
                AssertContains("test style", json);
            });

            await RunTest("Vessel ReflectionFields DefaultToNull", () =>
            {
                Vessel vessel = new Vessel();
                AssertNull(vessel.LastReflectionMissionId, nameof(Vessel.LastReflectionMissionId));
                AssertNull(vessel.ReflectionThreshold, nameof(Vessel.ReflectionThreshold));
                AssertNull(vessel.ReorganizeThreshold, nameof(Vessel.ReorganizeThreshold));
            });

            await RunTest("Vessel LastReflectionMissionId SetAndGet", () =>
            {
                Vessel vessel = new Vessel();
                vessel.LastReflectionMissionId = "msn_reflect_abc123";
                AssertEqual("msn_reflect_abc123", vessel.LastReflectionMissionId);
            });

            await RunTest("Vessel LastReflectionMissionId Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.LastReflectionMissionId = "msn_reflect_temp";
                vessel.LastReflectionMissionId = null;
                AssertNull(vessel.LastReflectionMissionId);
            });

            await RunTest("Vessel ReflectionThreshold SetAndGet", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ReflectionThreshold = 42;
                AssertEqual(42, vessel.ReflectionThreshold!.Value);
            });

            await RunTest("Vessel ReflectionThreshold Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ReflectionThreshold = 99;
                vessel.ReflectionThreshold = null;
                AssertNull(vessel.ReflectionThreshold);
            });

            await RunTest("Vessel ReorganizeThreshold SetAndGet", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ReorganizeThreshold = 55;
                AssertEqual(55, vessel.ReorganizeThreshold!.Value);
            });

            await RunTest("Vessel ReorganizeThreshold Nullable", () =>
            {
                Vessel vessel = new Vessel();
                vessel.ReorganizeThreshold = 1;
                vessel.ReorganizeThreshold = null;
                AssertNull(vessel.ReorganizeThreshold);
            });

            await RunTest("Vessel Serialization RoundTrip WithReflectionFields", () =>
            {
                Vessel vessel = new Vessel("ReflectionVessel", "https://github.com/test/reflect");
                vessel.LastReflectionMissionId = "msn_reflect_roundtrip";
                vessel.ReflectionThreshold = 17;

                string json = JsonSerializer.Serialize(vessel);
                Vessel deserialized = JsonSerializer.Deserialize<Vessel>(json)!;

                AssertEqual(vessel.Id, deserialized.Id);
                AssertEqual("msn_reflect_roundtrip", deserialized.LastReflectionMissionId);
                AssertEqual(17, deserialized.ReflectionThreshold!.Value);
            });

            await RunTest("Vessel Serialization JsonContainsReflectionFields", () =>
            {
                Vessel vessel = new Vessel("ReflectionJsonVessel", "https://github.com/test/reflect-json");
                vessel.LastReflectionMissionId = "msn_reflect_present";
                vessel.ReflectionThreshold = 31;

                string json = JsonSerializer.Serialize(vessel);
                AssertContains("LastReflectionMissionId", json);
                AssertContains("msn_reflect_present", json);
                AssertContains("ReflectionThreshold", json);
                AssertContains("31", json);
            });

            await RunTest("Vessel Serialization RoundTrip WithReorganizeThreshold", () =>
            {
                Vessel vessel = new Vessel("ReorganizeSerialization", "https://github.com/test/reorganize-ser");
                vessel.ReorganizeThreshold = 73;

                string json = JsonSerializer.Serialize(vessel);
                Vessel deserialized = JsonSerializer.Deserialize<Vessel>(json)!;

                AssertEqual(vessel.Id, deserialized.Id);
                AssertEqual(73, deserialized.ReorganizeThreshold!.Value);
            });

            await RunTest("Vessel Serialization JsonContainsReorganizeThreshold", () =>
            {
                Vessel vessel = new Vessel("ReorganizeJsonVessel", "https://github.com/test/reorganize-json");
                vessel.ReorganizeThreshold = 62;

                string json = JsonSerializer.Serialize(vessel);
                AssertContains("ReorganizeThreshold", json);
                AssertContains("62", json);
            });
        }
    }
}
