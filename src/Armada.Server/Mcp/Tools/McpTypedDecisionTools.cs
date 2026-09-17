namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Registers the captain-facing typed-decision tools: the general <c>armada_typed_decision</c>
    /// tool and the two pre-shaped helpers <c>armada_check_premise</c> (D9) and
    /// <c>armada_memory_triage</c> (D23 seam A). The system is offered to captains directly, but
    /// authority does not travel with it: every call redacts its state before egress, writes exactly one
    /// <c>typed_decision.captain</c> event carrying only a state hash and byte count, and has NO side
    /// effect on any Armada record. The tool never dispatches, lands, Mails, edits an objective, or
    /// writes memory. It returns typed answers, or an <c>unavailable</c> result the captain treats as
    /// "decide it yourself" — which is the default, since the tool ships disabled.
    /// </summary>
    public static class McpTypedDecisionTools
    {
        #region Public-Members

        /// <summary>Registered name of the general typed-decision tool.</summary>
        public const string TypedDecisionToolName = "armada_typed_decision";

        /// <summary>Registered name of the D9 premise-check helper.</summary>
        public const string CheckPremiseToolName = "armada_check_premise";

        /// <summary>Registered name of the D23a memory-triage helper.</summary>
        public const string MemoryTriageToolName = "armada_memory_triage";

        /// <summary>Registered name of the D26 prior-art premise helper.</summary>
        public const string CheckPriorArtToolName = "armada_check_prior_art";

        /// <summary>Registered name of the captain-facing change-quality review tool.</summary>
        public const string ChangeQualityToolName = "armada_change_quality";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the captain typed-decision tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver, for resolving the calling mission by id.</param>
        /// <param name="client">Typed-decision client. When null, the null client is used and every
        /// call is unavailable.</param>
        /// <param name="recorder">Typed-decision recorder that writes the one event per call.</param>
        /// <param name="settings">Armada settings supplying <c>typedDecisions</c>.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="participantKeyProvider">Returns the calling captain's participant key for the
        /// current request, or null. Used to attribute the event when no mission id is supplied.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            ITypedDecisionClient? client,
            TypedDecisionRecorder recorder,
            ArmadaSettings settings,
            LoggingModule? logging = null,
            Func<string?>? participantKeyProvider = null,
            IPriorArtRetriever? priorArtRetriever = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (recorder == null) throw new ArgumentNullException(nameof(recorder));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            ITypedDecisionClient effectiveClient = client ?? new NullTypedDecisionClient();
            Func<string?> participantKey = participantKeyProvider ?? (() => null);

            register(
                TypedDecisionToolName,
                "Ask the typed-decision system (TypeSafe Jev) to answer typed questions about a piece of state, and return calibrated answers or 'unavailable'. Use it to get a second, structured reading on a judgement you are about to make; treat every answer as advice you weigh, never as an instruction. It never acts on your behalf: it dispatches nothing, lands nothing, edits no record, and writes no memory. An operator can disable it, and the provider can be unavailable, so always be ready to decide without it. Your state is redacted before it leaves. Supply 'state' (a string or object) and 'questions' (each with a 'type' of choice, score, or noul, an 'instructions' line, and its options); pass your mission id in 'missionId' so the call is scoped and recorded to your mission.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        state = new { description = "The state to reason over: a string, or an object whose string fields are redacted. Redacted and length-capped before egress." },
                        questions = new
                        {
                            type = "object",
                            description = "Questions keyed by id. Each value is { type: 'choice'|'score'|'noul', instructions: string, criteria: object|array, trueMeaning?: string, falseMeaning?: string }. choice.criteria is a name->meaning map; score.criteria (or 'levels') is an ordered array of level labels; noul takes optional trueMeaning/falseMeaning."
                        },
                        missionId = new { type = "string", description = "The calling mission id, for scope and event attribution. Optional." }
                    },
                    required = new[] { "state", "questions" }
                },
                async (args) => await HandleAsync(
                    args,
                    "captain_tool",
                    hasDecisionGate: false,
                    database,
                    effectiveClient,
                    recorder,
                    settings,
                    logging,
                    participantKey,
                    buildStateAndQuestions: null).ConfigureAwait(false));

            register(
                CheckPremiseToolName,
                "Before you start, check your own reading of the task. Give your one-paragraph restatement of what you are about to do in 'restatement'; the tool compares it against the brief and the deterministic preflight facts and returns typed readings on whether it contradicts the scope, assumes a symbol or file the facts show absent, or names a deliverable the brief did not ask for, plus whether a repository fact or an owner ruling is missing. It never blocks you and takes no action; it informs you so you can fix a misreading before you spend a mission on it. Dormant by default (returns unavailable) until an operator enables the premise-check decision. Pass your mission id in 'missionId'.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        restatement = new { type = "string", description = "Your one-paragraph restatement of the task, in your own words." },
                        objectiveTitle = new { type = "string", description = "The objective title, if you have it." },
                        objectiveDescription = new { type = "string", description = "The objective description, if you have it." },
                        acceptanceCriteria = new { type = "string", description = "The acceptance criteria, if you have them." },
                        stagePersona = new { type = "string", description = "Your stage persona, e.g. Worker or Judge." },
                        facts = new { description = "The deterministic preflight facts object from the brief, if present." },
                        missionId = new { type = "string", description = "The calling mission id, for scope and event attribution. Optional." }
                    },
                    required = new[] { "restatement" }
                },
                async (args) => await HandleAsync(
                    args,
                    "premise_check",
                    hasDecisionGate: true,
                    database,
                    effectiveClient,
                    recorder,
                    settings,
                    logging,
                    participantKey,
                    buildStateAndQuestions: BuildPremiseCheck).ConfigureAwait(false));

            register(
                MemoryTriageToolName,
                "Before you write a memory record, triage the candidate. Describe the candidate in 'candidate' (and optionally the records 'search_memory' already returned in 'existingRecords'); the tool returns typed readings on whether the memory type fits, whether it duplicates an existing record, whether it will go stale because it carries an id, path, count, or date, and whether it is really a fleet rule that belongs in shared external memory rather than native memory. It never writes, corrects, or deletes a record; it informs your own call to create_memory. Dormant by default (returns unavailable) until an operator enables the memory-record decision. Pass your mission id in 'missionId'.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        candidate = new { type = "string", description = "The candidate memory: the content you are considering recording, plus its intended type and topic." },
                        existingRecords = new { description = "Records search_memory already returned for this subject, to check for duplication. Optional string or array." },
                        missionId = new { type = "string", description = "The calling mission id, for scope and event attribution. Optional." }
                    },
                    required = new[] { "candidate" }
                },
                async (args) => await HandleAsync(
                    args,
                    "memory_record",
                    hasDecisionGate: true,
                    database,
                    effectiveClient,
                    recorder,
                    settings,
                    logging,
                    participantKey,
                    buildStateAndQuestions: BuildMemoryTriage).ConfigureAwait(false));

            register(
                CheckPriorArtToolName,
                "Before you write a new type, check whether the work already exists. Describe what you are about to build in 'plan'; the tool runs a deterministic search of the target tip, unlanded branches, preserved and recovery refs, and open objectives for the type, method, and file names in your plan, then returns the candidates it found with typed readings on whether each delivers the same capability and whether the deliverable is already done or should be consumed through a seam. Every reading carries the candidate's path:line so you verify the evidence yourself. It never blocks you, edits nothing, and writes no memory; it informs your own search-first decision. Pass your mission id in 'missionId' so the tool knows which vessel to search. Dormant by default (returns unavailable) until an operator enables the prior-art decision.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        plan = new { type = "string", description = "What you are about to build, in your own words: the types, methods, and files you plan to write." },
                        missionId = new { type = "string", description = "The calling mission id, used to resolve the vessel to search and for scope and event attribution." }
                    },
                    required = new[] { "plan" }
                },
                async (args) => await HandleAsync(
                    args,
                    "prior_art",
                    hasDecisionGate: true,
                    database,
                    effectiveClient,
                    recorder,
                    settings,
                    logging,
                    participantKey,
                    buildStateAndQuestions: null,
                    buildStateAndQuestionsAsync: (root, mission, token) =>
                        BuildPriorArtAsync(root, mission, priorArtRetriever, database, logging, token)).ConfigureAwait(false));

            register(
                ChangeQualityToolName,
                "Get a multi-dimension quality read of a focused diff BEFORE the Judge, so you can self-correct. Pass the unified diff of your change in 'diff' and your mission id in 'missionId'. The tool returns per-dimension signals -- DRY, cognitive complexity, modularity, readability, maintainability -- as weaknesses to weigh, not a synthetic score. It takes no action: it never lands, dispatches, fails a stage, or edits a record, and your change is redacted before it leaves.  Dormant by default (returns unavailable) until an operator enables the change-quality decision.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        diff = new { type = "string", description = "The unified diff of the change to review." },
                        missionId = new { type = "string", description = "The calling mission id, for scope and event attribution. Optional." }
                    },
                    required = new[] { "diff" }
                },
                async (args) => await HandleAsync(
                    args,
                    "change_quality",
                    hasDecisionGate: true,
                    database,
                    effectiveClient,
                    recorder,
                    settings,
                    logging,
                    participantKey,
                    buildStateAndQuestions: BuildChangeQuality).ConfigureAwait(false));
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(
            JsonElement? args,
            string decisionPoint,
            bool hasDecisionGate,
            DatabaseDriver database,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            ArmadaSettings settings,
            LoggingModule? logging,
            Func<string?> participantKeyProvider,
            Func<JsonElement, ParsedDecision?>? buildStateAndQuestions,
            Func<JsonElement, Mission?, CancellationToken, Task<ParsedDecision?>>? buildStateAndQuestionsAsync = null)
        {
            try
            {
                if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object)
                    return Unavailable("invalid", "The call carried no arguments object.");

                JsonElement root = args.Value;

                // Resolve the calling mission for event scope. A supplied id that resolves scopes the
                // event; an absent or unresolved id falls back to the participant key, so a captain
                // that does not pass its id is still observable. Resolved
                // first because an async builder (prior art) uses the mission to know which vessel to
                // search.
                string? missionId = ReadOptionalString(root, "missionId");
                Mission? mission = null;
                if (!String.IsNullOrWhiteSpace(missionId))
                {
                    try
                    {
                        mission = await database.Missions.ReadAsync(missionId!, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logging?.Warn("[McpTypedDecisionTools] mission lookup failed for " + missionId + ": " + ex.Message);
                    }
                }

                ParsedDecision? parsed;
                if (buildStateAndQuestionsAsync != null)
                {
                    parsed = await buildStateAndQuestionsAsync(root, mission, CancellationToken.None).ConfigureAwait(false);
                }
                else if (buildStateAndQuestions != null)
                {
                    parsed = buildStateAndQuestions(root);
                }
                else
                {
                    parsed = ParseGeneral(root);
                }

                if (parsed == null)
                    return Unavailable("invalid", "The call is missing its state or questions.");
                if (parsed.Questions.Count == 0)
                    return Unavailable("invalid", "At least one question is required.");

                TypedDecisionCaptainToolSettings toolSettings = settings.TypedDecisions.CaptainTool;

                string? participantKey = participantKeyProvider();
                // The state is redacted here, before any egress and before the hash, so a disabled or
                // dormant call still records a hash of exactly what WOULD have left, and never the state.
                RedactedDecisionState redactedDecisionState = DecisionStateRedactor.RedactState(parsed.State, toolSettings.MaxStateChars);
                object redacted = redactedDecisionState.State;
                string redactedState = redactedDecisionState.Text;

                // 1. The tool is disabled: no egress, one event, unavailable.
                if (!toolSettings.Enabled)
                {
                    await RecordAsync(recorder, decisionPoint, redactedState, mission, participantKey, TypedDecisionResultUnavailable("disabled"), "disabled").ConfigureAwait(false);
                    return Unavailable("disabled", "The typed-decision tool is not enabled. Decide it yourself.");
                }

                // 2. A helper whose decision is Off is dormant: built, but no egress until enabled.
                if (hasDecisionGate)
                {
                    ResolvedTypedDecision resolved = settings.TypedDecisions.For(decisionPoint);
                    if (resolved.Mode == TypedDecisionModeEnum.Off)
                    {
                        await RecordAsync(recorder, decisionPoint, redactedState, mission, participantKey, TypedDecisionResultUnavailable("disabled"), "dormant").ConfigureAwait(false);
                        return Unavailable("disabled", "This helper is dormant. Decide it yourself.");
                    }
                }

                // 3. Egress. There is no call cap: the provider's own limits and the fail-closed
                // unavailable path bound a runaway caller. The client never throws into us; a failure is an unavailable result.
                TypedDecisionRequest request = new TypedDecisionRequest
                {
                    DecisionPoint = decisionPoint,
                    State = redacted,
                    Questions = parsed.Questions
                };
                TypedDecisionResult result = await client.DecideAsync(request, CancellationToken.None).ConfigureAwait(false);

                string outcome = result.Available ? "delivered" : "unavailable";
                await RecordAsync(recorder, decisionPoint, redactedState, mission, participantKey, result, outcome).ConfigureAwait(false);

                if (!result.Available)
                    return Unavailable(result.UnavailableReason ?? "unavailable", "The typed-decision system did not answer. Decide it yourself.");

                return BuildAnswer(result);
            }
            catch (Exception ex)
            {
                // The tool must never throw into the captain's runtime. An unexpected failure is just
                // an unavailable result the captain treats as "decide it yourself".
                logging?.Warn("[McpTypedDecisionTools] " + decisionPoint + " failed: " + ex.Message);
                return Unavailable("exception", "The typed-decision tool encountered an error. Decide it yourself.");
            }
        }

        private static Task RecordAsync(
            TypedDecisionRecorder recorder,
            string decisionPoint,
            string redactedState,
            Mission? mission,
            string? participantKey,
            TypedDecisionResult result,
            string outcome)
        {
            string? modelVerdict = null;
            double? confidence = null;
            if (result.Available && result.Answers != null)
            {
                foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
                {
                    TypedAnswer answer = entry.Value;
                    if (answer == null) continue;
                    if (modelVerdict == null)
                        modelVerdict = String.IsNullOrWhiteSpace(answer.Choice) ? answer.Type : answer.Choice;
                    if (!confidence.HasValue && answer.Confidence.HasValue)
                        confidence = answer.Confidence;
                }
            }

            TypedDecisionEventContext context = new TypedDecisionEventContext
            {
                DecisionPoint = decisionPoint,
                RuleVerdict = "none",
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState,
                Mission = mission,
                CaptainId = mission?.CaptainId
            };

            return recorder.RecordCaptainAsync(context, outcome, CancellationToken.None);
        }

        private static ParsedDecision? ParseGeneral(JsonElement root)
        {
            if (!root.TryGetProperty("state", out JsonElement stateElement)) return null;
            if (!root.TryGetProperty("questions", out JsonElement questionsElement)) return null;

            object state = ExtractState(stateElement);
            Dictionary<string, TypedQuestion> questions = ParseQuestions(questionsElement);
            return new ParsedDecision(state, questions);
        }

        private static ParsedDecision? BuildPremiseCheck(JsonElement root)
        {
            string restatement = ReadOptionalString(root, "restatement") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(restatement)) return null;

            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["restatement"] = restatement,
                ["objective_title"] = ReadOptionalString(root, "objectiveTitle"),
                ["objective_description"] = ReadOptionalString(root, "objectiveDescription"),
                ["acceptance_criteria"] = ReadOptionalString(root, "acceptanceCriteria"),
                ["stage_persona"] = ReadOptionalString(root, "stagePersona"),
                ["preflight_facts"] = ReadOptionalRaw(root, "facts")
            };

            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["contradicts_scope"] = new NoulQuestion(
                    "The restatement contradicts the brief's scope.",
                    "contradicts the scope", "matches the scope"),
                ["assumes_absent_symbol"] = new NoulQuestion(
                    "The restatement assumes a symbol or file the preflight facts show absent at the target tip.",
                    "assumes something absent", "assumes only what is present"),
                ["names_unrequested_deliverable"] = new NoulQuestion(
                    "The restatement names a deliverable the brief does not ask for.",
                    "adds an unrequested deliverable", "asks for only what the brief asks"),
                ["missing_context"] = new ChoiceQuestion(
                    "Which missing context, if any, would stop this task from being done correctly.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["none"] = "nothing is missing; the brief and facts are enough",
                        ["repo_fact"] = "a repository fact the captain must verify is missing",
                        ["owner_ruling"] = "an open question only the owner can answer is missing"
                    })
            };

            return new ParsedDecision(state, questions);
        }

        private static ParsedDecision? BuildMemoryTriage(JsonElement root)
        {
            string candidate = ReadOptionalString(root, "candidate") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(candidate)) return null;

            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidate"] = candidate,
                ["existing_records"] = ReadOptionalRaw(root, "existingRecords")
            };

            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["type_ok"] = new NoulQuestion(
                    "The stated memory type (working, episodic, semantic, or procedural) fits this candidate, and it is not working memory that should not be stored.",
                    "the type fits", "the type is wrong"),
                ["duplicate_of"] = new NoulQuestion(
                    "The candidate duplicates a record already returned for this subject.",
                    "duplicates an existing record", "is not a duplicate"),
                ["will_go_stale"] = new NoulQuestion(
                    "The candidate contains an id, path, count, or date that stops being true, so it will go stale.",
                    "will go stale", "states a durable rule"),
                ["belongs_in_ai_memory"] = new NoulQuestion(
                    "The candidate is a fleet rule that belongs in shared external memory, not a vessel fact for native memory.",
                    "belongs in shared memory", "is a native vessel fact")
            };

            return new ParsedDecision(state, questions);
        }

        private static ParsedDecision? BuildChangeQuality(JsonElement root)
        {
            string diff = ReadOptionalString(root, "diff") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(diff)) return null;

            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["diff"] = diff
            };

            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [ChangeQualityDimensions.Dry + "_weak"] = new NoulQuestion(
                    "The change duplicates logic that already exists or repeats itself, rather than reusing or extracting a shared path.",
                    "violates DRY", "does not duplicate logic"),
                [ChangeQualityDimensions.CognitiveComplexity + "_weak"] = new NoulQuestion(
                    "The change is more cognitively complex or bloated than the work requires: deep nesting, long methods, or convoluted control flow.",
                    "is over-complex or bloated", "is about as simple as the work allows"),
                [ChangeQualityDimensions.Modularity + "_weak"] = new NoulQuestion(
                    "The change weakens module boundaries or cohesion: a type or method takes on unrelated responsibilities, or reaches across a boundary it should not.",
                    "weakens modularity", "respects module boundaries"),
                [ChangeQualityDimensions.Readability + "_weak"] = new NoulQuestion(
                    "The change is hard to read: unclear names, missing intent, or dense code a later reader would struggle with.",
                    "is hard to read", "reads clearly"),
                [ChangeQualityDimensions.Maintainability + "_weak"] = new NoulQuestion(
                    "The change will be hard to maintain or change safely later: hidden coupling, fragile assumptions, or missing seams.",
                    "harms maintainability", "is maintainable")
            };

            return new ParsedDecision(state, questions);
        }

        private static async Task<ParsedDecision?> BuildPriorArtAsync(
            JsonElement root,
            Mission? mission,
            IPriorArtRetriever? retriever,
            DatabaseDriver database,
            LoggingModule? logging,
            CancellationToken token)
        {
            string plan = ReadOptionalString(root, "plan") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(plan)) return null;

            PriorArtRetrieval retrieval = PriorArtRetrieval.Empty();
            if (retriever != null && mission != null && !String.IsNullOrWhiteSpace(mission.VesselId))
            {
                try
                {
                    Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
                    if (vessel != null)
                    {
                        PriorArtQuery query = new PriorArtQuery
                        {
                            Context = TypedPriorArtAdapter.ContextFor(vessel),
                            ExtraText = plan
                        };
                        retrieval = await retriever.RetrieveAsync(query, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logging?.Warn("[McpTypedDecisionTools] prior_art retrieval failed, no candidates: " + ex.Message);
                    retrieval = PriorArtRetrieval.Empty();
                }
            }

            object state = PriorArtDecisionShapes.BuildState(plan, retrieval);
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(
                PriorArtDecisionShapes.BuildPreflightQuestions(retrieval.Candidates.Count), StringComparer.Ordinal);
            return new ParsedDecision(state, questions);
        }

        private static Dictionary<string, TypedQuestion> ParseQuestions(JsonElement questionsElement)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            if (questionsElement.ValueKind != JsonValueKind.Object) return questions;

            foreach (JsonProperty property in questionsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                TypedQuestion? question = ParseOneQuestion(property.Value);
                if (question != null) questions[property.Name] = question;
            }

            return questions;
        }

        private static TypedQuestion? ParseOneQuestion(JsonElement element)
        {
            string type = (ReadOptionalString(element, "type") ?? "noul").Trim().ToLowerInvariant();
            string instructions = ReadOptionalString(element, "instructions") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(instructions)) return null;

            switch (type)
            {
                case "choice":
                    return new ChoiceQuestion(instructions, ReadStringMap(element, "criteria"));
                case "score":
                    IReadOnlyList<string> levels = ReadStringList(element, "criteria");
                    if (levels.Count == 0) levels = ReadStringList(element, "levels");
                    return new ScoreQuestion(instructions, levels);
                case "noul":
                default:
                    return new NoulQuestion(
                        instructions,
                        ReadOptionalString(element, "trueMeaning"),
                        ReadOptionalString(element, "falseMeaning"));
            }
        }

        private static object ExtractState(JsonElement stateElement)
        {
            if (stateElement.ValueKind == JsonValueKind.String)
                return stateElement.GetString() ?? String.Empty;
            return stateElement.GetRawText();
        }

        private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement parent, string name)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Object) return map;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? String.Empty
                    : property.Value.GetRawText();
            }
            return map;
        }

        private static IReadOnlyList<string> ReadStringList(JsonElement parent, string name)
        {
            List<string> list = new List<string>();
            if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array) return list;
            foreach (JsonElement item in element.EnumerateArray())
            {
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? String.Empty : item.GetRawText());
            }
            return list;
        }

        private static string? ReadOptionalString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement element)) return null;
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined) return null;
            return element.GetRawText();
        }

        private static object? ReadOptionalRaw(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement element)) return null;
            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined) return null;
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            return element.GetRawText();
        }

        private static TypedDecisionResult TypedDecisionResultUnavailable(string reason)
        {
            return new TypedDecisionResult
            {
                Available = false,
                UnavailableReason = reason,
                Answers = new Dictionary<string, TypedAnswer>()
            };
        }

        private static object Unavailable(string reason, string message)
        {
            return new
            {
                Available = false,
                UnavailableReason = reason,
                Message = message
            };
        }

        private static object BuildAnswer(TypedDecisionResult result)
        {
            Dictionary<string, object?> answers = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
            {
                TypedAnswer answer = entry.Value;
                if (answer == null) continue;
                answers[entry.Key] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = answer.Type,
                    ["choice"] = answer.Choice,
                    ["score"] = answer.Score,
                    ["noul"] = answer.Noul,
                    ["probabilities"] = answer.Probabilities,
                    ["confidence"] = answer.Confidence
                };
            }

            return new
            {
                Available = true,
                Answers = answers
            };
        }

        #endregion

        #region Private-Types

        private sealed class ParsedDecision
        {
            public ParsedDecision(object state, Dictionary<string, TypedQuestion> questions)
            {
                State = state;
                Questions = questions;
            }

            public object State { get; }

            public Dictionary<string, TypedQuestion> Questions { get; }
        }

        #endregion
    }
}
