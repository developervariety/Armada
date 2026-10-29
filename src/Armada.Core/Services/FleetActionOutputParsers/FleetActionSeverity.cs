namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;

    /// <summary>Reads published severity labels and CVSS v3 base vectors without network lookups.</summary>
    internal static class FleetActionSeverity
    {
        internal static string? FromFinding(JsonElement finding)
        {
            if (finding.TryGetProperty("severity", out JsonElement severity))
            {
                if (severity.ValueKind == JsonValueKind.String) return severity.GetString();
                if (severity.ValueKind == JsonValueKind.Array)
                {
                    string? best = null;
                    foreach (JsonElement item in severity.EnumerateArray())
                        if (item.TryGetProperty("score", out JsonElement score) && score.ValueKind == JsonValueKind.String)
                        {
                            string? candidate = FromCvss(score.GetString());
                            if (Rank(candidate) > Rank(best)) best = candidate;
                        }
                    if (best != null) return best;
                }
            }
            if (finding.TryGetProperty("database_specific", out JsonElement specific) && specific.ValueKind == JsonValueKind.Object
                && specific.TryGetProperty("severity", out JsonElement label) && label.ValueKind == JsonValueKind.String) return label.GetString();
            if (finding.TryGetProperty("cvss", out JsonElement cvss) && cvss.ValueKind == JsonValueKind.String) return FromCvss(cvss.GetString());
            return null;
        }

        private static int Rank(string? severity) => severity?.ToLowerInvariant() switch { "critical" => 4, "high" => 3, "medium" or "moderate" => 2, "low" => 1, _ => 0 };

        private static string? FromCvss(string? vector)
        {
            if (Double.TryParse(vector, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double score)) return Rating(score);
            if (vector == null || (!vector.StartsWith("CVSS:3.1/", StringComparison.Ordinal) && !vector.StartsWith("CVSS:3.0/", StringComparison.Ordinal))) return null;
            Dictionary<string, string> metrics = new(StringComparer.Ordinal);
            foreach (string part in vector.Split('/'))
            {
                string[] pair = part.Split(':');
                if (pair.Length != 2 || !metrics.TryAdd(pair[0], pair[1])) return null;
            }
            try
            {
                bool changed = metrics["S"] switch { "C" => true, "U" => false, _ => throw new FormatException() };
                double av = Weight(metrics["AV"], ("N", .85), ("A", .62), ("L", .55), ("P", .2));
                double ac = Weight(metrics["AC"], ("L", .77), ("H", .44));
                double pr = Weight(metrics["PR"], ("N", .85), ("L", changed ? .68 : .62), ("H", changed ? .5 : .27));
                double ui = Weight(metrics["UI"], ("N", .85), ("R", .62));
                double c = Weight(metrics["C"], ("N", 0), ("L", .22), ("H", .56));
                double i = Weight(metrics["I"], ("N", 0), ("L", .22), ("H", .56));
                double a = Weight(metrics["A"], ("N", 0), ("L", .22), ("H", .56));
                // FIRST CVSS v3.1 specification, sections 7.1/7.4 and Appendix A:
                // https://www.first.org/cvss/v3.1/specification-document
                double iss = 1 - (1 - c) * (1 - i) * (1 - a);
                double impact = changed ? 7.52 * (iss - .029) - 3.25 * Math.Pow(iss - .02, 15) : 6.42 * iss;
                if (impact <= 0) return null;
                double raw = Math.Min(10, (changed ? 1.08 : 1) * (impact + 8.22 * av * ac * pr * ui));
                double integer = Math.Round(raw * 100000, MidpointRounding.AwayFromZero);
                double rounded = integer % 10000 == 0 ? integer / 100000 : (Math.Floor(integer / 10000) + 1) / 10;
                return Rating(rounded);
            }
            catch (Exception ex) when (ex is KeyNotFoundException || ex is FormatException) { return null; }
        }

        private static double Weight(string key, params (string Key, double Value)[] values)
        {
            foreach (var value in values) if (value.Key == key) return value.Value;
            throw new FormatException();
        }
        private static string? Rating(double score) => !Double.IsFinite(score) || score > 10 || score <= 0 ? null : score >= 9 ? "critical" : score >= 7 ? "high" : score >= 4 ? "medium" : "low";
    }
}
