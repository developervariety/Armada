namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task WriteWorkspaceBriefFilesAsync(string worktree, string folder, List<ContextBriefFile> files, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(worktree))
            {
                await WriteBriefFilesAsync(worktree, folder, files, token).ConfigureAwait(false);
                return;
            }
            HarborCheckoutAddress.TryParse(worktree, out HarborCheckoutAddress? parsed);
            Dock? dock = parsed == null ? null : await _Database.Docks.ReadAsync(parsed.DockId, token).ConfigureAwait(false);
            HarborCheckoutBinding binding = dock?.HostCheckout ?? throw new InvalidOperationException("harbor_checkout_binding_missing");
            HarborCheckoutService owner = RequireHarborCheckouts();
            HarborWorkspaceOperationResult stat = await owner.StatAsync(worktree, folder, token).ConfigureAwait(false);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("brief-files:" + binding.Revision, binding.BaseCommit))
            {
                if (stat.Exists)
                {
                    HarborWorkspaceOperationRequest list = HarborCheckoutService.CreateRequest(dock!, HarborWorkspaceOperationEnum.EnumerateFiles);
                    list.RelativePath = folder;
                    list.Recursive = true;
                    HarborWorkspaceOperationResult prior = await owner.ExecuteAsync(dock!, list, token).ConfigureAwait(false);
                    HashSet<string> retained = new HashSet<string>(files.ConvertAll(file => file.RelativePath.Replace('\\', '/')), StringComparer.Ordinal);
                    foreach (string path in prior.Paths)
                    {
                        if (retained.Contains(path)) continue;
                        HarborWorkspaceOperationRequest remove = HarborCheckoutService.CreateRequest(dock!, HarborWorkspaceOperationEnum.DeleteFile);
                        remove.RelativePath = path;
                        await owner.ExecuteAsync(dock!, remove, token).ConfigureAwait(false);
                    }
                }
                foreach (ContextBriefFile file in files)
                    await owner.WriteTextAsync(worktree, file.RelativePath, file.Content, true, token).ConfigureAwait(false);
            }
        }

        private async Task<List<string>> FindUnstagedWorkspaceBriefingPathsAsync(string? text, string worktree, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(worktree)) return FindUnstagedBriefingPaths(text, worktree);
            List<string> absent = new List<string>();
            if (String.IsNullOrEmpty(text)) return absent;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in _BriefingPathPattern.Matches(text))
            {
                string path = match.Value.TrimEnd('.');
                if (!seen.Add(path)) continue;
                HarborWorkspaceOperationResult stat = await RequireHarborCheckouts().StatAsync(worktree, path, token).ConfigureAwait(false);
                if (!stat.Exists) absent.Add(path);
            }
            return absent;
        }
    }
}
