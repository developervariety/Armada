namespace Armada.Core.Storage
{
    /// <summary>Body of a manual refresh request.</summary>
    public class StorageRefreshRequest
    {
        /// <summary>When true the database collection counts the rows of every table instead of reading provider statistics.</summary>
        public bool ExactCounts { get; set; } = false;
    }
}
