namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core.Enums;

    /// <summary>
    /// Reference to an external input required by a workflow profile.
    /// </summary>
    public class WorkflowInputReference
    {
        /// <summary>
        /// Input provider type.
        /// </summary>
        public WorkflowInputReferenceProviderEnum Provider { get; set; } = WorkflowInputReferenceProviderEnum.EnvironmentVariable;

        /// <summary>
        /// Provider-specific key or path.
        /// </summary>
        public string Key
        {
            get => _Key;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Key));
                _Key = value.Trim();
            }
        }

        private string _Key = String.Empty;

        /// <summary>
        /// Optional environment scope for this input reference.
        /// </summary>
        public string? EnvironmentName
        {
            get => _EnvironmentName;
            set => _EnvironmentName = NormalizeEmpty(value);
        }

        /// <summary>
        /// Optional operator-facing description.
        /// </summary>
        public string? Description
        {
            get => _Description;
            set => _Description = NormalizeEmpty(value);
        }

        private string? _EnvironmentName = null;
        private string? _Description = null;
        private const string _JsonPrefix = "json:";
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Parse one legacy input-reference string into a structured reference.
        /// </summary>
        public static WorkflowInputReference Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentNullException(nameof(value));

            string trimmed = value.Trim();
            if (trimmed.StartsWith(_JsonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                WorkflowInputReference? deserialized = TryDeserialize(trimmed.Substring(_JsonPrefix.Length));
                if (deserialized != null)
                    return deserialized;
            }

            int separatorIndex = trimmed.IndexOf(':');
            if (separatorIndex > 0)
            {
                string prefix = trimmed.Substring(0, separatorIndex).Trim().ToLowerInvariant();
                string key = trimmed.Substring(separatorIndex + 1).Trim();
                if (!String.IsNullOrWhiteSpace(key))
                {
                    if (prefix == "env" || prefix == "environment" || prefix == "environmentvariable")
                    {
                        return new WorkflowInputReference
                        {
                            Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                            Key = key
                        };
                    }

                    if (prefix == "file" || prefix == "filepath")
                    {
                        return new WorkflowInputReference
                        {
                            Provider = WorkflowInputReferenceProviderEnum.FilePath,
                            Key = key
                        };
                    }

                    if (prefix == "dir" || prefix == "directory" || prefix == "directorypath")
                    {
                        return new WorkflowInputReference
                        {
                            Provider = WorkflowInputReferenceProviderEnum.DirectoryPath,
                            Key = key
                        };
                    }
                }
            }

            return new WorkflowInputReference
            {
                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                Key = trimmed
            };
        }

        /// <summary>
        /// Parse many legacy input-reference strings into structured references.
        /// </summary>
        public static List<WorkflowInputReference> ParseMany(IEnumerable<string>? values)
        {
            if (values == null) return new List<WorkflowInputReference>();

            List<WorkflowInputReference> results = new List<WorkflowInputReference>();
            foreach (string value in values.Where(item => !String.IsNullOrWhiteSpace(item)))
            {
                results.Add(Parse(value));
            }

            return results;
        }

        /// <summary>
        /// Serialize one structured input reference into the legacy string representation.
        /// </summary>
        public static string Serialize(WorkflowInputReference value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (String.IsNullOrWhiteSpace(value.Key)) throw new ArgumentNullException(nameof(value.Key));

            bool simpleLegacyValue =
                String.IsNullOrWhiteSpace(value.EnvironmentName)
                && String.IsNullOrWhiteSpace(value.Description)
                && (value.Provider == WorkflowInputReferenceProviderEnum.EnvironmentVariable
                    || value.Provider == WorkflowInputReferenceProviderEnum.FilePath
                    || value.Provider == WorkflowInputReferenceProviderEnum.DirectoryPath);

            if (simpleLegacyValue)
            {
                return value.Provider switch
                {
                    WorkflowInputReferenceProviderEnum.EnvironmentVariable => "env:" + value.Key.Trim(),
                    WorkflowInputReferenceProviderEnum.FilePath => "file:" + value.Key.Trim(),
                    WorkflowInputReferenceProviderEnum.DirectoryPath => "dir:" + value.Key.Trim(),
                    _ => "env:" + value.Key.Trim()
                };
            }

            WorkflowInputReference normalized = new WorkflowInputReference
            {
                Provider = value.Provider,
                Key = value.Key.Trim(),
                EnvironmentName = NormalizeEmpty(value.EnvironmentName),
                Description = NormalizeEmpty(value.Description)
            };

            return _JsonPrefix + JsonSerializer.Serialize(normalized, _Json);
        }

        /// <summary>
        /// Serialize many structured input references into the legacy string representation.
        /// </summary>
        public static List<string> SerializeMany(IEnumerable<WorkflowInputReference>? values)
        {
            if (values == null) return new List<string>();
            return values
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Key))
                .Select(Serialize)
                .ToList();
        }

        private static WorkflowInputReference? TryDeserialize(string json)
        {
            try
            {
                WorkflowInputReference? result = JsonSerializer.Deserialize<WorkflowInputReference>(json, _Json);
                if (result == null || String.IsNullOrWhiteSpace(result.Key))
                    return null;

                result.Key = result.Key.Trim();
                result.EnvironmentName = NormalizeEmpty(result.EnvironmentName);
                result.Description = NormalizeEmpty(result.Description);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
