namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for declaration-boundary chunking of source files: a method becomes the same chunk
    /// wherever it sits, braces inside strings and comments do not move a boundary, unbalanced files
    /// fall back to line windows, and no meaningful line is lost or emitted twice.
    /// </summary>
    public class StructuralChunkerTests : TestSuite
    {
        private const int MinStandaloneLines = 6;

        /// <summary>Suite name.</summary>
        public override string Name => "Structural Chunker";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The same method is one identical chunk at different offsets; line windows split it", () =>
            {
                string[] first = Lines(DuplicateFixtures.FileWithTarget(prefixMembers: 9, suffixMembers: 2, seed: "alpha"));
                string[] second = Lines(DuplicateFixtures.FileWithTarget(prefixMembers: 5, suffixMembers: 6, seed: "beta"));
                string target = DuplicateFixtures.TargetMethod().Trim();

                AssertTrue(ChunkTexts(first, StructuralChunker.Chunk(first, "csharp", 20, MinStandaloneLines)).Contains(target), "structural chunking yields the method as a chunk in the first file");
                AssertTrue(ChunkTexts(second, StructuralChunker.Chunk(second, "csharp", 20, MinStandaloneLines)).Contains(target), "structural chunking yields the method as a chunk in the second file");

                AssertFalse(ChunkTexts(first, StructuralChunker.ChunkByLineWindows(first, 20)).Contains(target), "line windows do not yield the method alone in the first file");
                AssertFalse(ChunkTexts(second, StructuralChunker.ChunkByLineWindows(second, 20)).Contains(target), "line windows do not yield the method alone in the second file");
            });

            await RunTest("A method keeps its doc comment and attribute, and fields become their own chunk", () =>
            {
                string source =
                    "namespace Sample\n{\n    public class Holder\n    {\n        private int _Count = 0;\n        private string _Name = \"x\";\n\n" +
                    "        /// <summary>Adds.</summary>\n        [Obsolete]\n        public int Add(int a,\n            int b)\n        {\n            return a + b;\n        }\n\n" +
                    Filler("gamma", 6) + "    }\n}\n";
                string[] lines = Lines(source);
                List<string> texts = ChunkTexts(lines, StructuralChunker.Chunk(lines, "csharp", 10, MinStandaloneLines));

                AssertTrue(texts.Any(t => t.StartsWith("/// <summary>Adds.</summary>", StringComparison.Ordinal) && t.Contains("return a + b;", StringComparison.Ordinal)),
                    "the method chunk starts at its doc comment: " + String.Join(" || ", texts));
                AssertTrue(texts.Any(t => t.Contains("private int _Count = 0;", StringComparison.Ordinal) && !t.Contains("Add(", StringComparison.Ordinal)),
                    "the fields and type header are a chunk of their own");
            });

            await RunTest("Braces inside strings, characters and comments do not move a boundary", () =>
            {
                string tricky =
                    "        public string Tricky()\n        {\n" +
                    "            string a = \"{ not a block\";\n" +
                    "            string b = @\"verbatim }\"\" {\";\n" +
                    "            string c = $\"{a}{{literal}}{(b.Length > 0 ? \"}\" : \"{\")}\";\n" +
                    "            char d = '{';\n" +
                    "            char e = '\\'';\n" +
                    "            // } comment brace\n" +
                    "            /* { block comment\n               } */\n" +
                    "            string f = \"\"\"\n                raw { text\n                \"\"\";\n" +
                    "            return a + b + c + d + e + f;\n        }\n";
                string source = "namespace Sample\n{\n    public class Tricky\n    {\n" + Filler("delta", 5) + tricky + Filler("epsilon", 5) + "    }\n}\n";
                string[] lines = Lines(source);

                List<string> texts = ChunkTexts(lines, StructuralChunker.Chunk(lines, "csharp", 20, MinStandaloneLines));
                AssertTrue(texts.Contains(tricky.Trim()), "the tricky method is exactly one chunk: " + String.Join(" || ", texts));
            });

            await RunTest("Small members are packed together while a substantial method keeps its own chunk", () =>
            {
                string properties =
                    "        public int A { get; set; }\n\n        public int B { get; set; }\n\n        public int C { get; set; }\n\n";
                string source =
                    "namespace Packing\n{\n    public class Packing\n    {\n" + properties + DuplicateFixtures.TargetMethod() + "\n" + properties.Replace("A", "D").Replace("B", "E").Replace("C", "F") +
                    Filler("kappa", 3) + "    }\n}\n";
                string[] lines = Lines(source);
                List<string> texts = ChunkTexts(lines, StructuralChunker.Chunk(lines, "csharp", 20, MinStandaloneLines));

                AssertTrue(texts.Contains(DuplicateFixtures.TargetMethod().Trim()), "the substantial method is its own chunk: " + String.Join(" || ", texts));
                AssertTrue(texts.Any(t => t.Contains("public int A", StringComparison.Ordinal) && t.Contains("public int C", StringComparison.Ordinal)),
                    "the small properties before the method share one chunk");
                AssertTrue(texts.Any(t => t.Contains("public int F", StringComparison.Ordinal) && t.Contains("KappaMember0", StringComparison.Ordinal)),
                    "small members after the method are packed across member kinds");
            });

            await RunTest("A file whose braces do not balance falls back to line windows", () =>
            {
                string source = "namespace Broken\n{\n    public class Broken\n    {\n" + Filler("zeta", 8) + "    }\n";
                string[] lines = Lines(source);

                List<CodeChunkRange> structural = StructuralChunker.Chunk(lines, "csharp", 12, MinStandaloneLines);
                List<CodeChunkRange> windows = StructuralChunker.ChunkByLineWindows(lines, 12);
                AssertEqual(Describe(windows), Describe(structural));
            });

            await RunTest("A language without brace blocks uses line windows", () =>
            {
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < 50; i++) builder.Append("value_" + i + " = compute_" + i + "()\n");
                string[] lines = Lines(builder.ToString());

                AssertFalse(StructuralChunker.SupportsLanguage("python"));
                AssertEqual(Describe(StructuralChunker.ChunkByLineWindows(lines, 20)), Describe(StructuralChunker.Chunk(lines, "python", 20, MinStandaloneLines)));
            });

            await RunTest("A file that fits the budget is one chunk", () =>
            {
                string[] lines = Lines("namespace Small\n{\n    public class Small\n    {\n        public int One() => 1;\n    }\n}\n");
                List<CodeChunkRange> ranges = StructuralChunker.Chunk(lines, "csharp", 20, MinStandaloneLines);

                AssertEqual(1, ranges.Count);
                AssertEqual(0, ranges[0].StartIndex);
            });

            await RunTest("Every meaningful line is in exactly one chunk and chunks never exceed the budget", () =>
            {
                StringBuilder longBody = new StringBuilder();
                longBody.Append("        public void Long()\n        {\n");
                for (int i = 0; i < 45; i++) longBody.Append("            Step" + i + "();\n");
                longBody.Append("        }\n");
                string source =
                    "using System;\n\nnamespace Coverage\n{\n    public class Outer\n    {\n" + Filler("eta", 7) +
                    "        public class Inner\n        {\n" + Filler("theta", 6) + "        }\n" + longBody +
                    Filler("iota", 3) + "    }\n}\n";
                string[] lines = Lines(source);
                const int maxLines = 15;

                List<CodeChunkRange> ranges = StructuralChunker.Chunk(lines, "csharp", maxLines, MinStandaloneLines);
                int[] owners = new int[lines.Length];
                int previousEnd = 0;
                for (int r = 0; r < ranges.Count; r++)
                {
                    CodeChunkRange range = ranges[r];
                    AssertTrue(range.StartIndex >= previousEnd, "chunks are ordered and do not overlap");
                    AssertTrue(range.EndIndexExclusive - range.StartIndex <= maxLines, "a chunk never exceeds the budget");
                    for (int i = range.StartIndex; i < range.EndIndexExclusive; i++) owners[i]++;
                    previousEnd = range.EndIndexExclusive;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    bool meaningful = lines[i].Any(c => !Char.IsWhiteSpace(c) && c != '{' && c != '}' && c != ';' && c != '(' && c != ')' && c != ',');
                    if (meaningful) AssertEqual(1, owners[i], "line " + (i + 1) + " is in exactly one chunk: " + lines[i]);
                }
            });
        }

        private static string[] Lines(string source)
        {
            return source.Replace("\r\n", "\n").Split('\n');
        }

        private static List<string> ChunkTexts(string[] lines, List<CodeChunkRange> ranges)
        {
            return ranges
                .Select(r => String.Join("\n", lines.Skip(r.StartIndex).Take(r.EndIndexExclusive - r.StartIndex)).Trim())
                .ToList();
        }

        private static string Describe(List<CodeChunkRange> ranges)
        {
            return String.Join(",", ranges.Select(r => r.StartIndex + "-" + r.EndIndexExclusive));
        }

        private static string Filler(string seed, int count)
        {
            return DuplicateFixtures.Members(seed, count);
        }
    }
}
