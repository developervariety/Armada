namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage RefreshRequest measurement contract.
    /// </summary>
    public class StorageRefreshRequest
    {
        /// <summary>Opt in to bounded exact database table counts.</summary>
        public bool ExactCounts { get; set; } = false;

    }
}
