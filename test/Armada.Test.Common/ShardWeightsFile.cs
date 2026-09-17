namespace Armada.Test.Common
{
    using System.Collections.Generic;

    /// <summary>
    /// Committed expected duration per suite, used to balance shards. Generated from a unit log by
    /// <c>scripts/common/generate-shard-weights.py</c>.
    /// </summary>
    public class ShardWeightsFile
    {
        #region Public-Members

        /// <summary>Expected seconds for a suite the table does not name.</summary>
        public double DefaultSeconds { get; set; } = 1.0;

        /// <summary>Suite name to expected seconds.</summary>
        public Dictionary<string, double> Suites { get; set; } = new Dictionary<string, double>();

        #endregion
    }
}
