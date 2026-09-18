namespace Armada.Test.Unit.Suites.Routes
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// A captain must never be seated in a persona its runtime cannot serve. The API-endpoint runtime
    /// provides file tools only, so a Judge on it reviews without reading the diff or running the gate and
    /// still votes.
    ///
    /// The rule holds in two places and they must agree. ELIGIBILITY keeps such a captain out of selection,
    /// so the dispatcher picks a capable one instead; the LAUNCH refusal is the backstop for a captain
    /// pinned by hand or by a captain override, which reaches launch without passing eligibility. If
    /// eligibility were the more permissive of the two, a mission would be assigned to a captain that then
    /// refuses it, and "pick another captain" becomes a failed mission, an incident and a rescue.
    /// </summary>
    public sealed class ApiEndpointPersonaAdmissionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Api Endpoint Persona Admission";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("An API-endpoint captain is refused for a persona that must run commands", () =>
            {
                Captain captain = new Captain("api-judge");
                captain.Runtime = AgentRuntimeEnum.ApiEndpoint;

                foreach (string persona in new[]
                {
                    PersonaCatalog.Judge,
                    PersonaCatalog.Worker,
                    PersonaCatalog.TestEngineer,
                    PersonaCatalog.LegacyTestEngineer,
                    PersonaCatalog.Linter
                })
                {
                    string? error = AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, persona);
                    AssertNotNull(error, "the " + persona + " persona must be refused on this runtime");
                    AssertContains("file tools only", error!, "the refusal names the missing capability");
                }
            });

            await RunTest("The same personas are admitted on a CLI-harness captain", () =>
            {
                // The refusal is about the runtime's capability, never about the persona itself.
                foreach (AgentRuntimeEnum runtime in new[]
                {
                    AgentRuntimeEnum.ClaudeCode,
                    AgentRuntimeEnum.Codex,
                    AgentRuntimeEnum.OpenCode,
                    AgentRuntimeEnum.Cursor
                })
                {
                    Captain captain = new Captain("cli-judge");
                    captain.Runtime = runtime;
                    AssertNull(AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, PersonaCatalog.Judge),
                        runtime + " serves the Judge persona and must not be refused");
                }
            });

            await RunTest("An API-endpoint captain still serves the analysis personas", () =>
            {
                // Reading and writing files is the whole of their work, so the gap does not touch them.
                Captain captain = new Captain("api-analyst");
                captain.Runtime = AgentRuntimeEnum.ApiEndpoint;

                foreach (string persona in new[]
                {
                    PersonaCatalog.Architect,
                    PersonaCatalog.ProductManager,
                    PersonaCatalog.Recorder,
                    PersonaCatalog.PriorArtAnalyst
                })
                {
                    AssertNull(AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, persona),
                        persona + " needs no command execution and must still be admitted");
                }

                AssertNull(AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, null),
                    "a mission with no persona is not refused by this guard");
            });

            await RunTest("An API-endpoint captain is not ELIGIBLE for a persona that must run commands", () =>
            {
                // The guard has to bite at assignment, not only at launch. Refusing only at launch means
                // the dispatcher picks this captain, the launch throws, and the mission fails with an
                // incident and a rescue -- instead of the dispatcher simply choosing a capable captain.
                Captain apiJudge = new Captain("api-judge");
                apiJudge.Runtime = AgentRuntimeEnum.ApiEndpoint;
                apiJudge.AllowedPersonas = "[\"Judge\"]";
                AssertFalse(MissionService.CaptainAllowsPersona(apiJudge, PersonaCatalog.Judge),
                    "an allow-list naming Judge cannot make an API-endpoint captain eligible for it");

                // An empty allow-list means "any persona", which must still not defeat capability.
                Captain apiAny = new Captain("api-any");
                apiAny.Runtime = AgentRuntimeEnum.ApiEndpoint;
                apiAny.AllowedPersonas = null;
                AssertFalse(MissionService.CaptainAllowsPersona(apiAny, PersonaCatalog.Worker),
                    "an unrestricted API-endpoint captain is still not eligible for Worker");
                AssertTrue(MissionService.CaptainAllowsPersona(apiAny, PersonaCatalog.Architect),
                    "an unrestricted API-endpoint captain is still eligible for Architect");

                Captain cliJudge = new Captain("cli-judge");
                cliJudge.Runtime = AgentRuntimeEnum.ClaudeCode;
                cliJudge.AllowedPersonas = "[\"Judge\"]";
                AssertTrue(MissionService.CaptainAllowsPersona(cliJudge, PersonaCatalog.Judge),
                    "the same allow-list on a CLI-harness captain stays eligible");
            });

            await RunTest("Eligibility and the launch guard agree over every runtime and persona", () =>
            {
                // Compared across the whole input domain rather than asserted separately in two places:
                // if eligibility is ever more permissive than the launch guard, a mission is assigned to a
                // captain that refuses it. This is the drift that case would catch.
                string[] personas = new[]
                {
                    PersonaCatalog.Worker, PersonaCatalog.TestEngineer, PersonaCatalog.LegacyTestEngineer,
                    PersonaCatalog.Judge, PersonaCatalog.Linter, PersonaCatalog.Architect,
                    PersonaCatalog.ProductManager, PersonaCatalog.UsabilityEngineer, PersonaCatalog.Recorder,
                    PersonaCatalog.PriorArtAnalyst, "SomeCustomPersona"
                };

                List<string> disagreements = new List<string>();
                foreach (AgentRuntimeEnum runtime in Enum.GetValues<AgentRuntimeEnum>())
                {
                    foreach (string persona in personas)
                    {
                        Captain captain = new Captain("cross-check");
                        captain.Runtime = runtime;

                        bool eligible = MissionService.CaptainAllowsPersona(captain, persona);
                        bool launchAdmits = AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, persona) == null;
                        if (eligible != launchAdmits)
                            disagreements.Add(runtime + "/" + persona + " eligible=" + eligible + " launchAdmits=" + launchAdmits);
                    }
                }

                AssertEqual(0, disagreements.Count,
                    "eligibility and the launch guard must give the same answer, but these differ: " + String.Join("; ", disagreements));
            });

            await RunTest("The execution-persona list has one definition", () =>
            {
                // Asked through PersonaCatalog rather than re-derived, so a persona added later is
                // classified in one place instead of drifting between callers.
                AssertTrue(PersonaCatalog.RequiresCommandExecution(PersonaCatalog.Judge));
                AssertTrue(PersonaCatalog.RequiresCommandExecution("testengineer"), "the legacy spelling resolves");
                AssertFalse(PersonaCatalog.RequiresCommandExecution(PersonaCatalog.Architect));
                AssertFalse(PersonaCatalog.RequiresCommandExecution(null));
                AssertFalse(PersonaCatalog.RequiresCommandExecution("   "));
            });
        }
    }
}
