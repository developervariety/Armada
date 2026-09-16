namespace Armada.Core.Services
{
    using System;

    /// <summary>An account login request was refused, with a safe reason code and the HTTP status that fits it.</summary>
    public sealed class AccountLoginException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>Safe reason code, for example account_id_invalid.</summary>
        public string Code { get; }

        /// <summary>HTTP status code for the refusal.</summary>
        public int StatusCode { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="code">Safe reason code.</param>
        /// <param name="statusCode">HTTP status code.</param>
        /// <param name="message">Safe operator-facing message; never contains a key, code, or process output.</param>
        public AccountLoginException(string code, int statusCode, string message)
            : base(message)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            StatusCode = statusCode;
        }

        #endregion
    }
}
