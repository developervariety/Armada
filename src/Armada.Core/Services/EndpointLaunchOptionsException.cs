namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Launch options failed validation. The message names the field and the rule it broke.
    /// </summary>
    public sealed class EndpointLaunchOptionsException : ArgumentException
    {
        #region Public-Members

        /// <summary>
        /// Machine-readable code for this refusal.
        /// </summary>
        public const string ErrorCode = "InvalidEndpointOptions";

        /// <summary>
        /// Name of the field that failed validation.
        /// </summary>
        public string Field { get; }

        /// <summary>
        /// Always <see cref="ErrorCode"/>.
        /// </summary>
        public string Code => ErrorCode;

        /// <summary>
        /// The refusal text, without the parameter suffix an <see cref="ArgumentException"/> adds.
        /// </summary>
        public override string Message => _Message;

        #endregion

        #region Private-Members

        private readonly string _Message;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="field">Field name.</param>
        /// <param name="message">The rule the field broke.</param>
        public EndpointLaunchOptionsException(string field, string message)
            : base(message, field)
        {
            Field = field ?? String.Empty;
            _Message = message ?? String.Empty;
        }

        #endregion
    }
}
