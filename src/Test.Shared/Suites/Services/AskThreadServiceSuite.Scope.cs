namespace Test.Shared.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private static async Task OverlapAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging) { AutoComplete = false };
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                AskTurnSendRequest request = new AskTurnSendRequest { RequestId = "held", Content = "First question" };
                Task<AskTurnStart> running = service.SendTurnAsync(Owner(), thread.Id, request);
                AskThreadReplayRuntime runtime = await factory.Created.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                try
                {
                    AskTurnStart same = await service.SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                    AssertEqual(202, same.StatusCode);
                    AskTurnStart other = await service.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "other", Content = "Overtake" }).ConfigureAwait(false);
                    AssertEqual(409, other.StatusCode);
                    AskThreadService restarted = Service(db, factory, logging);
                    AskTurnStart uncertain = await restarted.SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                    AssertEqual(409, uncertain.StatusCode);
                    AssertEqual(AskTurnStateEnum.Uncertain, uncertain.Turn!.State);
                    AssertEqual(1, factory.Starts.Count);
                }
                finally
                {
                    runtime.Complete();
                }
                AssertEqual(200, (await running.ConfigureAwait(false)).StatusCode);
            }
        }

        private static async Task OwnerAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                AuthContext stranger = AuthContext.Authenticated("default", "another", true, true, "Session");
                AssertNull(await service.GetThreadAsync(stranger, thread.Id).ConfigureAwait(false));
                AuthContext foreignTenant = AuthContext.Authenticated("other", "default", true, true, "Session");
                AssertNull(await service.GetThreadAsync(foreignTenant, thread.Id).ConfigureAwait(false));
                AuthContext mission = Owner();
                mission.MissionId = "msn_example";
                bool refused = false;
                try { await service.CreateThreadAsync(mission, new AskThreadCreateRequest()).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { refused = true; }
                AssertTrue(refused, "A mission token is not a human conversation owner.");
                AssertEqual(0, factory.Starts.Count);
            }
        }
    }
}
