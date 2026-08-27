namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when a dispatch names a start ref that does not resolve in the vessel repository.
    /// The dispatch is refused before any voyage row exists, so the caller can report the ref by
    /// name instead of a generic dispatch error.
    /// </summary>
    public class StartFromRefMissingException : InvalidOperationException
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public StartFromRefMissingException(string message) : base(message)
        {
        }
    }
}
