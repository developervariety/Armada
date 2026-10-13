namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text.Json;

    internal static class PersonaPromptImprovements
    {
        internal const string Marker = "## Evidence and execution contract";

        internal static IReadOnlyDictionary<string, string> Additions { get; } = Load();

        internal static string Apply(string name, string content)
        {
            if (!Additions.TryGetValue(name, out string? addition)) return content;
            if (content.Contains(Marker, StringComparison.Ordinal)) return content;
            return content + addition;
        }

        private static Dictionary<string, string> Load()
        {
            Assembly assembly = typeof(PersonaPromptImprovements).Assembly;
            using Stream stream = assembly.GetManifestResourceStream("Armada.Core.Prompts.persona-improvements.json")
                ?? throw new InvalidOperationException("Embedded persona improvements are missing.");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException("Embedded persona improvements are invalid.");
        }
    }
}
