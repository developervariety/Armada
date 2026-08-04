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
                ModelTierSettings s = new ModelTierSettings();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    AssertFalse(order.Contains("opencode/laguna-s-2.1-free"),
                        "Unproven free-tier model must not be in the mid preference order -- eligible, not preferred");
                }
                return Task.CompletedTask;
            });


            await RunTest("KnownTierMembership_Unchanged_ByLagunaAddition", () =>
            {
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-4-7"), "zyloo opus 4-7 stays mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna"), "zyloo gpt-5.6-luna stays mid");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7"), "opus-4-7 stays high");
                return Task.CompletedTask;
            });

            await RunTest("ChallengerPool_AllRoutable_AsMidTier", () =>
            {
                // grok-4.5 is BARE because it runs under the Cursor harness, which uses unqualified
                // model ids. A provider-qualified form would not match ContainsModel's exact compare.
                // The Zyloo Opus captains and GPT-5.6 Luna are the retained Zyloo mid-tier roster.
                string[] challengers =
                {
                    "grok-4.5", "zyloo/claude-opus-4-7", "zyloo/claude-opus-4-8", "zyloo/gpt-5.6-luna", "composer-2.5"
                };
                foreach (string m in challengers)
                {
                    AssertEqual("mid", PreferredModelTierSelector.ClassifyModel(m),
                        m + " must classify as mid tier or Armada will never assign it work");
                }
                return Task.CompletedTask;
            });

            await RunTest("MidPreferenceOrder_UsesConfiguredOrder", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
                AssertTrue(s.WithinTierPreferenceOrder.TryGetValue("mid", out var order), "mid order must exist");
                AssertEqual(5, order!.Count, "the mid preference order lists exactly the five current mid-tier models");
                foreach (string m in order)
                {
                    AssertEqual("mid", PreferredModelTierSelector.ClassifyModel(m),
                        "preference entry '" + m + "' must actually classify mid");
                }
                AssertEqual("zyloo/claude-opus-4-7", order[0], "configured mid-tier order starts with Zyloo Opus 4.7");
                AssertEqual("zyloo/claude-opus-4-8", order[1], "Zyloo Opus 4.8 follows the primary");
                AssertEqual("zyloo/gpt-5.6-luna", order[2], "Zyloo GPT 5.6 Luna follows the Opus block");
                AssertEqual("composer-2.5", order[3], "composer-2.5 is fourth");
                AssertEqual("grok-4.5", order[4], "grok-4.5 closes the list");
                return Task.CompletedTask;
            });

            await RunTest("NonPreferredModels_AreNotInPreferenceOrder", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
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
                ModelTierSettings s = new ModelTierSettings();
                foreach (string m in new[]
                {
                    "opencode-go/deepseek-v4-flash",
                    "zyloo/claude-opus-4-7", "zyloo/claude-opus-4-8", "zyloo/gpt-5.6-luna", "composer-2.5", "grok-4.5",
                    "zyloo/claude-fable-5", "zyloo/claude-opus-5", "zyloo/gpt-5.6-sol"
                })
                {
                    AssertTrue(s.ModelCapabilityProfiles.ContainsKey(m), m + " needs a capability profile");
                }
                return Task.CompletedTask;
            });

            await RunTest("ConfiguredModels_AllClassify", () =>
            {
                var expected = new (string Model, string Tier)[]
                {
                    ("opencode-go/deepseek-v4-flash", "low"),
                    ("zyloo/claude-opus-4-7", "mid"), ("zyloo/claude-opus-4-8", "mid"), ("zyloo/gpt-5.6-luna", "mid"),
                    ("composer-2.5", "mid"), ("grok-4.5", "mid"),
                    ("zyloo/claude-fable-5", "high"), ("zyloo/claude-opus-5", "high"), ("zyloo/gpt-5.6-sol", "high"),
                    ("claude-opus-4-7", "high"), ("claude-opus-4-8", "high"), ("claude-opus-5", "high"), ("claude-fable-5", "high")
                };
                foreach (var e in expected)
                {
                    AssertEqual(e.Tier, PreferredModelTierSelector.ClassifyModel(e.Model),
                        e.Model + " must classify " + e.Tier);
                }
                return Task.CompletedTask;
            });

            await RunTest("DeepEngineeringModels_RemainHighTier", () =>
            {
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol"), "zyloo/gpt-5.6-sol stays high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-5"), "fable-5 must resolve high (canonical fable pattern)");
                return Task.CompletedTask;
            });
        }
    }
}
