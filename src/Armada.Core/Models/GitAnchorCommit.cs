namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One commit named in a mission brief's git anchors block. Holds only what a captain needs to
    /// decide whether to read the commit: the hash to pass to git show, the subject line, and the
    /// date. The body and the diff stay in the repository, where the captain can fetch them on
    /// demand instead of carrying them in its context window.
    /// </summary>
    public class GitAnchorCommit
    {
        #region Public-Members

        /// <summary>
        /// Abbreviated commit hash. Empty when unset.
        /// </summary>
        public string Sha
        {
            get
            {
                return _Sha;
            }
            set
            {
                _Sha = value ?? "";
            }
        }

        /// <summary>
        /// Commit subject line, clamped to a single line of reasonable length so one verbose subject
        /// cannot dominate the anchors block.
        /// </summary>
        public string Subject
        {
            get
            {
                return _Subject;
            }
            set
            {
                string effective = value ?? "";
                effective = effective.Replace("\r", " ").Replace("\n", " ").Trim();
                if (effective.Length > MaxSubjectChars) effective = effective.Substring(0, MaxSubjectChars) + "...";
                _Subject = effective;
            }
        }

        /// <summary>
        /// Author date in ISO-8601 short form, for example 2026-08-10. Empty when unset.
        /// </summary>
        public string DateUtc
        {
            get
            {
                return _DateUtc;
            }
            set
            {
                _DateUtc = value ?? "";
            }
        }

        #endregion

        #region Private-Members

        private const int MaxSubjectChars = 120;

        private string _Sha = "";
        private string _Subject = "";
        private string _DateUtc = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public GitAnchorCommit()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Render the commit as one brief line in the form "sha subject (date)".
        /// </summary>
        /// <returns>Single-line rendering.</returns>
        public string ToBriefLine()
        {
            string line = Sha;
            if (!String.IsNullOrEmpty(Subject)) line = line + " " + Subject;
            if (!String.IsNullOrEmpty(DateUtc)) line = line + " (" + DateUtc + ")";
            return line;
        }

        #endregion
    }
}
