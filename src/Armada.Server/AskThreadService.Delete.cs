namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadService
    {
        /// <summary>Most threads one bulk delete request may name.</summary>
        public const int MaxDeleteBatch = 100;

        /// <summary>Delete one archived thread the human caller owns, with its turns and proposals.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The outcome and, when the thread was kept or absent, the reason in words.</returns>
        public async Task<AskThreadDeleteResult> DeleteThreadAsync(AuthContext caller, string id, CancellationToken token = default)
        {
            RequireOwner(caller);
            AskThread? thread = await _Database.AskThreads.ReadAsync(caller.TenantId!, caller.UserId!, id, token).ConfigureAwait(false);
            if (thread == null) return new AskThreadDeleteResult(AskThreadDeleteOutcomeEnum.NotFound, "Ask thread not found.");
            AskThreadDeleteOutcomeEnum outcome = await _Database.AskThreads.TryDeleteAsync(caller.TenantId!, caller.UserId!, thread.Id, token).ConfigureAwait(false);
            if (outcome != AskThreadDeleteOutcomeEnum.Deleted) return new AskThreadDeleteResult(outcome, RefusalText(outcome, thread.Id));
            await RecordDeletedAsync(caller, thread, token).ConfigureAwait(false);
            return new AskThreadDeleteResult(AskThreadDeleteOutcomeEnum.Deleted, String.Empty);
        }

        /// <summary>Delete several threads. A thread that cannot be deleted is reported as skipped with its reason.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="ids">Thread identifiers; at most <see cref="MaxDeleteBatch"/> distinct values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The deleted count and one skipped entry per refused identifier.</returns>
        public async Task<DeleteMultipleResult> DeleteThreadsAsync(AuthContext caller, IReadOnlyList<string> ids, CancellationToken token = default)
        {
            RequireOwner(caller);
            if (ids == null || ids.Count == 0) throw new ArgumentException("At least one thread identifier is required.");
            List<string> distinct = ids.Select(value => value ?? String.Empty).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > MaxDeleteBatch) throw new ArgumentException("At most " + MaxDeleteBatch + " threads can be deleted in one request.");
            DeleteMultipleResult result = new DeleteMultipleResult();
            foreach (string id in distinct)
            {
                if (String.IsNullOrWhiteSpace(id))
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, "Empty ID"));
                    continue;
                }
                AskThreadDeleteResult deletion = await DeleteThreadAsync(caller, id, token).ConfigureAwait(false);
                if (deletion.Outcome == AskThreadDeleteOutcomeEnum.Deleted) result.Deleted++;
                else result.Skipped.Add(new DeleteMultipleSkipped(id, deletion.Message));
            }
            result.ResolveStatus();
            return result;
        }

        private bool HasLiveRuntime(string threadId)
        {
            string prefix = threadId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + threadId;
            return _Active.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        private string RefusalText(AskThreadDeleteOutcomeEnum outcome, string threadId)
        {
            switch (outcome)
            {
                case AskThreadDeleteOutcomeEnum.NotFound: return "Ask thread not found.";
                case AskThreadDeleteOutcomeEnum.NotArchived: return "Only archived conversations can be deleted.";
                case AskThreadDeleteOutcomeEnum.OpenTurn:
                    return HasLiveRuntime(threadId) ? "The conversation has an active turn." : "The conversation has an uncertain turn whose outcome is unknown.";
                case AskThreadDeleteOutcomeEnum.OpenProposal: return "The conversation has a proposal that is waiting for a decision or still executing.";
                default: return String.Empty;
            }
        }

        private async Task RecordDeletedAsync(AuthContext caller, AskThread thread, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent("ask_thread.deleted", "Deleted Ask conversation \"" + thread.Title + "\"")
            {
                TenantId = caller.TenantId,
                UserId = caller.UserId,
                EntityType = "ask_thread",
                EntityId = thread.Id,
                Payload = JsonSerializer.Serialize(new { threadId = thread.Id, title = thread.Title })
            };
            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }
    }
}
