namespace Armada.Server.Routes
{
    using System;

    /// <summary>Result of an accepted Harbor stop confirmation.</summary>
    public sealed class HarborJobConfirmStoppedResponse
    {
        /// <summary>Whether the exact uncertain job was confirmed stopped.</summary>
        public bool Confirmed { get; set; }
    }
}
