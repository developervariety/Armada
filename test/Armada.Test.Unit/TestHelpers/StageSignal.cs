namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Waits for a fake collaborator to report that the operation under test has reached a stage.
    /// A test that moves a controlled clock must do so only after that stage has started, and waiting
    /// on the stage's own signal replaces guessing at an elapsed time.
    /// </summary>
    public static class StageSignal
    {
        #region Public-Members

        /// <summary>
        /// Failure backstop for a stage that never starts. It bounds a broken run; it is not a pacing delay.
        /// </summary>
        public static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Complete when <paramref name="stageStarted"/> completes. Fails with the operation's own
        /// exception, or with a named reason, when the operation ends first or the stage never starts.
        /// </summary>
        /// <param name="stageStarted">Signal the fake raises when the stage begins.</param>
        /// <param name="operation">The operation under test.</param>
        /// <param name="stage">Stage name used in failure messages.</param>
        /// <returns>Task that completes when the stage has started.</returns>
        public static async Task WaitForAsync(Task stageStarted, Task operation, string stage)
        {
            if (stageStarted == null) throw new ArgumentNullException(nameof(stageStarted));
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Task backstop = Task.Delay(Backstop);
            Task first = await Task.WhenAny(stageStarted, operation, backstop).ConfigureAwait(false);
            if (first == stageStarted) return;
            if (first == operation)
            {
                await operation.ConfigureAwait(false);
                throw new InvalidOperationException("The operation finished before the " + stage + " started.");
            }

            throw new TimeoutException("The " + stage + " did not start within " + Backstop.TotalSeconds + "s.");
        }

        #endregion
    }
}
