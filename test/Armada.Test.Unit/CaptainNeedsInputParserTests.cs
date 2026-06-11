namespace Armada.Test.Unit
{
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for CaptainNeedsInputParser.
    /// </summary>
    public class CaptainNeedsInputParserTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Needs Input Parser";

        /// <summary>Run parser tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Parse_SoftMarker_ReturnsSoftFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "Some output\n[ARMADA:NEEDS-INPUT soft] Should I continue?\nMore output");
                AssertTrue(result.Found, "Found should be true for soft marker");
                AssertFalse(result.Malformed, "Malformed should be false for valid marker");
                AssertEqual(NeedsInputModeEnum.Soft, result.Mode, "Mode should be Soft");
                AssertEqual("Should I continue?", result.QuestionText, "QuestionText should be extracted");
                return Task.CompletedTask;
            });

            await RunTest("Parse_BlockMarker_ReturnsBlockFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT block] Which branch should I target?");
                AssertTrue(result.Found, "Found should be true for block marker");
                AssertFalse(result.Malformed, "Malformed should be false for valid marker");
                AssertEqual(NeedsInputModeEnum.Block, result.Mode, "Mode should be Block");
                AssertEqual("Which branch should I target?", result.QuestionText, "QuestionText should be extracted");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MalformedMarker_ReturnsMalformed", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT unknown] something");
                AssertTrue(result.Found, "Found should be true for malformed marker");
                AssertTrue(result.Malformed, "Malformed should be true for bad mode token");
                return Task.CompletedTask;
            });

            await RunTest("Parse_AbsentMarker_ReturnsNotFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "Normal agent output without any marker.");
                AssertFalse(result.Found, "Found should be false when no marker is present");
                AssertFalse(result.Malformed, "Malformed should be false when no marker is present");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NullInput_ReturnsNotFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(null);
                AssertFalse(result.Found, "Found should be false for null input");
                return Task.CompletedTask;
            });

            await RunTest("Parse_WhitespaceQuestion_ReturnsEmptyQuestionText", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT soft]    ");
                AssertTrue(result.Found, "Found should be true");
                AssertFalse(result.Malformed, "Malformed should be false");
                AssertEqual(NeedsInputModeEnum.Soft, result.Mode, "Mode should be Soft");
                AssertEqual("", result.QuestionText, "QuestionText should be empty after trimming");
                return Task.CompletedTask;
            });

            await RunTest("Parse_CaseInsensitiveMode_Parsed", () =>
            {
                CaptainNeedsInputRequest softUpper = CaptainNeedsInputParser.Parse("[ARMADA:NEEDS-INPUT SOFT] test");
                CaptainNeedsInputRequest blockMixed = CaptainNeedsInputParser.Parse("[ARMADA:NEEDS-INPUT Block] test");
                AssertEqual(NeedsInputModeEnum.Soft, softUpper.Mode, "SOFT uppercase should parse as Soft");
                AssertEqual(NeedsInputModeEnum.Block, blockMixed.Mode, "Block mixed-case should parse as Block");
                return Task.CompletedTask;
            });

            await RunTest("Parse_LastMarkerWins_WhenMultiplePresent", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT soft] first question\n[ARMADA:NEEDS-INPUT block] second question");
                AssertEqual(NeedsInputModeEnum.Block, result.Mode, "Last marker should win");
                AssertEqual("second question", result.QuestionText, "Last question text should be used");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EmptyString_ReturnsNotFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse("");
                AssertFalse(result.Found, "Found should be false for empty input");
                AssertFalse(result.Malformed, "Malformed should be false for empty input");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MarkerMidLine_NotFound", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "The captain wrote [ARMADA:NEEDS-INPUT block] inline in prose.");
                AssertFalse(result.Found, "Marker embedded mid-line should not match");
                AssertFalse(result.Malformed, "Mid-line marker should not be treated as malformed");
                return Task.CompletedTask;
            });

            await RunTest("Parse_LeadingWhitespaceMarker_Found", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "preamble\n   [ARMADA:NEEDS-INPUT block] Indented question?");
                AssertTrue(result.Found, "Marker with leading whitespace should match");
                AssertEqual(NeedsInputModeEnum.Block, result.Mode, "Mode should be Block");
                AssertEqual("Indented question?", result.QuestionText, "Question should be extracted after indentation");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MissingModeToken_ReturnsMalformed", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT] no mode given");
                AssertTrue(result.Found, "Marker without a mode token should be detected");
                AssertTrue(result.Malformed, "Marker without a mode token should be malformed");
                return Task.CompletedTask;
            });

            await RunTest("Parse_QuestionStopsAtLineEnd", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT block] only this line\r\nThis continuation must not be captured.");
                AssertTrue(result.Found, "Marker should be found");
                AssertEqual("only this line", result.QuestionText, "Question should stop at the end of the marker line, with CRLF trimmed");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MalformedAfterValid_ValidStillWins", () =>
            {
                CaptainNeedsInputRequest result = CaptainNeedsInputParser.Parse(
                    "[ARMADA:NEEDS-INPUT block] real question\n[ARMADA:NEEDS-INPUT bogus] junk");
                AssertTrue(result.Found, "A valid marker should be found");
                AssertFalse(result.Malformed, "Valid marker should win over a later malformed one");
                AssertEqual(NeedsInputModeEnum.Block, result.Mode, "Mode should come from the valid marker");
                AssertEqual("real question", result.QuestionText, "Question should come from the valid marker");
                return Task.CompletedTask;
            });
        }
    }
}
