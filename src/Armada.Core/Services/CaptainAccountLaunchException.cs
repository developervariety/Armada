namespace Armada.Core.Services
{
    using System;

    /// <summary>A captain account cannot supply its login, so the captain must not launch on the shared login instead.</summary>
    public sealed class CaptainAccountLaunchException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>Safe reason code, for example account_login_missing.</summary>
        public string Code { get; }

        /// <summary>Account that refused the launch.</summary>
        public string AccountId { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="code">Safe reason code.</param>
        /// <param name="accountId">Account identifier.</param>
        public CaptainAccountLaunchException(string code, string accountId)
            : base("Captain account " + accountId + " cannot launch: " + code)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            AccountId = accountId ?? String.Empty;
        }

        #endregion
    }
}
