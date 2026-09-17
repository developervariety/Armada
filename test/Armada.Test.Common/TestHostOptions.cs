namespace Armada.Test.Common
{
    using System.Collections.Generic;

    /// <summary>
    /// Command-line selection for a console test host: suite filters, an optional shard, and whether to list
    /// the selected suite names instead of running them.
    /// </summary>
    public class TestHostOptions
    {
        #region Public-Members

        /// <summary>Repeatable <c>--suite</c> filters; empty selects every suite.</summary>
        public List<string> SuiteFilters { get; set; } = new List<string>();

        /// <summary>The <c>--shard</c> slice to run, or null to run every selected suite.</summary>
        public TestShard? Shard { get; set; } = null;

        /// <summary>True when <c>--list-suites</c> was given: print the selected suite names and run nothing.</summary>
        public bool ListSuites { get; set; } = false;

        #endregion
    }
}
