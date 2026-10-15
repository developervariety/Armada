namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Protocol;

    /// <summary>
    /// Decodes <c>mux print --output-format jsonl</c> for a mission. Mux streams its reply as assistant_text deltas that
    /// split lines anywhere; the decoder joins them and stands each completed line for the stdout line text mode prints,
    /// so protocol lines such as <c>[ARMADA:STATUS]</c> written by the model are whole lines the progress parser reads as
    /// they arrive. A line still open is printed when the turn ends (a tool call, a heartbeat, an error, or
    /// <c>run_completed</c>) or when the process ends. Tool calls report activity as they are proposed and complete, the
    /// first line of each turn's text and the start of reasoning report activity, and an error event stands for the
    /// "Error: code: message" transcript line text mode prints on stderr. Heartbeats, context status, compaction,
    /// approvals, task plans, and the run's start and end stand for nothing. The final message file
    /// (--output-last-message) is written by mux as before.
    /// </summary>
    public class MuxMissionStreamDecoder : MissionStreamDecoder
    {
        #region Private-Members

        private readonly StringBuilder _Line = new StringBuilder();
        private bool _TurnHasText = false;
        private bool _Thinking = false;

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public override MissionStreamDecodeResult Decode(string line, DateTime nowUtc)
        {
            if (!MuxProtocolEvent.TryParse(line, out MuxProtocolEvent? evt) || evt == null) return MissionStreamDecodeResult.Plain();

            MissionStreamDecodeResult result = MissionStreamDecodeResult.Empty();
            if (evt.EventType != MuxProtocolEvent.AssistantThinking) _Thinking = false;

            switch (evt.EventType)
            {
                case MuxProtocolEvent.AssistantText:
                    AppendText(evt.Text, result, nowUtc);
                    break;

                case MuxProtocolEvent.AssistantThinking:
                    if (!_Thinking && !String.IsNullOrEmpty(evt.Text))
                    {
                        _Thinking = true;
                        result.Activities.Add(new RuntimeActivity { Kind = RuntimeActivityKindEnum.Thinking, Summary = "Thinking", TimestampUtc = nowUtc });
                    }
                    break;

                case MuxProtocolEvent.ToolCallProposed:
                    EndTurn(result);
                    if (evt.ToolCall != null && !String.IsNullOrWhiteSpace(evt.ToolCall.Name))
                        result.Activities.Add(ToolActivity(evt.ToolCall.Name!.Trim(), evt.ToolCall.Arguments, nowUtc));
                    break;

                case MuxProtocolEvent.ToolCallCompleted:
                    {
                        EndTurn(result);
                        string name = String.IsNullOrWhiteSpace(evt.ToolName) ? "tool" : evt.ToolName!.Trim();
                        bool ok = evt.Result?.Success ?? true;
                        result.Activities.Add(new RuntimeActivity
                        {
                            Kind = RuntimeActivityKindEnum.ToolCall,
                            ToolName = name,
                            Summary = ok ? "Finished " + name : name + " failed",
                            TimestampUtc = nowUtc
                        });
                        break;
                    }

                case MuxProtocolEvent.Error:
                    EndTurn(result);
                    result.AddTranscriptText("Error: " + (String.IsNullOrWhiteSpace(evt.Code) ? "error" : evt.Code!.Trim()) + (String.IsNullOrWhiteSpace(evt.Message) ? String.Empty : ": " + evt.Message!.Trim()));
                    break;

                case MuxProtocolEvent.Heartbeat:
                case MuxProtocolEvent.RunCompleted:
                    EndTurn(result);
                    break;

                default:
                    // run_started, tool_call_approved, context_status, context_compacted, task_plan_updated: noise here.
                    break;
            }

            return result;
        }

        /// <inheritdoc />
        public override MissionStreamDecodeResult Flush(DateTime nowUtc)
        {
            MissionStreamDecodeResult result = MissionStreamDecodeResult.Empty();
            EndTurn(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private void AppendText(string? text, MissionStreamDecodeResult result, DateTime nowUtc)
        {
            if (String.IsNullOrEmpty(text)) return;
            foreach (char c in text!)
            {
                if (c == '\n')
                {
                    CompleteLine(result, nowUtc);
                    continue;
                }

                _Line.Append(c);
            }
        }

        private void CompleteLine(MissionStreamDecodeResult result, DateTime? nowUtc)
        {
            string completed = _Line.ToString().TrimEnd('\r');
            _Line.Clear();
            if (String.IsNullOrWhiteSpace(completed)) return;

            if (!_TurnHasText && nowUtc.HasValue)
            {
                string? first = RuntimeActivityParser.FirstLine(completed, RuntimeActivityParser.MaxDetailChars);
                if (first != null) result.Activities.Add(new RuntimeActivity { Kind = RuntimeActivityKindEnum.Text, Detail = first, Summary = first, TimestampUtc = nowUtc.Value });
            }

            _TurnHasText = true;
            result.AddOutputText(completed);
        }

        private void EndTurn(MissionStreamDecodeResult result)
        {
            if (_Line.Length > 0) CompleteLine(result, null);
            _TurnHasText = false;
        }

        private static RuntimeActivity ToolActivity(string name, string? argumentsJson, DateTime nowUtc)
        {
            MuxToolInput input = ReadInput(argumentsJson);
            string? file = Shorten(input.FilePath);
            string? path = Shorten(input.Path);
            string? pattern = Shorten(input.Pattern);
            string? summary = null;
            string? detail = null;

            switch (name)
            {
                case "read_file":
                    detail = file;
                    summary = file != null ? "Reading " + file : "Reading a file";
                    break;
                case "write_file":
                    detail = file;
                    summary = file != null ? "Writing " + file : "Writing a file";
                    break;
                case "edit_file":
                case "multi_edit":
                    detail = file;
                    summary = file != null ? "Editing " + file : "Editing a file";
                    break;
                case "delete_file":
                    detail = file;
                    summary = file != null ? "Deleting " + file : "Deleting a file";
                    break;
                case "run_process":
                    {
                        string? command = Shorten(CommandLine(input));
                        detail = command;
                        summary = command != null ? "Running " + command : "Running a command";
                        break;
                    }
                case "grep":
                    detail = pattern;
                    summary = pattern != null ? "Searching for " + pattern + (path != null ? " in " + path : String.Empty) : "Searching the code";
                    break;
                case "glob":
                    detail = pattern;
                    summary = pattern != null ? "Finding files " + pattern : "Finding files";
                    break;
                case "list_directory":
                    detail = path;
                    summary = path != null ? "Listing " + path : "Listing a directory";
                    break;
                case "web_retrieve":
                    detail = Shorten(input.Url);
                    summary = detail != null ? "Fetching " + detail : "Fetching a web page";
                    break;
                case "web_search":
                    detail = Shorten(input.Query);
                    summary = detail != null ? "Searching the web for " + detail : "Searching the web";
                    break;
                case "plan_tasks":
                case "update_task":
                    summary = "Updating its task list";
                    break;
                case "spawn_subagent":
                    summary = "Delegating to a subagent";
                    break;
                default:
                    detail = file ?? path;
                    summary = "Calling tool " + name;
                    break;
            }

            return new RuntimeActivity
            {
                Kind = RuntimeActivityKindEnum.ToolCall,
                ToolName = name,
                Detail = detail,
                Summary = RuntimeActivityParser.FirstLine(summary, RuntimeActivityParser.MaxDetailChars * 2) ?? summary,
                TimestampUtc = nowUtc
            };
        }

        private static string? CommandLine(MuxToolInput input)
        {
            if (String.IsNullOrWhiteSpace(input.Command)) return null;
            List<string> parts = new List<string> { input.Command!.Trim() };
            if (input.Args != null)
            {
                foreach (string arg in input.Args)
                {
                    if (!String.IsNullOrEmpty(arg)) parts.Add(arg);
                }
            }

            return String.Join(" ", parts);
        }

        private static MuxToolInput ReadInput(string? argumentsJson)
        {
            if (String.IsNullOrWhiteSpace(argumentsJson)) return new MuxToolInput();
            string trimmed = argumentsJson!.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) return new MuxToolInput();
            try
            {
                return JsonSerializer.Deserialize<MuxToolInput>(trimmed) ?? new MuxToolInput();
            }
            catch (JsonException)
            {
                return new MuxToolInput();
            }
        }

        private static string? Shorten(string? value)
        {
            return RuntimeActivityParser.FirstLine(value, RuntimeActivityParser.MaxDetailChars);
        }

        #endregion
    }
}
