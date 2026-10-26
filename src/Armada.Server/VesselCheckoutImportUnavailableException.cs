namespace Armada.Server
{
    using System;

    /// <summary>Names local checkout import configuration as an unavailable host capability.</summary>
    internal sealed class VesselCheckoutImportUnavailableException : InvalidOperationException
    {
        internal VesselCheckoutImportUnavailableException()
            : base("Unavailable: local checkout import is not configured on this host.")
        {
        }
    }
}
