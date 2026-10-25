namespace Test.Shared.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private static async Task RecoveryAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThread thread = await ThreadAsync(db, Service(db, factory, logging)).ConfigureAwait(false);
                await db.Driver.AskThreads.TryReserveTurnAsync("default", "default", new AskTurn
                {
                    ThreadId = thread.Id, RequestId = "interrupted", CaptainId = thread.CaptainId!, Content = "Question"
                }).ConfigureAwait(false);
                AskThreadService recreated = Service(db, factory, logging);
                AskTurnStart replay = await recreated.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "interrupted", Content = "Question" }).ConfigureAwait(false);
                AssertEqual(409, replay.StatusCode);
                AssertEqual(AskTurnStateEnum.Uncertain, replay.Turn!.State);
                AskTurnStart replacement = await recreated.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "new", Content = "Question" }).ConfigureAwait(false);
                AssertEqual(409, replacement.StatusCode);
                AssertEqual(0, factory.Starts.Count);
                AssertEqual("interrupted", (await recreated.GetThreadAsync(Owner(), thread.Id).ConfigureAwait(false))!.ActiveTurnId);
            }
        }
    }
}
