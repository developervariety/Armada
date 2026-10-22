namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class GitService
    {
        private static readonly TimeSpan _SnapshotStepTimeout = TimeSpan.FromMinutes(5);
        private const int _SnapshotOutputLimitBytes = 1024 * 1024;
        private static readonly Regex _SnapshotMissionIdPattern = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

        /// <inheritdoc />
        public async Task<DockWorkSnapshot> SnapshotDockWorkAsync(string worktreePath, string missionId, string message, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (!_SnapshotMissionIdPattern.IsMatch(missionId))
                return DockWorkSnapshot.Failed("mission id " + missionId + " cannot name a branch");
            if (!Directory.Exists(worktreePath))
                return DockWorkSnapshot.Failed("dock worktree " + worktreePath + " is missing");

            BoundedProcessResult head = await RunSnapshotGitAsync(worktreePath, null, token, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
            if (!SnapshotSucceeded(head)) return SnapshotFailure("read HEAD", head);
            string headSha = SnapshotFirstLine(head.StandardOutput);
            BoundedProcessResult headTree = await RunSnapshotGitAsync(worktreePath, null, token, "rev-parse", "--verify", "HEAD^{tree}").ConfigureAwait(false);
            if (!SnapshotSucceeded(headTree)) return SnapshotFailure("read the HEAD tree", headTree);

            // A private index built from HEAD records the working tree, untracked files included and
            // ignored files excluded, without touching the dock's own index, files or branch.
            string indexPath = Path.Combine(Path.GetTempPath(), "armada-snapshot-" + Guid.NewGuid().ToString("N") + ".index");
            string tree;
            try
            {
                BoundedProcessResult read = await RunSnapshotGitAsync(worktreePath, indexPath, token, "read-tree", headSha).ConfigureAwait(false);
                if (!SnapshotSucceeded(read)) return SnapshotFailure("build the snapshot index", read);
                BoundedProcessResult add = await RunSnapshotGitAsync(worktreePath, indexPath, token, "add", "-A", "--", ".").ConfigureAwait(false);
                if (!SnapshotSucceeded(add)) return SnapshotFailure("stage the working tree", add);
                BoundedProcessResult write = await RunSnapshotGitAsync(worktreePath, indexPath, token, "write-tree").ConfigureAwait(false);
                if (!SnapshotSucceeded(write)) return SnapshotFailure("write the snapshot tree", write);
                tree = SnapshotFirstLine(write.StandardOutput);
            }
            finally
            {
                try { if (File.Exists(indexPath)) File.Delete(indexPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _Logging.Debug(_Header + "could not delete snapshot index " + indexPath + ": " + ex.Message);
                }
            }

            if (String.Equals(tree, SnapshotFirstLine(headTree.StandardOutput), StringComparison.Ordinal))
                return DockWorkSnapshot.NoChanges();

            string prefix = "refs/heads/recover/" + missionId + "-";
            BoundedProcessResult existing = await RunSnapshotGitAsync(worktreePath, null, token,
                "for-each-ref", "--format=%(refname) %(objectname) %(tree)", prefix + "*").ConfigureAwait(false);
            if (!SnapshotSucceeded(existing)) return SnapshotFailure("list the mission's recover branches", existing);

            int highest = 0;
            string? highestRef = null;
            string? highestSha = null;
            string? highestTree = null;
            foreach (string line in existing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Trim().Split(' ');
                if (fields.Length != 3 || !fields[0].StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!Int32.TryParse(fields[0].Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) continue;
                if (number <= highest) continue;
                highest = number;
                highestRef = fields[0];
                highestSha = fields[1];
                highestTree = fields[2];
            }

            if (highestRef != null && highestSha != null && String.Equals(highestTree, tree, StringComparison.Ordinal))
                return DockWorkSnapshot.AlreadySaved(highestRef, highestSha);

            BoundedProcessResult commit = await RunSnapshotGitAsync(worktreePath, null, token,
                "-c", "user.name=Armada", "-c", "user.email=armada@users.noreply.invalid",
                "commit-tree", tree, "-p", headSha, "-m", String.IsNullOrWhiteSpace(message) ? "Armada recovery snapshot" : message).ConfigureAwait(false);
            if (!SnapshotSucceeded(commit)) return SnapshotFailure("commit the snapshot", commit);
            string snapshotSha = SnapshotFirstLine(commit.StandardOutput);

            // The empty old value makes the write fail rather than move a branch another writer created.
            string refName = prefix + (highest + 1).ToString(CultureInfo.InvariantCulture);
            BoundedProcessResult update = await RunSnapshotGitAsync(worktreePath, null, token,
                "update-ref", "-m", "armada recovery snapshot", refName, snapshotSha, String.Empty).ConfigureAwait(false);
            if (!SnapshotSucceeded(update)) return SnapshotFailure("write " + refName, update);

            return DockWorkSnapshot.Saved(refName, snapshotSha);
        }

        private DockWorkSnapshot SnapshotFailure(string step, BoundedProcessResult result)
        {
            string detail = result.TimedOut ? "timed out" : RuntimeLogFormatter.RedactSecrets(SnapshotFirstLine(result.StandardError));
            _Logging.Warn(_Header + "dock work snapshot could not " + step + ": " + detail);
            return DockWorkSnapshot.Failed("could not " + step + ": " + detail);
        }

        private static bool SnapshotSucceeded(BoundedProcessResult result)
        {
            return !result.TimedOut && result.ExitCode == 0;
        }

        private static string SnapshotFirstLine(string? text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            int end = text.IndexOf('\n');
            return (end >= 0 ? text.Substring(0, end) : text).Trim();
        }

        private static async Task<BoundedProcessResult> RunSnapshotGitAsync(string worktreePath, string? indexPath,
            CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            ProcessStartInfo startInfo = GitProcessStartInfo.Create(worktreePath, arguments);
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);
            if (indexPath != null) startInfo.Environment["GIT_INDEX_FILE"] = indexPath;
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, _SnapshotStepTimeout)
            {
                OutputLimitBytes = _SnapshotOutputLimitBytes,
                OutputShape = BoundedOutputShapeEnum.Head
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled) token.ThrowIfCancellationRequested();
            return result;
        }
    }
}
