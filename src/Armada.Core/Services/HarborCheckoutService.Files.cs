namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>Read complete bounded text from a relative file on its checkout owner.</summary>
        public async Task<string> ReadTextAsync(string address, string relativePath, CancellationToken token = default)
        {
            HarborWorkspaceOperationResult result = await ExecuteFileAsync(address, relativePath,
                HarborWorkspaceOperationEnum.ReadText, null, token).ConfigureAwait(false);
            return result.Text ?? throw new InvalidOperationException("harbor_file_result_missing");
        }

        /// <summary>Read entry metadata on its checkout owner.</summary>
        public Task<HarborWorkspaceOperationResult> StatAsync(string address, string relativePath, CancellationToken token = default)
        {
            return ExecuteFileAsync(address, relativePath, HarborWorkspaceOperationEnum.Stat, null, token);
        }

        /// <summary>Write bounded text under an explicit mutation scope.</summary>
        public Task<HarborWorkspaceOperationResult> WriteTextAsync(string address, string relativePath, string content, bool overwrite = true, CancellationToken token = default)
        {
            return ExecuteFileAsync(address, relativePath, HarborWorkspaceOperationEnum.WriteText, content, token, overwrite);
        }

        /// <summary>Add a generated-artifact exclusion through the checkout owner.</summary>
        public Task<HarborWorkspaceOperationResult> AddGitExcludeAsync(string address, string relativePath, CancellationToken token = default)
        {
            return ExecuteFileAsync(address, relativePath, HarborWorkspaceOperationEnum.AddGitExclude, null, token);
        }

        private async Task<HarborWorkspaceOperationResult> ExecuteFileAsync(string address, string relativePath,
            HarborWorkspaceOperationEnum operation, string? content, CancellationToken token, bool overwrite = false)
        {
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null
                || parsed.UseRepository || parsed.UseWorkingDirectory || parsed.CheckoutId != null || parsed.RelativePath.Length > 0)
                throw new InvalidOperationException("harbor_checkout_address_invalid");
            Dock dock = await ReadDockAsync(parsed.DockId, token).ConfigureAwait(false);
            HarborWorkspaceOperationRequest request = CreateRequest(dock, operation);
            request.RelativePath = String.IsNullOrEmpty(relativePath) ? "." : relativePath.Replace('\\', '/');
            request.Text = content;
            request.Overwrite = overwrite;
            return await ExecuteAsync(dock, request, token).ConfigureAwait(false);
        }
    }
}
