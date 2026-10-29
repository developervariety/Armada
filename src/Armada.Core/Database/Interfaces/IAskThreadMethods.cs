namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Owner-scoped persistence for saved Ask threads and their durable turns.</summary>
    public interface IAskThreadMethods
    {
        /// <summary>Create a thread for its tenant and user.</summary>
        /// <param name="thread">Owned thread.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored thread.</returns>
        Task<AskThread> CreateAsync(AskThread thread, CancellationToken token = default);
        /// <summary>Read one thread for its exact owner.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owned thread, or null.</returns>
        Task<AskThread?> ReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default);
        /// <summary>List one owner's threads.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="pageNumber">One-based page number.</param>
        /// <param name="pageSize">Requested page size.</param>
        /// <param name="includeArchived">Whether to include archived rows.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The ordered page.</returns>
        Task<EnumerationResult<AskThread>> EnumerateAsync(string tenantId, string userId, int pageNumber, int pageSize, bool includeArchived, CancellationToken token = default);
        /// <summary>Update only editable thread fields for the exact owner.</summary>
        /// <param name="thread">Updated title, captain and display flags.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored thread, or null when it is not owned.</returns>
        Task<AskThread?> UpdateAsync(AskThread thread, CancellationToken token = default);
        /// <summary>Atomically deduplicate a request and claim the thread's one active turn.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="turn">Pending turn to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Creation, replay, conflict, busy, or missing-owner result.</returns>
        Task<AskTurnReservation> TryReserveTurnAsync(string tenantId, string userId, AskTurn turn, CancellationToken token = default);
        /// <summary>Read one turn by the request id within its owned thread.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Turn request id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored turn, or null.</returns>
        Task<AskTurn?> ReadTurnAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default);
        /// <summary>Read the newest successful turns for server-built history, oldest first.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="limit">Maximum completed turns.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Successful turns in sequence order.</returns>
        Task<List<AskTurn>> ReadHistoryAsync(string tenantId, string userId, string threadId, int limit, CancellationToken token = default);
        /// <summary>Read a page of stored turns, oldest first within the selected page.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="beforeSequence">Exclusive sequence cursor, or null for the newest page.</param>
        /// <param name="pageSize">Maximum rows.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Turns in sequence order.</returns>
        Task<List<AskTurn>> EnumerateTurnsAsync(string tenantId, string userId, string threadId, long? beforeSequence, int pageSize, CancellationToken token = default);
        /// <summary>Move a newly reserved turn from Pending to Running.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Turn request id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this pending active turn was claimed for execution.</returns>
        Task<bool> MarkRunningAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default);
        /// <summary>Clear the unread count of an owned thread.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the owner has this thread.</returns>
        Task<bool> MarkReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default);
        /// <summary>Set the unread count of an owned thread to one so the owner finds it again.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the owner has this thread.</returns>
        Task<bool> MarkUnreadAsync(string tenantId, string userId, string threadId, CancellationToken token = default);
        /// <summary>Persist the terminal response and release the matching active turn atomically.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Turn request id.</param>
        /// <param name="response">Full typed runtime response.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this active turn reached a terminal state.</returns>
        Task<bool> CompleteTurnAsync(string tenantId, string userId, string threadId, string requestId, CaptainChatResponse response, CancellationToken token = default);
    }
}
