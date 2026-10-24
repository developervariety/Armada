namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Removes only a journal-owned checkout with a confirmed Git result.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task RemoveCheckoutAsync(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutIntent intent,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            if (String.Equals(intent.State, "Removed", StringComparison.Ordinal))
            {
                result.Binding = intent.Binding;
                result.Exists = false;
                result.Accepted = true;
                return;
            }
            if (!String.Equals(intent.State, "Ready", StringComparison.Ordinal))
                throw Failure("workspace_checkout_uncertain", "The checkout is not in a confirmed removable state.");
            string path = CheckoutPath(grant, intent.Binding, requireExists: true);
            await CheckExpectedHeadAsync(request, path, token).ConfigureAwait(false);
            intent.State = "Removing";
            intent.UpdatedUtc = DateTime.UtcNow;
            _Journal.Update(intent);
            try
            {
                HarborWorkspaceProcessResult remove = await RunGitAsync(grant.RepositoryPath,
                    new[] { "worktree", "remove", path }, TimeSpan.FromSeconds(120), 64 * 1024, token).ConfigureAwait(false);
                RequireSuccess(remove, "workspace_checkout_remove_failed");
                if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
                    throw Failure("workspace_checkout_remove_unconfirmed", "Git did not remove the owned checkout directory.");
                intent.State = "Removed";
                intent.FailureReason = null;
                intent.UpdatedUtc = DateTime.UtcNow;
                _Journal.Update(intent);
                result.Binding = intent.Binding;
                result.Exists = false;
                result.Accepted = true;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                intent.State = "Uncertain";
                intent.FailureReason = exception is HarborJobJournalException journalException
                    ? journalException.ReasonCode
                    : "workspace_checkout_remove_uncertain";
                intent.UpdatedUtc = DateTime.UtcNow;
                _Journal.Update(intent);
                throw Failure("workspace_checkout_remove_uncertain", "The checkout removal did not prove a complete result.");
            }
        }

        private static string BoundUtf8(string value, int maximumBytes, out bool truncated)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            truncated = bytes.Length > maximumBytes;
            if (!truncated) return value;
            Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
            char[] characters = new char[value.Length];
            decoder.Convert(bytes, 0, maximumBytes, characters, 0, characters.Length, false,
                out int bytesUsed, out int charactersUsed, out bool completed);
            return new string(characters, 0, charactersUsed);
        }
    }
}
