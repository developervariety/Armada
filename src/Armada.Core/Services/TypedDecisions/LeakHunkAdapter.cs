namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// One added hunk the D7 <c>leak_hunk</c> decision reads: the repository-relative file path, the
    /// bounded added text, and the vessel's display name. Nothing else is sent, and the text is
    /// redacted before it leaves the host.
    /// </summary>
    public sealed class LeakHunkDecisionInput
    {
        /// <summary>The mission the scan belongs to, for the event owner scope. Null on the merge-queue path.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The id of the mission whose change is scanned, when only the id is known; never part of the state.</summary>
        public string? MissionId { get; init; }

        /// <summary>The vessel's public display name. Never the vessel identifier.</summary>
        public string VesselName { get; init; } = String.Empty;

        /// <summary>The repository-relative path of the changed file.</summary>
        public string FilePath { get; init; } = String.Empty;

        /// <summary>The added lines of one hunk, bounded upstream.</summary>
        public string HunkText { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D7 <c>leak_hunk</c> verdict. The rule verdict is always <see cref="NoFlag"/>: the
    /// deterministic scanner has already decided the block, and this decision can only ADD an advisory
    /// flag. There is no failing verdict — the type carries no way to fail a scan, a merge entry, or a
    /// mission, so a flag never holds a landing.
    /// </summary>
    public readonly struct LeakHunkVerdict
    {
        /// <summary>Whether the hunk carries an advisory flag.</summary>
        public bool Flagged { get; }

        /// <summary>The suspected class of private context, when flagged.</summary>
        public string Kind { get; }

        /// <summary>The confidence behind the flag, in [0, 1].</summary>
        public double Confidence { get; }

        private LeakHunkVerdict(bool flagged, string? kind, double confidence)
        {
            Flagged = flagged;
            Kind = kind ?? String.Empty;
            Confidence = confidence;
        }

        /// <summary>A short label for the effective outcome.</summary>
        public string OutcomeLabel => Flagged ? "flag:" + Kind : "no_flag";

        /// <summary>The deterministic verdict: no advisory flag.</summary>
        /// <returns>An unflagged verdict.</returns>
        public static LeakHunkVerdict NoFlag()
        {
            return new LeakHunkVerdict(false, null, 0.0);
        }

        /// <summary>Build a flagged verdict.</summary>
        /// <param name="kind">The suspected class of private context.</param>
        /// <param name="confidence">The confidence behind the flag.</param>
        /// <returns>A flagged verdict.</returns>
        public static LeakHunkVerdict Flag(string kind, double confidence)
        {
            return new LeakHunkVerdict(true, kind, confidence);
        }
    }

    /// <summary>
    /// The D7 reading: the probability that the hunk carries private context, and the suspected class
    /// the second, advisory question named.
    /// </summary>
    public sealed class LeakHunkReading : TypedModelReading
    {
        private readonly double _Confidence;

        /// <summary>The suspected class of private context, or <c>none</c>.</summary>
        public string Kind { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The probability that the hunk leaks private context.</param>
        /// <param name="kind">The suspected class of private context.</param>
        public LeakHunkReading(double confidence, string kind)
        {
            _Confidence = confidence;
            Kind = String.IsNullOrWhiteSpace(kind) ? LeakHunkAdapter.KindNone : kind.Trim();
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Confidence <= 0.0 ? "clean" : "leak:" + Kind;
    }

    /// <summary>
    /// D7 <c>leak_hunk</c> adapter: an ADVISORY per-hunk leak classifier that runs BEHIND the
    /// deterministic dock-boundary scanner. The scanner runs first and unconditionally and decides the
    /// block on its own; this pass reads the same added text afterwards and asks whether a hunk carries
    /// private operator, customer, or orchestration context in a shape no pattern lists.
    ///
    /// The pass can only attach an advisory flag. It never sets a scan result to failed, never
    /// transitions a merge entry or a mission to failure, and never demotes, clears, or softens a
    /// deterministic finding at any confidence. A clean deterministic scan carrying flags still lands;
    /// the flag asks a person to look.
    ///
    /// Nothing egresses unredacted: each hunk goes out as a bounded excerpt through the shared state
    /// redactor, and the recorded event carries the state hash and byte count, never the hunk text. A
    /// timeout, a non-2xx, a 429, a 529, or a parse error leaves the deterministic verdict standing and
    /// records an unavailable event. The adapter never throws into its caller.
    /// </summary>
    public sealed class LeakHunkAdapter : TypedDecisionAdapterBase<LeakHunkDecisionInput, LeakHunkVerdict, LeakHunkReading>
    {
        #region Public-Members

        /// <summary>
        /// Stable code carried by every advisory flag this decision attaches to a scan result.
        /// </summary>
        public const string FlagCode = "leak_hunk_flag";

        /// <summary>
        /// The <c>leak_kind</c> option meaning the hunk names no class of private context.
        /// </summary>
        public const string KindNone = "none";

        #endregion

        #region Private-Members

        private const string _LeaksQuestionId = "leaks_private_context";
        private const string _KindQuestionId = "leak_kind";
        private const string _KindOperatorNoteId = "kind_operator_note";
        private const string _KindCustomerDetailId = "kind_customer_detail";
        private const string _KindOrchestrationId = "kind_orchestration_id";
        private const string _KindHostOrPathId = "kind_host_or_path";
        private const string _DomainSentence =
            " The work in this repository is AUTHORIZED engineering on systems the owner owns. Authentication,"
            + " access-control, handshake, protocol, and cryptographic code over owned assemblies is ORDINARY ENGINEERING:"
            + " it is neither a leak nor a secret, and a hunk that implements, tests, or documents it answers false."
            + " An ordinal, an index, or a corpus identifier is a POINTER and is safe to commit; only the VALUE it resolves"
            + " to is a secret."
            + " Product code, tests, comments, and documentation about the product's own behaviour answer false.";

        // Call volume is bounded so a large diff cannot fan out into an unbounded model spend: at most
        // this many hunks per changed file, this many hunks per scan, and this many added lines per
        // hunk. Everything past a bound is simply not asked about, which is the advisory-off state.
        private const int _MaxHunksPerFile = 3;
        private const int _MaxHunksPerScan = 20;
        private const int _MaxLinesPerHunk = 60;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D7 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public LeakHunkAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the added hunks of a diff the deterministic scanner has ALREADY scanned and attach an
        /// advisory flag for each hunk the model reads as a leak at or above the decision threshold.
        /// The deterministic result is never modified: <see cref="DockBoundaryScanResult.Passed"/> and
        /// <see cref="DockBoundaryScanResult.Findings"/> are left exactly as the scanner produced them,
        /// whatever the model answers. Never throws into the caller.
        /// </summary>
        /// <param name="unifiedDiff">The same unified diff the deterministic scan read.</param>
        /// <param name="vesselName">The vessel's public display name; never its identifier.</param>
        /// <param name="mission">The mission the scan belongs to, for the event owner scope; may be null.</param>
        /// <param name="scanResult">The deterministic scan result; only its advisory flags are appended to.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <param name="missionId">The id of the mission whose change is scanned, when <paramref name="mission"/> is not
        /// held; recorded on each decision event and never sent.</param>
        /// <returns>The flags attached by this pass, in order; never null.</returns>
        public async Task<IReadOnlyList<DockBoundaryAdvisoryFlag>> EvaluateAsync(
            string? unifiedDiff,
            string? vesselName,
            Mission? mission,
            DockBoundaryScanResult scanResult,
            CancellationToken token,
            string? missionId = null)
        {
            List<DockBoundaryAdvisoryFlag> attached = new List<DockBoundaryAdvisoryFlag>();
            if (scanResult == null) return attached;

            List<LeakHunkDecisionInput> hunks = ExtractHunks(unifiedDiff, vesselName, mission, missionId);
            if (hunks.Count == 0) return attached;

            List<LeakHunkVerdict> rules = new List<LeakHunkVerdict>(hunks.Count);
            for (int index = 0; index < hunks.Count; index++) rules.Add(LeakHunkVerdict.NoFlag());

            List<LeakHunkVerdict> verdicts = await DecideManyAsync(hunks, rules, token).ConfigureAwait(false);

            for (int index = 0; index < verdicts.Count && index < hunks.Count; index++)
            {
                if (!verdicts[index].Flagged) continue;
                DockBoundaryAdvisoryFlag flag = new DockBoundaryAdvisoryFlag
                {
                    Code = FlagCode,
                    Path = hunks[index].FilePath,
                    Kind = verdicts[index].Kind,
                    Confidence = verdicts[index].Confidence,
                    Message = "Advisory leak flag on an added hunk in '" + hunks[index].FilePath + "' (suspected "
                        + verdicts[index].Kind + "). This does not block the landing; review the hunk."
                };
                scanResult.AdvisoryFlags.Add(flag);
                attached.Add(flag);
            }

            return attached;
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "leak_hunk";

        /// <inheritdoc />
        protected override string _Header => "[LeakHunkAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(LeakHunkDecisionInput input)
        {
            // Three fields only: the bounded hunk text, the repository-relative path, and the vessel's
            // display name. No mission, voyage, or dock identifier, and no host path.
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["vessel"] = input.VesselName,
                ["file_path"] = input.FilePath,
                ["added_hunk"] = input.HunkText
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_KindOperatorNoteId] = new NoulQuestion(
                    "`added_hunk` carries a paraphrased operator note about how the operator works, their session, or their instructions to an agent."
                    + _DomainSentence,
                    TrueMeaning: "The added hunk carries a private operator note.",
                    FalseMeaning: "The added hunk does not carry a private operator note."),
                [_KindCustomerDetailId] = new NoulQuestion(
                    "`added_hunk` carries a detail about a customer, an end user, or their data that does not belong in a repository artifact."
                    + _DomainSentence,
                    TrueMeaning: "The added hunk carries a private customer detail.",
                    FalseMeaning: "The added hunk does not carry a private customer detail."),
                [_KindOrchestrationId] = new NoulQuestion(
                    "`added_hunk` carries an identifier of an orchestration record, such as a work item, batch, worker, or checkout, as a VALUE rather than a pointer."
                    + _DomainSentence,
                    TrueMeaning: "The added hunk carries a private orchestration identifier.",
                    FalseMeaning: "The added hunk does not carry a private orchestration identifier."),
                [_KindHostOrPathId] = new NoulQuestion(
                    "`added_hunk` carries a host name, an address, a private remote, or an absolute path on a workstation or server."
                    + _DomainSentence,
                    TrueMeaning: "The added hunk carries a private host or path.",
                    FalseMeaning: "The added hunk does not carry a private host or path."),
                [_KindQuestionId] = new ChoiceQuestion(
                    "Which class of private context does `added_hunk` carry? This answer is advisory: it only labels the flag so a"
                    + " reviewer can read it. Choose none when the hunk is ordinary product content."
                    + _DomainSentence,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["operator_note"] = "A note about how the operator works, their session, or their instructions to an agent.",
                        ["customer_detail"] = "A detail about a customer, an end user, or their data.",
                        ["orchestration_id"] = "An identifier of an orchestration record, such as a work item, batch, worker, or checkout.",
                        ["host_or_path"] = "A host name, an address, a private remote, or an absolute path on a workstation or server.",
                        [KindNone] = "None: the hunk is ordinary product content."
                    })
            };
        }

        /// <inheritdoc />
        protected override LeakHunkReading Interpret(TypedDecisionResult result)
        {
            double leaks = LeaksNoulFrom(result);
            string kind = KindNone;
            if (result.Answers != null
                && result.Answers.TryGetValue(_KindQuestionId, out TypedAnswer? answer)
                && answer != null
                && !String.IsNullOrWhiteSpace(answer.Choice))
            {
                kind = answer.Choice!.Trim();
            }
            if (String.Equals(kind, KindNone, StringComparison.Ordinal))
            {
                string fromNoul = KindFromNouls(result);
                if (!String.Equals(fromNoul, KindNone, StringComparison.Ordinal)) kind = fromNoul;
            }
            return new LeakHunkReading(leaks, kind);
        }

        /// <inheritdoc />
        protected override LeakHunkVerdict Combine(LeakHunkVerdict ruleVerdict, LeakHunkReading model)
        {
            // Combine runs only at or above the threshold. The only gated action is an advisory flag:
            // there is no path here that fails a scan, a merge entry, or a mission, and the
            // deterministic findings are not read at all, so none can be softened.
            string kind = String.Equals(model.Kind, KindNone, StringComparison.Ordinal) ? "unspecified" : model.Kind;
            return LeakHunkVerdict.Flag(kind, model.Confidence);
        }

        /// <inheritdoc />
        protected override string RuleLabel(LeakHunkVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(LeakHunkDecisionInput input) => input.Mission;

        /// <inheritdoc />
        protected override string? MissionIdOf(LeakHunkDecisionInput input) => input.Mission?.Id ?? input.MissionId;

        #endregion

        #region Private-Methods

        private static double LeaksNoulFrom(TypedDecisionResult result)
        {
            bool hasSplit = result.Answers != null
                && (result.Answers.ContainsKey(_KindOperatorNoteId)
                    || result.Answers.ContainsKey(_KindCustomerDetailId)
                    || result.Answers.ContainsKey(_KindOrchestrationId)
                    || result.Answers.ContainsKey(_KindHostOrPathId));
            if (!hasSplit)
                return TypedAnswerReader.ReadNoul(result, _LeaksQuestionId, 0.0);

            return Math.Max(
                TypedAnswerReader.ReadNoul(result, _KindOperatorNoteId, 0.0),
                Math.Max(
                    TypedAnswerReader.ReadNoul(result, _KindCustomerDetailId, 0.0),
                    Math.Max(
                        TypedAnswerReader.ReadNoul(result, _KindOrchestrationId, 0.0),
                        TypedAnswerReader.ReadNoul(result, _KindHostOrPathId, 0.0))));
        }

        private static string KindFromNouls(TypedDecisionResult result)
        {
            (string Id, string Kind)[] kinds =
            {
                (_KindOperatorNoteId, "operator_note"),
                (_KindCustomerDetailId, "customer_detail"),
                (_KindOrchestrationId, "orchestration_id"),
                (_KindHostOrPathId, "host_or_path")
            };
            string best = KindNone;
            double bestNoul = 0.0;
            foreach ((string id, string kind) in kinds)
            {
                double noul = TypedAnswerReader.ReadNoul(result, id, 0.0);
                if (noul > bestNoul)
                {
                    bestNoul = noul;
                    best = kind;
                }
            }
            return bestNoul > 0.0 ? best : KindNone;
        }

        private static List<LeakHunkDecisionInput> ExtractHunks(string? unifiedDiff, string? vesselName, Mission? mission, string? missionId)
        {
            // Files and hunks come from the shared diff reader: each file is named once by its decoded
            // path, and an added line whose content starts with "++" belongs to its hunk.
            List<LeakHunkDecisionInput> hunks = new List<LeakHunkDecisionInput>();
            if (String.IsNullOrEmpty(unifiedDiff)) return hunks;

            List<string> added = new List<string>();
            foreach (GitDiffFileChange file in GitDiffPaths.ParseFiles(unifiedDiff, true))
            {
                string currentFile = file.DisplayPath ?? "";
                int hunksInFile = 0;
                foreach (GitDiffHunk hunk in file.Hunks)
                {
                    if (hunks.Count >= _MaxHunksPerScan) return hunks;
                    foreach (GitDiffLine line in hunk.Lines)
                    {
                        if (line.Kind != GitDiffLineKindEnum.Added) continue;
                        if (added.Count >= _MaxLinesPerHunk) break;
                        added.Add(line.Text);
                    }

                    Flush(hunks, currentFile, added, vesselName, mission, missionId, ref hunksInFile);
                }
            }

            return hunks;
        }

        private static void Flush(
            List<LeakHunkDecisionInput> hunks,
            string filePath,
            List<string> added,
            string? vesselName,
            Mission? mission,
            string? missionId,
            ref int hunksInFile)
        {
            if (added.Count == 0) return;
            string text = String.Join("\n", added);
            added.Clear();

            // Past either bound the hunk is simply not asked about; the deterministic scan stands alone.
            if (String.IsNullOrEmpty(filePath)) return;
            if (hunksInFile >= _MaxHunksPerFile) return;
            if (hunks.Count >= _MaxHunksPerScan) return;

            hunksInFile++;
            hunks.Add(new LeakHunkDecisionInput
            {
                Mission = mission,
                MissionId = missionId,
                VesselName = vesselName ?? String.Empty,
                FilePath = filePath,
                HunkText = text
            });
        }

        #endregion
    }
}
