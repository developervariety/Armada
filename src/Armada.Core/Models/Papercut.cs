namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One piece of friction a captain met during a mission and reported instead of working around it
    /// silently. Captains emit these as `[ARMADA:PAPERCUT]` lines; the admiral stores each one as an
    /// event so that repeated reports of the same friction can be counted and promoted to real work.
    ///
    /// The payload half (category, severity, title, detail, path) comes from the captain. The context
    /// half (mission, captain, vessel, voyage, runtime, timestamp) comes from the admiral, which knows
    /// it authoritatively and never trusts the captain for it.
    /// </summary>
    public class Papercut
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the event that stores this papercut. Set on read-back.
        /// </summary>
        public string? EventId { get; set; } = null;

        /// <summary>
        /// Friction category.
        /// </summary>
        public PapercutCategoryEnum Category { get; set; } = PapercutCategoryEnum.Other;

        /// <summary>
        /// Reported severity.
        /// </summary>
        public PapercutSeverityEnum Severity { get; set; } = PapercutSeverityEnum.Low;

        /// <summary>
        /// One-line summary of the friction. Truncated to <see cref="MaxTitleChars"/>.
        /// </summary>
        public string Title
        {
            get => _Title;
            set => _Title = Clamp(value, MaxTitleChars);
        }

        /// <summary>
        /// Optional longer explanation. Truncated to <see cref="MaxDetailChars"/>.
        /// </summary>
        public string? Detail
        {
            get => _Detail;
            set => _Detail = String.IsNullOrWhiteSpace(value) ? null : Clamp(value!, MaxDetailChars);
        }

        /// <summary>
        /// Optional repository-relative path the friction points at. Truncated to <see cref="MaxPathChars"/>.
        /// </summary>
        public string? Path
        {
            get => _Path;
            set => _Path = String.IsNullOrWhiteSpace(value) ? null : Clamp(value!, MaxPathChars);
        }

        /// <summary>
        /// Mission that reported it.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Captain that reported it.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Vessel the mission ran against.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Voyage the mission belonged to.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Captain runtime name, used to see whether one runtime reports far more or far less than the rest.
        /// </summary>
        public string? Runtime { get; set; } = null;

        /// <summary>
        /// Persona of the reporting mission.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Report timestamp in UTC.
        /// </summary>
        public DateTime ReportedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Maximum stored title length.
        /// </summary>
        public const int MaxTitleChars = 200;

        /// <summary>
        /// Maximum stored detail length.
        /// </summary>
        public const int MaxDetailChars = 1000;

        /// <summary>
        /// Maximum stored path length.
        /// </summary>
        public const int MaxPathChars = 260;

        #endregion

        #region Private-Members

        private string _Title = "";
        private string? _Detail = null;
        private string? _Path = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Papercut()
        {
        }

        #endregion

        #region Private-Methods

        private static string Clamp(string value, int maxChars)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string trimmed = value.Trim();
            if (trimmed.Length <= maxChars) return trimmed;
            return trimmed.Substring(0, maxChars).TrimEnd() + "...";
        }

        #endregion
    }
}
