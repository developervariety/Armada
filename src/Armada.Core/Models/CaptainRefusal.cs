namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// A classified captain refusal: its kind, the reason the captain gave, and the bounded output line it
    /// was recognised from.
    /// </summary>
    public class CaptainRefusal
    {
        #region Public-Members

        /// <summary>Kind of refusal.</summary>
        public CaptainRefusalKindEnum Kind { get; set; } = CaptainRefusalKindEnum.None;

        /// <summary>The reason the captain or provider gave, bounded; empty when none was given.</summary>
        public string Reason { get; set; } = "";

        /// <summary>The bounded output line the refusal was recognised from.</summary>
        public string Evidence { get; set; } = "";

        /// <summary>True when the captain refused.</summary>
        public bool IsRefusal => Kind != CaptainRefusalKindEnum.None;

        #endregion
    }
}
