namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One fact about a vessel that cannot be fixed today and that a captain will otherwise trip over
    /// or re-derive: work the owner has deliberately deferred, a test that is red pending a decision, or
    /// a truth about the dock itself.
    ///
    /// Every entry carries the objective that will remove it and a date it expires. Those two fields are
    /// the whole point. Without them the list becomes a place to write problems down instead of fixing
    /// them, and a written-down problem stops being annoying, so it stops getting fixed.
    /// </summary>
    public class DeferredFact
    {
        #region Public-Members

        /// <summary>
        /// What the captain needs to know. Empty when unset.
        /// </summary>
        public string Text
        {
            get
            {
                return _Text;
            }
            set
            {
                _Text = (value ?? "").Trim();
            }
        }

        /// <summary>
        /// Identifier of the objective that removes this fact. An entry without one is refused.
        /// </summary>
        public string FixObjectiveId
        {
            get
            {
                return _FixObjectiveId;
            }
            set
            {
                _FixObjectiveId = (value ?? "").Trim();
            }
        }

        /// <summary>
        /// Date this entry stops being trusted. After it, the entry renders as stale rather than as a
        /// current fact. It is never silently dropped: a fact that quietly disappears is indistinguishable
        /// from one that was fixed.
        /// </summary>
        public DateTime ExpiresUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Commit the fact was last checked against. Empty when unset.
        /// </summary>
        public string LastVerifiedCommit
        {
            get
            {
                return _LastVerifiedCommit;
            }
            set
            {
                _LastVerifiedCommit = (value ?? "").Trim();
            }
        }

        #endregion

        #region Private-Members

        private string _Text = "";
        private string _FixObjectiveId = "";
        private string _LastVerifiedCommit = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DeferredFact()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Reports whether the entry has everything it needs to be rendered.
        /// </summary>
        /// <returns>True when text, fix objective, and expiry are all present.</returns>
        public bool IsComplete()
        {
            return !String.IsNullOrEmpty(_Text)
                && !String.IsNullOrEmpty(_FixObjectiveId)
                && ExpiresUtc > DateTime.MinValue;
        }

        /// <summary>
        /// Reports whether the entry is past its expiry at the supplied time.
        /// </summary>
        /// <param name="nowUtc">Current time, supplied so the check is testable.</param>
        /// <returns>True when expired.</returns>
        public bool IsExpired(DateTime nowUtc)
        {
            return ExpiresUtc > DateTime.MinValue && nowUtc.Date > ExpiresUtc.Date;
        }

        #endregion
    }
}
