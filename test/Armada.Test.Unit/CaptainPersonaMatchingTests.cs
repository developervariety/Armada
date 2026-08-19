namespace Armada.Test.Unit
{
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins persona eligibility to normalized matching. Persona names have two spellings in persisted
    /// data: "Test Engineer" is canonical and "TestEngineer" is what older builds wrote. Eligibility was
    /// decided by a substring test against the raw AllowedPersonas JSON, which treats those as different
    /// personas, so a captain carrying the legacy spelling was eligible for nothing. It stayed Idle while
    /// its mission queued for ever, which reads as a capacity problem and is really a spelling one.
    /// </summary>
    public sealed class CaptainPersonaMatchingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Persona Matching";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("The two spellings of Test Engineer are not different personas", () =>
            {
                // Guards the premise: if these ever became genuinely distinct personas the rest of this
                // suite would be asserting the wrong thing.
                AssertEqual("Test Engineer", PersonaCatalog.TestEngineer, "Canonical spelling");
                AssertEqual("TestEngineer", PersonaCatalog.LegacyTestEngineer, "Legacy spelling");
                Assert(
                    PersonaCatalog.Matches(PersonaCatalog.LegacyTestEngineer, PersonaCatalog.TestEngineer),
                    "The catalog treats the two spellings as one persona");
            });

            await RunTest("A legacy-spelled allow-list matches the canonical persona", () =>
            {
                Captain captain = new Captain("legacy-allowlist-captain");
                captain.AllowedPersonas = "[\"TestEngineer\"]";

                // The predicate this replaced, evaluated inline. Keeping it here makes the regression
                // concrete: this is the exact expression that benched the captain, and it still answers
                // false, so the assertion below is measuring a real change and not a tautology.
                bool substringOnly = captain.AllowedPersonas.Contains(
                    "\"Test Engineer\"", StringComparison.OrdinalIgnoreCase);
                Assert(
                    !substringOnly,
                    "The old substring predicate rejected this captain -- if it now passes, the premise changed");

                Assert(
                    MissionService.CaptainAllowsPersona(captain, "Test Engineer"),
                    "A captain allowed the legacy spelling must take canonical Test Engineer missions");
            });

            await RunTest("A canonical allow-list matches the legacy persona", () =>
            {
                Captain captain = new Captain("canonical-allowlist-captain");
                captain.AllowedPersonas = "[\"Test Engineer\"]";

                Assert(
                    MissionService.CaptainAllowsPersona(captain, "TestEngineer"),
                    "Normalization must work in both directions");
            });

            await RunTest("An empty allow-list accepts any persona", () =>
            {
                Captain captain = new Captain("unrestricted-captain");
                captain.AllowedPersonas = null;

                Assert(MissionService.CaptainAllowsPersona(captain, "Worker"), "No restriction means any persona");
                Assert(MissionService.CaptainAllowsPersona(captain, "Judge"), "No restriction means any persona");
            });

            await RunTest("A persona outside the allow-list is still refused", () =>
            {
                Captain captain = new Captain("judge-only-captain");
                captain.AllowedPersonas = "[\"Judge\"]";

                Assert(
                    MissionService.CaptainAllowsPersona(captain, "Judge"),
                    "The listed persona is allowed");
                Assert(
                    !MissionService.CaptainAllowsPersona(captain, "Worker"),
                    "Normalizing must not widen the allow-list into accepting everything");
                Assert(
                    !MissionService.CaptainAllowsPersona(captain, "Architect"),
                    "An unlisted persona stays refused");
            });

            await RunTest("A multi-entry allow-list matches any of its personas", () =>
            {
                Captain captain = new Captain("multi-persona-captain");
                captain.AllowedPersonas = "[\"Worker\",\"TestEngineer\"]";

                Assert(MissionService.CaptainAllowsPersona(captain, "Worker"), "First entry matches");
                Assert(
                    MissionService.CaptainAllowsPersona(captain, "Test Engineer"),
                    "Second entry matches through normalization");
                Assert(!MissionService.CaptainAllowsPersona(captain, "Judge"), "An unlisted persona stays refused");
            });

            await RunTest("A malformed allow-list does not bench the captain silently", () =>
            {
                Captain captain = new Captain("malformed-allowlist-captain");
                // Not a JSON array. Refusing every persona here would quietly remove the captain from
                // every dispatch, which is the same invisible failure this suite exists to prevent.
                captain.AllowedPersonas = "Worker";

                Assert(
                    !MissionService.CaptainAllowsPersona(captain, "Judge"),
                    "A persona absent from the malformed value is still refused");

                Captain quoted = new Captain("quoted-allowlist-captain");
                quoted.AllowedPersonas = "not json but mentions \"Worker\" somewhere";
                Assert(
                    MissionService.CaptainAllowsPersona(quoted, "Worker"),
                    "The substring fallback still recognizes a named persona in a malformed value");
            });
        }
    }
}
