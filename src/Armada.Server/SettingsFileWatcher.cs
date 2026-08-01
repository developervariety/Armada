namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Settings;

    /// <summary>
    /// Watches settings.json and applies runtime-tunable edits to the live settings
    /// instance without a restart. Settings are otherwise read only once, at startup,
    /// so an operator editing the file by hand would see no effect until the process
    /// was restarted.
    ///
    /// Events are debounced because a single logical edit commonly arrives as several
    /// filesystem events (an in-place write, or the write-temp-then-rename that editors
    /// and sed perform). Content is hashed so a write that does not change the file is
    /// ignored, which also keeps an API-initiated save from producing a redundant apply.
    ///
    /// Only <see cref="ArmadaSettings.ApplyHotReloadableFrom"/> values are applied.
    /// Ports, paths, database settings, API keys, agent definitions and remote-control
    /// settings are bound at startup and still require a restart.
    /// </summary>
    public sealed class SettingsFileWatcher : IDisposable
    {
        #region Private-Members

        private const int _DebounceMilliseconds = 750;
        private const int _ReadRetries = 3;
        private const int _ReadRetryDelayMilliseconds = 120;

        private readonly string _Header = "[SettingsFileWatcher] ";
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly string _Path;
        private readonly object _Lock = new object();

        private FileSystemWatcher? _Watcher;
        private Timer? _Debounce;
        private string? _LastAppliedHash;
        private bool _Disposed;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Live settings instance to update in place.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="path">Settings file path. Defaults to the standard settings.json location.</param>
        public SettingsFileWatcher(ArmadaSettings settings, LoggingModule logging, string? path = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Path = String.IsNullOrWhiteSpace(path) ? ArmadaSettings.DefaultSettingsPath : path!;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Begin watching. Failure to start is logged and swallowed: settings hot-reload
        /// is an operator convenience and must never prevent the server from running.
        /// </summary>
        public void Start()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_Path);
                string fileName = Path.GetFileName(_Path);
                if (String.IsNullOrEmpty(directory) || String.IsNullOrEmpty(fileName))
                {
                    _Logging.Warn(_Header + "cannot watch settings path " + _Path + "; hot-reload disabled");
                    return;
                }

                // Seed the hash from the file the server just loaded, so the first
                // event after startup only applies when the content actually changed.
                _LastAppliedHash = TryReadHash();

                _Debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

                _Watcher = new FileSystemWatcher(directory, fileName);
                _Watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                _Watcher.Changed += OnFileEvent;
                _Watcher.Created += OnFileEvent;
                _Watcher.Renamed += OnFileEvent;
                _Watcher.EnableRaisingEvents = true;

                _Logging.Info(_Header + "watching " + _Path + " for settings changes");
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "unable to start settings watcher; hot-reload disabled: " + e.Message);
            }
        }

        /// <summary>
        /// Stop watching and release resources.
        /// </summary>
        public void Dispose()
        {
            lock (_Lock)
            {
                if (_Disposed) return;
                _Disposed = true;
            }

            try
            {
                if (_Watcher != null)
                {
                    _Watcher.EnableRaisingEvents = false;
                    _Watcher.Changed -= OnFileEvent;
                    _Watcher.Created -= OnFileEvent;
                    _Watcher.Renamed -= OnFileEvent;
                    _Watcher.Dispose();
                    _Watcher = null;
                }
            }
            catch (Exception e)
            {
                _Logging.Debug(_Header + "error disposing watcher: " + e.Message);
            }

            try
            {
                _Debounce?.Dispose();
                _Debounce = null;
            }
            catch (Exception e)
            {
                _Logging.Debug(_Header + "error disposing debounce timer: " + e.Message);
            }
        }

        #endregion

        #region Private-Methods

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            lock (_Lock)
            {
                if (_Disposed) return;
                // Restart the debounce window; a burst of events collapses into one apply.
                try { _Debounce?.Change(_DebounceMilliseconds, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private void OnDebounceElapsed(object? state)
        {
            _ = ApplyAsync();
        }

        private async Task ApplyAsync()
        {
            try
            {
                lock (_Lock)
                {
                    if (_Disposed) return;
                }

                if (!File.Exists(_Path))
                {
                    _Logging.Warn(_Header + "settings file " + _Path + " is missing; keeping current settings");
                    return;
                }

                string? hash = TryReadHash();
                if (hash == null)
                {
                    _Logging.Warn(_Header + "could not read " + _Path + " after retries; keeping current settings");
                    return;
                }

                if (String.Equals(hash, _LastAppliedHash, StringComparison.Ordinal))
                    return;

                ArmadaSettings loaded;
                try
                {
                    loaded = await ArmadaSettings.LoadAsync(_Path).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // A half-written or invalid file must never take the server down or
                    // clobber good in-memory settings. Leave the hash unset so a
                    // subsequent corrected write is retried.
                    _Logging.Warn(_Header + "settings file is not valid JSON; keeping current settings: " + e.Message);
                    return;
                }

                _Settings.ApplyHotReloadableFrom(loaded);
                _LastAppliedHash = hash;

                _Logging.Info(_Header + "settings reloaded from file: maxConcurrentCaptainWorkloads="
                    + _Settings.MaxConcurrentCaptainWorkloads
                    + " maxConcurrentBuilds=" + _Settings.ResourcePressureAdmission.MaxConcurrentBuilds
                    + " minAvailableMemoryMb=" + _Settings.ResourcePressureAdmission.MinAvailableMemoryMb
                    + " reservedHighTierSlots=" + _Settings.ModelTier.ReservedHighTierSlots);
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "settings reload failed; keeping current settings: " + e.Message);
            }
        }

        private string? TryReadHash()
        {
            for (int attempt = 0; attempt < _ReadRetries; attempt++)
            {
                try
                {
                    // Share-all so a concurrent writer does not produce a spurious failure.
                    using (FileStream stream = new FileStream(_Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] digest = sha.ComputeHash(stream);
                        return Convert.ToHexString(digest);
                    }
                }
                catch (Exception)
                {
                    if (attempt == _ReadRetries - 1) return null;
                    Thread.Sleep(_ReadRetryDelayMilliseconds);
                }
            }
            return null;
        }

        #endregion
    }
}
