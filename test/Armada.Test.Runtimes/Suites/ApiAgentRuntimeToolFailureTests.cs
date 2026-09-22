namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using Armada.Runtimes.Tools;
    using Armada.Test.Common;
    using PolyPrompt.Models;

    /// <summary>
    /// Two contracts for the API-endpoint runtime: a failed tool call must say WHY it failed, and model
    /// reasoning must never reach the text a terminal marker or a Judge verdict is parsed from.
    /// The activity line is the only failure record an operator sees,
    /// and a mission's recorded failure cause quotes that same line, so a status word with no class sends
    /// the incident, the classifier and the autonomous rescue to triage a string carrying no information.
    /// </summary>
    public class ApiAgentRuntimeToolFailureTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Api Agent Runtime Tool Failures";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A thrown tool failure is classified, not all reported as invalid arguments", () =>
            {
                AssertEqual("boundary_refused", ApiAgentRuntime.ClassifyToolException(new WorkspaceBoundaryException()));
                AssertEqual("enumeration_limit", ApiAgentRuntime.ClassifyToolException(new WorkspaceEnumerationLimitException(10)));
                AssertEqual("invalid_arguments", ApiAgentRuntime.ClassifyToolException(new JsonException("bad payload")));
                // The shape the argument parser actually throws: a JsonException wrapped in an ArgumentException.
                AssertEqual("invalid_arguments", ApiAgentRuntime.ClassifyToolException(
                    new ArgumentException("Tool arguments are invalid", new JsonException("bad payload"))));
                AssertEqual("permission_denied", ApiAgentRuntime.ClassifyToolException(new UnauthorizedAccessException()));
                AssertEqual("not_found", ApiAgentRuntime.ClassifyToolException(new FileNotFoundException()));
                AssertEqual("not_found", ApiAgentRuntime.ClassifyToolException(new DirectoryNotFoundException()));
                AssertEqual("io_error", ApiAgentRuntime.ClassifyToolException(new IOException("disk")));
                AssertEqual("tool_failed", ApiAgentRuntime.ClassifyToolException(new InvalidOperationException("other")));
            });

            await RunTest("A tool result that names its own error class is read from the result", () =>
            {
                string content = JsonSerializer.Serialize(new { error = "file_not_found", message = "File not found: /somewhere/CLAUDE.md" });
                AssertEqual("file_not_found", ApiAgentRuntime.ReadFailureClass(content));

                AssertNull(ApiAgentRuntime.ReadFailureClass("plain text, not json"));
                AssertNull(ApiAgentRuntime.ReadFailureClass("{\"message\":\"no error field\"}"));
                AssertNull(ApiAgentRuntime.ReadFailureClass(null));
                AssertNull(ApiAgentRuntime.ReadFailureClass(String.Empty));
            });

            await RunTest("The activity record renders the failure class beside the status", () =>
            {
                string rendered = StructuredRuntimeLogFormatter.BuildToolActivity(
                    "read", "CLAUDE.md", StructuredRuntimeLogFormatter.ErrorStatus, null, "boundary_refused");
                AssertContains("boundary_refused", rendered, "the class reaches the activity line");
                AssertContains("(error", rendered, "the status word is kept");

                // The record's own prefix contains a colon, so the compatibility check is on the status
                // group at the end of the line, not on the line as a whole.
                string withoutReason = StructuredRuntimeLogFormatter.BuildToolActivity(
                    "read", "CLAUDE.md", StructuredRuntimeLogFormatter.ErrorStatus);
                AssertTrue(withoutReason.EndsWith("(error)", StringComparison.Ordinal), "a call with no class renders as it always did");
                AssertTrue(rendered.EndsWith("(error: boundary_refused)", StringComparison.Ordinal), "a call with a class renders it inside the status group");
            });

            await RunTest("A failure class cannot carry a path or an exception message into the log", () =>
            {
                // Tool messages carry absolute workspace paths, so only a token may be rendered.
                string dirty = StructuredRuntimeLogFormatter.NormalizeFailureReason(
                    "File not found: /home/someone/.armada/docks/Vessel/msn_example/Vessel/CLAUDE.md");
                AssertFalse(dirty.Contains("/"), "path separators never survive");
                AssertFalse(dirty.Contains("."), "dotted path segments never survive");
                AssertTrue(dirty.Length <= StructuredRuntimeLogFormatter.FailureReasonLimit, "the class is length-bounded");

                AssertEqual(String.Empty, StructuredRuntimeLogFormatter.NormalizeFailureReason(null));
                AssertEqual(String.Empty, StructuredRuntimeLogFormatter.NormalizeFailureReason("   "));
                AssertEqual("boundary_refused", StructuredRuntimeLogFormatter.NormalizeFailureReason("Boundary Refused"));
            });

            await RunTest("Ordinary assistant text is never reshaped by the reasoning strip", () =>
            {
                string verdict = "## Verdict\n\nThe change is correct.\n\n[ARMADA:VERDICT] PASS";
                AssertEqual(verdict, ApiAgentRuntime.StripReasoningMarkup(verdict), "text with no markup is returned unchanged");
                AssertEqual(String.Empty, ApiAgentRuntime.StripReasoningMarkup(null));
                AssertEqual(String.Empty, ApiAgentRuntime.StripReasoningMarkup(String.Empty));
            });

            await RunTest("Leaked reasoning markup cannot reach the text a verdict is parsed from", () =>
            {
                // A matched block is dropped whole and the verdict survives.
                string matched = "<think>I should check the diff first.</think>\n## Verdict\n\n[ARMADA:VERDICT] PASS";
                string strippedMatched = ApiAgentRuntime.StripReasoningMarkup(matched);
                AssertFalse(strippedMatched.Contains("think", StringComparison.OrdinalIgnoreCase), "no think markup survives");
                AssertFalse(strippedMatched.Contains("check the diff"), "the reasoning body does not survive");
                AssertContains("[ARMADA:VERDICT] PASS", strippedMatched, "the verdict line survives");

                // The shape actually observed in a mission log: a closing tag with no opener.
                string orphanClose = "I will review from git metadata.\n</think>\n\n## Verdict\n\n[ARMADA:VERDICT] NEEDS_REVISION";
                string strippedOrphan = ApiAgentRuntime.StripReasoningMarkup(orphanClose);
                AssertFalse(strippedOrphan.Contains("think", StringComparison.OrdinalIgnoreCase), "the orphan tag is removed");
                AssertFalse(strippedOrphan.Contains("I will review from git metadata"), "text before a lone closing tag was reasoning and is dropped");
                AssertContains("[ARMADA:VERDICT] NEEDS_REVISION", strippedOrphan, "the verdict line survives an orphan closing tag");

                // An opener with no close means the model was still reasoning when it stopped.
                string orphanOpen = "## Verdict\n\n[ARMADA:VERDICT] FAIL\n<think>but wait, maybe";
                string strippedOpen = ApiAgentRuntime.StripReasoningMarkup(orphanOpen);
                AssertFalse(strippedOpen.Contains("but wait"), "trailing reasoning is dropped");
                AssertContains("[ARMADA:VERDICT] FAIL", strippedOpen, "the verdict line survives an orphan opening tag");

                // Exactly one standalone verdict line must remain, never two.
                AssertEqual(1, CountOccurrences(strippedMatched, "[ARMADA:VERDICT]"), "the strip cannot duplicate a verdict line");
            });

            await RunTest("A conversation under the threshold is left alone", () =>
            {
                List<ChatMessage> messages = BuildConversation(6, 64);
                AssertEqual(0, ApiAgentRuntime.CompactConversation(messages), "a small conversation is not compacted");
                foreach (ChatMessage message in messages)
                    AssertFalse(message.Content!.StartsWith(ApiAgentRuntime.CompactedToolResultMarker, StringComparison.Ordinal));
            });

            await RunTest("A conversation over the threshold compacts instead of being lost", () =>
            {
                // Before this, the loop only threw at the ceiling: the work was done and no result came back.
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                string systemBefore = messages[0].Content!;
                string launchBefore = messages[1].Content!;

                int compacted = ApiAgentRuntime.CompactConversation(messages);
                AssertTrue(compacted > 0, "older tool results are compacted");

                AssertEqual(systemBefore, messages[0].Content, "the system prompt survives whole");
                AssertEqual(launchBefore, messages[1].Content, "the launch prompt survives whole");

                for (int index = messages.Count - ApiAgentRuntime.RecentMessagesKeptWhole; index < messages.Count; index++)
                    AssertFalse(messages[index].Content!.StartsWith(ApiAgentRuntime.CompactedToolResultMarker, StringComparison.Ordinal),
                        "the most recent messages are never compacted");

                AssertTrue(messages.Count == 2 + (60 * 2), "no message is removed, so tool-call and tool-result pairing is intact");
            });

            await RunTest("Compaction is idempotent and stays under the hard ceiling", () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                ApiAgentRuntime.CompactConversation(messages);
                AssertEqual(0, ApiAgentRuntime.CompactConversation(messages), "a second pass finds nothing left to compact");
            });
        }

        private static List<ChatMessage> BuildConversation(int exchanges, int toolResultBytes)
        {
            List<ChatMessage> messages = new List<ChatMessage>();
            messages.Add(ChatMessage.System("system prompt"));
            messages.Add(ChatMessage.User("launch prompt"));
            for (int index = 0; index < exchanges; index++)
            {
                messages.Add(ChatMessage.Assistant("calling a tool"));
                messages.Add(ChatMessage.ToolResult("call_" + index, "read", new string('x', toolResultBytes)));
            }

            return messages;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }

            return count;
        }
    }
}
