namespace Armada.Server
{
    using Armada.Core.Models;

    /// <summary>Provider evidence owned by one process launch.</summary>
    internal sealed class ProcessProviderErrorCapture
    {
        internal ProcessProviderErrorCapture(long stateOwner, RuntimeProviderError error)
        {
            StateOwner = stateOwner;
            Error = error;
        }

        internal long StateOwner { get; }

        internal RuntimeProviderError Error { get; }
    }
}
