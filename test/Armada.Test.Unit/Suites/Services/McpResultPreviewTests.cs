namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the shared MCP result preview: it must shrink the payloads that blow
    /// the caller's tool output limit, and must never quietly drop what it trimmed.
    /// </summary>
    public class McpResultPreviewTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Result Preview";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A long field is previewed and its true length is reported", () =>
            {
                object payload = new { Id = "obj_1", Description = new string('d', 5000) };
                JsonElement result = JsonSerializer.SerializeToElement(
                    McpResultPreview.Apply(payload, includeFullContent: false));

                AssertEqual(McpResultPreview.DefaultPreviewChars,
                    result.GetProperty("Description").GetString()!.Length);
                AssertEqual(5000, result.GetProperty("DescriptionLength").GetInt32(),
                    "The reader must be able to see what was withheld.");
                AssertEqual(1, result.GetProperty("TruncatedFieldCount").GetInt32());
                AssertEqual("obj_1", result.GetProperty("Id").GetString(),
                    "Short fields must survive untouched.");
                return Task.CompletedTask;
            });

            await RunTest("includeFullContent returns the payload unchanged", () =>
            {
                object payload = new { Description = new string('d', 5000) };
                JsonElement result = JsonSerializer.SerializeToElement(
                    McpResultPreview.Apply(payload, includeFullContent: true));

                AssertEqual(5000, result.GetProperty("Description").GetString()!.Length);
                AssertFalse(result.TryGetProperty("TruncatedFieldCount", out _),
                    "Nothing was trimmed, so nothing should claim it was.");
                return Task.CompletedTask;
            });

            await RunTest("A payload with nothing long is returned untouched", () =>
            {
                object payload = new { Id = "obj_1", Title = "short" };
                JsonElement result = JsonSerializer.SerializeToElement(
                    McpResultPreview.Apply(payload, includeFullContent: false));

                AssertFalse(result.TryGetProperty("TruncatedFieldCount", out _),
                    "A count of zero must not appear; it would read as a trimmed result.");
                AssertFalse(result.TryGetProperty("TitleLength", out _));
                return Task.CompletedTask;
            });

            await RunTest("It reaches into nested collections, which is where the bulk lives", () =>
            {
                // list_objectives returns a paged envelope whose Objects carry the whale.
                object payload = new
                {
                    TotalRecords = 2,
                    Objects = new List<object>
                    {
                        new { Id = "a", Description = new string('a', 3000) },
                        new { Id = "b", Description = new string('b', 3000), Notes = new string('n', 3000) }
                    }
                };

                JsonElement result = JsonSerializer.SerializeToElement(
                    McpResultPreview.Apply(payload, includeFullContent: false));

                AssertEqual(3, result.GetProperty("TruncatedFieldCount").GetInt32(),
                    "Every long field in every member counts.");
                JsonElement first = result.GetProperty("Objects")[0];
                AssertEqual(3000, first.GetProperty("DescriptionLength").GetInt32());
                AssertEqual(2, result.GetProperty("TotalRecords").GetInt32(),
                    "Envelope fields must survive.");
                return Task.CompletedTask;
            });

            await RunTest("A bare array is wrapped so the count has somewhere to live", () =>
            {
                // list_prompt_templates returns a bare list. Without the wrapper a reader
                // could not tell a preview from the whole record.
                object payload = new List<object>
                {
                    new { Id = "t1", Body = new string('b', 4000) }
                };

                JsonElement result = JsonSerializer.SerializeToElement(
                    McpResultPreview.Apply(payload, includeFullContent: false));

                AssertEqual(1, result.GetProperty("TruncatedFieldCount").GetInt32());
                AssertEqual(4000, result.GetProperty("Items")[0].GetProperty("BodyLength").GetInt32());
                return Task.CompletedTask;
            });

            await RunTest("It measurably shrinks a payload of the size that broke callers", () =>
            {
                // The measured offender was 388,594 characters. The point of the helper is
                // the size reduction, so assert on that rather than only on structure.
                // 50 records, because that is the page size these tools actually use.
                // The bound is about one page fitting, not about an arbitrary fixture.
                List<object> objects = new List<object>();
                for (int i = 0; i < 50; i++)
                {
                    objects.Add(new
                    {
                        Id = "obj_" + i,
                        Description = new string('d', 2000),
                        AcceptanceCriteria = new string('a', 1500)
                    });
                }

                object payload = new { Objects = objects };
                int before = JsonSerializer.Serialize(payload).Length;
                int after = JsonSerializer.Serialize(
                    McpResultPreview.Apply(payload, includeFullContent: false)).Length;

                AssertTrue(before > 150000, "One page of these records is already oversized.");
                // The bound that matters is absolute, not proportional: the caller's
                // limit does not scale with the size of the backlog. A full page of the
                // worst-shaped records must fit.
                AssertTrue(after < 60000,
                    "A previewed page must fit well under the caller's tool output limit; got "
                        + after + " from " + before);
                AssertTrue(after < before / 5,
                    "A previewed payload should be a small fraction of the original; got "
                        + after + " from " + before);
                return Task.CompletedTask;
            });
        }
    }
}
