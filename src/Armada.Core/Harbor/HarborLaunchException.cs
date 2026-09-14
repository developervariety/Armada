namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// A mission launch routed to a Harbor runner was refused. The launch is not retried locally; the reason names why.
    /// </summary>
    public sealed class HarborLaunchException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>Stable refusal reason.</summary>
        public string Reason { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="reason">Stable refusal reason.</param>
        /// <param name="detail">Optional detail, for example the refused variable names. Never a secret value.</param>
        public HarborLaunchException(string reason, string? detail = null)
            : base(String.IsNullOrWhiteSpace(detail) ? reason : reason + ": " + detail)
        {
            Reason = String.IsNullOrWhiteSpace(reason) ? "harbor_launch_refused" : reason;
        }

        #endregion
    }
}
