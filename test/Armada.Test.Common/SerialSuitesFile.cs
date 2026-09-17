namespace Armada.Test.Common
{
    using System.Collections.Generic;

    /// <summary>
    /// Committed list of suites that always run together on the first shard, each with the reason it is kept
    /// out of the balanced split.
    /// </summary>
    public class SerialSuitesFile
    {
        #region Public-Members

        /// <summary>Suite name to the reason the suite is serial.</summary>
        public Dictionary<string, string> Suites { get; set; } = new Dictionary<string, string>();

        #endregion
    }
}
