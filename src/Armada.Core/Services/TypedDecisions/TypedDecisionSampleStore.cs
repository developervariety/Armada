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
        /// </summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="decisionPoint">The decision point key.</param>
        /// <returns>True when a sample should be written.</returns>
        public static bool Retains(TypedDecisionSettings? settings, string? decisionPoint)
        {
            if (settings == null || String.IsNullOrWhiteSpace(decisionPoint)) return false;
            if (!settings.Retention.Enabled) return false;
            if (!settings.Decisions.TryGetValue(decisionPoint, out TypedDecisionRuleSettings? rule) || rule == null) return false;
            return rule.RetainState;
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
        /// Count what has been retained, per decision. The report names every decision that has
        /// samples and says whether it has enough labelled examples to train on, so a decision short
        /// of the minimum is reported as not trainable rather than silently skipped.
        /// </summary>
        /// <param name="minimumSamples">The minimum samples a decision needs to be trainable.</param>
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
                        DecisionPoint = Path.GetFileName(folder)
                    };
                    foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl"))
                    {
                        foreach (string line in File.ReadLines(file))
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            if (line.Contains("\"kind\":\"" + KindReversal + "\"", StringComparison.Ordinal)) count.Reversals++;
                            else count.Samples++;
                        }
                    }
                    count.MinimumSamples = minimumSamples;
                    count.Trainable = count.Samples >= minimumSamples;
                    count.NotTrainableReason = count.Trainable
                        ? null
                        : "only " + count.Samples + " of " + minimumSamples + " samples retained";
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
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
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

        /// <summary>Whether the decision has at least the minimum samples.</summary>
        public bool Trainable { get; set; }

        /// <summary>Why the decision is not trainable, when it is not.</summary>
        public string? NotTrainableReason { get; set; }
    }
}
