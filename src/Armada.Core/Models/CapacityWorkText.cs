namespace Armada.Core.Models
{
    using System;

    /// <summary>The work text the capacity decision reads: the objective or mission title and description.</summary>
    public sealed class CapacityWorkText
    {
        #region Public-Members

        /// <summary>Title.</summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>Description.</summary>
        public string Description { get; set; } = String.Empty;

        #endregion
    }
}
