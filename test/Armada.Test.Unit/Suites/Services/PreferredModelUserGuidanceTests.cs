namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

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
                            if (name == "create_pipeline" || name == "update_pipeline")
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
                        "create_pipeline",
                        "update_pipeline",
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

            await RunTest("OperatorGuide_NamesEveryRegisteredMcpTool", () =>
            {
                string guide = File.ReadAllText("docs/armada-ops.md");
                string[] sourceFiles = Directory.GetFiles(
                    "src/Armada.Server/Mcp/Tools",
                    "Mcp*Tools.cs",
                    SearchOption.TopDirectoryOnly);
                HashSet<string> toolNames = new HashSet<string>(StringComparer.Ordinal);
                Regex registration = new Regex("register\\(\\s*\"([^\"]+)\"", RegexOptions.Multiline);

                foreach (string sourceFile in sourceFiles)
                {
                    string source = File.ReadAllText(sourceFile);
                    MatchCollection matches = registration.Matches(source);
                    foreach (Match match in matches)
                        toolNames.Add(match.Groups[1].Value);
                }

                AssertTrue(toolNames.Count >= 175, "The source scan should find the current MCP catalog");
                foreach (string toolName in toolNames)
                    AssertContains("`" + toolName + "`", guide, "Operator guide should name " + toolName);

                return Task.CompletedTask;
            });

            await RunTest("OperationalAssetGuide_NamesBuiltInPersonasAndPipelines", () =>
            {
                string guide = File.ReadAllText("docs/OPERATIONAL_ASSETS.md");
                string[] requiredNames =
                {
                    "Worker", "Architect", "Product Manager", "Usability Engineer", "Judge",
                    "TestEngineer", "DiagnosticProtocolReviewer", "TenantSecurityReviewer",
                    "MigrationDataReviewer", "PerformanceMemoryReviewer", "PortingReferenceAnalyst",
                    "FrontendWorkflowReviewer", "MemoryConsolidator", "WorkerOnly", "Reviewed",
                    "Tested", "FullPipeline", "ProductDevelopment", "DiagnosticProtocolTested",
                    "TenantSecurityTested", "MigrationDataTested", "PerformanceMemoryTested",
                    "ReferencePortingTested", "FrontendWorkflowTested", "Reflections",
                    "ReflectionsDualJudge"
                };

                foreach (string requiredName in requiredNames)
                    AssertContains("`" + requiredName + "`", guide, "Operational asset guide should name " + requiredName);

                AssertContains("Do not expose its catalog to captains", guide,
                    "Operational asset guide should state the captain MCP boundary");
                return Task.CompletedTask;
            });

            await RunTest("RunbookService_ListsOnlyRunbookBackedPlaybooks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                Playbook ordinary = new Playbook
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    FileName = "ordinary.md",
                    Description = "Ordinary playbook",
                    Content = "# Ordinary playbook"
                };
                await testDb.Driver.Playbooks.CreateAsync(ordinary).ConfigureAwait(false);

                RunbookService service = new RunbookService(testDb.Driver, new LoggingModule());
                Runbook created = await service.CreateAsync(auth, new RunbookUpsertRequest
                {
                    FileName = "RUNBOOK-test.md",
                    Title = "Test runbook",
                    OverviewMarkdown = "# Test runbook",
                    Steps = new List<RunbookStep>
                    {
                        new RunbookStep { Title = "Inspect", Instructions = "Record evidence." }
                    }
                }).ConfigureAwait(false);

                EnumerationResult<Runbook> result = await service.EnumerateAsync(auth, new RunbookQuery
                {
                    PageNumber = 1,
                    PageSize = 100
                }).ConfigureAwait(false);

                AssertEqual(1, result.TotalRecords);
                AssertEqual(created.Id, result.Objects[0].Id);
                AssertTrue(await service.ReadAsync(auth, ordinary.Id).ConfigureAwait(false) == null,
                    "A normal playbook must not be readable through the runbook API");
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
            AssertFalse(lower.Contains("claude-opus-5"), context + " should not mention claude-opus-5");
            AssertFalse(lower.Contains("gpt-5.6-luna"), context + " should not mention gpt-5.6-luna");
            AssertFalse(lower.Contains("claude-fable-5"), context + " should not mention claude-fable-5");
        }
    }
}
