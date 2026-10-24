namespace Armada.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Runtimes.Harbor;
    using SyslogLogging;

    /// <summary>Console entry point for the Harbor link and its independent job supervisors.</summary>
    public static class Program
    {
        /// <summary>Run the configured Harbor link or one private supervisor child.</summary>
        /// <param name="args">The configuration path, or the supervisor mode selector.</param>
        /// <returns>Zero on orderly shutdown; nonzero on an invalid or failed start.</returns>
        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--supervise-job")
                {
                    string envelope = await HarborConsoleConfiguration.ReadBoundedAsync(Console.In, 8 * 1024 * 1024).ConfigureAwait(false);
                    HarborSupervisorStart start = JsonSerializer.Deserialize<HarborSupervisorStart>(envelope,
                        HarborConsoleConfiguration.JsonOptions) ?? throw new ArgumentException("harbor_supervisor_request_invalid");
                    return await HarborJobSupervisor.RunAsync(start, CancellationToken.None).ConfigureAwait(false);
                }
                if (args.Length != 2 || args[0] != "--config")
                {
                    Console.Error.WriteLine("Usage: Armada.Harbor --config <private-settings-file>");
                    return 2;
                }
                HarborRunnerSettings settings = await HarborConsoleConfiguration.LoadAsync(args[1]).ConfigureAwait(false);
                settings.Validate();
                SelfCommand(out string executable, out List<string> arguments);
                using (CancellationTokenSource stop = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler handler = (sender, cancel) => { cancel.Cancel = true; stop.Cancel(); };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        HarborRunnerClient client = new HarborRunnerClient(settings, executable, arguments,
                            message => Console.Error.WriteLine(message), new GitService(new LoggingModule()));
                        await client.RunAsync(stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Console.CancelKeyPress -= handler;
                    }
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (HarborJobJournalException exception)
            {
                Console.Error.WriteLine(exception.ReasonCode);
                return 1;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is JsonException
                || exception is IOException || exception is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("harbor_runner_configuration_or_storage_invalid");
                return 2;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("harbor_runner_failed");
                return 1;
            }
        }

        private static void SelfCommand(out string executable, out List<string> arguments)
        {
            executable = Environment.ProcessPath ?? throw new InvalidOperationException("harbor_process_path_unavailable");
            arguments = new List<string>();
            string name = Path.GetFileNameWithoutExtension(executable);
            if (String.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
                arguments.Add(Assembly.GetExecutingAssembly().Location);
        }
    }
}
