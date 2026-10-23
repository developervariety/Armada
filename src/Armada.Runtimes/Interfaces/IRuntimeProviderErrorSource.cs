namespace Armada.Runtimes.Interfaces
{
    using System;
    using Armada.Core.Models;

    /// <summary>Optional source of typed provider-failure evidence from an agent runtime.</summary>
    public interface IRuntimeProviderErrorSource
    {
        /// <summary>Raised when a provider failure includes typed status or error-type evidence.</summary>
        event Action<int, RuntimeProviderError>? OnProviderErrorReceived;
    }
}
