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
    /// A captain must never be seated in a persona its runtime cannot serve: a Judge that cannot read the
    /// diff or run the gate still votes. Every runtime a mission launches today can run commands, the
    /// API-endpoint runtime through its run_command tool, so these cases pin two things: that the
    /// API-endpoint runtime is now admitted, and that the refusal path still works for a runtime that has
    /// proven no command tool.
    ///
    /// The rule holds in two places and they must agree. ELIGIBILITY decides selection; the LAUNCH refusal
    /// is the backstop for a captain pinned by hand or by a captain override. If eligibility were the more
    /// permissive of the two, a mission would be assigned to a captain that then refuses it.
    /// </summary>
    public sealed class ApiEndpointPersonaAdmissionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Api Endpoint Persona Admission";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("An API-endpoint mission is admitted to the execution personas now that it carries run_command", () =>
            {
                // The launch path registers the command tool for an API-endpoint mission, so the persona
                // that must run commands is now one this runtime can serve.
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
                    AssertNull(AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, persona),
                        "the " + persona + " persona must be admitted on an API-endpoint mission");
                }
            });

            await RunTest("A runtime with no proven command tool is still refused, with a reason", () =>
            {
                // The refusal path must keep working for a runtime added later without a command tool. An
                // unknown runtime value stands in for one: it has proven nothing, so it is not trusted.
                Captain captain = new Captain("unknown-runtime");
                captain.Runtime = (AgentRuntimeEnum)999;

                string? error = AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, PersonaCatalog.Judge);
                AssertNotNull(error, "a runtime with no proven command tool must be refused the Judge persona");
                AssertContains("no command tool", error!, "the refusal names the missing capability");
                AssertFalse(MissionService.CaptainAllowsPersona(captain, PersonaCatalog.Judge),
                    "and eligibility must agree with the refusal");
                AssertNull(AgentLifecycleHandler.ValidateRuntimeSupportsPersona(captain, PersonaCatalog.Architect),
                    "it is still admitted to a persona that needs no commands");
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

            await RunTest("An API-endpoint captain is ELIGIBLE for the execution personas its allow-list names", () =>
            {
                // Capability is still asked before permission; it simply answers yes now. The allow-list then
                // decides, exactly as it does for any other runtime.
                Captain apiJudge = new Captain("api-judge");
                apiJudge.Runtime = AgentRuntimeEnum.ApiEndpoint;
                apiJudge.AllowedPersonas = "[\"Judge\"]";
                AssertTrue(MissionService.CaptainAllowsPersona(apiJudge, PersonaCatalog.Judge),
                    "an API-endpoint captain allowed Judge is eligible for Judge");
                AssertFalse(MissionService.CaptainAllowsPersona(apiJudge, PersonaCatalog.Worker),
                    "but its allow-list still excludes the personas it does not name");

                Captain apiAny = new Captain("api-any");
                apiAny.Runtime = AgentRuntimeEnum.ApiEndpoint;
                apiAny.AllowedPersonas = null;
                AssertTrue(MissionService.CaptainAllowsPersona(apiAny, PersonaCatalog.Worker),
                    "an unrestricted API-endpoint captain is eligible for Worker");
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
                // Every real runtime answers yes today, so an unknown value is included to keep a refusing
                // case in the domain; without it this comparison would only ever compare yes with yes.
                List<AgentRuntimeEnum> runtimes = new List<AgentRuntimeEnum>(Enum.GetValues<AgentRuntimeEnum>());
                runtimes.Add((AgentRuntimeEnum)999);

                foreach (AgentRuntimeEnum runtime in runtimes)
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
