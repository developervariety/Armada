namespace Armada.Runtimes
{
    using System.Globalization;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Builds the one activity record shape every runtime writes into the mission log.
    /// </summary>
    /// <remarks>
    /// Every runtime renders a tool call as:
    ///
    /// <code>[ARMADA:ACTIVITY] tool &lt;name&gt; &lt;detail&gt; (&lt;status&gt;)</code>
    ///
    /// The three fields are normalized here, not in the runtimes, so a reader can compare a
    /// Cursor mission against a Codex mission without learning two vocabularies:
    ///
    /// <list type="bullet">
    /// <item>name: lower case, and runtime synonyms collapse to one canonical verb
    /// (<c>shellToolCall</c>, <c>command_execution</c>, and <c>Bash</c> all render <c>bash</c>).</item>
    /// <item>detail: the primary argument only, made relative to the dock, redacted, truncated.</item>
    /// <item>status: <c>ok</c>, <c>error</c>, or <c>error exit N</c>.</item>
    /// </list>
    ///
    /// Tool OUTPUT is never rendered. It is unbounded and frequently carries secrets.
    /// </remarks>
    internal static class StructuredRuntimeLogFormatter
    {
        #region Public-Members

        /// <summary>
        /// Maximum rendered length of a tool name in an activity record.
        /// </summary>
        public const int ToolNameLimit = 80;

        /// <summary>
        /// Maximum rendered length of a shell command in an activity record.
        /// </summary>
        public const int CommandDetailLimit = 240;

        /// <summary>
        /// Maximum rendered length of a path, pattern, or query in an activity record.
        /// </summary>
        public const int ShortDetailLimit = 160;

        /// <summary>
        /// Maximum rendered length of a status word in an activity record.
        /// </summary>
        public const int StatusLimit = 40;

        /// <summary>
        /// Canonical status for a tool call that finished normally.
        /// </summary>
        public const string OkStatus = "ok";

        /// <summary>
        /// Canonical status for a tool call that failed.
        /// </summary>
        public const string ErrorStatus = "error";

        /// <summary>
        /// Canonical status for a tool call that never reported an outcome, because the agent
        /// process ended while the call was still open.
        /// </summary>
        public const string IncompleteStatus = "incomplete";

        #endregion

        #region Private-Members

        private const string _ActivityPrefix = "[ARMADA:ACTIVITY] tool ";

        private static readonly string[] _ToolNameProperties =
        {
            "tool_name",
            "toolName",
            "name",
            "tool"
        };

        /// <summary>
        /// Property names that carry the one argument worth rendering, in priority order. A tool
        /// call is described by its target, so a path beats a pattern and a pattern beats a query.
        /// </summary>
        private static readonly string[] _DetailProperties =
        {
            "file_path",
            "filePath",
            "notebook_path",
            "notebookPath",
            "path",
            "command",
            "pattern",
            "query",
            "url",
            "description"
        };

        /// <summary>
        /// Property names of the nested object that holds a tool's arguments.
        /// </summary>
        private static readonly string[] _ArgumentContainerProperties =
        {
            "args",
            "arguments",
            "input",
            "parameters",
            "params"
        };

        /// <summary>
        /// Runtime tool synonyms collapsed to one canonical verb. Without this the same action
        /// reads as <c>Bash</c> on ClaudeCode, <c>bash</c> on OpenCode, <c>command_execution</c>
        /// on Codex, and <c>shellToolCall</c> on Cursor, and no reader can compare two missions.
        /// Names absent from this map are lower-cased and otherwise left alone, which is the
        /// right behavior for MCP tools that are already namespaced.
        /// </summary>
        private static readonly Dictionary<string, string> _ToolNameAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "read", "read" },
                { "readfile", "read" },
                { "read_file", "read" },
                { "view", "read" },
                { "cat", "read" },

                { "bash", "bash" },
                { "shell", "bash" },
                { "exec", "bash" },
                { "terminal", "bash" },
                { "command_execution", "bash" },
                { "run_shell_command", "bash" },
                { "run_terminal_cmd", "bash" },
                { "runterminalcommand", "bash" },

                { "write", "write" },
                { "writefile", "write" },
                { "write_file", "write" },
                { "create", "write" },

                { "edit", "edit" },
                { "multiedit", "edit" },
                { "str_replace", "edit" },
                { "search_replace", "edit" },
                { "searchreplace", "edit" },
                { "file_change", "edit" },
                { "apply_patch", "edit" },
                { "applypatch", "edit" },

                { "grep", "grep" },
                { "search", "grep" },
                { "ripgrep", "grep" },
                { "grep_search", "grep" },
                { "grepsearch", "grep" },
                { "codebase_search", "grep" },

                { "glob", "glob" },
                { "find_files", "glob" },
                { "findfiles", "glob" },
                { "fileglob", "glob" },

                { "ls", "ls" },
                { "list", "ls" },
                { "listdir", "ls" },
                { "list_dir", "ls" },
                { "listdirectory", "ls" },

                { "websearch", "websearch" },
                { "web_search", "websearch" },

                { "webfetch", "webfetch" },
                { "web_fetch", "webfetch" },
                { "fetch", "webfetch" },

                { "todowrite", "todo" },
                { "todo_write", "todo" },
                { "todoread", "todo" },
                { "todo_read", "todo" },

                { "task", "task" },
                { "agent", "task" },
                { "subagent", "task" },

                { "delete", "delete" },
                { "deletefile", "delete" },
                { "remove", "delete" },
                { "rm", "delete" }
            };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the canonical activity record for one tool call.
        /// </summary>
        /// <param name="toolName">Tool name reported by the runtime.</param>
        /// <param name="detail">Optional primary argument (file path, command, pattern, query).</param>
        /// <param name="status">Optional status, outcome word, or "exit N".</param>
        /// <param name="workingDirectory">Optional dock root, stripped from rendered paths.</param>
        /// <returns>Activity record, or an empty string when the tool name is missing.</returns>
        public static string BuildToolActivity(
            string? toolName,
            string? detail,
            string? status,
            string? workingDirectory = null)
        {
            if (String.IsNullOrWhiteSpace(toolName))
                return String.Empty;

            string rendered = _ActivityPrefix + NormalizeToolName(toolName);

            string normalizedDetail = NormalizeDetail(detail, workingDirectory, CommandDetailLimit);
            if (!String.IsNullOrEmpty(normalizedDetail))
                rendered += " " + normalizedDetail;

            string? normalizedStatus = NormalizeStatus(status);
            if (!String.IsNullOrEmpty(normalizedStatus))
                rendered += " (" + normalizedStatus + ")";

            return rendered;
        }

        /// <summary>
        /// Try to render a named tool event from a runtime that has no dedicated event model.
        /// Tool output is never copied; only the name, the primary argument, and the status.
        /// </summary>
        /// <param name="line">Raw structured output line.</param>
        /// <param name="workingDirectory">Optional dock root, stripped from rendered paths.</param>
        /// <param name="activity">Rendered activity record when the line describes a tool call.</param>
        /// <returns>True when the line was rendered as a tool activity record.</returns>
        public static bool TryBuildToolActivity(string line, string? workingDirectory, out string activity)
        {
            activity = String.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!ContainsToolMarker(root, 0))
                    return false;

                string? toolName = FindToolName(root, 0);
                if (String.IsNullOrWhiteSpace(toolName))
                    return false;

                activity = BuildToolActivity(
                    toolName,
                    FindToolDetail(root, 0),
                    FindToolStatus(root),
                    workingDirectory);
                return !String.IsNullOrEmpty(activity);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Collapse a runtime tool synonym to its canonical lower-case verb.
        /// </summary>
        /// <param name="toolName">Tool name reported by the runtime.</param>
        /// <returns>Canonical tool name.</returns>
        public static string NormalizeToolName(string toolName)
        {
            if (String.IsNullOrWhiteSpace(toolName))
                return String.Empty;

            string trimmed = toolName.Trim();

            // Cursor names a tool by the key of its payload object ("readToolCall"), so strip the
            // suffix before the alias lookup.
            if (trimmed.EndsWith("ToolCall", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 8)
                trimmed = trimmed.Substring(0, trimmed.Length - 8);

            string? mapped;
            if (_ToolNameAliases.TryGetValue(trimmed, out mapped))
                return mapped;

            return Truncate(trimmed.ToLowerInvariant(), ToolNameLimit);
        }

        /// <summary>
        /// Collapse a runtime outcome word or exit code to the canonical status vocabulary.
        /// </summary>
        /// <param name="status">Status, outcome word, or "exit N".</param>
        /// <returns>Canonical status, or null when the runtime reported none.</returns>
        public static string? NormalizeStatus(string? status)
        {
            if (String.IsNullOrWhiteSpace(status))
                return null;

            string normalized = status.Trim().ToLowerInvariant();

            if (normalized.StartsWith("exit ", StringComparison.Ordinal))
            {
                string codeText = normalized.Substring(5).Trim();
                int code;
                if (Int32.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                {
                    return code == 0
                        ? OkStatus
                        : ErrorStatus + " exit " + code.ToString(CultureInfo.InvariantCulture);
                }

                return Truncate(normalized, StatusLimit);
            }

            switch (normalized)
            {
                case "completed":
                case "complete":
                case "success":
                case "succeeded":
                case "ok":
                case "done":
                    return OkStatus;

                case "error":
                case "errored":
                case "failed":
                case "failure":
                    return ErrorStatus;

                default:
                    return Truncate(normalized, StatusLimit);
            }
        }

        /// <summary>
        /// Render a tool argument for the mission log: relative to the dock, redacted, truncated.
        /// </summary>
        /// <param name="detail">Raw argument value.</param>
        /// <param name="workingDirectory">Optional dock root, stripped from rendered paths.</param>
        /// <param name="maximumLength">Maximum rendered length.</param>
        /// <returns>Rendered argument, or an empty string when there is nothing to render.</returns>
        public static string NormalizeDetail(string? detail, string? workingDirectory, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(detail))
                return String.Empty;

            // Relativize BEFORE redacting and truncating: a dock path consumes most of the
            // budget, and removing it first leaves room for the part a reader needs.
            string rendered = RelativizePaths(detail.Trim(), workingDirectory);
            rendered = RedactSecretValues(rendered);
            return Truncate(rendered, maximumLength);
        }

        /// <summary>
        /// Rewrite absolute paths the way the captain was told to address them: files in its own
        /// dock become repository-relative, and files in a dock sibling become "../Name/...".
        /// Anything further away keeps its absolute form, because at that distance the location
        /// IS the information.
        /// </summary>
        /// <param name="value">Text that may contain absolute paths.</param>
        /// <param name="workingDirectory">Dock root.</param>
        /// <returns>Text with dock-rooted and sibling paths made relative.</returns>
        public static string RelativizePaths(string value, string? workingDirectory)
        {
            if (String.IsNullOrEmpty(value) || String.IsNullOrWhiteSpace(workingDirectory))
                return value;

            string root = workingDirectory.Trim().TrimEnd('/', '\\');
            if (root.Length == 0)
                return value;

            string rendered = value.Replace(root + "/", String.Empty, StringComparison.Ordinal);
            rendered = rendered.Replace(root + "\\", String.Empty, StringComparison.Ordinal);

            // A bare reference to the dock root itself still has to read as a path.
            rendered = rendered.Replace(root, ".", StringComparison.Ordinal);

            // Sibling checkouts sit next to the dock, and mission briefs point at them as
            // "../Name". Rendering the absolute form instead put the same long prefix on hundreds
            // of lines and did not match how the captain was asked to reach them.
            string? parent = GetParentDirectory(root);
            if (!String.IsNullOrEmpty(parent))
            {
                rendered = rendered.Replace(parent + "/", "../", StringComparison.Ordinal);
                rendered = rendered.Replace(parent + "\\", "..\\", StringComparison.Ordinal);
            }

            return rendered;
        }

        /// <summary>
        /// Bound activity text before it reaches durable telemetry.
        /// </summary>
        /// <param name="value">Text to bound.</param>
        /// <param name="maximumLength">Maximum length before truncation.</param>
        /// <returns>Bounded text.</returns>
        public static string TruncateActivityText(string value, int maximumLength)
        {
            return Truncate(value, maximumLength);
        }

        /// <summary>
        /// Redact token/password/key-shaped material while preserving structural text.
        /// </summary>
        /// <param name="value">Text to redact.</param>
        /// <returns>Redacted text.</returns>
        public static string RedactSecretValues(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            string redacted = Regex.Replace(
                value,
                "(?i)(password|token|secret|seed|private[_-]?key|api[_-]?key)\\s*[:=]\\s*([^\\s,;]+)",
                RedactNamedSecret);

            redacted = Regex.Replace(
                redacted,
                "-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
                RedactMatchedSecret,
                RegexOptions.Singleline);

            redacted = Regex.Replace(
                redacted,
                "\\b[0-9a-fA-F]{32,}\\b",
                RedactMatchedSecret);

            // Credentials carried by an auth scheme rather than a key=value pair.
            redacted = Regex.Replace(
                redacted,
                "(?i)\\b(bearer|basic)\\s+([A-Za-z0-9+/=_.-]{16,})",
                RedactSchemeCredential);

            // There is deliberately NO generic "long random-looking blob" rule. Two attempts at
            // one both did more harm than good: the first spanned whole filesystem paths, and a
            // narrower mixed-case-plus-digit version still ate ordinary CamelCase identifiers --
            // a batch of 40-character source file names logged as "<redacted len=40>.cs", which
            // hides what the captain touched while protecting nothing. A secret in a command
            // essentially always arrives labelled (key=value, Bearer, Basic) or as a PEM block or
            // a hex string, and those are matched above. An unlabelled blob is not distinguishable
            // from an identifier by shape, so guessing costs readability on every line and buys
            // little.
            return redacted;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Return the parent of a directory path, or null when it is too shallow to be a useful
        /// prefix. A near-root parent such as "/home" would match far more than dock siblings.
        /// </summary>
        private static string? GetParentDirectory(string path)
        {
            int lastSeparator = path.LastIndexOfAny(new char[] { '/', '\\' });
            if (lastSeparator <= 0)
                return null;

            string parent = path.Substring(0, lastSeparator);

            // Require at least two segments ("/a/b"), so a shallow prefix never becomes "../".
            int segments = 0;
            foreach (char character in parent)
            {
                if (character == '/' || character == '\\') segments++;
            }

            return segments >= 2 ? parent : null;
        }

        /// <summary>
        /// Regex evaluator for key=value secret values.
        /// </summary>
        private static string RedactNamedSecret(Match match)
        {
            return match.Groups[1].Value + "=<redacted len=" + match.Groups[2].Value.Length + ">";
        }

        /// <summary>
        /// Regex evaluator for standalone secret-looking values.
        /// </summary>
        private static string RedactMatchedSecret(Match match)
        {
            return "<redacted len=" + match.Value.Length + ">";
        }

        /// <summary>
        /// Regex evaluator for a credential carried by an auth scheme, keeping the scheme visible.
        /// </summary>
        private static string RedactSchemeCredential(Match match)
        {
            return match.Groups[1].Value + " <redacted len=" + match.Groups[2].Value.Length + ">";
        }

        private static bool ContainsToolMarker(JsonElement element, int depth)
        {
            if (depth > 5)
                return false;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((String.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase)
                         || String.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }

                    if (ContainsToolMarker(property.Value, depth + 1))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsToolMarker(item, depth + 1))
                        return true;
                }
            }

            return false;
        }

        private static string? FindToolName(JsonElement element, int depth)
        {
            if (depth > 5)
                return null;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (string propertyName in _ToolNameProperties)
                {
                    if (element.TryGetProperty(propertyName, out JsonElement value)
                        && value.ValueKind == JsonValueKind.String
                        && !String.IsNullOrWhiteSpace(value.GetString())
                        && !IsGenericToolLabel(value.GetString()!))
                    {
                        return value.GetString();
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string? nested = FindToolName(property.Value, depth + 1);
                    if (!String.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindToolName(item, depth + 1);
                    if (!String.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            return null;
        }

        /// <summary>
        /// Find the one argument worth rendering. Direct properties are preferred, then the
        /// contents of a recognized argument container, then any nested object.
        /// </summary>
        private static string? FindToolDetail(JsonElement element, int depth)
        {
            if (depth > 5 || element.ValueKind != JsonValueKind.Object)
                return null;

            foreach (string propertyName in _DetailProperties)
            {
                if (element.TryGetProperty(propertyName, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !String.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }

            foreach (string containerName in _ArgumentContainerProperties)
            {
                if (element.TryGetProperty(containerName, out JsonElement container))
                {
                    string? nested = FindToolDetail(container, depth + 1);
                    if (!String.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? nested = FindToolDetail(property.Value, depth + 1);
                if (!String.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            return null;
        }

        /// <summary>
        /// Read the outcome of a tool event from the shapes runtimes use for it.
        /// </summary>
        private static string? FindToolStatus(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty("status", out JsonElement status)
                && status.ValueKind == JsonValueKind.String
                && !String.IsNullOrWhiteSpace(status.GetString()))
            {
                return status.GetString();
            }

            // A "subtype" only reports an outcome when it names one; "started" is a lifecycle
            // phase, not a result, and rendering it would claim the call had finished.
            if (element.TryGetProperty("subtype", out JsonElement subtype)
                && subtype.ValueKind == JsonValueKind.String)
            {
                string? value = subtype.GetString();
                if (!String.IsNullOrWhiteSpace(value) && NamesAnOutcome(value!))
                    return value;
            }

            return null;
        }

        /// <summary>
        /// True when a word reports how a call ended rather than that it began.
        /// </summary>
        private static bool NamesAnOutcome(string value)
        {
            string normalized = value.Trim().ToLowerInvariant();
            return String.Equals(normalized, "completed", StringComparison.Ordinal)
                || String.Equals(normalized, "complete", StringComparison.Ordinal)
                || String.Equals(normalized, "success", StringComparison.Ordinal)
                || String.Equals(normalized, "succeeded", StringComparison.Ordinal)
                || String.Equals(normalized, "error", StringComparison.Ordinal)
                || String.Equals(normalized, "errored", StringComparison.Ordinal)
                || String.Equals(normalized, "failed", StringComparison.Ordinal)
                || String.Equals(normalized, "failure", StringComparison.Ordinal);
        }

        private static bool IsGenericToolLabel(string value)
        {
            string normalized = value.Replace('_', '-').Trim();
            return String.Equals(normalized, "tool", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-call", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-use", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-result", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string value, int maximumLength)
        {
            return String.IsNullOrEmpty(value) || value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength) + "...";
        }

        #endregion
    }
}
