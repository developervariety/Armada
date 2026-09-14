namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// A native client or runtime could not be started because it is not installed or not executable.
    /// </summary>
    public sealed class SelfDeployNativeClientMissingException : Exception
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="executable">Executable name that was requested.</param>
        /// <param name="notExecutable">True when the file exists but cannot be executed.</param>
        /// <param name="innerException">Operating-system start failure.</param>
        public SelfDeployNativeClientMissingException(string executable, bool notExecutable, Exception? innerException = null)
            : base((notExecutable ? "native_client_not_executable_" : "native_client_missing_") + SafeName(executable), innerException)
        {
            Executable = executable ?? String.Empty;
            FailureReason = Message;
        }

        /// <summary>
        /// Requested executable.
        /// </summary>
        public string Executable { get; }

        /// <summary>
        /// Stable reason, <c>native_client_missing_&lt;tool&gt;</c> or <c>native_client_not_executable_&lt;tool&gt;</c>.
        /// </summary>
        public string FailureReason { get; }

        private static string SafeName(string executable)
        {
            string name = Path.GetFileNameWithoutExtension(executable ?? String.Empty).ToLowerInvariant();
            StringBuilder safe = new StringBuilder();
            foreach (char character in name)
            {
                safe.Append((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') ? character : '_');
            }
            return safe.Length == 0 ? "unknown" : safe.ToString();
        }
    }
}
