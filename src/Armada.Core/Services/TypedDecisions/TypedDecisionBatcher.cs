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
    /// as a numbered list and asks each item's questions under an item-scoped key; the answers are
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
        /// The provider's question limit for one request.
        /// </summary>
        public const int MaxQuestionsPerRequest = 100;

        #endregion

        #region Private-Members

        // Characters allowed per item for the list wrapper ({"item":n,"state":...},) around its state.
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

        private static int ChunkEnd(IReadOnlyList<TypedDecisionBatchItem> items, int start, int maxStateChars)
        {
            int questions = 0;
            int stateChars = 0;
            int end = start;
            while (end < items.Count)
            {
                TypedDecisionBatchItem item = items[end];
                int itemQuestions = item.Questions.Count;
                int itemChars = item.State.Text.Length + _WrapperCharsPerItem;
                bool first = end == start;
                if (!first && (questions + itemQuestions > MaxQuestionsPerRequest || stateChars + itemChars > maxStateChars))
                    break;
                questions += itemQuestions;
                stateChars += itemChars;
                end++;
            }
            return end;
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

            List<Dictionary<string, object?>> listed = new List<Dictionary<string, object?>>(size);
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int offset = 0; offset < size; offset++)
            {
                int number = offset + 1;
                listed.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["item"] = number,
                    ["state"] = items[start + offset].State.State
                });

                string scope = "About item " + number.ToString(CultureInfo.InvariantCulture)
                    + " in the state's items list only, ignoring the other items: ";
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
