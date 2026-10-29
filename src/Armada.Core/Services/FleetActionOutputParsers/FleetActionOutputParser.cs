namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml;
    using System.Xml.Linq;

    /// <summary>Shared safe parsing boundary and count helpers. Never retain source records in results.</summary>
    public abstract class FleetActionOutputParser : IFleetActionOutputParser
    {
        /// <inheritdoc />
        public FleetActionParseResult Parse(string? output, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(output)) return new();
            try { return ParseReport(output!, token); }
            catch (Exception ex) when (ex is JsonException || ex is XmlException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException || ex is OverflowException) { return new(); }
        }
        /// <summary>Parse one complete report. Throwing on malformed data returns an unparsed result.</summary>
        protected abstract FleetActionParseResult ParseReport(string output, CancellationToken token);
        /// <summary>Read XML without DTDs or external entities.</summary>
        protected static XDocument ReadXml(string output)
        {
            using System.IO.StringReader text = new(output);
            using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16777216 });
            return XDocument.Load(reader);
        }
        /// <summary>Shared report parsing helper.</summary>
        protected static List<JsonElement> JsonSequence(string output, CancellationToken token)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(output);
            int offset = 0;
            List<JsonElement> values = new();
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                while (offset < bytes.Length && (bytes[offset] == 32 || bytes[offset] == 10 || bytes[offset] == 13 || bytes[offset] == 9)) offset++;
                if (offset == bytes.Length) break;
                Utf8JsonReader reader = new(bytes.AsSpan(offset), true, default);
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                values.Add(document.RootElement.Clone());
                offset += checked((int)reader.BytesConsumed);
            }
            return values;
        }

        /// <summary>Shared report parsing helper.</summary>
        protected static IEnumerable<JsonElement> Array(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
        /// <summary>Shared report parsing helper.</summary>
        protected static bool Has(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);
        /// <summary>Shared report parsing helper.</summary>
        protected static string? Str(JsonElement root, string name) => Has(root, name) && root.GetProperty(name).ValueKind == JsonValueKind.String ? root.GetProperty(name).GetString() : null;
        /// <summary>Shared report parsing helper.</summary>
        protected static int Attr(XElement element, string name)
        {
            string? value = element.Attribute(name)?.Value;
            if (value == null) return 0;
            if (!Int32.TryParse(value, out int number) || number < 0) throw new FormatException("Invalid test counter.");
            return number;
        }
        /// <summary>Shared report parsing helper.</summary>
        protected static void Add(FleetActionParseResult result, string? severity, CancellationToken token) { token.ThrowIfCancellationRequested(); result.Total++; Severity(result, severity); }
        /// <summary>Shared report parsing helper.</summary>
        protected static void Severity(FleetActionParseResult result, string? severity, int count = 1)
        {
            switch (severity?.ToLowerInvariant())
            {
                case "critical": result.Critical += count; break;
                case "high": result.High += count; break;
                case "moderate": case "medium": result.Medium += count; break;
                case "low": result.Low += count; break;
            }
        }
        /// <summary>Shared report parsing helper.</summary>
        protected static FleetActionParseResult Finish(FleetActionParseResult result, bool tests = false)
        {
            result.Parsed = true;
            if (tests) { result.Total = result.Failed; result.Summary = $"Tests: {result.Passed} passed, {result.Failed} failed, {result.Skipped} skipped."; }
            else result.Summary = $"Findings: {result.Total} total, {result.Critical} critical, {result.High} high, {result.Medium} medium, {result.Low} low, {result.Unrated} unrated.";
            return result;
        }
    }
}
