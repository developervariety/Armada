namespace Armada.Test.Common
{
    /// <summary>Strict suite selection for executables with internal fixtures.</summary>
    public static class SuiteCommandLineOptions
    {
        /// <summary>
        /// Parse repeatable suite filters and the legacy cleanup flag. Shard and list options are refused here,
        /// so a host that does not support them never ignores them silently.
        /// </summary>
        public static List<string> Parse(string[] args)
        {
            TestHostOptions options = ParseHost(args);
            if (options.Shard != null || options.ListSuites)
                throw new ArgumentException("--shard and --list-suites are not supported by this test host");
            return options.SuiteFilters;
        }

        /// <summary>
        /// Parse repeatable <c>--suite</c> filters, <c>--shard index/count</c>, <c>--list-suites</c> and the legacy
        /// cleanup flag. A shard cannot be combined with suite filters, because a filtered shard would silently
        /// drop the suites the filter excludes from the shard's totals.
        /// </summary>
        public static TestHostOptions ParseHost(string[] args)
        {
            TestHostOptions options = new TestHostOptions();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-cleanup") continue;
                if (args[i] == "--list-suites")
                {
                    options.ListSuites = true;
                    continue;
                }

                if (args[i] == "--shard")
                {
                    if (options.Shard != null) throw new ArgumentException("--shard may be given only once");
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                        throw new ArgumentException("--shard requires a value of the form <index>/<count>");
                    options.Shard = TestShard.Parse(args[++i]);
                    continue;
                }

                if (args[i] != "--suite") throw new ArgumentException("Unknown argument: " + args[i]);
                if (i + 1 >= args.Length || String.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--suite requires a nonblank value");
                options.SuiteFilters.Add(args[++i]);
            }

            if (options.Shard != null && options.SuiteFilters.Count > 0)
                throw new ArgumentException("--shard cannot be combined with --suite");
            return options;
        }
    }
}
