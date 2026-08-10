namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Request body for a captain chat turn: the new user message plus the prior conversation so the
    /// model has context. The captain is identified by the route.
    /// </summary>
    public class CaptainChatRequest
    {
        #region Public-Members

        /// <summary>
        /// The new user message to send to the captain's model.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Prior conversation turns (oldest first), excluding the new message. Optional.
        /// </summary>
        public List<CaptainChatMessage> History { get; set; } = new List<CaptainChatMessage>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainChatRequest()
        {
        }

        #endregion
    }
}
