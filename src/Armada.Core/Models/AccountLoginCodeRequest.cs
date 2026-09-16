namespace Armada.Core.Models
{
    /// <summary>Code the owner pastes back into a pending paste-code login.</summary>
    public sealed class AccountLoginCodeRequest
    {
        #region Public-Members

        /// <summary>The code shown by the provider after sign-in. Relayed to the login process only; never stored.</summary>
        public string? Code { get; set; }

        #endregion
    }
}
