namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Saved conversations that retain the current captain chat runtime and caller scope.</summary>
    public sealed partial class AskThreadService
    {
        private readonly DatabaseDriver _Database;
        private readonly CaptainChatService _Chat;
        private readonly ConcurrentDictionary<string, bool> _Active = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Create the saved conversation service.</summary>
        /// <param name="database">Durable thread store.</param>
        /// <param name="chat">Existing scoped captain chat service.</param>
        public AskThreadService(DatabaseDriver database, CaptainChatService chat)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Chat = chat ?? throw new ArgumentNullException(nameof(chat));
        }

        /// <summary>Create a thread owned by the human caller.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="request">Initial metadata.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public async Task<AskThread> CreateThreadAsync(AuthContext caller, AskThreadCreateRequest request, CancellationToken token = default)
        {
            RequireOwner(caller);
            if (request == null) throw new ArgumentNullException(nameof(request));
            await ValidateCaptainAsync(caller, request.CaptainId, token).ConfigureAwait(false);
            AskThread thread = new AskThread
            {
                TenantId = caller.TenantId!, UserId = caller.UserId!,
                Title = ValidateTitle(request.Title ?? "New conversation"), CaptainId = CleanCaptain(request.CaptainId)
            };
            return await _Database.AskThreads.CreateAsync(thread, token).ConfigureAwait(false);
        }

        /// <summary>Read metadata for a thread owned by the human caller.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public Task<AskThread?> GetThreadAsync(AuthContext caller, string id, CancellationToken token = default)
        {
            RequireOwner(caller);
            return _Database.AskThreads.ReadAsync(caller.TenantId!, caller.UserId!, id, token);
        }

        /// <summary>Read one bounded page of the caller's saved conversations.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="pageNumber">One-based page.</param>
        /// <param name="pageSize">Bounded page size.</param>
        /// <param name="includeArchived">Whether to include archived threads.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public Task<EnumerationResult<AskThread>> EnumerateThreadsAsync(AuthContext caller, int pageNumber = 1, int pageSize = 100, bool includeArchived = false, CancellationToken token = default)
        {
            RequireOwner(caller);
            return _Database.AskThreads.EnumerateAsync(caller.TenantId!, caller.UserId!, pageNumber, pageSize, includeArchived, token);
        }

        /// <summary>Read a bounded page of turns from an owned thread, or null if the thread is absent.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="beforeSequence">Exclusive turn sequence cursor.</param>
        /// <param name="pageSize">Bounded page size.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public async Task<List<AskTurn>?> EnumerateTurnsAsync(AuthContext caller, string id, long? beforeSequence = null, int pageSize = 100, CancellationToken token = default)
        {
            if (await GetThreadAsync(caller, id, token).ConfigureAwait(false) == null) return null;
            List<AskTurn> turns = await _Database.AskThreads.EnumerateTurnsAsync(caller.TenantId!, caller.UserId!, id, beforeSequence, pageSize, token).ConfigureAwait(false);
            foreach (AskTurn turn in turns) Decorate(turn);
            return turns;
        }

        /// <summary>Read one saved request from an owned thread.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="requestId">Saved request identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned result, or null when a requested thread is absent.</returns>
        public async Task<AskTurn?> GetTurnAsync(AuthContext caller, string id, string requestId, CancellationToken token = default)
        {
            RequireOwner(caller);
            AskTurn? turn = await _Database.AskThreads.ReadTurnAsync(caller.TenantId!, caller.UserId!, id, requestId, token).ConfigureAwait(false);
            if (turn != null) Decorate(turn);
            return turn;
        }
    }
}
