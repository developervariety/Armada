namespace Armada.Core.Services
{
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Default self-deploy preflight that refuses cutover until a provider is configured.
    /// </summary>
    public sealed class FailClosedSelfDeployPreflight : ISelfDeployPreflight
    {
        /// <inheritdoc />
        public Task<SelfDeployPreflightResult> ValidateAsync(
            SelfDeployPreflightRequest request,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            return Task.FromResult(new SelfDeployPreflightResult
            {
                FailureReason = "validated_backup_provider_not_configured",
                OutputTail = "Self-deploy preflight has no provider that can prove backup, restore verification, and candidate validation."
            });
        }
    }
}
