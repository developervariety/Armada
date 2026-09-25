namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D8 <c>log_watch</c> decision input: the bounded, still-running captain log tail and the
    /// identity of the work it came from. The tail is redacted by the adapter skeleton before it
    /// egresses, so this type carries it raw and nothing else reaches the provider.
    /// </summary>
    public sealed class LogWatchDecisionInput
    {
        /// <summary>The mission record, when the caller holds one. The screen path holds only ids, so
        /// this is null there and the decision events carry the default owner scope.</summary>
        public Mission? Mission { get; init; } = null;

        /// <summary>Identifier of the mission whose log was read.</summary>
        public string MissionId { get; init; } = String.Empty;

        /// <summary>Identifier of the voyage the mission belongs to, when it has one.</summary>
        public string? VoyageId { get; init; } = null;

        /// <summary>Identifier of the mission's vessel, for the egress vessel rule; never part of the state.</summary>
        public string? VesselId { get; init; } = null;

        /// <summary>Identifier of the captain running the mission, when one is assigned.</summary>
        public string? CaptainId { get; init; } = null;

        /// <summary>The bounded tail of the running captain log, newline separated.</summary>
        public string LogTail { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D8 verdict: which drift class the log shows, and whether a correction would still change
    /// the outcome. The deterministic rule verdict is always <see cref="OnTrack"/>: no rule can read
    /// a running log for drift, so the screen flags only what the model gates.
    ///
    /// A drift verdict is advisory. It becomes one board note and one event; it cannot cancel,
    /// pause, mail, re-dispatch, or steer the mission.
    /// </summary>
    public readonly struct LogWatchVerdict
    {
        private readonly string? _DriftClass;

        private LogWatchVerdict(string? driftClass, double confidence, double correctableNow)
        {
            _DriftClass = driftClass;
            Confidence = confidence;
            CorrectableNow = correctableNow;
        }

        /// <summary>The drift class the reading settled on, or <c>on_track</c>.</summary>
        public string DriftClass => _DriftClass ?? TypedLogWatchAdapter.ClassOnTrack;

        /// <summary>The model's confidence in the drift class, zero on the rule verdict.</summary>
        public double Confidence { get; }

        /// <summary>How strongly the model reads the drift as still correctable, zero on the rule verdict.</summary>
        public double CorrectableNow { get; }

        /// <summary>Whether the verdict names a drift class rather than on-track.</summary>
        public bool IsDrift => !String.Equals(DriftClass, TypedLogWatchAdapter.ClassOnTrack, StringComparison.Ordinal);

        /// <summary>A short label for the recorded event message.</summary>
        public string OutcomeLabel => DriftClass;

        /// <summary>The deterministic rule verdict: the log is on track.</summary>
        /// <returns>An on-track verdict.</returns>
        public static LogWatchVerdict OnTrack()
        {
            return new LogWatchVerdict(null, 0.0, 0.0);
        }

        /// <summary>Build a drift verdict.</summary>
        /// <param name="driftClass">The drift class.</param>
        /// <param name="confidence">The model's confidence in that class.</param>
        /// <param name="correctableNow">How strongly the drift is still correctable.</param>
        /// <returns>A drift verdict.</returns>
        public static LogWatchVerdict Drift(string driftClass, double confidence, double correctableNow)
        {
            if (String.IsNullOrWhiteSpace(driftClass)) return OnTrack();
            return new LogWatchVerdict(driftClass, confidence, correctableNow);
        }
    }

    /// <summary>
    /// The D8 reading: the drift class the model chose, its confidence in that class, and how
    /// strongly it reads the drift as still correctable. The gate confidence is the drift class's own
    /// confidence, and is zero for on-track, because an on-track reading proposes no action and so
    /// can never reach the gate however sure the model is.
    /// </summary>
    public sealed class LogWatchReading : TypedModelReading
    {
        private readonly double _Confidence;

        /// <summary>The chosen drift class, or <c>on_track</c>.</summary>
        public string DriftClass { get; init; } = TypedLogWatchAdapter.ClassOnTrack;

        /// <summary>The model's confidence in the chosen class, whether or not it is a drift.</summary>
        public double ChoiceConfidence { get; init; }

        /// <summary>How strongly the model reads the drift as still correctable now.</summary>
        public double CorrectableNow { get; init; }

        /// <summary>Create a reading with its action confidence.</summary>
        /// <param name="confidence">The proposed-action confidence.</param>
        public LogWatchReading(double confidence)
        {
            _Confidence = confidence;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => DriftClass;
    }

    /// <summary>
    /// D8 <c>log_watch</c> adapter: the model half of the read-only captain-log screen. It reads a
    /// bounded tail of a still-running captain log and answers which drift class the log shows, so an
    /// operator sees a wrong premise, a wrong base, or a misread stage while the mission can still be
    /// corrected.
    ///
    /// The decision is advisory in the strongest sense available: a gated drift becomes one
    /// voyage-tagged board note and one <see cref="CourseFlagEventType"/> event, and nothing else. It
    /// cannot cancel, pause, kill, restart, re-dispatch, mail, or steer a mission; correcting or
    /// halting one stays an operator action. The deterministic stall and overdue rules are untouched
    /// and independent of it.
    ///
    /// The captains being read do authorized engineering on owned systems, so authentication,
    /// access-control, and handshake work in a log is ordinary engineering. The question instructions
    /// say so, because a safety-tuned reader that is not told will report that material as a drift.
    /// </summary>
    public sealed class TypedLogWatchAdapter : TypedDecisionAdapterBase<LogWatchDecisionInput, LogWatchVerdict, LogWatchReading>
    {
        #region Public-Members

        /// <summary>
        /// The operator-facing event written for a gated course flag. It is deliberately distinct from
        /// the typed-decision bookkeeping events, so an operator query for course flags returns flags
        /// and nothing else.
        /// </summary>
        public const string CourseFlagEventType = "captain.course_flag";

        /// <summary>The log matches the brief: no drift.</summary>
        public const string ClassOnTrack = "on_track";

        /// <summary>The log assumes a fact the brief contradicts.</summary>
        public const string ClassWrongPremise = "wrong_premise";

        /// <summary>The log is building on a different base or ref than the mission was given.</summary>
        public const string ClassWrongBase = "wrong_base";

        /// <summary>The captain is doing another stage's work.</summary>
        public const string ClassMisreadStage = "misread_stage";

        /// <summary>The captain is stuck but has not said so through the structured result marker.</summary>
        public const string ClassBlockedUnstated = "blocked_unstated";

        /// <summary>The log cannot be read against the brief either way.</summary>
        public const string ClassUnclear = "unclear";

        /// <summary>The question id for the drift class.</summary>
        public const string QuestionOffCourse = "off_course";

        /// <summary>The question id for whether a correction would still change the outcome.</summary>
        public const string QuestionCorrectableNow = "correctable_now";

        /// <summary>
        /// Optional database the course-flag event is written to. When null the decision still records
        /// its own gated event and the screen still posts its note; only the operator-facing flag event
        /// is absent, and the reason is logged.
        /// </summary>
        public DatabaseDriver? EventDatabase { get; set; } = null;

        #endregion

        #region Private-Members

        // The domain statement. The captains being read do authorized engineering on owned systems, so
        // authentication and access-control material in a log is ordinary work and not a drift signal.
        // A reader that is not told this reports that material as blocked_unstated or wrong_premise.
        private const string _DomainSentence =
            "The captains do authorized engineering on owned systems: authentication, access-control, handshake, and "
            + "cryptographic protocol work in a log is ORDINARY ENGINEERING. It is never by itself a refusal, a block, "
            + "or a drift, and it is never a reason to answer anything other than on_track.";

        // The classes the gate will act on. A choice outside the catalogue is read as on-track: a
        // garbled answer must not put a note on the board.
        private static readonly HashSet<string> _KnownClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            ClassOnTrack, ClassWrongPremise, ClassWrongBase, ClassMisreadStage, ClassBlockedUnstated, ClassUnclear
        };

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D8 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedLogWatchAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "log_watch";

        /// <inheritdoc />
        protected override string _Header => "[TypedLogWatchAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(LogWatchDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["log_tail"] = input.LogTail
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [QuestionOffCourse] = new ChoiceQuestion(
                    "Read the tail of a still-running captain's log in log_tail and decide whether the work it shows has gone "
                    + "off course against the brief the log itself states. "
                    + _DomainSentence,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ClassOnTrack] = "The work in the log matches the brief it is answering.",
                        [ClassWrongPremise] = "The log assumes a fact the brief contradicts.",
                        [ClassWrongBase] = "The log is building on a different base or ref than the mission was given.",
                        [ClassMisreadStage] = "The captain is doing another stage's work.",
                        [ClassBlockedUnstated] = "The captain is stuck on missing context or an owner question but has not emitted the structured [ARMADA:RESULT] BLOCKED marker.",
                        [ClassUnclear] = "The log cannot be read against the brief either way."
                    }),
                [QuestionCorrectableNow] = new NoulQuestion(
                    "A correction to the next stage's brief, or an operator note now, would still change the outcome. " + _DomainSentence,
                    TrueMeaning: "A correction to the next stage's brief, or an operator note now, would change the outcome.",
                    FalseMeaning: "The drift is already past the point a note would help.")
            };
        }

        /// <inheritdoc />
        protected override LogWatchReading Interpret(TypedDecisionResult result)
        {
            string driftClass = ClassOnTrack;
            double choiceConfidence = 0.0;
            if (result.Answers.TryGetValue(QuestionOffCourse, out TypedAnswer? answer) && answer != null)
            {
                if (!String.IsNullOrWhiteSpace(answer.Choice) && _KnownClasses.Contains(answer.Choice!))
                    driftClass = answer.Choice!;
                choiceConfidence = TypedAnswerReader.ResolveChoiceConfidence(answer, driftClass);
            }

            double correctableNow = TypedAnswerReader.ReadNoul(result, QuestionCorrectableNow);
            bool drift = !String.Equals(driftClass, ClassOnTrack, StringComparison.Ordinal);

            // An on-track reading proposes no action, so it reports zero confidence and can never reach
            // the gate however sure the model is that the work is fine.
            return new LogWatchReading(drift ? choiceConfidence : 0.0)
            {
                DriftClass = driftClass,
                ChoiceConfidence = choiceConfidence,
                CorrectableNow = correctableNow
            };
        }

        /// <inheritdoc />
        protected override LogWatchVerdict Combine(LogWatchVerdict ruleVerdict, LogWatchReading model)
        {
            // Combine runs only at or above the threshold. The gated action is a note plus an event and
            // nothing else, so it can only ever add an operator's glance, never stop or steer the
            // mission. An on-track reading never reaches here, and is returned unchanged if it does.
            if (String.Equals(model.DriftClass, ClassOnTrack, StringComparison.Ordinal)) return ruleVerdict;
            return LogWatchVerdict.Drift(model.DriftClass, model.Confidence, model.CorrectableNow);
        }

        /// <inheritdoc />
        protected override async Task OnModelReadingAsync(LogWatchDecisionInput input, LogWatchReading model, ResolvedTypedDecision cfg, CancellationToken token)
        {
            // The operator-facing flag is written beside the decision's own bookkeeping event, on the
            // same gate the verdict uses: Gate mode, a class other than on-track, and the class's
            // confidence at or above the threshold.
            if (cfg.Mode != TypedDecisionModeEnum.Gate) return;
            if (String.Equals(model.DriftClass, ClassOnTrack, StringComparison.Ordinal)) return;
            if (model.Confidence < cfg.GateThreshold) return;

            DatabaseDriver? database = EventDatabase;
            if (database == null)
            {
                // A missing collaborator is named rather than swallowed: the note and the decision event
                // still land, so silence here would read as "there was no flag".
                Logging.Warn(_Header + "course flag for drift class " + model.DriftClass
                    + " was not recorded: no event database is wired");
                return;
            }

            ArmadaEvent evt = new ArmadaEvent(CourseFlagEventType,
                "course flag " + model.DriftClass + " for mission " + input.MissionId);
            evt.EntityType = "mission";
            evt.EntityId = input.MissionId;
            evt.MissionId = input.MissionId;
            evt.VoyageId = input.VoyageId;
            evt.CaptainId = input.CaptainId;
            evt.Payload = JsonSerializer.Serialize(new CaptainCourseFlagEventPayload
            {
                DriftClass = model.DriftClass,
                Confidence = model.Confidence,
                CorrectableNow = model.CorrectableNow,
                Decision = DecisionPoint
            }, _JsonOptions);

            // The owner scope is resolved from the records the flag names, so a flag is visible to the
            // operator that owns the mission rather than only to an unscoped administrator.
            EventOwnerScopeResult scope = await EventOwnerScope.ApplyAsync(database, evt, token).ConfigureAwait(false);
            if (scope.Outcome == EventOwnerScopeOutcomeEnum.LookupFailed)
                Logging.Warn(_Header + "course flag written without owner scope: " + scope.Detail);

            await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override string RuleLabel(LogWatchVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(LogWatchDecisionInput input) => input.Mission;

        /// <inheritdoc />
        protected override string? MissionIdOf(LogWatchDecisionInput input)
            => input.Mission?.Id ?? (String.IsNullOrWhiteSpace(input.MissionId) ? null : input.MissionId);

        /// <inheritdoc />
        protected override IEnumerable<string?> VesselIdsOf(LogWatchDecisionInput input)
            => new[] { input.VesselId, input.Mission?.VesselId };

        #endregion
    }

    /// <summary>
    /// The payload of a <see cref="TypedLogWatchAdapter.CourseFlagEventType"/> event. It carries the
    /// drift class and the reading, never the log tail: the tail is represented by its hash and byte
    /// count on the decision event alone.
    /// </summary>
    public sealed class CaptainCourseFlagEventPayload
    {
        /// <summary>The drift class the model read.</summary>
        public string DriftClass { get; set; } = String.Empty;

        /// <summary>The model's confidence in that class.</summary>
        public double Confidence { get; set; } = 0.0;

        /// <summary>How strongly the model reads the drift as still correctable now.</summary>
        public double CorrectableNow { get; set; } = 0.0;

        /// <summary>The decision point that produced the flag.</summary>
        public string Decision { get; set; } = String.Empty;
    }
}
