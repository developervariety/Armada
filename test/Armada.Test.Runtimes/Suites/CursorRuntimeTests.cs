namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class CursorRuntimeTests : TestSuite
    {
        public override string Name => "Cursor Runtime Tests";

        private sealed class InspectableCursorRuntime : CursorRuntime
        {
            public InspectableCursorRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);

            public bool StdinEnabled() => UsePromptStdin;
        }

        private InspectableCursorRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableCursorRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecutablePath Default Is CursorAgent", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertEqual("cursor-agent", runtime.ExecutablePath);
            });

            await RunTest("BuildArguments Uses NonInteractive Text Output", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("--print", args[0]);
                AssertTrue(args.Contains("--force"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("text"));
            });

            await RunTest("BuildArguments Includes Model When Supplied", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gpt-5");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gpt-5", args[modelIndex + 1]);
            });

            await RunTest("Command Uses CursorAgent", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                string command = runtime.Command();
                AssertTrue(command.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase), "Expected cursor-agent command");
            });

            await RunTest("UsePromptStdin Is True", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertTrue(runtime.StdinEnabled(), "Cursor runtime must use stdin to avoid Windows cmd.exe length limit");
            });

            await RunTest("BuildArguments_LongPrompt_PromptNotInArguments", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                string longPrompt = new string('x', 16384);
                List<string> args = runtime.Args(longPrompt);
                foreach (string arg in args)
                {
                    AssertFalse(arg.Length > 1000, "No single argument should contain the long prompt; prompt must be sent via stdin");
                }
                AssertFalse(args.Contains(longPrompt), "Long prompt must not appear as a CLI argument");
            });
        }
    }
}
