namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>Bounded readable projections of runtime logs with shared redaction.</summary>
    public static class RuntimeLogFormatter
    {
        private const int _MaxLineChars = 2000;
        private const int _MaxEntries = 16;
        private static readonly JsonSerializerOptions _EventOptions = new JsonSerializerOptions { MaxDepth = 16 };
        private static readonly Regex _NoiseLine = new Regex(@"^\s*(\[dotnet\]|Determining projects to restore|Restored\s|MSBuild version|Welcome to \.NET)", RegexOptions.Compiled);

        /// <summary>Format one raw line using the existing single-line compatibility contract.</summary>
        public static FormattedLogLine Format(string? rawLine, AgentRuntimeEnum runtime)
        {
            IReadOnlyList<FormattedLogLine> entries = FormatEntries(rawLine, runtime);
            if (entries.Count == 0) return new FormattedLogLine { Dropped = true };
            if (entries.Count == 1) return entries[0];
            FormattedLogLine result = new FormattedLogLine
            {
                Text = String.Join("\n", entries.Select(entry => entry.Text)), Kind = LogEntryKindEnum.Mixed,
                IsToolCall = entries.Any(entry => entry.IsToolCall),
                ToolName = entries.FirstOrDefault(entry => entry.IsToolCall)?.ToolName,
                Redacted = entries.Any(entry => entry.Redacted), Truncated = entries.Any(entry => entry.Truncated)
            };
            Protect(result);
            return result;
        }

        /// <summary>Return up to sixteen distinct text, thinking and tool entries from one raw line.</summary>
        public static IReadOnlyList<FormattedLogLine> FormatEntries(string? rawLine, AgentRuntimeEnum runtime)
        {
            List<FormattedLogLine> entries = new List<FormattedLogLine>();
            if (String.IsNullOrWhiteSpace(rawLine)) return entries;
            string line = rawLine.TrimEnd('\r', '\n');
            if (_NoiseLine.IsMatch(line)) return entries;
            bool recognized = false;
            // Do not parse an arbitrarily large event tree merely to produce a small display entry.
            if (line.Length <= 65536 && line.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    RuntimeLogEvent? value = JsonSerializer.Deserialize<RuntimeLogEvent>(line, _EventOptions);
                    if (value != null) recognized = Project(value, runtime, entries);
                }
                catch (JsonException) { }
            }
            if (!recognized) entries.Add(new FormattedLogLine { Text = line });
            if (entries.Count > _MaxEntries)
            {
                entries.RemoveRange(_MaxEntries, entries.Count - _MaxEntries);
                entries[_MaxEntries - 1].Truncated = true;
                entries[_MaxEntries - 1].Text = "[additional event blocks omitted] " + entries[_MaxEntries - 1].Text;
            }
            foreach (FormattedLogLine entry in entries) Protect(entry);
            return entries;
        }

        /// <summary>Project a readable page with at most 500 entries and explicit omission state.</summary>
        public static List<FormattedLogLine> FormatPage(IEnumerable<string> lines, AgentRuntimeEnum runtime, out bool truncated)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            List<FormattedLogLine> result = new List<FormattedLogLine>();
            truncated = false;
            foreach (string line in lines)
            {
                foreach (FormattedLogLine entry in FormatEntries(line, runtime))
                {
                    if (result.Count == 500) { truncated = true; return result; }
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>Compatibility entry point for unbounded shared secret redaction.</summary>
        public static string RedactSecrets(string? text) => SecretRedactor.Redact(text);

        private static bool Project(RuntimeLogEvent value, AgentRuntimeEnum runtime, List<FormattedLogLine> entries)
        {
            if (value.EventType == "tool_call_proposed" && value.ToolCall != null)
            {
                AddTool(entries, value.ToolCall.Name, false, null);
                return true;
            }
            if (value.EventType == "tool_call_completed")
            {
                AddTool(entries, value.ToolName, true, value.Result?.Success switch { true => "ok", false => "failed", _ => "status unknown" });
                return true;
            }
            if (value.EventType == "assistant_text" && value.Text != null)
            {
                entries.Add(new FormattedLogLine { Text = value.Text });
                return true;
            }
            if (value.Part != null)
            {
                if (value.Type == "step_start" || value.Type == "step_finish") return true;
                if (value.Type == "tool_use")
                {
                    string? status = value.Part.State?.Status;
                    bool completed = status == "completed" || status == "error";
                    AddTool(entries, value.Part.Tool, completed, status);
                    if (completed && !String.IsNullOrEmpty(value.Part.State?.Output))
                        entries.Add(new FormattedLogLine { Text = value.Part.State.Output, Kind = LogEntryKindEnum.ToolResult, IsToolCall = true, ToolName = value.Part.Tool });
                    return true;
                }
                if ((value.Type == "text" || value.Type == "reasoning") && value.Part.Text != null)
                {
                    AddText(entries, value.Part.Text, value.Type == "reasoning");
                    return true;
                }
            }
            if (value.Item != null)
            {
                RuntimeLogBlock item = value.Item;
                if (item.Type == "agent_message" || item.Type == "reasoning")
                {
                    if (item.Text == null) return false;
                    AddText(entries, item.Text, item.Type == "reasoning");
                    return true;
                }
                if (item.Type == "command_execution" || item.Type == "mcp_tool_call")
                {
                    string name = item.Type == "command_execution" ? "command" : (item.Server ?? "mcp") + "/" + (item.Tool ?? "unknown");
                    string detail = item.Type == "command_execution" ? item.Command ?? "" : "";
                    bool completed = value.Type == "item.completed";
                    AddTool(entries, name, completed, String.Join(" ", new[] { item.Status, detail }.Where(text => !String.IsNullOrEmpty(text))));
                    if (completed && !String.IsNullOrEmpty(item.AggregatedOutput))
                        entries.Add(new FormattedLogLine { Text = item.AggregatedOutput, Kind = LogEntryKindEnum.ToolResult, IsToolCall = true, ToolName = name });
                    return true;
                }
            }
            if (runtime != AgentRuntimeEnum.ClaudeCode && runtime != AgentRuntimeEnum.Codex
                && value.Type != "assistant" && value.Type != "user" && value.Type != "tool_use") return false;
            if (value.Type == "tool_use")
            {
                AddTool(entries, value.Name, false, null);
                return true;
            }
            RuntimeLogContent? content = value.Message?.Content ?? value.Content;
            if (content?.Text != null)
            {
                AddText(entries, content.Text, false);
                return true;
            }
            if (content?.Blocks == null) return false;
            foreach (RuntimeLogBlock? block in content.Blocks)
            {
                if (block == null) continue;
                if (block.Type == "tool_use") AddTool(entries, block.Name, false, null);
                else if (block.Type == "text" && block.Text != null) AddText(entries, block.Text, false);
                else if (block.Type == "thinking" && block.Thinking != null) AddText(entries, block.Thinking, true);
                else if (block.Type == "tool_result")
                {
                    string detail = block.Content?.Text ?? String.Join("\n", block.Content?.Blocks?.Select(part => part?.Text ?? "") ?? Array.Empty<string>());
                    entries.Add(new FormattedLogLine
                    {
                        Text = "<- tool result" + (block.IsError == true ? " failed" : "") + (detail.Length > 0 ? "\n" + detail : ""),
                        Kind = LogEntryKindEnum.ToolResult, IsToolCall = true
                    });
                }
                else entries.Add(new FormattedLogLine { Text = "[unsupported runtime content block]", Kind = LogEntryKindEnum.Status });
                if (entries.Count > _MaxEntries) break;
            }
            return entries.Count > 0;
        }

        private static void AddTool(List<FormattedLogLine> entries, string? name, bool result, string? detail)
        {
            entries.Add(new FormattedLogLine
            {
                IsToolCall = true, ToolName = name, Kind = result ? LogEntryKindEnum.ToolResult : LogEntryKindEnum.ToolCall,
                Text = (result ? "<- tool " : "-> tool ") + (name ?? "unknown") + (String.IsNullOrEmpty(detail) ? "" : " " + detail)
            });
        }

        private static void AddText(List<FormattedLogLine> entries, string text, bool thinking)
        {
            entries.Add(new FormattedLogLine { Text = thinking ? "(thinking) " + text : text, Kind = thinking ? LogEntryKindEnum.Thinking : LogEntryKindEnum.Text });
        }

        private static void Protect(FormattedLogLine entry)
        {
            string text = RedactSecrets(entry.Text);
            string? tool = entry.ToolName == null ? null : RedactSecrets(entry.ToolName);
            entry.Redacted |= text != entry.Text || tool != entry.ToolName;
            if (text.Length > _MaxLineChars)
            {
                text = text.Substring(0, _MaxLineChars) + " ... [truncated " + (text.Length - _MaxLineChars) + " chars]";
                entry.Truncated = true;
            }
            if (tool?.Length > 200) { tool = tool.Substring(0, 200) + "..."; entry.Truncated = true; }
            entry.Text = text;
            entry.ToolName = tool;
        }
    }
}
