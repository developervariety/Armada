namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A task of a battery pack as listed with the pack.
    /// </summary>
    public class BatteryTaskInfo
    {
        #region Public-Members

        /// <summary>
        /// Task identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = value ?? String.Empty;
        }

        /// <summary>
        /// Title.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Grader type.
        /// </summary>
        public BatteryGraderTypeEnum? Grader { get; set; } = null;

        /// <summary>
        /// Toolchain names the task needs.
        /// </summary>
        public List<string> Toolchain
        {
            get => _Toolchain;
            set => _Toolchain = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private string _Id = String.Empty;
        private List<string> _Toolchain = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryTaskInfo()
        {
        }

        #endregion
    }
}
