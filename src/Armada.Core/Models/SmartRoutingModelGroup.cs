namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>One persona model group in Smart Routing order, with the usage-filtered captains it holds.</summary>
    public sealed class SmartRoutingModelGroup
    {
        #region Public-Members

        /// <summary><c>default</c>, <c>lighter</c>, <c>stronger</c>, or <c>unlisted</c> for captains whose model is in no list.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>The group's configured models; empty for the unlisted group.</summary>
        public List<string> Models { get; set; } = new List<string>();

        /// <summary>The group's captains in Smart Routing order.</summary>
        public List<string> CaptainIds { get; set; } = new List<string>();

        #endregion
    }
}
