namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins occupant listing, the git holder-path parse, and releasing a detached child
    /// whose cwd is the dock — the live-lock that kept a finished stage's worktree registered.
    /// </summary>
    public sealed class DockPathOccupantsTests : TestSuite
    {
        public override string Name => "Dock Path Occupants";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ParseHolderPath reads git's already-used worktree line", () =>
            {
                string? path = DockPathOccupants.ParseHolderPath(
                    "git failed (exit 128): fatal: 'armada/c/msn_one' is already used by worktree at '/tmp/docks/ExampleVessel/msn_prev/ExampleVessel'");
                AssertEqual("/tmp/docks/ExampleVessel/msn_prev/ExampleVessel", path);
                AssertNull(DockPathOccupants.ParseHolderPath("unrelated git error"));
                AssertNull(DockPathOccupants.ParseHolderPath(null));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Release kills a detached child whose cwd is the dock", () =>
            {
                if (!OperatingSystem.IsLinux())
                {
                    SkipTest("Release kills a detached child whose cwd is the dock", "occupant cwd inspection uses /proc and is Linux-only");
                    return Task.CompletedTask;
                }

                string dir = Path.Combine(Path.GetTempPath(), "armada-occupants-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                Process? child = null;
                try
                {
                    child = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/sleep",
                        Arguments = "30",
                        WorkingDirectory = dir,
                        UseShellExecute = false
                    });
                    AssertNotNull(child, "sleep should start");
                    // /proc/pid/cwd is visible once the process is running.
                    SpinWait.SpinUntil(() => DockPathOccupants.ListPids(dir).Contains(child!.Id), 2000);
                    AssertTrue(DockPathOccupants.ListPids(dir).Contains(child!.Id), "the sleep cwd must be listed as an occupant");

                    List<int> released = DockPathOccupants.Release(dir);
                    AssertTrue(released.Contains(child.Id), "Release must signal the occupant");
                    AssertTrue(child.WaitForExit(3000), "the occupant must exit after Release");
                    AssertEqual(0, DockPathOccupants.ListPids(dir).Count, "no occupant may remain");
                }
                finally
                {
                    try { if (child != null && !child.HasExited) child.Kill(entireProcessTree: true); } catch { }
                    try { Directory.Delete(dir, true); } catch { }
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
