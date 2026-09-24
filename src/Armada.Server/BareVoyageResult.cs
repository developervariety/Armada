namespace Armada.Server
{
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of creating a voyage without a vessel or missions.
    /// </summary>
    public sealed class BareVoyageResult
    {
        #region Public-Members

        /// <summary>
        /// Refusal code for a playbook selection that is not valid for the caller.
        /// </summary>
        public const string InvalidPlaybookCode = "invalid_playbook_selection";

        /// <summary>
        /// True when the voyage was created.
        /// </summary>
        public bool Succeeded => Voyage != null;

        /// <summary>
        /// Refusal code when refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The created voyage, with its playbook selections; null when refused.
        /// </summary>
        public Voyage? Voyage { get; private set; }

        #endregion

        #region Constructors-and-Factories

        private BareVoyageResult()
        {
        }

        /// <summary>
        /// A created voyage.
        /// </summary>
        /// <param name="voyage">Created voyage.</param>
        /// <returns>The result.</returns>
        public static BareVoyageResult Created(Voyage voyage)
        {
            return new BareVoyageResult { Voyage = voyage };
        }

        /// <summary>
        /// A refused create. Nothing was stored.
        /// </summary>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static BareVoyageResult Refused(string code, string message)
        {
            return new BareVoyageResult { Code = code, Message = message };
        }

        #endregion
    }
}
