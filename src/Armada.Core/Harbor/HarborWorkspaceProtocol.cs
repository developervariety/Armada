namespace Armada.Core.Harbor
{
    /// <summary>Names the typed checkout capability and its supported contract.</summary>
    public static class HarborWorkspaceProtocol
    {
        /// <summary>Capability required for owned repository and checkout operations.</summary>
        public const string CapabilityName = "workspace";
        /// <summary>Supported workspace operation contract version.</summary>
        public const int Version = 1;
    }
}
