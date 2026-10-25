namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class AskThreadService
    {
        /// <summary>Change only the selected metadata fields on an owned thread.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="request">Selected metadata changes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public async Task<AskThread?> UpdateThreadAsync(AuthContext caller, string id, AskThreadUpdateRequest request, CancellationToken token = default)
        {
            AskThread? thread = await GetThreadAsync(caller, id, token).ConfigureAwait(false);
            if (thread == null) return null;
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.SetTitle) thread.Title = ValidateTitle(request.Title);
            if (request.SetCaptainId)
            {
                await ValidateCaptainAsync(caller, request.CaptainId, token).ConfigureAwait(false);
                thread.CaptainId = CleanCaptain(request.CaptainId);
            }
            if (request.SetPinned) thread.Pinned = request.Pinned;
            if (request.SetArchived) thread.Archived = request.Archived;
            return await _Database.AskThreads.UpdateAsync(thread, token).ConfigureAwait(false);
        }

    }
}
