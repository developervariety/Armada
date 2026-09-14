namespace Armada.Runtimes.Tools
{
    using System.IO;

    /// <summary>Indicates that recursive workspace enumeration exceeded its safety limit.</summary>
    public sealed class WorkspaceEnumerationLimitException : IOException
    {
        /// <summary>Instantiate the limit error.</summary>
        /// <param name="maximumEntries">Maximum permitted entries.</param>
        public WorkspaceEnumerationLimitException(int maximumEntries)
            : base("Workspace enumeration exceeded the maximum entry limit of " + maximumEntries + ".")
        {
        }
    }
}
