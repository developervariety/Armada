namespace Armada.Test.Unit.Suites.Settings
{
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Covers two related fleet-management gaps:
    /// (a) armada_update_vessel must be able to repoint Vessel.LocalPath -- without it a renamed or
    ///     relocated bare repo leaves DockService resolving the stale path and re-cloning into it;
    /// (b) opencode/laguna-s-2.1-free must classify as mid tier, but must NOT be promoted into the
    ///     within-tier preference order while it is unproven.
    /// </summary>
    public class VesselLocalPathAndLagunaTierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "VesselLocalPathAndLagunaTier";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            JsonSerializerOptions opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            await RunTest("VesselUpdateArgs_LocalPath_Deserializes", () =>
            {
                VesselUpdateArgs args = JsonSerializer.Deserialize<VesselUpdateArgs>(
                    "{\"vesselId\":\"vsl_x\",\"localPath\":\"E:/armada/repos/BenchSim.git\"}", opts)!;
                AssertEqual("vsl_x", args.VesselId, "vesselId should round-trip");
                AssertEqual("E:/armada/repos/BenchSim.git", args.LocalPath, "localPath should deserialize");
                return Task.CompletedTask;
            });

            await RunTest("VesselUpdateArgs_LocalPath_OmittedStaysNull", () =>
            {
                VesselUpdateArgs args = JsonSerializer.Deserialize<VesselUpdateArgs>(
                    "{\"vesselId\":\"vsl_x\",\"name\":\"BenchSim\"}", opts)!;
                AssertNull(args.LocalPath, "Omitted localPath must stay null so the handler leaves it unchanged");
                return Task.CompletedTask;
            });

            await RunTest("VesselUpdateArgs_LocalPath_IndependentOfWorkingDirectory", () =>
            {
                VesselUpdateArgs args = JsonSerializer.Deserialize<VesselUpdateArgs>(
                    "{\"vesselId\":\"vsl_x\",\"workingDirectory\":\"E:/project/Tools/BenchSim\",\"localPath\":\"E:/armada/repos/BenchSim.git\"}",
                    opts)!;
                AssertEqual("E:/project/Tools/BenchSim", args.WorkingDirectory, "workingDirectory should be independent");
                AssertEqual("E:/armada/repos/BenchSim.git", args.LocalPath, "localPath should be independent");
                return Task.CompletedTask;
            });

            await RunTest("Laguna_ClassifiesAs_MidTier", () =>
            {
                string? tier = PreferredModelTierSelector.ClassifyModel("opencode/laguna-s-2.1-free");
                AssertEqual("mid", tier, "laguna-s-2.1-free should be recognized as mid tier");
                return Task.CompletedTask;
            });

            await RunTest("Laguna_IsInMidTierMembership", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
                AssertTrue(s.MidTierModels.Contains("opencode/laguna-s-2.1-free"),
                    "laguna should be a member of MidTierModels");
                AssertFalse(s.HighTierModels.Contains("opencode/laguna-s-2.1-free"),
                    "laguna must not be high tier");
                AssertFalse(s.LowTierModels.Contains("opencode/laguna-s-2.1-free"),
                    "laguna must not be low tier");
                return Task.CompletedTask;
            });

            await RunTest("Laguna_NotPromotedIntoWithinTierPreferenceOrder", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    AssertFalse(order.Contains("opencode/laguna-s-2.1-free"),
                        "Unproven free-tier model must not be in the mid preference order -- eligible, not preferred");
                }
                return Task.CompletedTask;
            });

            await RunTest("Laguna_HasCapabilityProfile", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
                AssertTrue(s.ModelCapabilityProfiles.ContainsKey("opencode/laguna-s-2.1-free"),
                    "laguna needs a capability profile for within-tier capability-hint routing");
                return Task.CompletedTask;
            });

            await RunTest("KnownTierMembership_Unchanged_ByLagunaAddition", () =>
            {
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("claude-sonnet-4-6"), "sonnet-4-6 stays mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("opencode-go/kimi-k2.7-code"), "kimi-k2.7-code stays mid");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7"), "opus-4-7 stays high");
                return Task.CompletedTask;
            });
        }
    }
}
