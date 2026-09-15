namespace Armada.Test.Unit.Suites.Settings
{
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

    /// <summary>
    /// Covers two related fleet-management gaps:
    /// (a) armada_update_vessel must be able to repoint Vessel.LocalPath -- without it a renamed or
    ///     relocated bare repo leaves DockService resolving the stale path and re-cloning into it;
    /// (b) every captain-backed model classifies into a tier, and an eligible-but-unproven model is
    ///     never promoted into the within-tier preference order. A model that classifies to no tier
    ///     can only be reached by an exact literal pin, so tier routing silently skips it.
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
                    "{\"vesselId\":\"vsl_x\",\"localPath\":\"E:/armada/repos/ExampleVessel.git\"}", opts)!;
                AssertEqual("vsl_x", args.VesselId, "vesselId should round-trip");
                AssertEqual("E:/armada/repos/ExampleVessel.git", args.LocalPath, "localPath should deserialize");
                return Task.CompletedTask;
            });

            await RunTest("VesselUpdateArgs_LocalPath_OmittedStaysNull", () =>
            {
                VesselUpdateArgs args = JsonSerializer.Deserialize<VesselUpdateArgs>(
                    "{\"vesselId\":\"vsl_x\",\"name\":\"ExampleVessel\"}", opts)!;
                AssertNull(args.LocalPath, "Omitted localPath must stay null so the handler leaves it unchanged");
                return Task.CompletedTask;
            });

            await RunTest("VesselUpdateArgs_LocalPath_IndependentOfWorkingDirectory", () =>
            {
                VesselUpdateArgs args = JsonSerializer.Deserialize<VesselUpdateArgs>(
                    "{\"vesselId\":\"vsl_x\",\"workingDirectory\":\"E:/project/Tools/ExampleVessel\",\"localPath\":\"E:/armada/repos/ExampleVessel.git\"}",
                    opts)!;
                AssertEqual("E:/project/Tools/ExampleVessel", args.WorkingDirectory, "workingDirectory should be independent");
                AssertEqual("E:/armada/repos/ExampleVessel.git", args.LocalPath, "localPath should be independent");
                return Task.CompletedTask;
            });



            await RunTest("Laguna_NotPromotedIntoWithinTierPreferenceOrder", () =>
            {
                ModelTierSettings s = FleetRoutingSettings.CreateModelTier();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    AssertFalse(order.Contains("opencode/laguna-s-2.1-free"),
                        "Unproven free-tier model must not be in the mid preference order -- eligible, not preferred");
                }
                return Task.CompletedTask;
            });


            await RunTest("KnownTierMembership_Unchanged_ByLagunaAddition", () =>
            {
                ModelTierSettings fleet = FleetRoutingSettings.CreateModelTier();
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("gpt-5.6-luna", fleet), "gpt-5.6-luna stays mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("example/mid-audit", fleet), "example/mid-audit stays mid");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7", fleet), "opus-4-7 stays high");
                return Task.CompletedTask;
            });

            await RunTest("ChallengerPool_AllRoutable_AsMidTier", () =>
            {
                // The mid-tier roster is luna (native), deepseek (opencode-go), and example/mid-audit.
                ModelTierSettings fleet = FleetRoutingSettings.CreateModelTier();
                string[] challengers =
                {
                    "gpt-5.6-luna", "opencode-go/deepseek-v4-flash", "example/mid-audit"
                };
                foreach (string m in challengers)
                {
                    AssertEqual("mid", PreferredModelTierSelector.ClassifyModel(m, fleet),
                        m + " must classify as mid tier or Armada will never assign it work");
                }
                return Task.CompletedTask;
            });

            await RunTest("MidPreferenceOrder_IsEmpty_AllWorkerModelsEqual", () =>
            {
                ModelTierSettings s = FleetRoutingSettings.CreateModelTier();
                AssertTrue(s.WithinTierPreferenceOrder.TryGetValue("mid", out var order), "mid order must exist");
                AssertEqual(0, order!.Count, "all worker models are equal: the mid preference order is empty");
                return Task.CompletedTask;
            });

            await RunTest("NonPreferredModels_AreNotInPreferenceOrder", () =>
            {
                ModelTierSettings s = FleetRoutingSettings.CreateModelTier();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    foreach (string m in new[]
                    {
                        "gemini-4.0-pro", "composer-3"
                    })
                    {
                        AssertFalse(order.Contains(m),
                            m + " is eligible but is not part of the configured preference order");
                    }
                }
                return Task.CompletedTask;
            });

            await RunTest("ChallengerPool_HasCapabilityProfiles", () =>
            {
                ModelTierSettings s = FleetRoutingSettings.CreateModelTier();
                foreach (string m in new[]
                {
                    "opencode-go/deepseek-v4-flash",
                    "gpt-5.6-luna", "opencode-go/deepseek-v4-flash", "example/mid-audit",
                    "claude-fable-5", "claude-opus-5", "gpt-5.6-sol"
                })
                {
                    AssertTrue(s.ModelCapabilityProfiles.ContainsKey(m), m + " needs a capability profile");
                }
                return Task.CompletedTask;
            });

            await RunTest("ConfiguredModels_AllClassify", () =>
            {
                ModelTierSettings fleet = FleetRoutingSettings.CreateModelTier();
                string[] models = new string[]
                {
                    "gpt-5.6-luna",
                    "opencode-go/deepseek-v4-flash", "example/mid-audit",
                    "claude-fable-5",
                    "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5",
                    "gpt-5.6-sol"
                };
                string[] tiers = new string[]
                {
                    "mid", "mid", "mid", "high", "high", "high", "high", "high"
                };
                for (int i = 0; i < models.Length; i++)
                {
                    AssertEqual(tiers[i], PreferredModelTierSelector.ClassifyModel(models[i], fleet),
                        models[i] + " must classify " + tiers[i]);
                }
                return Task.CompletedTask;
            });

            await RunTest("DeepEngineeringModels_RemainHighTier", () =>
            {
                ModelTierSettings fleet = FleetRoutingSettings.CreateModelTier();
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.6-sol", fleet), "gpt-5.6-sol stays high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-5", fleet), "fable-5 must resolve high (canonical fable pattern)");
                return Task.CompletedTask;
            });
        }
    }
}
