namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Pins the cursor-agent launch shape: the flags a non-interactive dock run needs so the
    /// captain actually receives the dock's MCP servers, not only discovers them.
    /// </summary>
    public class CursorLaunchArgumentsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Cursor Launch Arguments";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Cursor launch approves workspace MCP servers and trusts the workspace", () =>
            {
                CursorRuntime runtime = new CursorRuntime(new LoggingModule { Settings = { EnableConsole = false } });
                List<string> args = InvokeBuildArguments(runtime, "composer-2.5");
                AssertTrue(args.Contains("--print"), "non-interactive print mode");
                AssertTrue(args.Contains("--trust"), "workspace trusted without a prompt");
                AssertTrue(args.Contains("--approve-mcps"), "workspace MCP servers approved, so the dock's Armada server loads");
                AssertTrue(args.Contains("--force"), "commands allowed unless denied");
                int format = args.IndexOf("--output-format");
                AssertTrue(format >= 0 && format + 1 < args.Count && args[format + 1] == "stream-json", "stream-json output");
                int model = args.IndexOf("--model");
                AssertTrue(model >= 0 && args[model + 1] == "composer-2.5", "model forwarded");
                return Task.CompletedTask;
            });

            await RunTest("Cursor launch omits --model when none is set", () =>
            {
                CursorRuntime runtime = new CursorRuntime(new LoggingModule { Settings = { EnableConsole = false } });
                List<string> args = InvokeBuildArguments(runtime, null);
                AssertFalse(args.Contains("--model"), "no model flag without a model");
                AssertTrue(args.Contains("--approve-mcps"), "approval flag is unconditional");
                return Task.CompletedTask;
            });
        }

        private static List<string> InvokeBuildArguments(CursorRuntime runtime, string? model)
        {
            MethodInfo? method = typeof(CursorRuntime).GetMethod("BuildArguments", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Could not find BuildArguments.");
            List<string>? args = method.Invoke(runtime, new object?[] { "/tmp", "prompt", model, null, null }) as List<string>;
            return args ?? new List<string>();
        }
    }
}
