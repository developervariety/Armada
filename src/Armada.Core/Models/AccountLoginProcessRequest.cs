namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>A runtime login command to start inside an account folder.</summary>
    public sealed class AccountLoginProcessRequest
    {
        #region Public-Members

        /// <summary>CLI to run.</summary>
        public string Executable { get; set; } = String.Empty;

        /// <summary>Arguments, passed without a shell.</summary>
        public List<string> Arguments { get; set; } = new List<string>();

        /// <summary>Variables added to the inherited environment.</summary>
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Working directory, normally the account folder.</summary>
        public string WorkingDirectory { get; set; } = String.Empty;

        #endregion
    }
}
