namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the read-only production summary MCP adapter.
    /// </summary>
    public sealed class McpProductionToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Production Tools";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("RegistersAndReturnsBoundedSummary", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = Register(testDb.Driver);

                AssertTrue(handlers.ContainsKey("armada_production_summary"));
                object response = await handlers["armada_production_summary"](JsonSerializer.SerializeToElement(new
                {
                    fromUtc = "2026-01-01T00:00:00Z",
                    toUtc = "2026-01-08T00:00:00Z",
                    sourceFamily = "ecu",
                    workType = "Feature"
                })).ConfigureAwait(false);

                ProductionSummaryResult result = (ProductionSummaryResult)response;
                AssertEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.FromUtc);
                AssertEqual(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), result.ToUtc);
                AssertNotNull(result.Groups);
                AssertNotNull(result.Warnings);
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> Register(DatabaseDriver database)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            McpProductionTools.Register((name, description, schema, handler) => handlers[name] = handler, database);
            return handlers;
        }
    }
}
