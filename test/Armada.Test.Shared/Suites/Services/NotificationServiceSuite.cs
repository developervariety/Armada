namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="NotificationService"/>: best-effort desktop notifications and
    /// the terminal bell. Every path is wrapped in a swallow-all try/catch, so the contract is
    /// simply "never throws". Positive cases exercise the bell and well-formed sends; negative
    /// cases exercise empty and null inputs (the audit adds the null-string boundary the legacy
    /// suite skipped, confirmed against the swallow-all source).
    /// </summary>
    public sealed class NotificationServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the NotificationService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("bell_does_not_throw", "Bell DoesNotThrow", TestTags.Positive, () =>
            {
                NotificationService.Bell();
            }));

            cases.Add(Case("send_does_not_throw", "Send DoesNotThrow", TestTags.Positive, () =>
            {
                NotificationService.Send("Test Title", "Test Message");
            }));

            cases.Add(Case("send_with_special_characters_does_not_throw", "Send WithSpecialCharacters DoesNotThrow", TestTags.Positive, () =>
            {
                NotificationService.Send("Test's \"Title\"", "Message with 'quotes' and \"doubles\"");
            }));

            cases.Add(Case("send_empty_strings_does_not_throw", "Send EmptyStrings DoesNotThrow", TestTags.Negative, () =>
            {
                NotificationService.Send("", "");
            }));

            // Audit addition: null inputs are swallowed by the best-effort try/catch (confirmed against source)

            cases.Add(Case("send_null_strings_does_not_throw", "Send NullStrings DoesNotThrow", TestTags.Negative, () =>
            {
                NotificationService.Send(null!, null!);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.NotificationService",
                displayName: "Notification Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.NotificationService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.NotificationService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
