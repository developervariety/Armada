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

            await RunTest("MidPreferenceOrder_LeadsWithCurrentGenerationIds", () =>
            {
                // Regression guard for the real defect: the order previously listed ONLY
                // prior-generation ids (kimi-k2.7-code, claude-sonnet-4-6, composer-2.5), none of
                // which a live captain carries, so preference steered nothing in production.
                // Prior generations are still listed as generation fallbacks -- what matters is that
                // the CURRENT-generation id leads each family, and that every entry classifies mid.
                ModelTierSettings s = new ModelTierSettings();
                AssertTrue(s.WithinTierPreferenceOrder.TryGetValue("mid", out var order), "mid order must exist");
                foreach (string m in order!)
                {
                    AssertEqual("mid", PreferredModelTierSelector.ClassifyModel(m),
                        "preference entry '" + m + "' must actually classify mid");
                }
                AssertEqual("opencode-go/kimi-k3", order[0], "Kimi K3 is the owner-designated primary mid-tier model");
                // Each current-generation id must precede its own prior generation.
                AssertTrue(order.IndexOf("opencode-go/kimi-k3") < order.IndexOf("opencode-go/kimi-k2.7-code"),
                    "current-gen kimi must precede the prior generation");
                AssertTrue(order.IndexOf("claude-sonnet-5") < order.IndexOf("claude-sonnet-4-6"),
                    "current-gen sonnet must precede the prior generation");
                AssertTrue(order.IndexOf("composer-2-fast") < order.IndexOf("composer-2.5"),
                    "current-gen composer must precede the prior generation");
                return Task.CompletedTask;
            });

            await RunTest("UnmeasuredChallengers_NotPromoted", () =>
            {
                // Kimi K3 is intentionally excluded from this list: it was promoted by explicit owner
                // decision. These remain eligible-but-not-preferred until the bake-off measures them.
                ModelTierSettings s = new ModelTierSettings();
                if (s.WithinTierPreferenceOrder.TryGetValue("mid", out var order))
                {
                    foreach (string m in new[]
                    {
                        "grok-4.5", "opencode/glm-5.2", "opencode-go/glm-5.2", "opencode/laguna-s-2.1-free"
                    })
                    {
                        AssertFalse(order.Contains(m),
                            m + " is unmeasured -- it must be eligible but NOT first-choice");
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

            await RunTest("LiveCaptainModels_AllClassify", () =>
            {
                // Every model string a real captain carries must resolve to a tier, or that captain
                // is unroutable (this is exactly how the four kimi-k3 captains went dormant).
                var expected = new (string Model, string Tier)[]
                {
                    ("claude-opus-4-8", "high"), ("claude-fable-5", "high"), ("gpt-5.6-sol", "high"),
                    ("claude-sonnet-5", "mid"), ("composer-2-fast", "mid"),
                    ("opencode-go/kimi-k3", "mid"), ("opencode/laguna-s-2.1-free", "mid"),
                    ("opencode/glm-5.2", "mid"), ("grok-4.5", "mid")
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
                // The owner's "deep engineering" picks must stay high tier -- these back the Judge and
                // specialist personas, which are the safety net against fabricated/silenced results.
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.6-sol"), "gpt-5.6-sol stays high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-5"), "fable-5 must resolve high (canonical fable pattern)");
                return Task.CompletedTask;
            });
        }
    }
}
