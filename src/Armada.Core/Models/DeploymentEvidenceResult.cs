namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Verdict on whether an environment's required Check types passed at a deployment's source commit.
    /// </summary>
    public class DeploymentEvidenceResult
    {
        #region Public-Members

        /// <summary>
        /// True when every required Check type has a deciding Passed record at the source commit, or when the
        /// environment requires none.
        /// </summary>
        public bool Satisfied { get; set; } = false;

        /// <summary>
        /// The deciding Passed records, one per required type, in the order the types are required.
        /// </summary>
        public List<string> CheckRunIds
        {
            get => _CheckRunIds;
            set => _CheckRunIds = value ?? new List<string>();
        }

        /// <summary>
        /// One line naming what each required type found, or why the evidence refused.
        /// </summary>
        public string Summary
        {
            get => _Summary;
            set => _Summary = value ?? String.Empty;
        }

        #endregion

        #region Private-Members

        private List<string> _CheckRunIds = new List<string>();
        private string _Summary = String.Empty;

        #endregion
    }
}
