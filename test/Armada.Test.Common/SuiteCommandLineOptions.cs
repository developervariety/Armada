namespace Armada.Test.Common
{
    /// <summary>Strict suite selection for executables with internal fixtures.</summary>
    public static class SuiteCommandLineOptions
    {
        /// <summary>Parse repeatable suite filters and the legacy cleanup flag.</summary>
        public static List<string> Parse(string[] args)
        {
            List<string> filters = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-cleanup") continue;
                if (args[i] != "--suite") throw new ArgumentException("Unknown argument: " + args[i]);
                if (i + 1 >= args.Length || String.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--suite requires a nonblank value");
                filters.Add(args[++i]);
            }
            return filters;
        }
    }
}
