namespace Armada.Core.Services
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>A real login process. Output is handed to the reader only; nothing here logs it.</summary>
    internal sealed class SystemAccountLoginProcess : IAccountLoginProcess
    {
        #region Public-Members

        /// <inheritdoc />
        public int? ExitCode
        {
            get
            {
                try { return _Process.HasExited ? _Process.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        #endregion

        #region Private-Members

        private readonly Process _Process;
        private readonly Channel<string> _Output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _InputLock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Wrap a started process and begin draining its output.</summary>
        internal SystemAccountLoginProcess(Process process)
        {
            _Process = process ?? throw new ArgumentNullException(nameof(process));
            Task stdout = PumpAsync(_Process.StandardOutput);
            Task stderr = PumpAsync(_Process.StandardError);
            _ = Task.WhenAll(stdout, stderr).ContinueWith(_ => _Output.Writer.TryComplete(), TaskScheduler.Default);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string?> ReadOutputAsync(CancellationToken token = default)
        {
            while (await _Output.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                if (_Output.Reader.TryRead(out string? chunk)) return chunk;
            return null;
        }

        /// <inheritdoc />
        public async Task WriteInputLineAsync(string line, CancellationToken token = default)
        {
            await _InputLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _Process.StandardInput.WriteAsync(line.AsMemory(), token).ConfigureAwait(false);
                await _Process.StandardInput.WriteAsync("\n".AsMemory(), token).ConfigureAwait(false);
                await _Process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
            finally { _InputLock.Release(); }
        }

        /// <inheritdoc />
        public Task WaitForExitAsync(CancellationToken token = default)
        {
            return _Process.WaitForExitAsync(token);
        }

        /// <inheritdoc />
        public void KillTree()
        {
            try { if (!_Process.HasExited) _Process.Kill(true); }
            catch (InvalidOperationException) { /* The process exited between the check and the kill. */ }
            catch (Win32Exception) { /* The process tree is already gone. */ }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            KillTree();
            _Process.Dispose();
            _InputLock.Dispose();
        }

        #endregion

        #region Private-Methods

        private async Task PumpAsync(StreamReader reader)
        {
            char[] buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    _Output.Writer.TryWrite(new string(buffer, 0, read));
            }
            catch (IOException) { /* The pipe broke when the process was stopped; output has ended. */ }
            catch (ObjectDisposedException) { /* The process was disposed; output has ended. */ }
        }

        #endregion
    }
}
