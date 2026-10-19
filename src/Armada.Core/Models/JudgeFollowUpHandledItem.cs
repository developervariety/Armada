namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The marker that one item of a Judge follow-up was given a home. An item is addressed by its
    /// follow-up id and its one-based position in the follow-up's split items.
    /// </summary>
    public sealed class JudgeFollowUpHandledItem
    {
        #region Public-Members

        /// <summary>Maximum stored length of the disposition text.</summary>
        public const int MaximumDispositionLength = 2000;

        /// <summary>Maximum stored length of the handler name.</summary>
        public const int MaximumHandledByLength = 256;

        /// <summary>Unique identifier.</summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>Owning user identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Judge follow-up that holds the item.</summary>
        public string FollowUpId
        {
            get => _FollowUpId;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(FollowUpId));
                _FollowUpId = value;
            }
        }

        /// <summary>One-based position of the item in the follow-up's split items.</summary>
        public int ItemNumber
        {
            get => _ItemNumber;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(ItemNumber));
                _ItemNumber = value;
            }
        }

        /// <summary>Kind of home the item was given.</summary>
        public JudgeFollowUpDispositionKindEnum Kind { get; set; } = JudgeFollowUpDispositionKindEnum.Note;

        /// <summary>The objective id, the decision, or the note, according to <see cref="Kind"/>.</summary>
        public string Disposition { get; set; } = String.Empty;

        /// <summary>Who handled the item.</summary>
        public string HandledBy { get; set; } = String.Empty;

        /// <summary>When the item was handled, in UTC.</summary>
        public DateTime HandledUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.JudgeFollowUpHandledItemIdPrefix, 24);
        private string _FollowUpId = "unknown";
        private int _ItemNumber = 1;

        #endregion
    }
}
