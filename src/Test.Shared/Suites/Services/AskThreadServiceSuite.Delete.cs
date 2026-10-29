namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private static async Task DeleteAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThreadService service = Service(db, factory, logging);
                AskThread done = await ThreadAsync(db, service).ConfigureAwait(false);
                await service.SendTurnAsync(Owner(), done.Id, new AskTurnSendRequest { RequestId = "answered", Content = "Question" }).ConfigureAwait(false);
                AskThread uncertain = await service.CreateThreadAsync(Owner(), new AskThreadCreateRequest { Title = "Interrupted", CaptainId = done.CaptainId }).ConfigureAwait(false);
                await db.Driver.AskThreads.TryReserveTurnAsync("default", "default", new AskTurn
                {
                    ThreadId = uncertain.Id, RequestId = "interrupted", CaptainId = uncertain.CaptainId!, Content = "Question"
                }).ConfigureAwait(false);
                await service.UpdateThreadAsync(Owner(), uncertain.Id, new AskThreadUpdateRequest { SetArchived = true, Archived = true }).ConfigureAwait(false);

                AskThreadDeleteResult notArchived = await service.DeleteThreadAsync(Owner(), done.Id).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.NotArchived, notArchived.Outcome);
                AskThreadDeleteResult refused = await service.DeleteThreadAsync(Owner(), uncertain.Id).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.OpenTurn, refused.Outcome);
                AssertContains("uncertain", refused.Message);
                AssertNotNull(await service.GetThreadAsync(Owner(), uncertain.Id).ConfigureAwait(false));

                await service.UpdateThreadAsync(Owner(), done.Id, new AskThreadUpdateRequest { SetArchived = true, Archived = true }).ConfigureAwait(false);
                DeleteMultipleResult batch = await service.DeleteThreadsAsync(Owner(), new List<string> { done.Id, uncertain.Id, "ask_missing", done.Id }).ConfigureAwait(false);
                AssertEqual(1, batch.Deleted);
                AssertEqual(DeleteMultipleStatusEnum.PartiallyDeleted, batch.Status);
                AssertEqual(2, batch.Skipped.Count);
                AssertTrue(batch.Skipped.Any(item => item.Id == uncertain.Id && item.Reason.Contains("uncertain")));
                AssertTrue(batch.Skipped.Any(item => item.Id == "ask_missing"));
                AssertNull(await service.GetThreadAsync(Owner(), done.Id).ConfigureAwait(false));
                AssertNotNull(await service.GetThreadAsync(Owner(), uncertain.Id).ConfigureAwait(false));

                List<ArmadaEvent> audit = await db.Driver.Events.EnumerateByTypeAsync("ask_thread.deleted", 10).ConfigureAwait(false);
                ArmadaEvent evt = audit.Single();
                AssertEqual(done.Id, evt.EntityId);
                AssertEqual("default", evt.UserId);
                AssertContains(done.Title, evt.Payload!);

                List<string> tooMany = Enumerable.Range(0, AskThreadService.MaxDeleteBatch + 1).Select(i => "ask_" + i).ToList();
                bool capped = false;
                try { await service.DeleteThreadsAsync(Owner(), tooMany).ConfigureAwait(false); }
                catch (ArgumentException) { capped = true; }
                AssertTrue(capped, "a batch over the cap is refused before any thread is touched");
            }
        }
    }
}
