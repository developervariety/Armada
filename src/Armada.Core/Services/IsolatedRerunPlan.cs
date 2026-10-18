namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// How the definition-of-done gate re-runs a red suite's failing tests alone, or the named reason it
    /// cannot. One rule serves the vessel, flake-score and consumer re-runs, each with its own workflow
    /// profile. A profile that sets <c>IsolatedTestRerunCommand</c> re-runs through that command, and the
    /// failing names reach it only through the <see cref="NamesEnvironmentVariable"/> environment variable,
    /// never through the command text. Without that field, a <c>dotnet test</c> command is narrowed to the
    /// failing classes by <see cref="FlakeRerunCommand"/>; any other command has no re-run route.
    /// </summary>
    public sealed class IsolatedRerunPlan
    {
        #region Public-Members

        /// <summary>
        /// Environment variable that carries the failing test names to a profile's isolated re-run command,
        /// one name per line.
        /// </summary>
        public const string NamesEnvironmentVariable = "ARMADA_RERUN_TESTS";

        /// <summary>
        /// Reason recorded when failing names were read but the profile has no isolated re-run command and
        /// the test command is not a <c>dotnet test</c> command the gate can narrow.
        /// </summary>
        public const string NoRerunRouteReason = "the failing test names were read, but the workflow profile sets no "
            + "IsolatedTestRerunCommand and the test command is not a dotnet test command that can be narrowed";

        /// <summary>
        /// Reason recorded when no test class can be derived from the failing names of a <c>dotnet test</c> command.
        /// </summary>
        public const string NoClassReason = "no test class could be derived from the failing test names";

        /// <summary>
        /// Reason recorded when a <c>dotnet test</c> command cannot be narrowed to the failing classes.
        /// </summary>
        public const string NoCommandReason = "no isolated command could be formed from the test command";

        /// <summary>
        /// The isolated re-run command, or null when there is none.
        /// </summary>
        public string? Command { get; private set; } = null;

        /// <summary>
        /// Extra environment for the re-run command. Empty unless the command comes from the profile.
        /// </summary>
        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();

        /// <summary>
        /// How many tests or classes the re-run covers.
        /// </summary>
        public int Count { get; private set; } = 0;

        /// <summary>
        /// What <see cref="Count"/> counts, for messages: "test(s)" or "class(es)".
        /// </summary>
        public string Unit { get; private set; } = "class(es)";

        /// <summary>
        /// True when the command comes from the profile's <c>IsolatedTestRerunCommand</c>.
        /// </summary>
        public bool FromProfile { get; private set; } = false;

        /// <summary>
        /// Why the failing tests cannot be re-run alone, when <see cref="Command"/> is null.
        /// </summary>
        public string? NotRerunReason { get; private set; } = null;

        /// <summary>
        /// True when the failing names give the gate nothing to re-run, before any command is formed: no
        /// re-run route, no derivable class, or more profile-path names than the bound. A flake-score
        /// decision is not worth asking for then.
        /// </summary>
        public bool NothingToRerun { get; private set; } = false;

        #endregion

        #region Constructors-and-Factories

        private IsolatedRerunPlan()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Plan the isolated re-run of a red suite.
        /// </summary>
        /// <param name="profile">The workflow profile of the suite that failed (the consumer's own for a consumer suite).</param>
        /// <param name="testCommand">The test command that failed.</param>
        /// <param name="failingNames">The complete set of failing test names.</param>
        /// <param name="maxCount">Largest number of names (profile command) or classes (dotnet test) to re-run.</param>
        /// <param name="boundClasses">False to skip the bound on the dotnet test class path.</param>
        /// <returns>The plan.</returns>
        public static IsolatedRerunPlan Build(
            WorkflowProfile? profile,
            string testCommand,
            IReadOnlyList<string> failingNames,
            int maxCount,
            bool boundClasses = true)
        {
            if (failingNames == null) throw new ArgumentNullException(nameof(failingNames));
            IsolatedRerunPlan plan = new IsolatedRerunPlan();

            string? profileCommand = String.IsNullOrWhiteSpace(profile?.IsolatedTestRerunCommand)
                ? null
                : profile!.IsolatedTestRerunCommand!.Trim();
            if (profileCommand != null)
            {
                plan.FromProfile = true;
                plan.Unit = "test(s)";
                plan.Count = failingNames.Count;
                if (failingNames.Count > maxCount)
                {
                    plan.NothingToRerun = true;
                    plan.NotRerunReason = failingNames.Count + " failing tests exceed the bound of " + maxCount;
                    return plan;
                }

                plan.Command = profileCommand;
                plan.Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [NamesEnvironmentVariable] = String.Join("\n", failingNames)
                };
                return plan;
            }

            if (String.IsNullOrWhiteSpace(testCommand) || testCommand.IndexOf("dotnet test", StringComparison.OrdinalIgnoreCase) < 0)
            {
                plan.NothingToRerun = true;
                plan.NotRerunReason = NoRerunRouteReason;
                return plan;
            }

            IReadOnlyList<string> classNames = FlakeRerunCommand.DeriveClassNames(failingNames);
            plan.Count = classNames.Count;
            if (classNames.Count == 0)
            {
                plan.NothingToRerun = true;
                plan.NotRerunReason = NoClassReason;
                return plan;
            }

            if (boundClasses && classNames.Count > maxCount)
            {
                plan.NotRerunReason = classNames.Count + " failing classes exceed the bound of " + maxCount;
                return plan;
            }

            if (!FlakeRerunCommand.TryBuild(testCommand, classNames, out string filteredCommand))
            {
                plan.NotRerunReason = NoCommandReason;
                return plan;
            }

            plan.Command = filteredCommand;
            return plan;
        }

        #endregion
    }
}
