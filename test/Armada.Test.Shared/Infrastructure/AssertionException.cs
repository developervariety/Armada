namespace Armada.Test.Shared.Infrastructure
{
    using System;

    /// <summary>
    /// Thrown by <see cref="Asserts"/> when a test expectation is not met. Distinct from
    /// arbitrary runtime exceptions so runners and diagnostics can tell an assertion failure
    /// apart from an unexpected fault in the code under test.
    /// </summary>
    public sealed class AssertionException : Exception
    {
        /// <summary>
        /// Instantiate with a failure message. The message is prefixed with a stable marker.
        /// </summary>
        /// <param name="message">Description of the failed expectation.</param>
        public AssertionException(string message)
            : base("Assertion failed: " + message)
        {
        }
    }
}
