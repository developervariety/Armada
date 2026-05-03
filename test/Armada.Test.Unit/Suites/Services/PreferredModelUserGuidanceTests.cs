namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests user-facing preferredModel guidance in docs and MCP schemas.
    /// </summary>
    public class PreferredModelUserGuidanceTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "PreferredModel User Guidance";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Docs_MentionComplexityTiersWithoutConcreteModelExamples", () =>
            {
                string[] files =
                {
                    "docs/INSTRUCTIONS_FOR_CLAUDE_CODE.md",
                    "docs/INSTRUCTIONS_FOR_CODEX.md",
                    "docs/INSTRUCTIONS_FOR_CURSOR.md",
                    "docs/INSTRUCTIONS_FOR_GEMINI.md"
                };

                foreach (string file in files)
                {
                    string text = File.ReadAllText(file);
                    AssertContains("preferredModel", text, file + " should document preferredModel");
                    AssertContains("`low`, `mid`, or `high`", text, file + " should mention low/mid/high tiers");
                    AssertNoConcreteModelExamples(text, file);
                }
            });

            await RunTest("McpSchemas_MentionComplexityTiersWithoutConcreteModelExamples", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, string> schemas = new Dictionary<string, string>();
                    McpVoyageTools.Register(
                        (name, description, schema, _) =>
                        {
                            if (name == "armada_dispatch") schemas[name] = SerializeSchema(description, schema);
                        },
                        testDb.Driver,
                        null!,
                        null);
                    McpPipelineTools.Register(
                        (name, description, schema, _) =>
                        {
                            if (name == "armada_create_pipeline" || name == "armada_update_pipeline")
                                schemas[name] = SerializeSchema(description, schema);
                        },
                        testDb.Driver);
                    McpArchitectTools.Register(
                        (name, description, schema, _) =>
                        {
                            if (name == "armada_decompose_plan") schemas[name] = SerializeSchema(description, schema);
                        },
                        testDb.Driver,
                        new ArchitectOutputParser(),
                        null!);

                    string[] toolNames =
                    {
                        "armada_dispatch",
                        "armada_create_pipeline",
                        "armada_update_pipeline",
                        "armada_decompose_plan"
                    };

                    foreach (string toolName in toolNames)
                    {
                        AssertTrue(schemas.ContainsKey(toolName), toolName + " schema should be captured");
                        string schemaJson = schemas[toolName];
                        AssertContains("preferredModel", schemaJson, toolName + " should include preferredModel guidance");
                        AssertContains("low", schemaJson, toolName + " should mention low tier");
                        AssertContains("mid", schemaJson, toolName + " should mention mid tier");
                        AssertContains("high", schemaJson, toolName + " should mention high tier");
                        AssertNoConcreteModelExamples(schemaJson, toolName);
                    }
                }
            });
        }

        private static string SerializeSchema(string description, object schema)
        {
            return JsonSerializer.Serialize(new
            {
                description,
                schema
            });
        }

        private void AssertNoConcreteModelExamples(string text, string context)
        {
            string lower = text.ToLowerInvariant();
            AssertFalse(lower.Contains("claude-opus-4-7"), context + " should not mention claude-opus-4-7");
            AssertFalse(lower.Contains("gpt-5.5"), context + " should not mention gpt-5.5");
            AssertFalse(lower.Contains("claude-sonnet-4-6"), context + " should not mention claude-sonnet-4-6");
        }
    }
}
