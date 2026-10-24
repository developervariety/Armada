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
        private HarborCheckoutService? _HarborCheckouts;

        /// <summary>Attach the checkout owner provider before missions can be assigned.</summary>
        /// <param name="checkouts">Authenticated owner-bound checkout provider.</param>
        public void SetHarborCheckouts(HarborCheckoutService checkouts)
        {
            _HarborCheckouts = checkouts ?? throw new ArgumentNullException(nameof(checkouts));
        }

        private HarborCheckoutService RequireHarborCheckouts() => _HarborCheckouts
            ?? throw new InvalidOperationException("harbor_workspace_unavailable");

        private static string WorkspaceAddress(string path, out string relative)
        {
            string normalized = path.Replace('\\', '/');
            const string prefix = "harbor:/dock/";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_checkout_address_invalid");
            int separator = normalized.IndexOf('/', prefix.Length);
            string address = separator < 0 ? normalized : normalized.Substring(0, separator);
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null)
                throw new InvalidOperationException("harbor_checkout_address_invalid");
            relative = separator < 0 ? String.Empty : normalized.Substring(separator + 1);
            return address;
        }

        private async Task<bool> WorkspaceFileExistsAsync(string path, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(path)) return File.Exists(path);
            string address = WorkspaceAddress(path, out string relative);
            HarborWorkspaceOperationResult stat = await RequireHarborCheckouts().StatAsync(address, relative, token).ConfigureAwait(false);
            return stat.Exists && !stat.IsDirectory;
        }

        private async Task<string> ReadWorkspaceTextAsync(string path, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(path)) return await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            string address = WorkspaceAddress(path, out string relative);
            return await RequireHarborCheckouts().ReadTextAsync(address, relative, token).ConfigureAwait(false);
        }

        private async Task WriteWorkspaceTextAsync(string path, string text, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text, token).ConfigureAwait(false);
                return;
            }
            string address = WorkspaceAddress(path, out string relative);
            HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed);
            Dock? dock = await _Database.Docks.ReadAsync(parsed!.DockId, token).ConfigureAwait(false);
            HarborCheckoutBinding binding = dock?.HostCheckout ?? throw new InvalidOperationException("harbor_checkout_binding_missing");
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("brief:" + binding.Revision, binding.BaseCommit))
            {
                await RequireHarborCheckouts().WriteTextAsync(address, relative, text, true, token).ConfigureAwait(false);
            }
        }

        private async Task AddWorkspaceExcludeAsync(string worktree, string entry, CancellationToken token)
        {
            if (!HarborCheckoutAddress.IsHarbor(worktree))
            {
                string? exclude = GitInfoExclude.ResolvePath(worktree);
                if (exclude != null) await GitInfoExclude.EnsureEntryAsync(exclude, entry, token).ConfigureAwait(false);
                return;
            }
            string address = WorkspaceAddress(worktree, out string relative);
            if (relative.Length > 0) throw new InvalidOperationException("harbor_checkout_address_invalid");
            HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed);
            Dock? dock = await _Database.Docks.ReadAsync(parsed!.DockId, token).ConfigureAwait(false);
            HarborCheckoutBinding binding = dock?.HostCheckout ?? throw new InvalidOperationException("harbor_checkout_binding_missing");
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("brief-exclude:" + binding.Revision, binding.BaseCommit))
            {
                await RequireHarborCheckouts().AddGitExcludeAsync(address, entry, token).ConfigureAwait(false);
            }
        }
    }
}
