namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>The inputs of one Smart Routing selection, shared by dispatch and the usage preview.</summary>
    public sealed class SmartRoutingRequest
    {
        #region Public-Members

        /// <summary>Model tier settings for the Legacy Routing order.</summary>
        public required ModelTierSettings Tiers { get; init; }

        /// <summary>The Smart Routing policy.</summary>
        public required UsageRoutingSettings Policy { get; init; }

        /// <summary>The usage state service that classifies accounts.</summary>
        public required UsageRoutingService Usage { get; init; }

        /// <summary>The mission being routed.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The gated, idle captain pool.</summary>
        public required List<Captain> Pool { get; init; }

        /// <summary>
        /// Every captain of the fleet, busy ones included, used to tell a persona model list that waits for a busy
        /// listed captain from one no listed captain can serve. Null means the pool is the whole roster.
        /// </summary>
        public IReadOnlyList<Captain>? Roster { get; init; }

        /// <summary>Captains working or reserved, for account concurrency limits.</summary>
        public IReadOnlyCollection<string> BusyCaptainIds { get; init; } = new List<string>();

        /// <summary>The evaluation time.</summary>
        public DateTime NowUtc { get; init; } = DateTime.UtcNow;

        /// <summary>True when a requested-captain tier fallback keeps only the lowest tier present.</summary>
        public bool NarrowToLowestTier { get; init; } = false;

        /// <summary>Returns an index below its argument, for equal peers within a tier.</summary>
        public Func<int, int> RandomPick { get; init; } = n => Random.Shared.Next(n);

        /// <summary>The capacity reading resolver, or null when none is available.</summary>
        public CapacityEscalationResolver? Capacity { get; init; }

        /// <summary>Supplies the work text for the capacity decision; null means the client is not called.</summary>
        public Func<CancellationToken, Task<CapacityWorkText>>? WorkText { get; init; }

        /// <summary>False for a preview, which must not share cached readings with dispatch.</summary>
        public bool UseCapacityCache { get; init; } = true;

        #endregion
    }
}
