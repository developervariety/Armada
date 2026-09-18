namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using Armada.Runtimes.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// A failed tool call must say WHY it failed. The activity line is the only record an operator sees,
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
                AssertEqual("permission_denied", ApiAgentRuntime.ClassifyToolException(new UnauthorizedAccessException()));
                AssertEqual("not_found", ApiAgentRuntime.ClassifyToolException(new FileNotFoundException()));
                AssertEqual("not_found", ApiAgentRuntime.ClassifyToolException(new DirectoryNotFoundException()));
                AssertEqual("io_error", ApiAgentRuntime.ClassifyToolException(new IOException("disk")));
                AssertEqual("tool_failed", ApiAgentRuntime.ClassifyToolException(new InvalidOperationException("other")));
            });

            await RunTest("A path refused at the workspace boundary is not reported as a malformed payload", () =>
            {
                // The distinction that was missing: an absolute path outside the dock and a truncated
                // tool-call payload both reported invalid_arguments, so the log could not tell them apart.
                string boundary = ApiAgentRuntime.ClassifyToolException(new WorkspaceBoundaryException());
                string malformed = ApiAgentRuntime.ClassifyToolException(new JsonException("unterminated"));
                AssertFalse(String.Equals(boundary, malformed, StringComparison.Ordinal),
                    "a boundary refusal and a malformed payload must not share a class");
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
        }
    }
}
