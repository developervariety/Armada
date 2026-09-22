namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Watches settings.json and applies runtime-tunable edits to the live settings
    /// instance without a restart. Settings are otherwise read only once, at startup,
    /// so an operator editing the file by hand would see no effect until the process
    /// was restarted.
    ///
    /// Events are debounced because a single logical edit commonly arrives as several
    /// filesystem events (an in-place write, or the write-temp-then-rename that editors
    /// and sed perform). Content equal to the content last applied, by this watcher or by
    /// the manual reload endpoint, is skipped.
    ///
    /// The watched file is the one <see cref="SettingsReloadService"/> is bound to, and every
    /// apply goes through that service, so a watched reload reads, validates and applies
    /// exactly as the manual reload endpoint does. A missing or invalid candidate is refused
    /// and the current settings are kept.
    ///
    /// Only <see cref="ArmadaSettings.ApplyHotReloadableFrom"/> values are applied.
    /// Ports, paths, database settings, API keys, agent definitions and remote-control
    /// settings are bound at startup and still require a restart.
    /// </summary>
    public sealed class SettingsFileWatcher : IDisposable
    {
        #region Private-Members

        private const int _DebounceMilliseconds = 750;

        private readonly string _Header = "[SettingsFileWatcher] ";
        private readonly SettingsReloadService _Reload;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly string _Path;
        private readonly object _Lock = new object();

        private FileSystemWatcher? _Watcher;
        private Timer? _Debounce;
        private bool _Disposed;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Live settings instance the reload service updates in place.</param>
        /// <param name="reload">Reload service bound to the live settings file; the watcher watches that file.</param>
        /// <param name="logging">Logging module.</param>
        public SettingsFileWatcher(ArmadaSettings settings, SettingsReloadService reload, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Reload = reload ?? throw new ArgumentNullException(nameof(reload));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Path = reload.SettingsFilePath;
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

                // Record the file the server just loaded, so the first event after
                // startup only applies when the content actually changed.
                _Reload.RecordCurrentFileAsAppliedAsync().GetAwaiter().GetResult();

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

                SettingsReloadResult result = await _Reload.ReloadAsync(skipIfUnchanged: true).ConfigureAwait(false);
                if (result.Outcome == SettingsReloadOutcomeEnum.Unchanged)
                    return;

                if (!result.Applied)
                {
                    // A half-written or invalid file must never take the server down or clobber
                    // good in-memory settings. A subsequent corrected write is applied.
                    _Logging.Warn(_Header + "settings file refused (" + result.Outcome + "); keeping current settings: " + result.Reason);
                    return;
                }

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

        #endregion
    }
}
