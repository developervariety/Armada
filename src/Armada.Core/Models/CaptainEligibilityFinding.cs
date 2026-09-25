namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Why a named captain cannot take work of a persona: a machine-readable code and a reason that reads
    /// after "the captain is".
    /// </summary>
    public class CaptainEligibilityFinding
    {
        #region Public-Members

        /// <summary>
        /// Machine-readable code, for example <c>captain_persona_not_allowed</c>.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// Human-readable reason that names the code.
        /// </summary>
        public string Reason { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="code">Machine-readable code.</param>
        /// <param name="reason">Human-readable reason.</param>
        public CaptainEligibilityFinding(string code, string reason)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        }

        #endregion
    }
}
