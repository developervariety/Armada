namespace Armada.Core.Context
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;

    /// <summary>
    /// Pins a voyage's staged AI-Memory to the first slimmed brief that staged it. Every later stage of
    /// the voyage receives the same files and section, so a reviewer reads the same bytes the Worker read
    /// and can check them against the manifest. Without a pin each stage retrieves afresh from its own
    /// title and description, and from an index that may have been rebuilt since, so the files differ
    /// from stage to stage.
    ///
    /// Pins are JSON files under one directory, keyed by voyage id, so they survive an admiral restart.
    /// Only a healthy slimmed delivery is pinned; a fallback to the full section is not, so a later stage
    /// retries retrieval. Pins older than <see cref="RetentionDays"/> are pruned when a new pin is saved.
    /// A store failure never fails a dispatch: it is logged and the brief is built without the pin.
    /// </summary>
    public sealed class MemoryVoyagePinStore
    {
        #region Public-Members

        /// <summary>Days a pin is kept after it was last written.</summary>
        public const int RetentionDays = 30;

        /// <summary>The directory pins are written to.</summary>
        public string Directory => _Directory;

        #endregion

        #region Private-Members

        private static readonly Regex _SafeId = new Regex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private readonly string _Header = "[MemoryVoyagePinStore] ";
        private readonly string _Directory;
        private readonly LoggingModule? _Logging;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _Locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a store. Nothing is written until the first pin is saved.
        /// </summary>
        /// <param name="directory">Directory pins are written to.</param>
        /// <param name="logging">Optional logging for store failures.</param>
        public MemoryVoyagePinStore(string directory, LoggingModule? logging = null)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentNullException(nameof(directory));
            _Directory = directory;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the voyage's pinned memory, or build it and pin it when the voyage has none yet. Briefs
        /// for the same voyage resolve one at a time, so two stages dispatched together still share one pin.
        /// </summary>
        /// <param name="voyageId">The mission's voyage, or null for a mission outside a voyage.</param>
        /// <param name="missionId">The mission being briefed, recorded on a new pin.</param>
        /// <param name="build">Builds a fresh delivery.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The pinned delivery, or the freshly built one.</returns>
        public async Task<ContextBriefDelivery> ResolveAsync(
            string? voyageId,
            string? missionId,
            Func<Task<ContextBriefDelivery>> build,
            CancellationToken token = default)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (String.IsNullOrWhiteSpace(voyageId) || !_SafeId.IsMatch(voyageId))
                return await build().ConfigureAwait(false);

            SemaphoreSlim gate = _Locks.GetOrAdd(voyageId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                MemoryVoyagePin? pin = Load(voyageId);
                if (pin != null) return ToDelivery(pin);

                ContextBriefDelivery built = await build().ConfigureAwait(false);
                if (!IsPinnable(built)) return built;

                MemoryVoyagePin created = new MemoryVoyagePin
                {
                    VoyageId = voyageId,
                    MissionId = missionId,
                    PinnedUtc = DateTime.UtcNow,
                    Section = built.Section,
                    Files = built.Files,
                    Telemetry = built.Telemetry
                };
                if (Save(created))
                {
                    built.PinnedVoyageId = created.VoyageId;
                    built.PinnedByMissionId = created.MissionId;
                    built.PinnedUtc = created.PinnedUtc;
                }
                return built;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Read a voyage's pin.
        /// </summary>
        /// <param name="voyageId">Voyage id.</param>
        /// <returns>The pin, or null when the voyage has none or it cannot be read.</returns>
        public MemoryVoyagePin? Load(string voyageId)
        {
            if (String.IsNullOrWhiteSpace(voyageId) || !_SafeId.IsMatch(voyageId)) return null;
            string path = PathFor(voyageId);
            try
            {
                if (!File.Exists(path)) return null;
                MemoryVoyagePin? pin = JsonSerializer.Deserialize<MemoryVoyagePin>(File.ReadAllText(path, Encoding.UTF8), _JsonOptions);
                if (pin == null || pin.Files == null || pin.Files.Count == 0) return null;
                return pin;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "could not read the memory pin for " + voyageId + "; staging afresh: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Private-Methods

        private static bool IsPinnable(ContextBriefDelivery delivery)
        {
            return delivery != null
                && delivery.Files != null
                && delivery.Files.Count > 0
                && delivery.Telemetry != null
                && delivery.Telemetry.Enabled
                && !delivery.Telemetry.FellBack;
        }

        private static ContextBriefDelivery ToDelivery(MemoryVoyagePin pin)
        {
            ContextBriefTelemetry telemetry = pin.Telemetry ?? new ContextBriefTelemetry { Enabled = true };
            telemetry.PinnedFromVoyage = true;
            return new ContextBriefDelivery
            {
                Section = pin.Section,
                Files = pin.Files,
                Telemetry = telemetry,
                PinnedVoyageId = pin.VoyageId,
                PinnedByMissionId = pin.MissionId,
                PinnedUtc = pin.PinnedUtc
            };
        }

        private bool Save(MemoryVoyagePin pin)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_Directory);
                string path = PathFor(pin.VoyageId);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(pin, _JsonOptions), new UTF8Encoding(false));
                File.Move(temp, path, true);
                Prune();
                return true;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "could not pin memory for " + pin.VoyageId + "; later stages stage afresh: " + ex.Message);
                return false;
            }
        }

        private void Prune()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string file in System.IO.Directory.EnumerateFiles(_Directory, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex)
                {
                    _Logging?.Warn(_Header + "could not prune an expired memory pin: " + ex.Message);
                }
            }
        }

        private string PathFor(string voyageId)
        {
            return Path.Combine(_Directory, voyageId + ".json");
        }

        #endregion
    }
}
