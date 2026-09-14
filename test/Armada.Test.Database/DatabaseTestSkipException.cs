namespace Armada.Test.Database
{
    using System;

    /// <summary>
    /// A case cannot run in this environment. The runner prints and counts it as a named skip, never as a pass.
    /// </summary>
    public sealed class DatabaseTestSkipException : Exception
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="reason">Stable reason, followed by what would let the case run.</param>
        public DatabaseTestSkipException(string reason)
            : base(String.IsNullOrWhiteSpace(reason) ? "skipped_without_reason" : reason)
        {
        }
    }
}
