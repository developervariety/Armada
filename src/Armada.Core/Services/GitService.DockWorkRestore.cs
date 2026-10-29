namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class GitService
    {
        /// <inheritdoc />
        public async Task<DockWorkRestore> ApplyDockWorkAsync(string worktreePath, string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (!_SnapshotMissionIdPattern.IsMatch(missionId) || !Directory.Exists(worktreePath))
                return DockWorkRestore.NothingSaved();

            string prefix = "refs/heads/recover/" + missionId + "-";
            BoundedProcessResult refs = await RunSnapshotGitAsync(worktreePath, null, token,
                "for-each-ref", "--format=%(refname) %(objectname)", prefix + "*").ConfigureAwait(false);
            if (!SnapshotSucceeded(refs)) return RestoreSkipped(null, "could not list the mission's recover branches", refs);

            int highest = 0;
            string? refName = null;
            string? snapshotSha = null;
            foreach (string line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Trim().Split(' ');
                if (fields.Length != 2 || !fields[0].StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!Int32.TryParse(fields[0].Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) continue;
                if (number <= highest) continue;
                highest = number;
                refName = fields[0];
                snapshotSha = fields[1];
            }
            if (refName == null || snapshotSha == null) return DockWorkRestore.NothingSaved();

            BoundedProcessResult head = await RunSnapshotGitAsync(worktreePath, null, token, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
            if (!SnapshotSucceeded(head)) return RestoreSkipped(refName, "could not read the checkout HEAD", head);
            BoundedProcessResult parent = await RunSnapshotGitAsync(worktreePath, null, token, "rev-parse", "--verify", snapshotSha + "^{commit}^").ConfigureAwait(false);
            if (!SnapshotSucceeded(parent)) return RestoreSkipped(refName, "could not read the parent of the saved work", parent);
            string headSha = SnapshotFirstLine(head.StandardOutput);
            string parentSha = SnapshotFirstLine(parent.StandardOutput);
            if (!String.Equals(headSha, parentSha, StringComparison.Ordinal))
                return DockWorkRestore.Skipped(refName, "the saved work was made on " + parentSha + " but the checkout is at " + headSha);

            BoundedProcessResult status = await RunSnapshotGitAsync(worktreePath, null, token, "status", "--porcelain").ConfigureAwait(false);
            if (!SnapshotSucceeded(status)) return RestoreSkipped(refName, "could not read the checkout status", status);
            if (!String.IsNullOrWhiteSpace(status.StandardOutput))
                return DockWorkRestore.Skipped(refName, "the checkout already holds uncommitted changes");

            BoundedProcessResult pick = await RunSnapshotGitAsync(worktreePath, null, token,
                "-c", "user.name=Armada", "-c", "user.email=armada@users.noreply.invalid",
                "cherry-pick", "-n", snapshotSha).ConfigureAwait(false);
            if (!SnapshotSucceeded(pick))
            {
                // The checkout was clean, so restoring it to HEAD discards only what the failed pick wrote.
                await RunSnapshotGitAsync(worktreePath, null, token, "cherry-pick", "--abort").ConfigureAwait(false);
                await RunSnapshotGitAsync(worktreePath, null, token, "reset", "--hard", headSha).ConfigureAwait(false);
                await RunSnapshotGitAsync(worktreePath, null, token, "clean", "-fd").ConfigureAwait(false);
                return RestoreSkipped(refName, "applying the saved work conflicted", pick);
            }

            // The pick stages its changes; unstaging leaves the edits, deletions and new files as the
            // working-tree changes the stopped captain left.
            BoundedProcessResult unstage = await RunSnapshotGitAsync(worktreePath, null, token, "reset", "-q").ConfigureAwait(false);
            if (!SnapshotSucceeded(unstage)) _Logging.Warn(_Header + "applied dock work stays staged in " + worktreePath + ": " + SnapshotFirstLine(unstage.StandardError));
            return DockWorkRestore.Applied(refName);
        }

        private DockWorkRestore RestoreSkipped(string? refName, string step, BoundedProcessResult result)
        {
            string detail = result.TimedOut ? "timed out" : RuntimeLogFormatter.RedactSecrets(SnapshotFirstLine(result.StandardError));
            _Logging.Warn(_Header + "dock work restore: " + step + ": " + detail);
            return DockWorkRestore.Skipped(refName, step + ": " + detail);
        }
    }
}
