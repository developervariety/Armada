namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// One run of a battery pack against one captain: the grade and timing of each task. Battery missions are
    /// evaluation work, so they stay out of production summaries.
    /// </summary>
    public class BatteryRun
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (bat_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = String.IsNullOrEmpty(value) ? throw new ArgumentNullException(nameof(Id)) : value;
        }

        /// <summary>
        /// Tenant that started the run.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User that started the run.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Captain that ran every task (cpt_ prefix).
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Captain name at the time of the run.
        /// </summary>
        public string? CaptainName { get; set; } = null;

        /// <summary>
        /// Pack identifier.
        /// </summary>
        public string PackId
        {
            get => _PackId;
            set => _PackId = value ?? String.Empty;
        }

        /// <summary>
        /// Pack version at the time of the run. Results of different versions are never comparable.
        /// </summary>
        public int PackVersion { get; set; } = 0;

        /// <summary>
        /// Lifecycle status.
        /// </summary>
        public BatteryRunStatusEnum Status { get; set; } = BatteryRunStatusEnum.Running;

        /// <summary>
        /// Why the run stopped on an error, or null.
        /// </summary>
        public string? Error { get; set; } = null;

        /// <summary>
        /// UTC time the run started.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time the run ended, or null while it runs.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// The grade of each task in manifest order; tasks not yet reached are absent.
        /// </summary>
        public List<BatteryTaskResult> Results
        {
            get => _Results;
            set => _Results = value ?? new List<BatteryTaskResult>();
        }

        /// <summary>
        /// Tasks graded Passed.
        /// </summary>
        public int Passed => _Results.Count(r => r.Grade == BatteryTaskGradeEnum.Passed);

        /// <summary>
        /// Tasks graded Failed.
        /// </summary>
        public int Failed => _Results.Count(r => r.Grade == BatteryTaskGradeEnum.Failed);

        /// <summary>
        /// Tasks graded CouldNotRun.
        /// </summary>
        public int CouldNotRun => _Results.Count(r => r.Grade == BatteryTaskGradeEnum.CouldNotRun);

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.BatteryRunIdPrefix, 24);
        private string _PackId = String.Empty;
        private List<BatteryTaskResult> _Results = new List<BatteryTaskResult>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryRun()
        {
        }

        #endregion
    }
}
