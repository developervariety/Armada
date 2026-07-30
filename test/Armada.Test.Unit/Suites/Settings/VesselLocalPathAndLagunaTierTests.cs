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

            await RunTest("ChallengerPool_AllRoutable_AsMidTier", () =>
            {
                // grok-4.5 is BARE because it runs under the Cursor harness, which uses unqualified
                // model ids. A provider-qualified form would not match ContainsModel's exact compare.
                string[] challengers =
                {
                    "grok-4.5", "opencode-go/kimi-k3", "opencode/glm-5.2", "opencode-go/glm-5.2"
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
                foreach (string m in order!)
                {
                    AssertEqual("mid", PreferredModelTierSelector.ClassifyModel(m),
                        "preference entry '" + m + "' must actually classify mid");
                }
                AssertEqual("zyloo/glm-5.2", order[0], "configured mid-tier order starts with Zyloo GLM");
                AssertEqual("opencode-go/kimi-k3", order[1], "kimi-k3 ranks ahead of the OpenCode GLM entry");
                AssertEqual("opencode-go/glm-5.2", order[2], "OpenCode GLM follows kimi-k3");
                AssertTrue(order.IndexOf("opencode-go/kimi-k3") < order.IndexOf("opencode-go/kimi-k2.7-code"),
                    "current-gen kimi must precede the prior generation");
                AssertTrue(order.IndexOf("zyloo/claude-sonnet-5") < order.IndexOf("claude-sonnet-4-6"),
                    "the ranked sonnet must precede the prior generation");
                AssertTrue(order.IndexOf("composer-2.5") < order.IndexOf("composer-2-fast"),
                    "the ranked composer must precede the unranked one");
                return Task.CompletedTask;
            });

            await RunTest("NonPreferredModels_AreNotInPreferenceOrder", () =>
            {
                ModelTierSettings s = new ModelTierSettings();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    foreach (string m in new[]
                    {
                        "opencode/laguna-s-2.1-free", "gemini-3.5-pro", "gpt-5.3-codex"
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
                    "grok-4.5", "opencode-go/kimi-k3", "opencode/glm-5.2", "opencode-go/glm-5.2"
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
                    ("claude-opus-4-8", "high"), ("claude-fable-5", "high"), ("gpt-5.6-sol", "high"),
                    ("claude-sonnet-5", "mid"), ("composer-2-fast", "mid"),
                    ("opencode-go/kimi-k3", "mid"), ("opencode/laguna-s-2.1-free", "mid"),
                    ("opencode/glm-5.2", "mid"), ("zyloo/glm-5.2", "mid"), ("grok-4.5", "mid")
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
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.6-sol"), "gpt-5.6-sol stays high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-5"), "fable-5 must resolve high (canonical fable pattern)");
                return Task.CompletedTask;
            });
        }
    }
}
