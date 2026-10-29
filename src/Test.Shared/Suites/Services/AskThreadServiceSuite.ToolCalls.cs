namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private const string ToolStarted = "[ARMADA:ACTIVITY] tool read src/File.cs";
        private const string ToolFinished = "[ARMADA:ACTIVITY] tool read src/File.cs (ok)";

        private static async Task ToolCallsSavedAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging) { Lines = new List<string> { ToolStarted, ToolFinished } };
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                await service.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "with-tools", Content = "Look at the file" }).ConfigureAwait(false);

                AskTurn? reloaded = await Service(db, factory, logging).GetTurnAsync(Owner(), thread.Id, "with-tools").ConfigureAwait(false);
                AssertEqual(AskTurnStateEnum.Completed, reloaded!.State);
                AssertEqual(1, reloaded.Response!.ToolCalls.Count);
                AssertEqual("read", reloaded.Response.ToolCalls[0].Name);
                AssertEqual("success", reloaded.Response.ToolCalls[0].Status);
                AssertEqual("src/File.cs", reloaded.Response.ToolCalls[0].Arguments);

                // A turn that fails after calling a tool keeps the call it made.
                AskThreadRuntimeFactory failing = new AskThreadRuntimeFactory(logging) { Reply = String.Empty, Lines = new List<string> { ToolStarted, ToolFinished, "[ARMADA:ACTIVITY] codex error quota reached" } };
                AskThreadService failingService = Service(db, failing, logging);
                await failingService.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "failed-tools", Content = "Try again" }).ConfigureAwait(false);
                AskTurn? failed = await Service(db, failing, logging).GetTurnAsync(Owner(), thread.Id, "failed-tools").ConfigureAwait(false);
                AssertEqual(AskTurnStateEnum.Failed, failed!.State);
                AssertEqual(1, failed.Response!.ToolCalls.Count);
                AssertEqual("read", failed.Response.ToolCalls[0].Name);
            }
        }

        private static async Task ToolCallsLiveAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging) { AutoComplete = false };
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                Task<AskTurnStart> running = service.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "live", Content = "Long job" });
                AskThreadReplayRuntime runtime = await factory.Created.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                try
                {
                    runtime.Emit(ToolStarted);
                    AskTurn? midway = await service.GetTurnAsync(Owner(), thread.Id, "live").ConfigureAwait(false);
                    AssertEqual(AskTurnStateEnum.Running, midway!.State);
                    AssertEqual(1, midway.LiveToolCalls.Count);
                    AssertEqual("running", midway.LiveToolCalls[0].Status);

                    runtime.Emit(ToolFinished);
                    AskTurn? afterTool = await service.GetTurnAsync(Owner(), thread.Id, "live").ConfigureAwait(false);
                    AssertEqual(1, afterTool!.LiveToolCalls.Count);
                    AssertEqual("success", afterTool.LiveToolCalls[0].Status);

                    // A restarted server has no live calls for a turn it did not run.
                    AskTurn? foreign = await Service(db, factory, logging).GetTurnAsync(Owner(), thread.Id, "live").ConfigureAwait(false);
                    AssertEqual(AskTurnStateEnum.Uncertain, foreign!.State);
                    AssertEqual(0, foreign.LiveToolCalls.Count);
                }
                finally
                {
                    runtime.Complete();
                }
                AskTurnStart done = await running.ConfigureAwait(false);
                AssertEqual(1, done.Turn!.Response!.ToolCalls.Count);
                AssertEqual(0, done.Turn.LiveToolCalls.Count);
                AssertEqual(0, (await service.GetTurnAsync(Owner(), thread.Id, "live").ConfigureAwait(false))!.LiveToolCalls.Count);
            }
        }

        private static async Task UnreadAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                await service.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "unread", Content = "Question" }).ConfigureAwait(false);
                AssertEqual(1, (await service.GetThreadAsync(Owner(), thread.Id).ConfigureAwait(false))!.UnreadCount);

                AuthContext stranger = AuthContext.Authenticated("default", "usr_stranger", true, true, "Session");
                AssertTrue(await service.MarkThreadReadAsync(stranger, thread.Id).ConfigureAwait(false) == null);
                AssertEqual(1, (await service.GetThreadAsync(Owner(), thread.Id).ConfigureAwait(false))!.UnreadCount);

                AssertEqual(0, (await service.MarkThreadReadAsync(Owner(), thread.Id).ConfigureAwait(false))!.UnreadCount);
                AssertEqual(1, (await service.MarkThreadUnreadAsync(Owner(), thread.Id).ConfigureAwait(false))!.UnreadCount);
                AssertTrue(await service.MarkThreadReadAsync(Owner(), "ask_missing").ConfigureAwait(false) == null);
            }
        }
    }
}
