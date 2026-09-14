namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// The credential the admiral hands to the captain processes it launches, so their MCP calls
    /// authenticate like every other client.
    ///
    /// The token is random, exists only in this process's memory and in the environment of the
    /// processes it starts, and changes on every admiral start. Configuration files reference it by
    /// environment variable name only, so the value never lands in a dock or a scoped config file.
    /// </summary>
    public static class McpLaunchCredential
    {
        #region Public-Members

        /// <summary>
        /// Environment variable that carries the token into a launched captain process.
        /// </summary>
        public const string EnvironmentVariable = "ARMADA_MCP_TOKEN";

        /// <summary>
        /// The token for this admiral process.
        /// </summary>
        public static string Token => _Token.Value;

        #endregion

        #region Private-Members

        private static readonly Lazy<string> _Token = new Lazy<string>(CreateToken);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide whether a presented bearer value is this process's launch token, in constant time.
        /// </summary>
        /// <param name="presented">Presented bearer value.</param>
        /// <returns>True when it matches.</returns>
        public static bool Matches(string? presented)
        {
            if (String.IsNullOrEmpty(presented)) return false;
            byte[] expected = Encoding.UTF8.GetBytes(Token);
            byte[] supplied = Encoding.UTF8.GetBytes(presented.Trim());
            return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }

        #endregion

        #region Private-Methods

        private static string CreateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return "armada-launch-" + Convert.ToHexString(bytes).ToLowerInvariant();
        }

        #endregion
    }
}
