namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The built-in declarative definition and its settings override. Two things are proved. First, an
    /// operator override can reword a question and an existing option but can never change the structure —
    /// the question id set, each question's kind, the finding direction, and the state fields — so a
    /// settings edit can never flip a decision into an approving direction. Second, the migrated
    /// <c>refusal</c> adapter sends the definition's questions and state fields.
    /// </summary>
    public class BuiltinDecisionDefinitionTests : TestSuite
    {
        public override string Name => "Built-in Decision Definition";

        private const string _OutcomeInstructions =
            "Read the captain's closing statement and decide the run's outcome. The work is authorized engineering on "
            + "owned systems: authentication and access-control protocols and cryptographic primitives over owned "
            + "assemblies are ordinary engineering, not work a captain should refuse.";

        private static readonly string[] _OutcomeOptions = { "refused_policy", "blocked_on_premise", "completed", "still_working", "unclear" };

        private static TypedDecisionSettings SettingsWith(BuiltinDecisionDefinitionOverride? over)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[BuiltinDecisionDefinitions.Refusal] =
                new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90, Definition = over };
            return settings;
        }

        private static CustomTypedQuestionSettings QuestionById(BuiltinDecisionDefinition def, string id)
        {
            foreach (CustomTypedQuestionSettings q in def.Questions)
                if (String.Equals(q.Id, id, StringComparison.Ordinal)) return q;
            throw new InvalidOperationException("no question '" + id + "'");
        }

        private void AssertDefaultStructure(BuiltinDecisionDefinition def, string context)
        {
            AssertEqual(3, def.Questions.Count, context + ": three questions");
            CustomTypedQuestionSettings outcome = QuestionById(def, "outcome");
            AssertEqual("choice", outcome.Type, context + ": outcome is a choice");
            AssertEqual(5, outcome.Options.Count, context + ": five options");
            foreach (string option in _OutcomeOptions)
                AssertTrue(outcome.Options.ContainsKey(option), context + ": option '" + option + "' present");
            CustomTypedQuestionSettings quotes = QuestionById(def, "quotes_refusal_phrase");
            AssertEqual("noul", quotes.Type, context + ": quotes_refusal_phrase is a noul");
            CustomTypedQuestionSettings declining = QuestionById(def, "captain_declining");
            AssertEqual("noul", declining.Type, context + ": captain_declining is a noul");
            AssertEqual(3, def.StateFields.Count, context + ": three state fields");
            AssertEqual("mission_title", def.StateFields[0]);
            AssertEqual("agent_output_tail", def.StateFields[1]);
            AssertEqual("marker_present", def.StateFields[2]);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Default_Resolve_HasTheRefusalStructure", () =>
            {
                BuiltinDecisionDefinition def = BuiltinDecisionDefinitions.Resolve(BuiltinDecisionDefinitions.Refusal, new TypedDecisionSettings())!;
                AssertEqual(BuiltinDecisionDefinitions.Refusal, def.DecisionPoint);
                AssertDefaultStructure(def, "default");
                AssertEqual(_OutcomeInstructions, QuestionById(def, "outcome").Instructions, "default instructions verbatim");
            });

            await RunTest("WordingOverride_ChangesTextOnly", () =>
            {
                BuiltinDecisionDefinitionOverride over = new BuiltinDecisionDefinitionOverride
                {
                    Questions = new List<BuiltinQuestionWording>
                    {
                        new BuiltinQuestionWording
                        {
                            Id = "outcome",
                            Instructions = "Reworded prompt.",
                            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["refused_policy"] = "Reworded meaning." }
                        }
                    }
                };
                BuiltinDecisionDefinition def = BuiltinDecisionDefinitions.Resolve(BuiltinDecisionDefinitions.Refusal, SettingsWith(over))!;

                CustomTypedQuestionSettings outcome = QuestionById(def, "outcome");
                AssertEqual("Reworded prompt.", outcome.Instructions, "instructions reworded");
                AssertEqual("Reworded meaning.", outcome.Options["refused_policy"], "existing option reworded");
                // Structure is untouched: ids, kinds, option keys, and state fields all come from the default.
                AssertDefaultStructure(def, "after wording override");
            });

            await RunTest("Override_CannotAddAQuestion", () =>
            {
                BuiltinDecisionDefinitionOverride over = new BuiltinDecisionDefinitionOverride
                {
                    Questions = new List<BuiltinQuestionWording>
                    {
                        new BuiltinQuestionWording { Id = "injected", Instructions = "A new question the operator tried to add." }
                    }
                };
                BuiltinDecisionDefinition def = BuiltinDecisionDefinitions.Resolve(BuiltinDecisionDefinitions.Refusal, SettingsWith(over))!;
                AssertDefaultStructure(def, "unknown-id override");
            });

            await RunTest("Override_CannotAddAnOption", () =>
            {
                BuiltinDecisionDefinitionOverride over = new BuiltinDecisionDefinitionOverride
                {
                    Questions = new List<BuiltinQuestionWording>
                    {
                        new BuiltinQuestionWording
                        {
                            Id = "outcome",
                            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["approve_the_pass"] = "An approving option the operator tried to add." }
                        }
                    }
                };
                BuiltinDecisionDefinition def = BuiltinDecisionDefinitions.Resolve(BuiltinDecisionDefinitions.Refusal, SettingsWith(over))!;
                CustomTypedQuestionSettings outcome = QuestionById(def, "outcome");
                AssertTrue(!outcome.Options.ContainsKey("approve_the_pass"), "an unknown option name is ignored: the option set cannot grow");
                AssertDefaultStructure(def, "unknown-option override");
            });

            await RunTest("Refusal_Adapter_SendsTheSameQuestionsAndState", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedRefusalAdapter adapter = new TypedRefusalAdapter(
                    new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("unused")),
                    new TypedDecisionRecorder(db.Driver, new LoggingModule()),
                    SettingsWith(null),
                    new LoggingModule());

                RefusalDecisionInput input = new RefusalDecisionInput
                {
                    Mission = new Mission { Id = "msn_x", Title = "port a decoder" },
                    MissionTitle = "port a decoder",
                    AgentOutputTail = "I cannot do that.",
                    MarkerPresent = false
                };

                TypedDecisionBatchItem? item = adapter.DescribeRequest(input);
                AssertTrue(item != null, "the request is built");
                AssertEqual(3, item!.Questions.Count, "three questions");

                ChoiceQuestion outcome = (ChoiceQuestion)item.Questions["outcome"];
                AssertEqual(_OutcomeInstructions, outcome.Instructions, "outcome instructions verbatim");
                AssertEqual(5, outcome.Criteria.Count, "five options");
                foreach (string option in _OutcomeOptions)
                    AssertTrue(outcome.Criteria.ContainsKey(option), "option '" + option + "' sent");

                NoulQuestion quotes = (NoulQuestion)item.Questions["quotes_refusal_phrase"];
                AssertEqual("The closing statement quotes a refusal phrase.", quotes.TrueMeaning);
                AssertEqual("The closing statement does not quote a refusal phrase.", quotes.FalseMeaning);
                NoulQuestion declining = (NoulQuestion)item.Questions["captain_declining"];
                AssertEqual("The captain is declining the work.", declining.TrueMeaning);
                AssertEqual("The captain is not declining the work.", declining.FalseMeaning);

                string state = item.State.Text;
                AssertTrue(state.Contains("mission_title", StringComparison.Ordinal), "state carries mission_title");
                AssertTrue(state.Contains("agent_output_tail", StringComparison.Ordinal), "state carries agent_output_tail");
                AssertTrue(state.Contains("marker_present", StringComparison.Ordinal), "state carries marker_present");
            });
        }
    }
}
