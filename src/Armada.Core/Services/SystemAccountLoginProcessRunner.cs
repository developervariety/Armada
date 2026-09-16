namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>Starts runtime login commands as real child processes, without a shell.</summary>
    public sealed class SystemAccountLoginProcessRunner : IAccountLoginProcessRunner
    {
        #region Public-Methods

        /// <inheritdoc />
        public IAccountLoginProcess Start(AccountLoginProcessRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.Executable)) throw new ArgumentException("Executable is required.", nameof(request));
            ProcessStartInfo info = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = request.WorkingDirectory
            };
            foreach (string argument in request.Arguments) info.ArgumentList.Add(argument);
            foreach (KeyValuePair<string, string> pair in request.Environment) info.Environment[pair.Key] = pair.Value;
            Process process = new Process { StartInfo = info };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Login process did not start.");
            }
            catch
            {
                process.Dispose();
                throw;
            }
            return new SystemAccountLoginProcess(process);
        }

        #endregion
    }
}
