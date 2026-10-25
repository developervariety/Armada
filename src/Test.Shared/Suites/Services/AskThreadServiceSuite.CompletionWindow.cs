namespace Test.Shared.Suites.Services
{
    using System;
    using System.Runtime.ExceptionServices;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private static async Task CompletionFenceWindowAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThreadCompletionReadBarrier barrier = new AskThreadCompletionReadBarrier(db.Driver.AskThreads, "first");
                AskThreadBarrierDatabaseDriver serviceDatabase = new AskThreadBarrierDatabaseDriver(db.Driver, barrier);
                CaptainChatService chat = new CaptainChatService(serviceDatabase, factory, null, null, logging);
                AskThreadService service = new AskThreadService(serviceDatabase, chat);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                Task<AskTurnStart> firstTask = service.SendTurnAsync(Owner(), thread.Id,
                    new AskTurnSendRequest { RequestId = "first", Content = "First question" });
                AskTurnStart? secondResult = null;
                Exception? failure = null;
                try
                {
                    await barrier.FinalReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    AskThread? betweenTurns = await db.Driver.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false);
                    AssertNotNull(betweenTurns);
                    AssertNull(betweenTurns!.ActiveTurnId, "The first terminal commit released the durable thread fence before its final read.");
                    secondResult = await service.SendTurnAsync(Owner(), thread.Id,
                        new AskTurnSendRequest { RequestId = "second", Content = "Second question" }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    AssertEqual(200, secondResult!.StatusCode);
                    AssertEqual(AskTurnStateEnum.Completed, secondResult.Turn!.State);
                    AssertEqual(2, factory.Starts.Count);
                    AskThread? afterSecond = await db.Driver.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false);
                    AssertNotNull(afterSecond);
                    AssertNull(afterSecond!.ActiveTurnId);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    barrier.ReleaseFinalRead();
                }

                AskTurnStart firstResult = await firstTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                AssertEqual(200, firstResult.StatusCode);
                AssertEqual(AskTurnStateEnum.Completed, firstResult.Turn!.State);
                AssertNotNull(secondResult);
                AssertEqual(1L, firstResult.Turn.Sequence);
                AssertEqual(2L, secondResult!.Turn!.Sequence);
            }
        }
    }
}
