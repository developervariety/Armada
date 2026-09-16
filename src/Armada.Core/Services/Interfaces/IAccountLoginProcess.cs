namespace Armada.Core.Services.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>A running runtime login command. Implementations must never log the output or input they carry.</summary>
    public interface IAccountLoginProcess : IDisposable
    {
        /// <summary>Exit code once the process has exited; null while it runs.</summary>
        int? ExitCode { get; }

        /// <summary>Next chunk of combined standard output and error text, or null once all output has ended.</summary>
        Task<string?> ReadOutputAsync(CancellationToken token = default);

        /// <summary>Write one line to standard input.</summary>
        Task WriteInputLineAsync(string line, CancellationToken token = default);

        /// <summary>Wait for the process to exit.</summary>
        Task WaitForExitAsync(CancellationToken token = default);

        /// <summary>Stop the process and its children. Safe to call after exit.</summary>
        void KillTree();
    }
}
