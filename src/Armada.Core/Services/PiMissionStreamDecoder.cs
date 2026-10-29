namespace Armada.Core.Services
{
    using System;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Protocol;

    /// <summary>
    /// Decodes <c>pi -p --mode json</c> mission output. Reply text arrives as <c>text_delta</c> fragments, which are
    /// joined into whole lines (as <see cref="MuxMissionStreamDecoder"/> does), so the mission output and protocol lines
    /// read as plain text. Thinking and tool executions become activities; a failed stop (error, aborted, length) is
    /// written to the transcript. Every other pi record is structured noise.
    /// </summary>
    public class PiMissionStreamDecoder : MissionStreamDecoder
    {
        #region Private-Members

        private readonly StringBuilder _Line = new StringBuilder();
        private bool _TurnHasText = false;
        private bool _Thinking = false;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decode one stdout line.
        /// </summary>
        /// <param name="line">Raw line.</param>
        /// <param name="nowUtc">When it arrived, UTC.</param>
        /// <returns>What the line stands for.</returns>
        public override MissionStreamDecodeResult Decode(string line, DateTime nowUtc)
        {
            if (!PiStreamEvent.TryParse(line, out PiStreamEvent? evt) || evt == null) return MissionStreamDecodeResult.Plain();
            MissionStreamDecodeResult result = MissionStreamDecodeResult.Empty();

            string? thinking = evt.ThinkingDelta;
            if (thinking == null && evt.Type == PiStreamEvent.TypeMessageUpdate && evt.AssistantMessageEvent?.Type == "thinking_start") thinking = String.Empty;
            if (thinking != null)
            {
                if (!_Thinking)
                {
                    _Thinking = true;
                    result.Activities.Add(new RuntimeActivity { Kind = RuntimeActivityKindEnum.Thinking, Summary = "Thinking", TimestampUtc = nowUtc });
                }

                return result;
            }

            if (evt.Type == PiStreamEvent.TypeMessageUpdate)
            {
                string? text = evt.TextDelta;
                if (text != null)
                {
                    _Thinking = false;
                    AppendText(text, result, nowUtc);
                }

                return result;
            }

            _Thinking = false;
            switch (evt.Type)
            {
                case PiStreamEvent.TypeToolExecutionStart:
                    EndTurn(result);
                    if (!String.IsNullOrWhiteSpace(evt.ToolName))
                        result.Activities.Add(ToolActivity(evt.ToolName!.Trim(), evt.Args, nowUtc));
                    break;
                case PiStreamEvent.TypeToolExecutionEnd:
                    {
                        EndTurn(result);
                        string name = String.IsNullOrWhiteSpace(evt.ToolName) ? "tool" : evt.ToolName!.Trim();
                        bool failed = evt.IsError == true;
                        result.Activities.Add(new RuntimeActivity
                        {
                            Kind = RuntimeActivityKindEnum.ToolCall,
                            ToolName = name,
                            Summary = failed ? name + " failed" : "Finished " + name,
                            TimestampUtc = nowUtc
                        });
                        break;
                    }
                case PiStreamEvent.TypeMessageEnd:
                    {
                        EndTurn(result);
                        PiMessage? message = evt.AssistantMessageEnd;
                        if (message != null && message.IsFailedStop)
                            result.AddTranscriptText("Error: pi stopped (" + message.StopReason + ")" + (String.IsNullOrWhiteSpace(message.ErrorMessage) ? String.Empty : ": " + message.ErrorMessage!.Trim()));
                        break;
                    }
                case PiStreamEvent.TypeAutoRetryStart:
                    EndTurn(result);
                    result.AddTranscriptText("Retrying after a provider error" + (String.IsNullOrWhiteSpace(evt.ErrorMessage) ? String.Empty : ": " + evt.ErrorMessage!.Trim()));
                    break;
                default:
                    // session, agent/turn lifecycle, tool progress, retry end, compaction, queue updates.
                    EndTurn(result);
                    break;
            }

            return result;
        }

        /// <summary>
        /// Complete a reply line still being joined when the process ends.
        /// </summary>
        /// <param name="nowUtc">Now, UTC.</param>
        /// <returns>What is left.</returns>
        public override MissionStreamDecodeResult Flush(DateTime nowUtc)
        {
            MissionStreamDecodeResult result = MissionStreamDecodeResult.Empty();
            EndTurn(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private void AppendText(string text, MissionStreamDecodeResult result, DateTime nowUtc)
        {
            foreach (char c in text)
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
            PiToolArguments input = ReadArguments(argumentsJson);
            string? path = Shorten(input.Path);
            string? pattern = Shorten(input.Pattern);
            string? detail;
            string summary;
            switch (name)
            {
                case "bash":
                    detail = Shorten(input.Command);
                    summary = detail != null ? "Running " + detail : "Running a command";
                    break;
                case "read":
                    detail = path;
                    summary = path != null ? "Reading " + path : "Reading a file";
                    break;
                case "write":
                    detail = path;
                    summary = path != null ? "Writing " + path : "Writing a file";
                    break;
                case "edit":
                    detail = path;
                    summary = path != null ? "Editing " + path : "Editing a file";
                    break;
                case "ls":
                    detail = path;
                    summary = path != null ? "Listing " + path : "Listing a directory";
                    break;
                case "grep":
                    detail = pattern;
                    summary = pattern != null ? "Searching for " + pattern + (path != null ? " in " + path : String.Empty) : "Searching the code";
                    break;
                case "find":
                    detail = pattern;
                    summary = pattern != null ? "Finding files " + pattern : "Finding files";
                    break;
                default:
                    detail = path;
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

        private static PiToolArguments ReadArguments(string? argumentsJson)
        {
            if (String.IsNullOrWhiteSpace(argumentsJson)) return new PiToolArguments();
            string trimmed = argumentsJson!.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) return new PiToolArguments();
            try
            {
                return JsonSerializer.Deserialize<PiToolArguments>(trimmed) ?? new PiToolArguments();
            }
            catch (JsonException)
            {
                return new PiToolArguments();
            }
        }

        private static string? Shorten(string? value)
        {
            return RuntimeActivityParser.FirstLine(value, RuntimeActivityParser.MaxDetailChars);
        }

        #endregion
    }
}
