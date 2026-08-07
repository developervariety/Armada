namespace Armada.Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;

    /// <summary>
    /// Runner-agnostic assertion helpers used by Touchstone test descriptors.
    /// A failed assertion throws an <see cref="AssertionException"/>; Touchstone (and any
    /// host adapter) treats a thrown exception as a failed test case. Import with
    /// <c>using static Armada.Test.Shared.Infrastructure.Asserts;</c> so ported suites can
    /// call these unqualified, matching the retired custom harness surface.
    /// </summary>
    public static class Asserts
    {
        #region Public-Methods

        /// <summary>
        /// Assert a condition is true; throw with the supplied message when false.
        /// </summary>
        /// <param name="condition">Condition to evaluate.</param>
        /// <param name="message">Message describing the expectation.</param>
        public static void Assert(bool condition, string message)
        {
            if (!condition) throw new AssertionException(message);
        }

        /// <summary>
        /// Assert two values are equal using the default equality comparer.
        /// </summary>
        /// <typeparam name="T">Value type.</typeparam>
        /// <param name="expected">Expected value.</param>
        /// <param name="actual">Actual value.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertEqual<T>(T expected, T actual, string? label = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new AssertionException((label != null ? label + ": " : "") +
                    "expected <" + expected + "> but got <" + actual + ">");
            }
        }

        /// <summary>
        /// Assert two values are not equal using the default equality comparer.
        /// </summary>
        /// <typeparam name="T">Value type.</typeparam>
        /// <param name="unexpected">Value the actual must not equal.</param>
        /// <param name="actual">Actual value.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertNotEqual<T>(T unexpected, T actual, string? label = null)
        {
            if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            {
                throw new AssertionException((label != null ? label + ": " : "") +
                    "values should not be equal but both were <" + actual + ">");
            }
        }

        /// <summary>
        /// Assert a value is not null.
        /// </summary>
        /// <param name="value">Value to check.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertNotNull(object? value, string? label = null)
        {
            if (value == null) throw new AssertionException((label ?? "Value") + " was null");
        }

        /// <summary>
        /// Assert a value is null.
        /// </summary>
        /// <param name="value">Value to check.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertNull(object? value, string? label = null)
        {
            if (value != null) throw new AssertionException((label ?? "Value") + " was not null (was: " + value + ")");
        }

        /// <summary>
        /// Assert a condition is true.
        /// </summary>
        /// <param name="condition">Condition to evaluate.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertTrue(bool condition, string? label = null)
        {
            if (!condition) throw new AssertionException((label ?? "Condition") + " was false");
        }

        /// <summary>
        /// Assert a condition is false.
        /// </summary>
        /// <param name="condition">Condition to evaluate.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertFalse(bool condition, string? label = null)
        {
            if (condition) throw new AssertionException((label ?? "Condition") + " was true");
        }

        /// <summary>
        /// Assert an HTTP response has the expected status code.
        /// </summary>
        /// <param name="expected">Expected status code.</param>
        /// <param name="response">HTTP response.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertStatusCode(HttpStatusCode expected, HttpResponseMessage response, string? label = null)
        {
            if (response == null) throw new AssertionException((label ?? "Response") + " was null");
            if (response.StatusCode != expected)
            {
                throw new AssertionException((label != null ? label + ": " : "") +
                    "expected " + (int)expected + " " + expected + " but got " + (int)response.StatusCode + " " + response.StatusCode);
            }
        }

        /// <summary>
        /// Assert a string starts with the expected prefix.
        /// </summary>
        /// <param name="expected">Expected prefix.</param>
        /// <param name="actual">Actual string.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertStartsWith(string expected, string actual, string? label = null)
        {
            if (actual == null || !actual.StartsWith(expected, StringComparison.Ordinal))
            {
                throw new AssertionException((label != null ? label + ": " : "") +
                    "expected to start with <" + expected + "> but got <" + actual + ">");
            }
        }

        /// <summary>
        /// Assert a string contains the expected substring.
        /// </summary>
        /// <param name="expected">Expected substring.</param>
        /// <param name="actual">Actual string.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertContains(string expected, string actual, string? label = null)
        {
            if (actual == null || !actual.Contains(expected, StringComparison.Ordinal))
            {
                throw new AssertionException((label != null ? label + ": " : "") +
                    "expected to contain <" + expected + "> but got <" + actual + ">");
            }
        }

        /// <summary>
        /// Assert that the given synchronous action throws the specified exception type.
        /// </summary>
        /// <typeparam name="TException">Expected exception type.</typeparam>
        /// <param name="action">Action expected to throw.</param>
        /// <param name="label">Optional label for context.</param>
        public static void AssertThrows<TException>(Action action, string? label = null) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new AssertionException((label != null ? label + ": " : "") +
                "expected " + typeof(TException).Name + " but no exception was thrown");
        }

        /// <summary>
        /// Assert that the given asynchronous action throws the specified exception type.
        /// </summary>
        /// <typeparam name="TException">Expected exception type.</typeparam>
        /// <param name="action">Async action expected to throw.</param>
        /// <param name="label">Optional label for context.</param>
        /// <returns>Task.</returns>
        public static async Task AssertThrowsAsync<TException>(Func<Task> action, string? label = null) where TException : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException)
            {
                return;
            }

            throw new AssertionException((label != null ? label + ": " : "") +
                "expected " + typeof(TException).Name + " but no exception was thrown");
        }

        #endregion
    }
}
