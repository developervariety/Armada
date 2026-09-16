namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Runs Smart Routing over a fixed pool with no tier configuration and a first-index pick, so the Legacy
    /// Routing order is the pool order and a test asserts only the usage filter and route restriction.
    /// </summary>
    public static class SmartRoutingTestSelect
    {
        /// <summary>Select for one mission.</summary>
        /// <param name="usage">Usage state service.</param>
        /// <param name="policy">Smart Routing policy.</param>
        /// <param name="mission">Mission.</param>
        /// <param name="pool">Candidate pool in legacy order.</param>
        /// <param name="busy">Busy captain ids.</param>
        /// <param name="now">Evaluation time.</param>
        /// <returns>The decision.</returns>
        public static UsageRoutingDecision Select(UsageRoutingService usage, UsageRoutingSettings policy, Mission mission, List<Captain> pool, IReadOnlyCollection<string> busy, DateTime now)
        {
            return SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
            {
                Tiers = new ModelTierSettings(),
                Policy = policy,
                Usage = usage,
                Mission = mission,
                Pool = pool,
                BusyCaptainIds = busy,
                NowUtc = now,
                RandomPick = n => 0
            }).GetAwaiter().GetResult();
        }
    }
}
