namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A shared coordination room where operator sessions, captains, and the admiral
    /// post notes so concurrent sessions stay aware of fleet activity.
    /// </summary>
    public class CoordinationRoom
    {
        /// <summary>
        /// Key of the one shared room every operator session and captain reads. Every
        /// board tool falls back to it when no room key is given.
        /// </summary>
        public const string DefaultKey = "fleet";

        /// <summary>
        /// Resolve a caller-supplied room key to the key that is stored. A missing key,
        /// the default key in any casing, and the literal word "default" all mean the
        /// shared room: clients read "omit for the default room" and send the word, and
        /// a stored second room under that spelling splits the board in two, so the
        /// alias is resolved here, at the only seam every room lookup goes through.
        /// </summary>
        /// <param name="key">Caller-supplied key, or null.</param>
        /// <returns>The key to store or look up.</returns>
        public static string NormalizeKey(string? key)
        {
            if (String.IsNullOrWhiteSpace(key)) return DefaultKey;
            string trimmed = key.Trim();
            if (String.Equals(trimmed, DefaultKey, StringComparison.OrdinalIgnoreCase)) return DefaultKey;
            if (String.Equals(trimmed, "default", StringComparison.OrdinalIgnoreCase)) return DefaultKey;
            return trimmed;
        }

        #region Public-Members

        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// URL-safe unique key for the room, for example "fleet" or a voyage identifier.
        /// </summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>
        /// Display name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Optional description of the room's purpose.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CoordinationRoomIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationRoom()
        {
        }

        #endregion
    }
}
