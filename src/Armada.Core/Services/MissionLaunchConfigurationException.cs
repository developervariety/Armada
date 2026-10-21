namespace Armada.Core.Services
{
    using System;

    /// <summary>A mission cannot start until its configuration is corrected; retrying the same launch cannot repair it.</summary>
    public sealed class MissionLaunchConfigurationException : InvalidOperationException
    {
        /// <summary>Describe the configuration problem and the operator's corrective action.</summary>
        /// <param name="message">Actionable explanation, without credentials or endpoint payloads.</param>
        public MissionLaunchConfigurationException(string message) : base(message) { }
    }
}
