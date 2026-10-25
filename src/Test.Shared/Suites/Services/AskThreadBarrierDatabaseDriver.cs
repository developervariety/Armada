namespace Test.Shared.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;

    internal sealed class AskThreadBarrierDatabaseDriver : DatabaseDriver
    {
        private readonly DatabaseDriver _Inner;

        public AskThreadBarrierDatabaseDriver(DatabaseDriver inner, IAskThreadMethods askThreads)
        {
            _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Captains = inner.Captains;
            ModelEndpoints = inner.ModelEndpoints;
            AskThreads = askThreads ?? throw new ArgumentNullException(nameof(askThreads));
        }

        public override Task InitializeAsync(CancellationToken token = default)
        {
            return _Inner.InitializeAsync(token);
        }

        public override Task<int> GetSchemaVersionAsync(CancellationToken token = default)
        {
            return _Inner.GetSchemaVersionAsync(token);
        }

        public override void Dispose()
        {
        }
    }
}
