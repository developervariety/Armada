namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<string?> StageOwnedFilesAsync(Mission mission, Dock dock, CancellationToken token)
        {
            HarborCheckoutBinding binding = dock.HostCheckout ?? throw new InvalidOperationException("harbor_checkout_binding_missing");
            try
            {
                using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("prestage:" + binding.Revision, binding.BaseCommit))
                {
                    foreach (PrestagedFile entry in mission.PrestagedFiles!)
                    {
                        HarborWorkspaceOperationRequest write = await CreateOwnedPrestageRequestAsync(dock, entry, token).ConfigureAwait(false);
                        await RequireHarborCheckouts().ExecuteAsync(dock, write, token).ConfigureAwait(false);
                    }
                }
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { return "harbor_prestaging_failed: " + exception.Message; }
        }

        private static async Task<HarborWorkspaceOperationRequest> CreateOwnedPrestageRequestAsync(Dock dock,
            PrestagedFile entry, CancellationToken token)
        {
            HarborWorkspaceOperationRequest write = HarborCheckoutService.CreateRequest(dock,
                entry.Content != null ? HarborWorkspaceOperationEnum.WriteText : HarborWorkspaceOperationEnum.WriteBytes);
            write.RelativePath = entry.DestPath;
            write.Overwrite = false;
            write.ReadOnly = entry.ReadOnly;
            if (entry.Content != null)
                write.Text = entry.Content.Replace("\r\n", "\n").Replace("\r", "\n");
            else
            {
                FileInfo source = new FileInfo(entry.SourcePath ?? String.Empty);
                if (!source.Exists || source.Length > write.MaxBytes)
                    throw new InvalidOperationException("harbor_prestaged_source_unavailable_or_too_large");
                byte[] bytes = await File.ReadAllBytesAsync(source.FullName, token).ConfigureAwait(false);
                if (bytes.Length > write.MaxBytes)
                    throw new InvalidOperationException("harbor_prestaged_source_unavailable_or_too_large");
                write.BytesBase64 = Convert.ToBase64String(bytes);
            }
            return write;
        }

        private async Task<string> MaterializeOwnedReferencePlaybookAsync(string worktree, MissionPlaybookSnapshot snapshot,
            int selectionOrder, CancellationToken token)
        {
            string relative = ".armada/playbooks/" + BuildMaterializedPlaybookFileName(selectionOrder, snapshot.FileName);
            await WriteWorkspaceTextAsync(Path.Combine(worktree, relative), snapshot.Content, token).ConfigureAwait(false);
            await AddWorkspaceExcludeAsync(worktree, ".armada/playbooks/", token).ConfigureAwait(false);
            return relative;
        }
    }
}
