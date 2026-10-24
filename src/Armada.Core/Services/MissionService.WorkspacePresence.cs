namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    public partial class MissionService
    {
        private async Task<HashSet<string>?> ReadOwnedInstructionNamesAsync(string worktree, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(worktree)) return null;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in _ArmadaOnlyInstructionFileNames)
                if (await WorkspaceFileExistsAsync(Path.Combine(worktree, name), token).ConfigureAwait(false)) names.Add(name);
            return names;
        }

        private async Task<HashSet<string>?> ReadOwnedBriefPathsAsync(string? text, string worktree, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(worktree)) return null;
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            if (String.IsNullOrEmpty(text)) return paths;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in _BriefingPathPattern.Matches(text))
            {
                string path = match.Value.TrimEnd('.');
                if (!seen.Add(path)) continue;
                HarborWorkspaceOperationResult stat = await RequireHarborCheckouts().StatAsync(worktree, path, token).ConfigureAwait(false);
                if (stat.Exists) paths.Add(path);
            }
            return paths;
        }
    }
}
