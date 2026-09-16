namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>An approved account and optional model subset for one persona.</summary>
    public sealed class UsageRouteSettings
    {
        #region Public-Members

        /// <summary>Account to try at this position in the preference order.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Allowed model subset; empty means all models on this account, subject to mission eligibility.</summary>
        public List<string> Models { get; set; } = new List<string>();

        /// <summary>
        /// Optional work-shape tags (for example <c>mechanical</c>, <c>reasoning-heavy</c>,
        /// <c>policy-tolerant</c>). The D16 <c>routing_hint</c> typed decision prefers, among the routes
        /// already found eligible, the first route whose tags contain the work's chosen shape. A route
        /// with no tags is eligible for every shape, so a configuration that sets none behaves exactly as
        /// before. Tags never widen or narrow eligibility; they only order already-eligible routes.
        /// </summary>
        public List<string> Shapes { get; set; } = new List<string>();

        #endregion
    }
}
