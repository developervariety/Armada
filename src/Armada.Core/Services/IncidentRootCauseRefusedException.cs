namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when a person or operator closes an incident without a root cause they wrote. It carries
    /// the refusal code from <see cref="IncidentRootCauseRule"/>, so every surface reports the same code.
    /// </summary>
    public sealed class IncidentRootCauseRefusedException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>
        /// Stable machine-readable refusal code.
        /// </summary>
        public string Code { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="code">Refusal code from <see cref="IncidentRootCauseRule"/>.</param>
        public IncidentRootCauseRefusedException(string code)
            : base(IncidentRootCauseRule.Describe(code))
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
        }

        #endregion
    }
}
