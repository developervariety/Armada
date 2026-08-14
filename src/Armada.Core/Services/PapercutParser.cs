namespace Armada.Core.Services
{
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Parses `[ARMADA:PAPERCUT]` marker lines emitted by captains.
    ///
    /// The preferred form is one line of JSON:
    ///   [ARMADA:PAPERCUT] {"category":"MissingDoc","severity":"Low","title":"...","detail":"...","path":"..."}
    ///
    /// A pipe-delimited form and bare text are also accepted. Captains run on six runtimes and several
    /// model families; a report that is slightly off-format is still a report, and rejecting it teaches
    /// the captain that reporting friction does not work.
    /// </summary>
    public static class PapercutParser
    {
        #region Public-Members

        /// <summary>
        /// Marker type emitted in the signal line, lower-cased as ProgressParser reports it.
        /// </summary>
        public const string SignalType = "papercut";

        /// <summary>
        /// Event type used to store a papercut.
        /// </summary>
        public const string EventType = "papercut";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Credentials must never reach a stored record, and a captain quoting the command that failed
        // is exactly how one would. Cheap prefix matching only: this is a backstop for the brief rule,
        // not a secret scanner.
        private static readonly Regex _SecretPattern = new Regex(
            @"(sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|AKIA[0-9A-Z]{12,}|xox[abprs]-[A-Za-z0-9\-]{10,}|-----BEGIN[^-]*PRIVATE KEY-----|(?i:password|passwd|secret|token|api[_\-]?key)\s*[=:]\s*\S+)",
            RegexOptions.Compiled);

        private static readonly Regex _ControlPattern = new Regex(@"[\p{Cc}\p{Cf}]", RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Try to parse a whole output line into a papercut. Returns null when the line is not a
        /// papercut marker, or carries no usable title.
        /// </summary>
        /// <param name="line">Agent output line.</param>
        /// <returns>Parsed papercut or null.</returns>
        public static Papercut? TryParseLine(string? line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;

            ProgressParser.ProgressSignal? signal = ProgressParser.TryParse(line!);
            if (signal == null) return null;
            if (!String.Equals(signal.Type, SignalType, StringComparison.OrdinalIgnoreCase)) return null;

            return TryParseValue(signal.Value);
        }

        /// <summary>
        /// Try to parse the value that follows the marker.
        /// </summary>
        /// <param name="value">Marker value.</param>
        /// <returns>Parsed papercut or null.</returns>
        public static Papercut? TryParseValue(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;

            string trimmed = value!.Trim();

            PapercutPayload? payload;
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                // A line that opens as JSON and does not parse is truncated or corrupt. Its text is not
                // a usable title, so drop it rather than store the fragment.
                payload = TryDeserialize(trimmed);
                if (payload == null) return null;
            }
            else
            {
                payload = ParseDelimited(trimmed);
            }

            // The brief's instruction template shows a literal example report with the fields
            // "one line" / "optional" / "optional/file.cs". A scan that walks the log
            // instructions directory counts that example as a report; a captain may also echo it
            // verbatim while testing the format. Either way it is not friction anyone met, so it
            // must never reach the store or a grouping.
            if (IsTemplatePlaceholder(payload)) return null;

            string? title = Sanitize(payload.Title);
            if (String.IsNullOrWhiteSpace(title)) return null;

            Papercut papercut = new Papercut();
            papercut.Category = ParseCategory(payload.Category);
            papercut.Severity = ParseSeverity(payload.Severity);
            papercut.Title = title;
            papercut.Detail = Sanitize(payload.Detail);
            papercut.Path = Sanitize(payload.Path);
            return papercut;
        }

        /// <summary>
        /// Whether a payload still carries the brief template's literal example values. The
        /// example exists so captains can see the report shape; it is not a report.
        /// </summary>
        /// <param name="payload">Parsed payload.</param>
        /// <returns>True when every field is the template placeholder.</returns>
        public static bool IsTemplatePlaceholder(PapercutPayload payload)
        {
            if (payload == null) return false;

            return String.Equals(Sanitize(payload.Title), "one line", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Sanitize(payload.Detail), "optional", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Sanitize(payload.Path), "optional/file.cs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolve a category name, falling back to Other for an unknown or missing value.
        /// </summary>
        /// <param name="value">Category name.</param>
        /// <returns>Category.</returns>
        public static PapercutCategoryEnum ParseCategory(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return PapercutCategoryEnum.Other;

            string normalized = Regex.Replace(value!, "[\\s_\\-]+", "");
            PapercutCategoryEnum parsed;
            if (Enum.TryParse<PapercutCategoryEnum>(normalized, true, out parsed)) return parsed;
            return PapercutCategoryEnum.Other;
        }

        /// <summary>
        /// Resolve a severity name, falling back to Low for an unknown or missing value. An unknown
        /// severity must not read as High: that would let a formatting slip outrank a real defect.
        /// </summary>
        /// <param name="value">Severity name.</param>
        /// <returns>Severity.</returns>
        public static PapercutSeverityEnum ParseSeverity(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return PapercutSeverityEnum.Low;

            string normalized = value!.Trim();
            PapercutSeverityEnum parsed;
            if (Enum.TryParse<PapercutSeverityEnum>(normalized, true, out parsed)) return parsed;
            return PapercutSeverityEnum.Low;
        }

        /// <summary>
        /// Remove control characters and redact anything that looks like a credential.
        /// </summary>
        /// <param name="value">Input text.</param>
        /// <returns>Sanitized text, or null when the input held nothing.</returns>
        public static string? Sanitize(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;

            string cleaned = _ControlPattern.Replace(value!, " ");
            cleaned = _SecretPattern.Replace(cleaned, "[redacted]");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            if (String.IsNullOrEmpty(cleaned)) return null;
            return cleaned;
        }

        #endregion

        #region Private-Methods

        private static PapercutPayload? TryDeserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<PapercutPayload>(json, _JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static PapercutPayload ParseDelimited(string value)
        {
            PapercutPayload payload = new PapercutPayload();

            string[] parts = value.Split('|');
            if (parts.Length >= 3)
            {
                payload.Category = parts[0];
                payload.Severity = parts[1];
                payload.Title = parts[2];
                if (parts.Length >= 4) payload.Detail = String.Join("|", parts.Skip(3));
                return payload;
            }

            payload.Title = value;
            return payload;
        }

        #endregion
    }
}
