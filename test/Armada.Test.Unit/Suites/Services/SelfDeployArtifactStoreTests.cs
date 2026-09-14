namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Immutable content-addressed candidate and rollback artifacts.
    /// </summary>
    public sealed class SelfDeployArtifactStoreTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Artifact Store";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CaptureAsync_CopiesIntoReadOnlyDigestDirectory", async () =>
            {
                if (SkipWindows("CaptureAsync_CopiesIntoReadOnlyDigestDirectory")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    string source = CreateSource(directory, "one");
                    SelfDeployReleaseArtifact artifact = await store.CaptureAsync(source, "Armada.Server.dll");
                    AssertEqual(artifact.Digest, Path.GetFileName(artifact.Directory), "directory named by digest");
                    AssertEqual(3, artifact.FileCount, "all files captured");
                    AssertEqual(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Combine(artifact.Directory, "Armada.Server.dll")), "file is read-only");
                    AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(artifact.Directory), "directory is read-only");
                    AssertNull(await store.VerifyAsync(artifact), "fresh artifact verifies");
                }
            });

            await RunTest("CaptureAsync_SameContentTwice_ReusesTheImmutableArtifact", async () =>
            {
                if (SkipWindows("CaptureAsync_SameContentTwice_ReusesTheImmutableArtifact")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    string source = CreateSource(directory, "same");
                    SelfDeployReleaseArtifact first = await store.CaptureAsync(source, "Armada.Server.dll");
                    DateTime firstWrite = File.GetLastWriteTimeUtc(Path.Combine(first.Directory, "Armada.Server.dll"));
                    SelfDeployReleaseArtifact second = await store.CaptureAsync(source, "Armada.Server.dll");
                    AssertEqual(first.Directory, second.Directory, "same directory");
                    AssertEqual(firstWrite, File.GetLastWriteTimeUtc(Path.Combine(second.Directory, "Armada.Server.dll")), "existing artifact not rewritten");
                }
            });

            await RunTest("CaptureAsync_LaterSourceChange_DoesNotChangeCapturedArtifact", async () =>
            {
                if (SkipWindows("CaptureAsync_LaterSourceChange_DoesNotChangeCapturedArtifact")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    string source = CreateSource(directory, "before");
                    SelfDeployReleaseArtifact artifact = await store.CaptureAsync(source, "Armada.Server.dll");
                    File.WriteAllText(Path.Combine(source, "Armada.Server.dll"), "rebuilt output");
                    AssertNull(await store.VerifyAsync(artifact), "captured artifact still verifies");
                    AssertEqual("server before", File.ReadAllText(Path.Combine(artifact.Directory, "Armada.Server.dll")), "captured bytes unchanged");
                }
            });

            await RunTest("VerifyAsync_ModifiedOrAddedFile_ReportsDigestMismatch", async () =>
            {
                if (SkipWindows("VerifyAsync_ModifiedOrAddedFile_ReportsDigestMismatch")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    SelfDeployReleaseArtifact modified = await store.CaptureAsync(CreateSource(directory, "modified"), "Armada.Server.dll");
                    SelfDeployTestDirectory.MakeWritable(modified.Directory);
                    File.AppendAllText(Path.Combine(modified.Directory, "Armada.Server.dll"), "x");
                    AssertEqual("artifact_digest_mismatch", await store.VerifyAsync(modified), "modified file detected");

                    SelfDeployReleaseArtifact added = await store.CaptureAsync(CreateSource(directory, "added"), "Armada.Server.dll");
                    SelfDeployTestDirectory.MakeWritable(added.Directory);
                    File.WriteAllText(Path.Combine(added.Directory, "extra.dll"), "injected");
                    AssertEqual("artifact_digest_mismatch", await store.VerifyAsync(added), "added file detected");
                }
            });

            await RunTest("VerifyAsync_DirectoryOutsideStore_IsRefused", async () =>
            {
                if (SkipWindows("VerifyAsync_DirectoryOutsideStore_IsRefused")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    SelfDeployReleaseArtifact artifact = await store.CaptureAsync(CreateSource(directory, "outside"), "Armada.Server.dll");
                    SelfDeployReleaseArtifact forged = new SelfDeployReleaseArtifact
                    {
                        Digest = artifact.Digest,
                        Directory = Path.Combine(directory.Root, "elsewhere", artifact.Digest),
                        EntryAssembly = artifact.EntryAssembly
                    };
                    AssertEqual("artifact_outside_store", await store.VerifyAsync(forged), "path outside the store refused");
                }
            });

            await RunTest("CaptureAsync_SymlinkOrMissingEntry_IsRefused", async () =>
            {
                if (SkipWindows("CaptureAsync_SymlinkOrMissingEntry_IsRefused")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    string linked = CreateSource(directory, "linked");
                    File.CreateSymbolicLink(Path.Combine(linked, "outside.dll"), Path.Combine(directory.Root, "target.dll"));
                    AssertEqual("artifact_symlink_refused", await CaptureFailureAsync(store, linked, "Armada.Server.dll"), "symlink refused");
                    AssertEqual("artifact_entry_missing", await CaptureFailureAsync(store, CreateSource(directory, "noentry"), "Missing.dll"), "missing entry refused");
                    AssertEqual("artifact_entry_invalid", await CaptureFailureAsync(store, CreateSource(directory, "nested"), "../Armada.Server.dll"), "entry path traversal refused");
                }
            });

            await RunTest("CaptureAsync_PublicStoreRoot_FailsClosed", async () =>
            {
                if (SkipWindows("CaptureAsync_PublicStoreRoot_FailsClosed")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    string root = Path.Combine(directory.Root, "public-releases");
                    Directory.CreateDirectory(root);
                    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    SelfDeployArtifactStore store = new SelfDeployArtifactStore(root);
                    AssertEqual("private_storage_permissions_unverified", await CaptureFailureAsync(store, CreateSource(directory, "public"), "Armada.Server.dll"),
                        "public artifact root refused");
                }
            });
        }

        private bool SkipWindows(string testName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(testName, "Self-deploy private storage fails closed on Windows.");
            return true;
        }

        private static SelfDeployArtifactStore CreateStore(SelfDeployTestDirectory directory)
        {
            return new SelfDeployArtifactStore(Path.Combine(directory.Root, "state", "releases"));
        }

        private static string CreateSource(SelfDeployTestDirectory directory, string label)
        {
            string source = Path.Combine(directory.Root, "build-" + label);
            Directory.CreateDirectory(Path.Combine(source, "runtimes"));
            File.WriteAllText(Path.Combine(source, "Armada.Server.dll"), "server " + label);
            File.WriteAllText(Path.Combine(source, "Armada.Core.dll"), "core " + label);
            File.WriteAllText(Path.Combine(source, "runtimes", "native.so"), "native " + label);
            return source;
        }

        private static async Task<string> CaptureFailureAsync(SelfDeployArtifactStore store, string source, string entry)
        {
            try
            {
                await store.CaptureAsync(source, entry);
                return "captured";
            }
            catch (SelfDeployCutoverException ex)
            {
                return ex.FailureReason;
            }
        }
    }
}
