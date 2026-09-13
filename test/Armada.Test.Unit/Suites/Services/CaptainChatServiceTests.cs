namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for how Ask Armada assembles a captain chat reply from runtime output.
    /// </summary>
    public class CaptainChatServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Chat Service";

        /// <summary>
        /// Captured opencode --format json events: a tool call followed by the assistant answer.
        /// </summary>
        internal static readonly string[] OpenCodeToolThenAnswer = new string[]
        {
            "{\"type\":\"step_start\",\"part\":{\"type\":\"step-start\"}}",
            "{\"type\":\"tool_use\",\"timestamp\":1781834748604,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"callID\":\"read_0\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"src/File.cs\"},\"output\":\"<path>...</path>\"},\"title\":\"\"}}",
            "{\"type\":\"step_finish\",\"part\":{\"reason\":\"tool-calls\",\"type\":\"step-finish\"}}",
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Here are the entries\"}}",
            "{\"type\":\"step_finish\",\"part\":{\"reason\":\"stop\",\"type\":\"step-finish\"}}"
        };

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("An OpenCode chat reply contains only the answer, not tool activity records", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-opencode", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<string> records = new OpenCodeRecordTransform(logging).Records(OpenCodeToolThenAnswer);
                    ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, records);
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "List the entries" }).ConfigureAwait(false);

                    Console.WriteLine("CHAT reply: " + (response.Reply ?? "<null>").Replace("\n", "\\n") + " | error: " + (response.Error ?? "<none>"));
                    AssertTrue(response.Success, "The chat turn succeeds");
                    AssertEqual("Here are the entries", response.Reply, "The reply is the answer text only");
                    AssertFalse((response.Reply ?? String.Empty).Contains(ActivityRecords.ActivityMarker, StringComparison.Ordinal), "No activity record reaches the reply");
                }
            }).ConfigureAwait(false);

            await RunTest("ChatAsync_PassesAskIsolationPlanAndCleansTemporaryConfig", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-isolation", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, new[] { "Ask reply" });
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, new ArmadaSettings());
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);
                    AssertTrue(response.Success, "Chat should complete with the fake runtime.");
                    AssertNotNull(factory.LastRuntime);
                    AssertNotNull(factory.LastRuntime!.ReceivedIsolationPlan);
                    AssertTrue(factory.LastRuntime.ReceivedIsolationPlan!.FilesToWrite.Any(file => file.RelativePath == "opencode.json"), "Chat must pass the OpenCode MCP plan.");
                    AssertFalse(Directory.Exists(factory.LastRuntime.ReceivedWorkingDirectory!), "Chat must clean the temporary runtime directory.");
                }
            }).ConfigureAwait(false);

            await RunTest("Non-tool activity records stay out of a chat reply", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-codex", AgentRuntimeEnum.Codex);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<string> records = new List<string> { "[ARMADA:ACTIVITY] codex error rate limited", "The answer" };
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, new ReplayRuntimeFactory(logging, records), null, null, logging);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);

                    AssertEqual("The answer", response.Reply, "A non-tool activity record is not part of the reply");
                }
            }).ConfigureAwait(false);

            await RunTest("A canonical tool activity record reads back as name, detail and status", () =>
            {
                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/File.cs (ok)", out ToolActivityRecord read), "A finished read parses");
                AssertEqual("read", read.Name, "Name");
                AssertEqual("src/File.cs", read.Detail, "Detail");
                AssertEqual("ok", read.Status, "Status");
                AssertTrue(read.Succeeded == true, "ok succeeded");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool bash dotnet test (error exit 1)", out ToolActivityRecord failed), "A failed command parses");
                AssertEqual("dotnet test", failed.Detail, "Command detail");
                AssertEqual("error exit 1", failed.Status, "Error with exit code");
                AssertTrue(failed.Succeeded == false, "error did not succeed");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool grep", out ToolActivityRecord running), "A bare tool name parses");
                AssertNull(running.Detail, "No detail");
                AssertFalse(running.IsFinished, "No status means the call has not finished");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A (copy).cs (ok)", out ToolActivityRecord parenthesized), "A detail with parentheses parses");
                AssertEqual("src/A (copy).cs", parenthesized.Detail, "Parentheses inside the detail stay in the detail");
                AssertEqual("ok", parenthesized.Status, "The trailing status is read");

                AssertTrue(ActivityRecords.IsActivityRecord("[ARMADA:ACTIVITY] codex error"), "A non-tool activity record is an activity record");
                AssertFalse(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] codex error", out ToolActivityRecord _), "A non-tool activity record is not a tool record");
                AssertFalse(ActivityRecords.IsActivityRecord("Here are the entries"), "Answer text is not an activity record");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Tool activity cards pair a started call with its completion", () =>
            {
                ChatToolActivityTracker tracker = new ChatToolActivityTracker();
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A.cs", out ToolActivityRecord started);
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A.cs (ok)", out ToolActivityRecord completed);
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/B.cs (error)", out ToolActivityRecord other);

                ChatToolActivityEvent first = tracker.Next(started);
                ChatToolActivityEvent second = tracker.Next(completed);
                ChatToolActivityEvent third = tracker.Next(other);

                AssertEqual("started", first.Phase, "An unfinished call starts a card");
                AssertEqual(first.Id, second.Id, "The completion updates the same card");
                AssertEqual("completed", second.Phase, "The completion finishes the card");
                AssertTrue(second.Ok == true, "ok is success");
                AssertFalse(third.Id == first.Id, "A different call gets its own card");
                AssertTrue(third.Ok == false, "error is failure");
                AssertEqual("src/B.cs", third.Arguments, "The detail is the card argument");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
