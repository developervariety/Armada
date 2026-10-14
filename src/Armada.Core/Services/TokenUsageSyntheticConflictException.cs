namespace Armada.Core.Services
{
    using System;

    /// <summary>An existing synthetic batch identifier was reused with different record contents.</summary>
    public sealed class TokenUsageSyntheticConflictException : InvalidOperationException
    {
        /// <summary>Instantiate a safe, operator-facing batch conflict.</summary>
        public TokenUsageSyntheticConflictException() : base("BatchId was already used with different ordered records.") { }
    }
}
