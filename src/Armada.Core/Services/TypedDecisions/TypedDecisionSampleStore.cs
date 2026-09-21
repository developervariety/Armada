namespace Armada.Core.Services.TypedDecisions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Retains the REDACTED state of typed-decision calls on this host, as the training and
    /// evaluation set for a local classifier (owner ruling 2026-09-17). Three properties decide the
    /// design: retention is opt-in per decision, the retained text never leaves the host, and it
    /// never enters an event payload — an event still carries only the state's hash and byte count.
    ///
    /// Samples are JSON lines under <c>&lt;data directory&gt;/typed-decision-samples/&lt;decision&gt;/&lt;date&gt;.jsonl</c>.
    /// A file store rather than a table because the state is large, the set is read in bulk by a
    /// trainer, and pruning a retention window is a file delete. Every write is best-effort: a
    /// retention failure must never change a decision's outcome.
    /// </summary>
    public sealed class TypedDecisionSampleStore
    {
        #region Public-Members

        /// <summary>Folder under the data directory that holds every retained sample.</summary>
        public const string FolderName = "typed-decision-samples";

        /// <summary>Sample kind for a recorded decision call.</summary>
        public const string KindDecision = "decision";

        /// <summary>Sample kind for an operator reversal of a gated outcome.</summary>
        public const string KindReversal = "reversal";

        /// <summary>The absolute root folder of the store.</summary>
        public string RootPath { get; }

        #endregion

        #region Private-Members

        private const string _Header = "[TypedDecisionSampleStore] ";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        // Plain UTF-8: a byte-order mark on a file's first line makes a strict JSON-lines reader
        // reject the first sample of every day.
        private static readonly UTF8Encoding _Utf8NoBom = new UTF8Encoding(false);
        private readonly LoggingModule? _Logging;
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a sample store rooted in a data directory.</summary>
        /// <param name="dataDirectory">The Admiral data directory.</param>
        /// <param name="logging">Optional logging module.</param>
        public TypedDecisionSampleStore(string dataDirectory, LoggingModule? logging = null)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException(nameof(dataDirectory));
            RootPath = RootPathFor(dataDirectory);
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>The store's root folder for a data directory.</summary>
        /// <param name="dataDirectory">The Admiral data directory.</param>
        /// <returns>The absolute root folder path.</returns>
        public static string RootPathFor(string dataDirectory)
        {
            return Path.GetFullPath(Path.Combine(dataDirectory, FolderName));
        }

        /// <summary>
        /// Whether this decision's state is retained under the current settings. Retention needs the
        /// feature enabled AND the decision's own opt-in, so enabling the feature alone retains nothing.
        /// A custom decision (<c>custom:&lt;name&gt;</c>) opts in with its own definition's
        /// <c>retainState</c>. A decision point with no settings entry (the general captain tool) has no
        /// opt-in, so it is never retained.
        /// </summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="decisionPoint">The decision point key.</param>
        /// <returns>True when a sample should be written.</returns>
        public static bool Retains(TypedDecisionSettings? settings, string? decisionPoint)
        {
            if (settings == null || String.IsNullOrWhiteSpace(decisionPoint)) return false;
            if (!settings.Retention.Enabled) return false;
            return OptsIn(settings, decisionPoint!);
        }

        /// <summary>
        /// Every decision point whose own opt-in is set, built-in and custom, sorted. The feature switch
        /// is not consulted, so the report can name what would be retained once it is on. This is the
        /// same rule <see cref="Retains"/> applies, read the same way.
        /// </summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <returns>The opted-in decision points.</returns>
        public static List<string> OptedInDecisionPoints(TypedDecisionSettings? settings)
        {
            List<string> optedIn = new List<string>();
            if (settings == null) return optedIn;
            foreach (string decisionPoint in settings.Decisions.Keys)
                if (OptsIn(settings, decisionPoint)) optedIn.Add(decisionPoint);
            foreach (string name in settings.Custom.Keys)
            {
                string decisionPoint = CustomTypedDecisionAdapter.DecisionPointFor(name);
                if (OptsIn(settings, decisionPoint)) optedIn.Add(decisionPoint);
            }
            optedIn.Sort(StringComparer.Ordinal);
            return optedIn;
        }

        /// <summary>
        /// Append one decision sample. The caller passes the same redacted text the event hashes, so a
        /// sample and its event describe the same call. Never throws.
        /// </summary>
        /// <param name="sample">The sample to retain.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a line was written.</returns>
        public Task<bool> AppendAsync(TypedDecisionSample sample, CancellationToken token = default)
        {
            if (sample == null || String.IsNullOrWhiteSpace(sample.DecisionPoint)) return Task.FromResult(false);
            return Task.FromResult(Write(sample));
        }

        /// <summary>
        /// Delete sample files older than the retention window. Returns the number of files deleted.
        /// A window of zero or less deletes nothing. Never throws.
        /// </summary>
        /// <param name="retentionDays">Days of samples to keep.</param>
        /// <param name="utcNow">The current UTC time; the default is now.</param>
        /// <returns>The number of files deleted.</returns>
        public int Prune(int retentionDays, DateTime? utcNow = null)
        {
            if (retentionDays <= 0) return 0;
            DateTime cutoff = (utcNow ?? DateTime.UtcNow).Date.AddDays(-retentionDays);
            int deleted = 0;
            try
            {
                if (!Directory.Exists(RootPath)) return 0;
                foreach (string file in Directory.EnumerateFiles(RootPath, "*.jsonl", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day))
                        continue;
                    if (day.Date >= cutoff) continue;
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "prune failed: " + ex.Message);
            }
            return deleted;
        }

        /// <summary>
        /// Count retained calls per decision and redactor version. The report names decisions below
        /// the sample minimum but does not certify labels, question cohorts, or a held-out set.
        /// Raw teacher sample counts cannot establish training readiness.
        /// </summary>
        /// <param name="minimumSamples">The configured sample-count minimum, not proof of trainability.</param>
        /// <returns>One entry per decision with samples, ordered by decision point.</returns>
        public List<TypedDecisionSampleCount> Summarize(int minimumSamples)
        {
            List<TypedDecisionSampleCount> counts = new List<TypedDecisionSampleCount>();
            try
            {
                if (!Directory.Exists(RootPath)) return counts;
                foreach (string folder in Directory.EnumerateDirectories(RootPath).OrderBy(x => x, StringComparer.Ordinal))
                {
                    TypedDecisionSampleCount count = new TypedDecisionSampleCount
                    {
                        DecisionPoint = Path.GetFileName(folder),
                        CurrentRedactorVersion = DecisionStateRedactor.Version
                    };
                    foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl"))
                    {
                        foreach (string line in File.ReadLines(file))
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            if (line.Contains("\"kind\":\"" + KindReversal + "\"", StringComparison.Ordinal))
                            {
                                count.Reversals++;
                                continue;
                            }
                            count.Samples++;
                            int version = ReadRedactorVersion(line);
                            count.SamplesByRedactorVersion[version] = count.SamplesByRedactorVersion.TryGetValue(version, out int seen) ? seen + 1 : 1;
                            if (version == DecisionStateRedactor.Version) count.CurrentSamples++;
                        }
                    }
                    count.MinimumSamples = minimumSamples;
                    // Only samples redacted under the running rules train together; an older cohort is
                    // reported beside them, never counted towards the minimum.
                    count.MinimumSampleCountMet = count.CurrentSamples >= minimumSamples;
                    // Retained teacher answers and a raw sample count are not independently reviewed
                    // labels or a frozen held-out set. This store cannot certify training readiness.
                    count.Trainable = false;
                    int older = count.Samples - count.CurrentSamples;
                    count.NotTrainableReason = count.MinimumSampleCountMet
                        ? "sample-count minimum met; independent labels, question/model cohorts, and a frozen held-out set have not been verified"
                        : "only " + count.CurrentSamples + " of " + minimumSamples + " samples retained under redactor version "
                          + DecisionStateRedactor.Version
                          + (older > 0 ? " (" + older + " more under older or unknown redaction rules do not count)" : String.Empty);
                    counts.Add(count);
                }
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "summarize failed: " + ex.Message);
            }
            return counts;
        }

        #endregion

        #region Private-Methods

        private static readonly System.Text.RegularExpressions.Regex _RedactorVersionField =
            new System.Text.RegularExpressions.Regex("\"redactor_version\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The redactor version a sample line was stamped with, or zero when it carries none.</summary>
        private static int ReadRedactorVersion(string line)
        {
            System.Text.RegularExpressions.Match match = _RedactorVersionField.Match(line);
            return match.Success && Int32.TryParse(match.Groups[1].Value, out int version) ? version : 0;
        }

        private static bool OptsIn(TypedDecisionSettings settings, string decisionPoint)
        {
            if (decisionPoint.StartsWith(CustomTypedDecisionAdapter.DecisionPointPrefix, StringComparison.Ordinal))
            {
                string name = decisionPoint.Substring(CustomTypedDecisionAdapter.DecisionPointPrefix.Length);
                return settings.Custom.TryGetValue(name, out CustomTypedDecisionSettings? custom)
                    && custom != null
                    && custom.RetainState;
            }
            return settings.Decisions.TryGetValue(decisionPoint, out TypedDecisionRuleSettings? rule)
                && rule != null
                && rule.RetainState;
        }

        private bool Write(TypedDecisionSample sample)
        {
            try
            {
                string folder = Path.Combine(RootPath, Sanitize(sample.DecisionPoint));
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                string line = JsonSerializer.Serialize(sample, _JsonOptions);
                lock (_Lock)
                {
                    File.AppendAllText(file, line + Environment.NewLine, _Utf8NoBom);
                }
                return true;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "append failed for " + sample.DecisionPoint + ": " + ex.Message);
                return false;
            }
        }

        private static string Sanitize(string decisionPoint)
        {
            StringBuilder sb = new StringBuilder(decisionPoint.Length);
            foreach (char c in decisionPoint)
            {
                sb.Append(Char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        #endregion
    }

    /// <summary>
    /// One retained line: either a decision call or an operator reversal of one. It carries the
    /// redacted state, never the original, and never a credential.
    /// </summary>
    public sealed class TypedDecisionSample
    {
        /// <summary>When the line was written.</summary>
        public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Sample kind: a decision call or a reversal.</summary>
        public string Kind { get; set; } = TypedDecisionSampleStore.KindDecision;

        /// <summary>The decision point key.</summary>
        public string DecisionPoint { get; set; } = String.Empty;

        /// <summary>The event this sample belongs to, when one was recorded.</summary>
        public string? EventId { get; set; }

        /// <summary>The redacted state exactly as transmitted. Empty on a reversal.</summary>
        public string RedactedState { get; set; } = String.Empty;

        /// <summary>SHA-256 of the redacted state, so a reversal can be joined to its call.</summary>
        public string StateSha256 { get; set; } = String.Empty;

        /// <summary>Redacted request evidence or a capture-failure diagnostic. Null when none was captured;
        /// response-validation failures can still carry evidence.</summary>
        public TypedDecisionProvenance? Provenance { get; set; }

        /// <summary>The concrete model returned by the provider, not the configured alias.</summary>
        public string? Model { get; set; }

        /// <summary>All typed answers and distributions; model readings are not verified labels.</summary>
        public IReadOnlyDictionary<string, TypedAnswer>? Answers { get; set; }

        /// <summary>How many items shared the request; one for an unwrapped call.</summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>What the deterministic rule decided.</summary>
        public string? RuleVerdict { get; set; }

        /// <summary>What the model answered.</summary>
        public string? ModelVerdict { get; set; }

        /// <summary>The model's confidence in that answer.</summary>
        public double? Confidence { get; set; }

        /// <summary>The gate outcome the recorder wrote.</summary>
        public string? GateOutcome { get; set; }

        /// <summary>On a reversal, the answer the operator says was correct.</summary>
        public string? CorrectedVerdict { get; set; }

        /// <summary>On a reversal, why the gated outcome was wrong.</summary>
        public string? Reason { get; set; }

        /// <summary>The mission the call belonged to, when any.</summary>
        public string? MissionId { get; set; }

        /// <summary>
        /// The <see cref="DecisionStateRedactor.Version"/> that produced <see cref="RedactedState"/>.
        /// Zero on a line written before samples were stamped: its cohort is unknown until an
        /// operator backfills it.
        /// </summary>
        public int RedactorVersion { get; set; }
    }

    /// <summary>What one decision has retained, and whether that is enough to train on.</summary>
    public sealed class TypedDecisionSampleCount
    {
        /// <summary>The decision point key.</summary>
        public string DecisionPoint { get; set; } = String.Empty;

        /// <summary>Retained decision calls.</summary>
        public int Samples { get; set; }

        /// <summary>Operator reversals recorded against this decision.</summary>
        public int Reversals { get; set; }

        /// <summary>The minimum samples configured for trainability.</summary>
        public int MinimumSamples { get; set; }

        /// <summary>Whether the raw current-redactor sample count reaches the configured minimum.</summary>
        public bool MinimumSampleCountMet { get; set; }

        /// <summary>Whether training readiness is proved; this count-only store cannot prove it.</summary>
        public bool Trainable { get; set; }

        /// <summary>Why the decision is not trainable, when it is not.</summary>
        public string? NotTrainableReason { get; set; }

        /// <summary>The redactor version the running admiral applies.</summary>
        public int CurrentRedactorVersion { get; set; }

        /// <summary>Samples redacted under the running rules; only these count towards the minimum.</summary>
        public int CurrentSamples { get; set; }

        /// <summary>Samples per redactor version; version 0 means a line that was never stamped.</summary>
        public Dictionary<int, int> SamplesByRedactorVersion { get; set; } = new Dictionary<int, int>();
    }
}
