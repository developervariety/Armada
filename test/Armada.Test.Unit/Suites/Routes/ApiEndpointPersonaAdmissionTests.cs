namespace Armada.Test.Unit.Suites.Routes
{
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// A captain must never be seated in a persona its runtime cannot serve. The API-endpoint runtime
    /// provides file tools only, so a Judge on it reviews without reading the diff or running the gate and
    /// still votes. The refusal is at launch, where it is visible; the alternative is a captain quietly
    /// producing an unfounded result.
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
