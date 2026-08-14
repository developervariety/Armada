namespace Armada.Core.Models
{
    /// <summary>
    /// Describes a mission's title and description for voyage dispatch.
    /// Carries a mission title and description as a dedicated model.
    /// </summary>
    public class MissionDescription
    {
        #region Public-Members

        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Mission description.
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Optional required capability tier for dispatch routing (Economy/Standard/Premium). Null routes
        /// to any idle captain. When a preferred captain is set via <see cref="RequestedCaptainId"/>, this is
        /// the fallback tier used when that captain is busy.
        /// </summary>
        public Armada.Core.Enums.CaptainTierEnum? Tier { get; set; } = null;

        /// <summary>
        /// Optional preferred (dictated) captain identifier for this mission, referenced by captain id
        /// (cpt_ prefix). When set and idle, dispatch assigns it; when busy, dispatch falls back by
        /// <see cref="Tier"/>. Null means normal persona/tier routing.
        /// </summary>
        public string? RequestedCaptainId { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public MissionDescription()
        {
        }

        /// <summary>
        /// Instantiate with title and description.
        /// </summary>
        /// <param name="title">Mission title.</param>
        /// <param name="description">Mission description.</param>
        public MissionDescription(string title, string description)
        {
            Title = title ?? "";
            Description = description ?? "";
        }

        #endregion
    }
}
