namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A single typed-decision request sent to the typed-decision client (TypeSafe Jev). The
    /// caller builds the state and questions; the client transmits them. The state must already
    /// be redacted before it reaches the client — see <c>DecisionStateRedactor</c>.
    /// </summary>
    public sealed class TypedDecisionRequest
    {
        /// <summary>
        /// The decision point being consulted (for example <c>failure_cause</c>). Names a row in
        /// the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public required string DecisionPoint { get; init; }

        /// <summary>
        /// The already-redacted decision state. Serialized as the request body's <c>state</c>.
        /// </summary>
        public required object State { get; init; }

        /// <summary>
        /// The questions to answer, keyed by question id. Each question is a choice, score, or noul.
        /// </summary>
        public required IReadOnlyDictionary<string, TypedQuestion> Questions { get; init; }
    }

    /// <summary>
    /// Base type for a typed-decision question. Carries the plain-language instructions the model
    /// answers against.
    /// </summary>
    /// <param name="Instructions">What the model is being asked to decide.</param>
    public abstract record TypedQuestion(string Instructions);

    /// <summary>
    /// A question answered by selecting one named option.
    /// </summary>
    /// <param name="Instructions">What the model is being asked to decide.</param>
    /// <param name="Criteria">The allowed options keyed by option name, each with its meaning.</param>
    public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria)
        : TypedQuestion(Instructions);

    /// <summary>
    /// A question answered by a numeric score within an ordered set of levels.
    /// </summary>
    /// <param name="Instructions">What the model is being asked to decide.</param>
    /// <param name="Levels">The ordered level labels, lowest first.</param>
    public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels)
        : TypedQuestion(Instructions);

    /// <summary>
    /// A question answered by a numeric-of-unit (noul) value, optionally with a stated meaning for
    /// the true and false poles.
    /// </summary>
    /// <param name="Instructions">What the model is being asked to decide.</param>
    /// <param name="TrueMeaning">Optional meaning of the high pole.</param>
    /// <param name="FalseMeaning">Optional meaning of the low pole.</param>
    public sealed record NoulQuestion(string Instructions, string? TrueMeaning = null, string? FalseMeaning = null)
        : TypedQuestion(Instructions);

    /// <summary>
    /// The result of a typed-decision call. When <see cref="Available"/> is false the caller falls
    /// back to its deterministic rule; the client never throws into a caller.
    /// </summary>
    public sealed class TypedDecisionResult
    {
        /// <summary>
        /// Whether the model produced a usable answer. False means the caller must use its rule.
        /// </summary>
        public bool Available { get; init; }

        /// <summary>
        /// When unavailable, the reason: <c>disabled</c>, <c>timeout</c>, <c>http_429</c>,
        /// <c>http_529</c>, <c>http_401</c>, <c>http_422</c>, <c>parse</c>,
        /// <c>response_validation</c>, <c>exception</c>, or a
        /// generic <c>http_&lt;code&gt;</c>.
        /// </summary>
        public string? UnavailableReason { get; init; }

        /// <summary>
        /// When the provider rejected the request, the provider's own short explanation (for example
        /// which question field was invalid), redacted and capped. Null when no explanation was sent.
        /// </summary>
        public string? UnavailableDetail { get; init; }

        /// <summary>
        /// The answers keyed by question id. Empty when unavailable.
        /// </summary>
        public IReadOnlyDictionary<string, TypedAnswer> Answers { get; init; } = new Dictionary<string, TypedAnswer>();

        /// <summary>
        /// The concrete model version the provider reported running (for example <c>jev-1.13.0</c>),
        /// which can differ from the requested alias. Null when unavailable or not reported.
        /// </summary>
        public string? Model { get; init; }

        /// <summary>
        /// Prompt tokens the provider reported consuming.
        /// </summary>
        public int InputTokens { get; init; }

        /// <summary>
        /// Completion tokens the provider reported producing.
        /// </summary>
        public int OutputTokens { get; init; }

        /// <summary>
        /// Wall-clock latency of the call in milliseconds.
        /// </summary>
        public long LatencyMs { get; init; }

        /// <summary>
        /// How many independent items shared the provider request this result came from. One for a
        /// decision asked on its own; token counts on a batched item are its share of the request.
        /// </summary>
        public int BatchSize { get; init; } = 1;

        /// <summary>
        /// The unavailable result an adapter falls back to when the client throws into a caller despite
        /// its no-throw contract. The caller then keeps its deterministic rule and records an unavailable
        /// event. Every adapter uses this one factory so the exception fallback is defined in a single
        /// place.
        /// </summary>
        /// <returns>An unavailable result carrying the <c>exception</c> reason.</returns>
        public static TypedDecisionResult Exception()
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
        }
    }

    /// <summary>
    /// One answer to one typed-decision question. Exactly one of <see cref="Choice"/>,
    /// <see cref="Score"/>, or <see cref="Noul"/> is populated, per <see cref="Type"/>.
    /// </summary>
    public sealed class TypedAnswer
    {
        /// <summary>
        /// The answer kind: <c>choice</c>, <c>score</c>, or <c>noul</c>.
        /// </summary>
        public required string Type { get; init; }

        /// <summary>
        /// The selected option name, when <see cref="Type"/> is <c>choice</c>.
        /// </summary>
        public string? Choice { get; init; }

        /// <summary>
        /// The numeric score, when <see cref="Type"/> is <c>score</c>.
        /// </summary>
        public double? Score { get; init; }

        /// <summary>
        /// The noul value, when <see cref="Type"/> is <c>noul</c>.
        /// </summary>
        public double? Noul { get; init; }

        /// <summary>
        /// Optional probability mass over the choice options.
        /// </summary>
        public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

        /// <summary>
        /// Optional model confidence in [0, 1].
        /// </summary>
        public double? Confidence { get; init; }
    }
}
