namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Answers many independent typed-decision items with as few provider requests as the limits
    /// allow. Items are packed in order into requests of at most <see cref="MaxQuestionsPerRequest"/>
    /// questions whose combined state stays within the per-request state budget, so batching never
    /// sends more state in one request than a single decision may. A packed request carries the items
    /// as a JSON <c>items</c> array and names each question at its 0-based path; the answers are
    /// split back so every item receives its own result.
    ///
    /// Only independent items may be batched: a decision whose later item depends on an earlier item's
    /// outcome must keep calling one at a time. A request of one item is sent exactly as that item
    /// alone. Requests are sent in order, and the first unavailable request ends the pass: it and every
    /// later item report that unavailable result, so a caller that stops at its first unavailable item
    /// records exactly one unavailable event. Never throws into the caller.
    /// </summary>
    public static class TypedDecisionBatcher
    {
        #region Public-Members

        /// <summary>
        /// Armada's question-count safeguard for one request.
        /// </summary>
        public const int MaxQuestionsPerRequest = 100;

        /// <summary>
        /// The character budget for one request: state plus question text. Sized from measured traffic
        /// against the provider's state-plus-longest-question limit: redacted state runs about three bytes per
        /// token and question prose about four, so 80,000 characters stays near 25,000 tokens with room
        /// to spare. The state budget alone cannot keep a batch under the limit, because a batch's
        /// questions grow with its item count and a large question set costs thousands of tokens.
        /// </summary>
        public const int MaxRequestChars = 80000;

        #endregion

        #region Private-Members

        // Characters allowed per item for the JSON array wrapper around its state.
        private const int _WrapperCharsPerItem = 32;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide every item, batching them into as few requests as the question and state limits allow.
        /// </summary>
        /// <param name="client">The typed-decision client.</param>
        /// <param name="decisionPoint">The decision point every item belongs to.</param>
        /// <param name="items">The independent items, in order.</param>
        /// <param name="maxStateChars">The per-request state budget.</param>
        /// <param name="token">Cancellation token, forwarded to the client.</param>
        /// <returns>One result per item, in the same order; never null.</returns>
        public static async Task<List<TypedDecisionResult>> DecideAllAsync(
            ITypedDecisionClient client,
            string decisionPoint,
            IReadOnlyList<TypedDecisionBatchItem> items,
            int maxStateChars,
            CancellationToken token)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            List<TypedDecisionResult> results = new List<TypedDecisionResult>();
            if (items == null || items.Count == 0) return results;

            int start = 0;
            while (start < items.Count)
            {
                int end = ChunkEnd(items, start, maxStateChars);
                int size = end - start;

                TypedDecisionResult shared;
                try
                {
                    shared = await client.DecideAsync(BuildRequest(decisionPoint, items, start, size), token).ConfigureAwait(false)
                        ?? Unavailable("exception", null);
                }
                catch (Exception)
                {
                    shared = Unavailable("exception", null);
                }

                // A batch the provider rejected as too large is split in half and each half decided on its own,
                // rather than failing every item in it. A single item cannot be split here.
                if (!shared.Available
                    && String.Equals(shared.UnavailableReason, TypedDecisionAdapterRequestTooLarge, StringComparison.Ordinal)
                    && size > 1)
                {
                    int half = size / 2;
                    List<TypedDecisionBatchItem> left = new List<TypedDecisionBatchItem>();
                    List<TypedDecisionBatchItem> right = new List<TypedDecisionBatchItem>();
                    for (int offset = 0; offset < size; offset++)
                        (offset < half ? left : right).Add(items[start + offset]);

                    List<TypedDecisionResult> leftResults = await DecideAllAsync(client, decisionPoint, left, maxStateChars, token).ConfigureAwait(false);
                    results.AddRange(leftResults);
                    if (leftResults.Exists(r => !r.Available)) { FillUnavailable(results, items.Count, leftResults.Find(r => !r.Available)!); return results; }

                    List<TypedDecisionResult> rightResults = await DecideAllAsync(client, decisionPoint, right, maxStateChars, token).ConfigureAwait(false);
                    results.AddRange(rightResults);
                    if (rightResults.Exists(r => !r.Available)) { FillUnavailable(results, items.Count, rightResults.Find(r => !r.Available)!); return results; }

                    start = end;
                    continue;
                }

                if (!shared.Available)
                {
                    TypedDecisionResult unavailable = new TypedDecisionResult
                    {
                        Available = false,
                        UnavailableReason = shared.UnavailableReason,
                        UnavailableDetail = shared.UnavailableDetail,
                        LatencyMs = shared.LatencyMs,
                        BatchSize = size
                    };
                    while (results.Count < items.Count) results.Add(unavailable);
                    return results;
                }

                for (int offset = 0; offset < size; offset++)
                    results.Add(SplitResult(shared, offset, size));

                start = end;
            }

            return results;
        }

        #endregion

        #region Private-Methods

        // Same label the client gives a provider rejection; the adapter skeleton retries on it too.
        private const string TypedDecisionAdapterRequestTooLarge = "http_400";

        private static void FillUnavailable(List<TypedDecisionResult> results, int total, TypedDecisionResult unavailable)
        {
            while (results.Count < total) results.Add(unavailable);
        }

        private static int ChunkEnd(IReadOnlyList<TypedDecisionBatchItem> items, int start, int maxStateChars)
        {
            int questions = 0;
            int stateChars = 0;
            int requestChars = 0;
            int end = start;
            while (end < items.Count)
            {
                TypedDecisionBatchItem item = items[end];
                int itemQuestions = item.Questions.Count;
                int itemChars = item.State.Text.Length + _WrapperCharsPerItem;
                int itemRequestChars = itemChars + QuestionChars(item.Questions);
                bool first = end == start;

                // The first item always goes, alone if it must: splitting one item is not possible, and its
                // own state is already capped by the state budget.
                if (!first && (questions + itemQuestions > MaxQuestionsPerRequest
                    || stateChars + itemChars > maxStateChars
                    || requestChars + itemRequestChars > MaxRequestChars))
                    break;
                questions += itemQuestions;
                stateChars += itemChars;
                requestChars += itemRequestChars;
                end++;
            }
            return end;
        }

        /// <summary>
        /// The characters a question set adds to a request: every instruction and every option's text.
        /// </summary>
        /// <param name="questions">The questions, keyed by id.</param>
        /// <returns>The character count.</returns>
        internal static int QuestionChars(IReadOnlyDictionary<string, TypedQuestion> questions)
        {
            if (questions == null) return 0;
            int chars = 0;
            foreach (KeyValuePair<string, TypedQuestion> entry in questions)
            {
                chars += entry.Key.Length;
                switch (entry.Value)
                {
                    case NoulQuestion noul:
                        chars += (noul.Instructions?.Length ?? 0) + (noul.TrueMeaning?.Length ?? 0) + (noul.FalseMeaning?.Length ?? 0);
                        break;
                    case ChoiceQuestion choice:
                        chars += choice.Instructions?.Length ?? 0;
                        if (choice.Criteria != null)
                            foreach (KeyValuePair<string, string> option in choice.Criteria) chars += option.Key.Length + (option.Value?.Length ?? 0);
                        break;
                    case ScoreQuestion score:
                        chars += score.Instructions?.Length ?? 0;
                        if (score.Levels != null)
                            foreach (string level in score.Levels) chars += level?.Length ?? 0;
                        break;
                    default:
                        chars += entry.Value?.Instructions?.Length ?? 0;
                        break;
                }
            }
            return chars;
        }

        private static TypedDecisionRequest BuildRequest(string decisionPoint, IReadOnlyList<TypedDecisionBatchItem> items, int start, int size)
        {
            if (size == 1)
            {
                return new TypedDecisionRequest
                {
                    DecisionPoint = decisionPoint,
                    State = items[start].State.State,
                    Questions = items[start].Questions
                };
            }

            // Pack the item states as a JSON array and point each question at `items[i]` by
            // 0-based index. A wrapper object plus "ignore the other items" forces a count and a
            // hop; naming the path lets code own the tally, the same shape as one Noul per real item.
            List<object> listed = new List<object>(size);
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int offset = 0; offset < size; offset++)
            {
                listed.Add(items[start + offset].State.State);

                string scope = "This question's state is `items[" + offset.ToString(CultureInfo.InvariantCulture) + "]` only. ";
                foreach (KeyValuePair<string, TypedQuestion> entry in items[start + offset].Questions)
                    questions[KeyPrefix(offset) + entry.Key] = entry.Value with { Instructions = scope + entry.Value.Instructions };
            }

            return new TypedDecisionRequest
            {
                DecisionPoint = decisionPoint,
                State = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = listed },
                Questions = questions
            };
        }

        private static TypedDecisionResult SplitResult(TypedDecisionResult shared, int offset, int size)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            if (size == 1)
            {
                foreach (KeyValuePair<string, TypedAnswer> entry in shared.Answers) answers[entry.Key] = entry.Value;
            }
            else
            {
                string prefix = KeyPrefix(offset);
                foreach (KeyValuePair<string, TypedAnswer> entry in shared.Answers)
                    if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                        answers[entry.Key.Substring(prefix.Length)] = entry.Value;
            }

            // Token usage is shared by the request; each item carries an even share (the first takes any
            // remainder) so summing the per-item events reproduces the request's true usage.
            return new TypedDecisionResult
            {
                Available = true,
                Answers = answers,
                Model = shared.Model,
                Provenance = shared.Provenance == null ? null : shared.Provenance with
                {
                    BatchItemIndex = size == 1 ? null : offset
                },
                InputTokens = Share(shared.InputTokens, offset, size),
                OutputTokens = Share(shared.OutputTokens, offset, size),
                LatencyMs = shared.LatencyMs,
                BatchSize = size
            };
        }

        private static int Share(int total, int offset, int size)
        {
            int each = total / size;
            return offset == 0 ? each + total % size : each;
        }

        private static string KeyPrefix(int offset)
        {
            return "item" + (offset + 1).ToString(CultureInfo.InvariantCulture) + "__";
        }

        private static TypedDecisionResult Unavailable(string reason, string? detail)
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = reason, UnavailableDetail = detail };
        }

        #endregion
    }
}
