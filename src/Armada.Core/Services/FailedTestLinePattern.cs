namespace Armada.Core.Services
{
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The rule for a workflow profile's <c>FailedTestLinePattern</c>: a .NET regular expression with a named
    /// group <c>name</c> that reads one failing test name from one line of a test runner's output. A profile
    /// save validates the pattern with <see cref="Validate"/>, and the definition-of-done gate builds the
    /// runnable expression with <see cref="TryCreate"/>, so both apply the same rule. Every match runs under
    /// <see cref="MatchTimeout"/>, so a pattern that backtracks without end cannot hang the gate.
    /// </summary>
    public static class FailedTestLinePattern
    {
        #region Public-Members

        /// <summary>
        /// The named group that carries the failing test name.
        /// </summary>
        public const string NameGroup = "name";

        /// <summary>
        /// How long one match against one output line may run before it is abandoned.
        /// </summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a pattern as a profile save stores it.
        /// </summary>
        /// <param name="pattern">The pattern, or null or blank when the profile sets none.</param>
        /// <returns>Null when the pattern is absent or valid; otherwise the named error.</returns>
        public static string? Validate(string? pattern)
        {
            if (String.IsNullOrWhiteSpace(pattern)) return null;
            TryCreate(pattern, MatchTimeout, out Regex? _, out string? error);
            return error;
        }

        /// <summary>
        /// Build the runnable expression for a pattern.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="matchTimeout">How long one match may run.</param>
        /// <param name="regex">The expression, when the pattern is valid.</param>
        /// <param name="error">The named error, when the pattern is not valid.</param>
        /// <returns>True when the pattern is valid.</returns>
        public static bool TryCreate(string? pattern, TimeSpan matchTimeout, out Regex? regex, out string? error)
        {
            regex = null;
            error = null;
            if (String.IsNullOrWhiteSpace(pattern))
            {
                error = "FailedTestLinePattern is empty.";
                return false;
            }

            Regex candidate;
            try
            {
                candidate = new Regex(pattern, RegexOptions.CultureInvariant, matchTimeout);
            }
            catch (ArgumentException ex)
            {
                error = "FailedTestLinePattern is not a valid regular expression: " + ex.Message;
                return false;
            }

            if (!candidate.GetGroupNames().Contains(NameGroup, StringComparer.Ordinal))
            {
                error = "FailedTestLinePattern has no named group '" + NameGroup + "'; write the test name as (?<" + NameGroup + ">...).";
                return false;
            }

            regex = candidate;
            return true;
        }

        #endregion
    }
}
