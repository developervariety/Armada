namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the Layer 1/3/5 settings fields added in V2 Index M1: clamp ranges,
    /// null-string coalescing, and configured defaults.
    /// </summary>
    public class CodeIndexSettingsClampTests : TestSuite
    {
        public override string Name => "Code Index Settings Clamps";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_NewFields_MatchConfiguredValues", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();

                AssertFalse(settings.UseSemanticSearch, "UseSemanticSearch default must be false");
                AssertEqual("deepseek-embedding", settings.EmbeddingModel);
                AssertEqual("https://api.deepseek.com", settings.EmbeddingApiBaseUrl);
                AssertEqual(string.Empty, settings.EmbeddingApiKey);
                AssertEqual(0.7, settings.SemanticWeight);
                AssertEqual(0.3, settings.LexicalWeight);

                AssertFalse(settings.UseSummarizer, "UseSummarizer default must be false");
                AssertEqual("deepseek-chat", settings.SummarizerModel);
                AssertEqual(string.Empty, settings.SummarizerApiBaseUrl);
                AssertEqual(string.Empty, settings.SummarizerApiKey);
                AssertEqual(2048, settings.MaxSummaryOutputTokens);

                AssertFalse(settings.UseFileSignatures, "UseFileSignatures default must be false");
                AssertEqual(string.Empty, settings.SignatureModel);
                AssertEqual(0.2, settings.FileSignatureBoostWeight);
            });

            await RunTest("SemanticWeight_AboveOne_ClampsToOne", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SemanticWeight = 5.0;
                AssertEqual(1.0, settings.SemanticWeight);
            });

            await RunTest("SemanticWeight_BelowZero_ClampsToZero", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SemanticWeight = -2.5;
                AssertEqual(0.0, settings.SemanticWeight);
            });

            await RunTest("SemanticWeight_WithinRange_AssignedExactly", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SemanticWeight = 0.42;
                AssertEqual(0.42, settings.SemanticWeight);
            });

            await RunTest("LexicalWeight_AboveOne_ClampsToOne", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.LexicalWeight = 1.5;
                AssertEqual(1.0, settings.LexicalWeight);
            });

            await RunTest("LexicalWeight_BelowZero_ClampsToZero", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.LexicalWeight = -0.1;
                AssertEqual(0.0, settings.LexicalWeight);
            });

            await RunTest("MaxSummaryOutputTokens_BelowMinimum_ClampsTo256", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxSummaryOutputTokens = 0;
                AssertEqual(256, settings.MaxSummaryOutputTokens);
            });

            await RunTest("MaxSummaryOutputTokens_AboveMaximum_ClampsTo8192", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxSummaryOutputTokens = 1_000_000;
                AssertEqual(8192, settings.MaxSummaryOutputTokens);
            });

            await RunTest("MaxSummaryOutputTokens_WithinRange_AssignedExactly", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxSummaryOutputTokens = 1024;
                AssertEqual(1024, settings.MaxSummaryOutputTokens);
            });

            await RunTest("FileSignatureBoostWeight_AboveOne_ClampsToOne", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.FileSignatureBoostWeight = 2.0;
                AssertEqual(1.0, settings.FileSignatureBoostWeight);
            });

            await RunTest("FileSignatureBoostWeight_BelowZero_ClampsToZero", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.FileSignatureBoostWeight = -0.5;
                AssertEqual(0.0, settings.FileSignatureBoostWeight);
            });

            await RunTest("EmbeddingModel_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.EmbeddingModel = null!;
                AssertEqual(string.Empty, settings.EmbeddingModel);
            });

            await RunTest("EmbeddingApiBaseUrl_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.EmbeddingApiBaseUrl = null!;
                AssertEqual(string.Empty, settings.EmbeddingApiBaseUrl);
            });

            await RunTest("EmbeddingApiKey_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.EmbeddingApiKey = null!;
                AssertEqual(string.Empty, settings.EmbeddingApiKey);
            });

            await RunTest("SummarizerModel_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SummarizerModel = null!;
                AssertEqual(string.Empty, settings.SummarizerModel);
            });

            await RunTest("SummarizerApiBaseUrl_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SummarizerApiBaseUrl = null!;
                AssertEqual(string.Empty, settings.SummarizerApiBaseUrl);
            });

            await RunTest("SummarizerApiKey_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SummarizerApiKey = null!;
                AssertEqual(string.Empty, settings.SummarizerApiKey);
            });

            await RunTest("SignatureModel_NullAssignment_CoalescesToEmptyString", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.SignatureModel = null!;
                AssertEqual(string.Empty, settings.SignatureModel);
            });

            await RunTest("ClampBoundaries_ExactEndpointsAreAccepted", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();

                settings.SemanticWeight = 0.0;
                AssertEqual(0.0, settings.SemanticWeight);
                settings.SemanticWeight = 1.0;
                AssertEqual(1.0, settings.SemanticWeight);

                settings.LexicalWeight = 0.0;
                AssertEqual(0.0, settings.LexicalWeight);
                settings.LexicalWeight = 1.0;
                AssertEqual(1.0, settings.LexicalWeight);

                settings.FileSignatureBoostWeight = 0.0;
                AssertEqual(0.0, settings.FileSignatureBoostWeight);
                settings.FileSignatureBoostWeight = 1.0;
                AssertEqual(1.0, settings.FileSignatureBoostWeight);

                settings.MaxSummaryOutputTokens = 256;
                AssertEqual(256, settings.MaxSummaryOutputTokens);
                settings.MaxSummaryOutputTokens = 8192;
                AssertEqual(8192, settings.MaxSummaryOutputTokens);
            });
        }
    }
}
