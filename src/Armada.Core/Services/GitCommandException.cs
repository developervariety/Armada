namespace Armada.Core.Services
{
    using System;

    /// <summary>Retain a command exit code without changing existing exception message handling.</summary>
    internal sealed class GitCommandException : InvalidOperationException
    {
        internal int ExitCode { get; }
        internal GitCommandException(int exitCode, string message) : base(message) { ExitCode = exitCode; }
    }
}
