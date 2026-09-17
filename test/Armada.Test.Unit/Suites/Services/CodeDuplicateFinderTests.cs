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

    /// <summary>
    /// Tests for grouping similar code chunks: planted pairs are found and unrelated chunks are not,
    /// identical content groups without embeddings, pairs join transitively, coverage names every chunk
    /// left out, and a pair cap marks the report truncated.
    /// </summary>
    public class CodeDuplicateFinderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Code Duplicate Finder";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A planted similar pair is the only group; an unrelated pair below the threshold is not grouped", () =>
            {
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/Orders/OrderMerge.cs", 10, Body("orders", 8), Vector(1F, 0.05F, 0F, 0F)),
                    Chunk("src/Invoices/InvoiceMerge.cs", 40, Body("invoices", 8), Vector(1F, 0F, 0.05F, 0F)),
                    Chunk("src/Models/OrderDto.cs", 1, Body("dto-a", 8), Vector(0F, 1F, 0F, 0F)),
                    Chunk("src/Models/InvoiceDto.cs", 1, Body("dto-b", 8), Vector(0F, 0.7F, 0.7F, 0F))
                };

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, Request(), 0.92, 6);

                AssertTrue(report.Available);
                AssertTrue(report.SimilarityCompared);
                AssertEqual(1, report.GroupCount, "only the planted pair is grouped");
                AssertEqual(1, report.PairCount);
                CodeDuplicateGroup group = report.Groups[0];
                AssertEqual("src/Invoices/InvoiceMerge.cs", group.Members[0].Path);
                AssertEqual(40, group.Members[0].StartLine);
                AssertEqual("src/Orders/OrderMerge.cs", group.Members[1].Path);
                AssertFalse(group.IdenticalContent);
                AssertTrue(group.MaxPairSimilarity >= 0.99, "the planted pair is nearly parallel: " + group.MaxPairSimilarity);
            });

            await RunTest("Identical content groups without any embeddings", () =>
            {
                string shared = Body("shared", 7);
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/A.cs", 5, shared, null),
                    Chunk("src/B.cs", 90, "    " + shared.Replace("\n", "\n    ") + "\n\n", null),
                    Chunk("src/C.cs", 5, Body("other", 7), null)
                };

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, Request(), 0.92, 6);

                AssertFalse(report.SimilarityCompared, "no vectors, so no similarity pass");
                AssertEqual(3, report.Coverage.WithoutEmbedding);
                AssertEqual(1, report.GroupCount);
                AssertTrue(report.Groups[0].IdenticalContent, "indentation and blank lines do not change identity");
                AssertEqual(1.0, report.Groups[0].MaxPairSimilarity);
            });

            await RunTest("Pairs join transitively and the weakest linking pair is reported", () =>
            {
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/A.cs", 1, Body("a", 6), Vector(1F, 0F, 0F, 0F)),
                    Chunk("src/B.cs", 1, Body("b", 6), Vector(0.94F, 0.34F, 0F, 0F)),
                    Chunk("src/C.cs", 1, Body("c", 6), Vector(0.77F, 0.64F, 0F, 0F))
                };

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, Request(), 0.92, 6);

                AssertEqual(1, report.GroupCount, "A-B and B-C link all three although A-C is below the threshold");
                AssertEqual(3, report.Groups[0].Members.Count);
                AssertEqual(2, report.PairCount);
                AssertTrue(report.Groups[0].MinPairSimilarity < report.Groups[0].MaxPairSimilarity);
            });

            await RunTest("Coverage counts every chunk left out, and left-out chunks are never grouped", () =>
            {
                string shared = Body("shared", 8);
                CodeIndexRecord referenceOnly = Chunk("src/Ref.cs", 1, shared, null);
                referenceOnly.IsReferenceOnly = true;
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/Keep/A.cs", 1, shared, null),
                    referenceOnly,
                    Chunk("tools/Other.cs", 1, shared, null),
                    Chunk("src/Keep/generated/Gen.cs", 1, shared, null),
                    Chunk("src/Keep/Short.cs", 1, "int x = 1;\nint y = 2;", null),
                    Chunk("src/Keep/Doc.md", 1, shared, null, "markdown")
                };
                CodeDuplicateRequest request = Request();
                request.PathPrefix = "src/Keep";
                request.Language = "csharp";
                request.ExcludePathFragments = new List<string> { "/generated/" };

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, request, 0.92, 6);

                AssertEqual(6, report.Coverage.ChunksInIndex);
                AssertEqual(1, report.Coverage.SkippedReferenceOnly);
                AssertEqual(2, report.Coverage.SkippedByFilter, "the path prefix and the language filter");
                AssertEqual(1, report.Coverage.SkippedExcluded);
                AssertEqual(1, report.Coverage.SkippedTooShort);
                AssertEqual(1, report.Coverage.ChunksCompared);
                AssertEqual(1, report.Coverage.Languages["csharp"]);
                AssertEqual(0, report.GroupCount, "the identical copies that were left out form no group");
            });

            await RunTest("A pair cap stops collection and marks the report truncated", () =>
            {
                List<CodeIndexRecord> records = new List<CodeIndexRecord>();
                for (int i = 0; i < 8; i++) records.Add(Chunk("src/F" + i + ".cs", 1, Body("f" + i, 6), Vector(1F, 0F, 0F, 0F)));

                CodeDuplicateReport capped = CodeDuplicateFinder.Find(records, Request(), 0.92, 6, maxPairs: 5);
                CodeDuplicateReport full = CodeDuplicateFinder.Find(records, Request(), 0.92, 6);

                AssertTrue(capped.Truncated);
                AssertEqual(5, capped.PairCount);
                AssertFalse(full.Truncated);
                AssertEqual(28, full.PairCount, "all eight parallel chunks pair with each other");
                AssertEqual(1, full.GroupCount);
            });

            await RunTest("Chunks with a mismatched or zero vector take part in the identical-content pass only", () =>
            {
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/A.cs", 1, Body("a", 6), Vector(1F, 0F, 0F, 0F)),
                    Chunk("src/B.cs", 1, Body("b", 6), Vector(1F, 0F, 0F, 0F)),
                    Chunk("src/C.cs", 1, Body("c", 6), new float[] { 1F, 0F }),
                    Chunk("src/D.cs", 1, Body("d", 6), Vector(0F, 0F, 0F, 0F))
                };

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, Request(), 0.92, 6);

                AssertEqual(2, report.Coverage.ChunksWithEmbeddings);
                AssertEqual(2, report.Coverage.WithoutEmbedding);
                AssertEqual(1, report.GroupCount);
                AssertEqual(2, report.Groups[0].Members.Count);
            });

            await RunTest("Larger groups come first and maxGroups limits the list but not the count", () =>
            {
                List<CodeIndexRecord> records = new List<CodeIndexRecord>
                {
                    Chunk("src/P1.cs", 1, Body("p1", 6), Vector(1F, 0F, 0F, 0F)),
                    Chunk("src/P2.cs", 1, Body("p2", 6), Vector(1F, 0F, 0F, 0F)),
                    Chunk("src/T1.cs", 1, Body("t1", 6), Vector(0F, 1F, 0F, 0F)),
                    Chunk("src/T2.cs", 1, Body("t2", 6), Vector(0F, 1F, 0F, 0F)),
                    Chunk("src/T3.cs", 1, Body("t3", 6), Vector(0F, 1F, 0F, 0F))
                };
                CodeDuplicateRequest request = Request();
                request.MaxGroups = 1;

                CodeDuplicateReport report = CodeDuplicateFinder.Find(records, request, 0.92, 6);

                AssertEqual(2, report.GroupCount);
                AssertEqual(1, report.Groups.Count);
                AssertEqual(3, report.Groups[0].Members.Count, "the three-member group ranks first");
            });
        }

        private static CodeDuplicateRequest Request()
        {
            return new CodeDuplicateRequest { VesselId = "vsl_example" };
        }

        private static CodeIndexRecord Chunk(string path, int startLine, string content, float[]? vector, string language = "csharp")
        {
            int lineCount = content.Split('\n').Length;
            return new CodeIndexRecord
            {
                VesselId = "vsl_example",
                Path = path,
                StartLine = startLine,
                EndLine = startLine + lineCount - 1,
                Language = language,
                Content = content,
                EmbeddingVector = vector
            };
        }

        private static float[] Vector(params float[] values)
        {
            return values;
        }

        private static string Body(string seed, int lines)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < lines; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append("var " + seed.Replace("-", "_") + "_" + i + " = Compute(" + i + ");");
            }

            return builder.ToString();
        }
    }
}
