namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Reloads live settings from the settings file they are bound to (<see cref="ArmadaSettings.SettingsFilePath"/>).
    /// The manual reload endpoint and the settings-file watcher both call this service, so both read the same file
    /// and accept the same candidates. A missing file, an unreadable file, or a candidate that fails
    /// <see cref="SettingsCandidateValidator"/> is refused and the current settings are kept; only a valid candidate
    /// reaches <see cref="ArmadaSettings.ApplyHotReloadableFrom"/>, which copies hot-reloaded sections in place.
    /// Reloads are serialized, and the service remembers the content it last applied by either path, so a watched
    /// reload can skip content that is already live.
    /// </summary>
    public sealed class SettingsReloadService
    {
        #region Public-Members

        /// <summary>Settings file this service reads: the file the live settings are bound to.</summary>
        public string SettingsFilePath => _Settings.SettingsFilePath;

        #endregion

        #region Private-Members

        private const int _ReadRetries = 3;
        private const int _ReadRetryDelayMilliseconds = 120;

        private readonly ArmadaSettings _Settings;
        private readonly Func<CancellationToken, Task<List<Captain>>> _LoadCaptains;
        private readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);
        private string? _LastAppliedHash;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Live settings instance to update in place.</param>
        /// <param name="loadCaptains">Reads the current captain roster for account-binding validation.</param>
        public SettingsReloadService(ArmadaSettings settings, Func<CancellationToken, Task<List<Captain>>> loadCaptains)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _LoadCaptains = loadCaptains ?? throw new ArgumentNullException(nameof(loadCaptains));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record the bound file's current content as the content the live settings were loaded from, so a later
        /// watched reload of unchanged content is skipped. Call once after the live settings are loaded.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task RecordCurrentFileAsAppliedAsync(CancellationToken token = default)
        {
            await _Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string? content = File.Exists(SettingsFilePath) ? await TryReadAsync(SettingsFilePath, token).ConfigureAwait(false) : null;
                if (content != null) _LastAppliedHash = Hash(content);
            }
            finally
            {
                _Gate.Release();
            }
        }

        /// <summary>
        /// Read the bound settings file, validate it as a candidate, and apply its runtime-tunable values in place.
        /// </summary>
        /// <param name="skipIfUnchanged">When true, content identical to the content last applied is not applied
        /// again and the outcome is <see cref="SettingsReloadOutcomeEnum.Unchanged"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What the reload did and, when refused, why.</returns>
        public async Task<SettingsReloadResult> ReloadAsync(bool skipIfUnchanged = false, CancellationToken token = default)
        {
            string path = SettingsFilePath;
            SettingsReloadResult result = new SettingsReloadResult { SettingsFilePath = path };

            await _Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Loading an absent file yields defaults; applying them would silently reset every tunable value.
                if (!File.Exists(path))
                {
                    result.Outcome = SettingsReloadOutcomeEnum.FileMissing;
                    result.Reason = "Settings file " + path + " does not exist; current settings kept.";
                    return result;
                }

                string? content = await TryReadAsync(path, token).ConfigureAwait(false);
                if (content == null)
                {
                    result.Outcome = SettingsReloadOutcomeEnum.Invalid;
                    result.Reason = "Settings file " + path + " could not be read; current settings kept.";
                    return result;
                }

                string hash = Hash(content);
                if (skipIfUnchanged && String.Equals(hash, _LastAppliedHash, StringComparison.Ordinal))
                {
                    result.Outcome = SettingsReloadOutcomeEnum.Unchanged;
                    return result;
                }

                ArmadaSettings candidate;
                try
                {
                    candidate = ArmadaSettings.FromJson(content, path);
                }
                catch (Exception e)
                {
                    result.Outcome = SettingsReloadOutcomeEnum.Invalid;
                    result.Reason = "Settings file is not valid settings JSON: " + e.Message;
                    return result;
                }

                try
                {
                    List<Captain> captains = await _LoadCaptains(token).ConfigureAwait(false);
                    SettingsCandidateValidator.Validate(candidate, _Settings.DataDirectory, captains);
                }
                catch (ArgumentException e)
                {
                    result.Outcome = SettingsReloadOutcomeEnum.Invalid;
                    result.Reason = e.Message;
                    return result;
                }

                _Settings.ApplyHotReloadableFrom(candidate);
                _LastAppliedHash = hash;
                result.Outcome = SettingsReloadOutcomeEnum.Applied;
                return result;
            }
            finally
            {
                _Gate.Release();
            }
        }

        #endregion

        #region Private-Methods

        private static async Task<string?> TryReadAsync(string path, CancellationToken token)
        {
            for (int attempt = 0; attempt < _ReadRetries; attempt++)
            {
                try
                {
                    // Share-all so a concurrent writer does not produce a spurious failure.
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    if (attempt == _ReadRetries - 1) return null;
                    await Task.Delay(_ReadRetryDelayMilliseconds, token).ConfigureAwait(false);
                }
            }
            return null;
        }

        private static string Hash(string content)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        }

        #endregion
    }
}
