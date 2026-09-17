namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Text;

    /// <summary>
    /// C# source fixtures for chunking and duplicate-detection tests: one shared method placed at
    /// different offsets in otherwise distinct files, surrounded by members with unique vocabulary.
    /// </summary>
    public static class DuplicateFixtures
    {
        /// <summary>
        /// The method both fixture files share, indented as a class member, with a doc comment.
        /// </summary>
        /// <returns>Method source.</returns>
        public static string TargetMethod()
        {
            return
                "        /// <summary>Merges overlapping ranges.</summary>\n" +
                "        public static List<int> MergeOverlappingRanges(List<int> starts, List<int> ends)\n" +
                "        {\n" +
                "            List<int> merged = new List<int>();\n" +
                "            for (int index = 0; index < starts.Count; index++)\n" +
                "            {\n" +
                "                int start = starts[index];\n" +
                "                int end = ends[index];\n" +
                "                if (merged.Count > 0 && start <= merged[merged.Count - 1])\n" +
                "                {\n" +
                "                    merged[merged.Count - 1] = Math.Max(merged[merged.Count - 1], end);\n" +
                "                    continue;\n" +
                "                }\n" +
                "                merged.Add(start);\n" +
                "                merged.Add(end);\n" +
                "            }\n" +
                "            return merged;\n" +
                "        }\n";
        }

        /// <summary>
        /// Class members with vocabulary unique to a seed, each followed by a blank line.
        /// </summary>
        /// <param name="seed">Word that makes the members unique to one file.</param>
        /// <param name="count">Number of members.</param>
        /// <returns>Member source.</returns>
        public static string Members(string seed, int count)
        {
            StringBuilder builder = new StringBuilder();
            string title = Char.ToUpperInvariant(seed[0]) + seed.Substring(1);
            for (int i = 0; i < count; i++)
            {
                builder.Append("        public string " + title + "Member" + i + "()\n");
                builder.Append("        {\n");
                builder.Append("            return \"" + seed + " " + seed + "word" + i + " " + seed + "token" + i + "\";\n");
                builder.Append("        }\n");
                builder.Append("\n");
            }

            return builder.ToString();
        }

        /// <summary>
        /// A C# file holding the target method between seed-specific members.
        /// </summary>
        /// <param name="prefixMembers">Members before the target.</param>
        /// <param name="suffixMembers">Members after the target.</param>
        /// <param name="seed">Word that makes the other members unique to this file.</param>
        /// <returns>File source.</returns>
        public static string FileWithTarget(int prefixMembers, int suffixMembers, string seed)
        {
            string title = Char.ToUpperInvariant(seed[0]) + seed.Substring(1);
            return
                "namespace Fixture" + title + "\n{\n    public class " + title + "Holder\n    {\n" +
                Members(seed, prefixMembers) +
                TargetMethod() + "\n" +
                Members(seed + "tail", suffixMembers) +
                "    }\n}\n";
        }
    }
}
