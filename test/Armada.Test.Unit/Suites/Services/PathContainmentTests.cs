namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins the shared path containment rule: a path is inside a root when it is the root itself or lies
    /// below the root plus a directory separator, so a sibling whose name starts with the root's name is
    /// outside, and a <c>..</c> escape is outside.
    /// </summary>
    public sealed class PathContainmentTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Path Containment";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "armada_containment_" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(baseDirectory, "workspace");
            string sep = Path.DirectorySeparatorChar.ToString();

            await RunTest("IsWithin accepts the root itself and a path below it", () =>
            {
                AssertTrue(PathContainment.IsWithin(root, root), "the root itself is inside");
                AssertTrue(PathContainment.IsWithin(root, root + sep), "the root with a trailing separator is inside");
                AssertTrue(PathContainment.IsWithin(root, Path.Combine(root, "a", "b.txt")), "a nested child is inside");
                AssertTrue(PathContainment.IsWithin(root + sep, Path.Combine(root, "a.txt")), "a root given with a trailing separator contains its child");
                AssertTrue(PathContainment.IsWithin(root, Path.Combine(root, "a", "..", "b.txt")), "a traversal that stays inside is inside");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("IsWithin refuses a sibling that shares the root's name prefix", () =>
            {
                AssertFalse(PathContainment.IsWithin(root, Path.Combine(baseDirectory, "workspace-backup", "x.txt")), "a hyphenated sibling is outside");
                AssertFalse(PathContainment.IsWithin(root, Path.Combine(baseDirectory, "workspaceX")), "a longer sibling name is outside");
                AssertFalse(PathContainment.IsWithin(root + sep, Path.Combine(baseDirectory, "workspace-backup")), "a trailing separator on the root does not admit the sibling");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("IsWithin refuses a dot-dot escape and a case-only difference", () =>
            {
                AssertFalse(PathContainment.IsWithin(root, Path.Combine(root, "..", "secret.txt")), "the parent directory is outside");
                AssertFalse(PathContainment.IsWithin(root, baseDirectory), "the root's parent is outside");
                AssertFalse(PathContainment.IsWithin(root, Path.Combine(baseDirectory, "WORKSPACE", "a.txt")), "a case-only difference is refused on every platform");
                AssertFalse(PathContainment.IsWithin(root, ""), "an empty path is outside");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("TryResolve returns the full path only inside the root", () =>
            {
                AssertEqual(Path.Combine(root, "a", "b.txt"), PathContainment.TryResolve(root, Path.Combine("a", "b.txt")), "a relative child resolves");
                AssertEqual(root, PathContainment.TryResolve(root, "."), "the root itself resolves");
                AssertEqual(Path.Combine(root, "b.txt"), PathContainment.TryResolve(root + sep, "b.txt"), "a trailing-separator root resolves the same way");
                AssertEqual(root, PathContainment.TryResolve(root, Path.Combine("..", "workspace")), "leaving and re-entering the root resolves to the root");
                AssertNull(PathContainment.TryResolve(root, Path.Combine("..", "workspace-backup", "x.txt")), "a sibling-prefix traversal is refused");
                AssertNull(PathContainment.TryResolve(root, Path.Combine("..", "secret.txt")), "a dot-dot escape is refused");
                AssertNull(PathContainment.TryResolve(root, Path.Combine(baseDirectory, "workspace-backup", "x.txt")), "an absolute sibling-prefix path is refused");
                AssertEqual(Path.Combine(root, "x.txt"), PathContainment.TryResolve(root, Path.Combine(root, "x.txt")), "an absolute path inside the root resolves");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
