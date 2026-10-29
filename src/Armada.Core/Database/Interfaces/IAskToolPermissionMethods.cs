namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Owner-scoped persistence for native tool permission requests and the remembered rules of an Ask thread.</summary>
    public interface IAskToolPermissionMethods
    {
        /// <summary>Store a request. A request already decided by a rule is stored with that state.</summary>
        /// <param name="request">Request to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored request.</returns>
        Task<AskToolPermissionRequest> CreateRequestAsync(AskToolPermissionRequest request, CancellationToken token = default);
        /// <summary>Read one request for its exact owner and thread.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Permission request id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The request, or null.</returns>
        Task<AskToolPermissionRequest?> ReadRequestAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default);
        /// <summary>List the newest requests of a thread, newest first. A pending request past its expiry reads as Expired.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="limit">Maximum rows.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The requests.</returns>
        Task<List<AskToolPermissionRequest>> EnumerateRequestsAsync(string tenantId, string userId, string threadId, int limit = 100, CancellationToken token = default);
        /// <summary>Decide a pending, unexpired request. Only one decision wins.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Permission request id.</param>
        /// <param name="allow">True to allow, false to deny.</param>
        /// <param name="source">What decided it.</param>
        /// <param name="nowUtc">Decision time.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this call decided the request.</returns>
        Task<bool> TryDecideAsync(string tenantId, string userId, string threadId, string requestId, bool allow, AskToolPermissionSourceEnum source, DateTime nowUtc, CancellationToken token = default);
        /// <summary>Move a still-pending request to Expired.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="requestId">Permission request id.</param>
        /// <param name="nowUtc">Time of the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this call expired the request.</returns>
        Task<bool> TryExpireAsync(string tenantId, string userId, string threadId, string requestId, DateTime nowUtc, CancellationToken token = default);
        /// <summary>Remember a decision for a tool in a thread, replacing any earlier rule for that tool.</summary>
        /// <param name="rule">Rule to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored rule.</returns>
        Task<AskToolPermissionRule> UpsertRuleAsync(AskToolPermissionRule rule, CancellationToken token = default);
        /// <summary>Read the rule for a tool in a thread.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="toolName">Native tool name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The rule, or null.</returns>
        Task<AskToolPermissionRule?> ReadRuleAsync(string tenantId, string userId, string threadId, string toolName, CancellationToken token = default);
        /// <summary>List the rules of a thread.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The rules, oldest first.</returns>
        Task<List<AskToolPermissionRule>> EnumerateRulesAsync(string tenantId, string userId, string threadId, CancellationToken token = default);
        /// <summary>Forget a remembered rule.</summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="userId">User id.</param>
        /// <param name="threadId">Thread id.</param>
        /// <param name="ruleId">Rule id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the owner had that rule.</returns>
        Task<bool> DeleteRuleAsync(string tenantId, string userId, string threadId, string ruleId, CancellationToken token = default);
    }
}
