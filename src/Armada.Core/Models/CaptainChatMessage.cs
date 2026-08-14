namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A single message in a captain chat conversation, exchanged between the operator (<c>user</c>)
    /// and the captain's model (<c>assistant</c>). Used to carry prior turns back to the server so the
    /// model sees the running conversation.
    /// </summary>
    public class CaptainChatMessage
    {
        #region Public-Members

        /// <summary>
        /// Message role: <c>user</c> or <c>assistant</c>. Defaults to <c>user</c>.
        /// </summary>
        public string Role
        {
            get => _Role;
            set => _Role = String.IsNullOrWhiteSpace(value) ? "user" : value.Trim();
        }

        /// <summary>
        /// Message text.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        #endregion

        #region Private-Members

        private string _Role = "user";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainChatMessage()
        {
        }

        #endregion
    }
}
