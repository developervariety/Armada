namespace Inventory.Tests
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.
    /// Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Entry point.
        /// </summary>
        /// <param name="args">Arguments.</param>
        /// <returns>Exit code.</returns>
        public static int Main(string[] args)
        {
            List<TestCase> tests = TestRegistry.All();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (TestCase test in tests) names.Add(test.Name);

            bool listOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list")
                {
                    listOnly = true;
                }
                else if (args[i] == "--require" && i + 1 < args.Length)
                {
                    string required = args[++i];
                    if (!names.Contains(required))
                    {
                        Console.WriteLine("MISSING " + required + " is not registered in TestRegistry.");
                        return 2;
                    }
                }
            }

            if (listOnly)
            {
                foreach (TestCase test in tests) Console.WriteLine(test.Name);
                return 0;
            }

            int failed = 0;
            foreach (TestCase test in tests)
            {
                try
                {
                    test.Body();
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Console.WriteLine((tests.Count - failed) + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
