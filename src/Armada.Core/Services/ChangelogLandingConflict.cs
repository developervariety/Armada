namespace Armada.Core.Services
{
    /// <summary>Resolve only a CHANGELOG.md content conflict, preserving both sides with Git's union algorithm.</summary>
    public static class ChangelogLandingConflict
    {
        /// <summary>Finish a stopped landing merge when CHANGELOG.md is the only unresolved path.</summary>
        public static async Task<bool> TryResolveAsync(IHostCommandExecutor executor, string worktree, bool windows, CancellationToken token = default)
        {
            async Task<HostCommandResult> Git(params string[] args) => await executor.RunAsync(new HostCommandRequest
            {
                Executable = "git", WorkingDirectory = worktree, Arguments = new List<string>(args), TimeoutMs = 120000
            }, token).ConfigureAwait(false);
            HostCommandResult conflicts = await Git("diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
            if (!conflicts.Success || conflicts.StandardOutput.Trim() != "CHANGELOG.md") return false;
            // Require all three stages. Delete/modify and add/add conflicts need human review.
            foreach (string stage in new[] { "1", "2", "3" })
                if (!(await Git("cat-file", "-e", ":" + stage + ":CHANGELOG.md").ConfigureAwait(false)).Success) return false;

            string prefix = ".armada-changelog-" + Guid.NewGuid().ToString("N");
            async Task<HostCommandResult> Shell(string script, CancellationToken ct) => await executor.RunAsync(new HostCommandRequest
            {
                Executable = windows ? "cmd.exe" : "/bin/sh", WorkingDirectory = worktree,
                Arguments = new List<string> { windows ? "/c" : "-c", script }, TimeoutMs = 120000
            }, ct).ConfigureAwait(false);
            try
            {
                string script = "git show :2:CHANGELOG.md > " + prefix + "-ours && git show :1:CHANGELOG.md > " + prefix
                    + "-base && git show :3:CHANGELOG.md > " + prefix + "-theirs && git merge-file --union " + prefix + "-ours "
                    + prefix + "-base " + prefix + "-theirs && " + (windows ? "move /y " : "mv ") + prefix + "-ours CHANGELOG.md";
                if (!(await Shell(script, token).ConfigureAwait(false)).Success) return false;
                if (!(await Git("add", "--", "CHANGELOG.md").ConfigureAwait(false)).Success) return false;
                return (await Git("-c", "core.editor=true", "commit", "--no-edit").ConfigureAwait(false)).Success;
            }
            finally
            {
                await Shell((windows ? "del /q " : "rm -f ") + prefix + "-ours " + prefix + "-base " + prefix + "-theirs", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
