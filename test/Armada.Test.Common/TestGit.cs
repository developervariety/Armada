namespace Armada.Test.Common
{
    /// <summary>Git commands for isolated executable test fixtures.</summary>
    public static class TestGit
    {
        /// <summary>Initialize and commit the files already prepared in a fixture directory.</summary>
        public static async Task InitializeAsync(string directory)
        {
            await RunAsync(directory, "init", "--initial-branch=main").ConfigureAwait(false);
            await CommitAsync(directory).ConfigureAwait(false);
        }

        /// <summary>Commit fixture files with local author settings and signing disabled.</summary>
        public static async Task CommitAsync(string directory)
        {
            await RunAsync(directory, "add", ".").ConfigureAwait(false);
            await RunAsync(directory, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Fixture state").ConfigureAwait(false);
        }

        /// <summary>Run Git with separate arguments and fail on a nonzero exit code.</summary>
        public static async Task<string> RunAsync(string directory, params string[] arguments)
        {
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Cannot start fixture Git"))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string outputText = await output.ConfigureAwait(false);
                string errorText = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Git failed: " + errorText);
                return outputText.Trim();
            }
        }

    }
}
