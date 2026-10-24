namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class CheckRunService
    {
        private async Task ReleaseHarborCheckDockAsync(CheckRun run, HarborCheckExecution? execution, CancellationToken token)
        {
            if (execution == null) return;
            bool stopped = execution.Result.ExitCode.HasValue
                && (!execution.Result.TimedOut && !execution.Result.Cancelled || execution.Result.KillConfirmed);
            if (!stopped)
            {
                AppendCleanupNote(run, "harbor_auxiliary_cleanup_held: the verification process stop is unconfirmed.");
                return;
            }
            try
            {
                await HarborCheckouts!.ReleaseAuxiliaryAsync(execution.Dock, "check:" + run.Id + ":owned", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendCleanupNote(run, "harbor_auxiliary_cleanup_failed: " + exception.Message);
            }
        }

        private static void AppendCleanupNote(CheckRun run, string note)
        {
            run.Summary = String.IsNullOrWhiteSpace(run.Summary) ? note : run.Summary + " " + note;
            run.Output = String.IsNullOrEmpty(run.Output) ? note : run.Output + Environment.NewLine + note;
        }
    }
}
