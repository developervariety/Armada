namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
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

            await RunTest("PruneAsync_KeepsProtectedAndNewestPrevious_RemovesOlder", async () =>
            {
                if (SkipWindows("PruneAsync_KeepsProtectedAndNewestPrevious_RemovesOlder")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    List<SelfDeployReleaseArtifact> releases = await CaptureAgedAsync(store, directory, 5);
                    SelfDeployReleaseArtifact rollback = releases[0];
                    SelfDeployReleaseArtifact running = releases[1];

                    SelfDeployReleasePruneResult result = await store.PruneAsync(new[] { running.Digest, rollback.Digest }, 2);

                    AssertTrue(String.IsNullOrEmpty(result.FailureReason), "prune reason: " + result.FailureReason);
                    AssertTrue(Directory.Exists(rollback.Directory), "oldest release kept because it is the rollback release");
                    AssertTrue(Directory.Exists(running.Directory), "running release kept");
                    AssertTrue(Directory.Exists(releases[4].Directory), "newest previous release kept");
                    AssertTrue(Directory.Exists(releases[3].Directory), "second newest previous release kept");
                    AssertFalse(Directory.Exists(releases[2].Directory), "older previous release removed");
                    AssertEqual(1, result.Removed.Count, "one release removed");
                    AssertEqual(releases[2].Digest, result.Removed[0], "removed digest");
                }
            });

            await RunTest("PruneAsync_RetainZero_KeepsOnlyProtectedAndIgnoresOtherEntries", async () =>
            {
                if (SkipWindows("PruneAsync_RetainZero_KeepsOnlyProtectedAndIgnoresOtherEntries")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    List<SelfDeployReleaseArtifact> releases = await CaptureAgedAsync(store, directory, 3);
                    string root = Path.Combine(directory.Root, "state", "releases");
                    Directory.CreateDirectory(Path.Combine(root, ".staging-inflight"));
                    Directory.CreateDirectory(Path.Combine(root, "operator-notes"));

                    SelfDeployReleasePruneResult result = await store.PruneAsync(new[] { releases[1].Digest }, 0);

                    AssertEqual(2, result.Removed.Count, "every unprotected release removed");
                    AssertTrue(Directory.Exists(releases[1].Directory), "protected release kept");
                    AssertTrue(Directory.Exists(Path.Combine(root, ".staging-inflight")), "staging entry untouched");
                    AssertTrue(Directory.Exists(Path.Combine(root, "operator-notes")), "non-release entry untouched");
                }
            });

            await RunTest("Retention_UnresolvedRestartRecordReleases_AreNeverRemoved", async () =>
            {
                if (SkipWindows("Retention_UnresolvedRestartRecordReleases_AreNeverRemoved")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    SelfDeployRestartRecordStore records = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    List<SelfDeployReleaseArtifact> releases = await CaptureAgedAsync(store, directory, 4);
                    await records.CreateAsync(Record(SelfDeployRestartStateEnum.RollingBack, releases[0], releases[1]));

                    SelfDeployReleasePruneResult result = await SelfDeployReleaseRetention.PruneAsync(store, records, new[] { releases[3].Digest }, 0);

                    AssertTrue(Directory.Exists(releases[0].Directory), "unresolved record candidate kept");
                    AssertTrue(Directory.Exists(releases[1].Directory), "unresolved record rollback kept");
                    AssertTrue(Directory.Exists(releases[3].Directory), "protected release kept");
                    AssertFalse(Directory.Exists(releases[2].Directory), "unreferenced release removed");
                    AssertEqual(1, result.Removed.Count, "one release removed");
                }
            });

            await RunTest("Retention_TerminalRestartRecordReleases_ArePrunable", async () =>
            {
                if (SkipWindows("Retention_TerminalRestartRecordReleases_ArePrunable")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    SelfDeployRestartRecordStore records = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    List<SelfDeployReleaseArtifact> releases = await CaptureAgedAsync(store, directory, 3);
                    await records.CreateAsync(Record(SelfDeployRestartStateEnum.Committed, releases[0], releases[1]));

                    SelfDeployReleasePruneResult result = await SelfDeployReleaseRetention.PruneAsync(store, records, new[] { releases[2].Digest }, 0);

                    AssertEqual(2, result.Removed.Count, "a finished restart no longer protects its releases");
                    AssertTrue(Directory.Exists(releases[2].Directory), "protected release kept");
                }
            });

            await RunTest("Retention_UnreadableRestartRecord_RemovesNothing", async () =>
            {
                if (SkipWindows("Retention_UnreadableRestartRecord_RemovesNothing")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployArtifactStore store = CreateStore(directory);
                    SelfDeployRestartRecordStore records = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    List<SelfDeployReleaseArtifact> releases = await CaptureAgedAsync(store, directory, 3);
                    await records.CreateAsync(Record(SelfDeployRestartStateEnum.Committed, releases[0], releases[1]));
                    File.WriteAllText(records.RecordPath, "{ corrupt");

                    SelfDeployReleasePruneResult result = await SelfDeployReleaseRetention.PruneAsync(store, records, Array.Empty<string>(), 0);

                    AssertEqual(0, result.Removed.Count, "nothing removed while restart state is unknown");
                    AssertEqual("release_prune_skipped_restart_record_unreadable", result.FailureReason, "skip reason");
                    foreach (SelfDeployReleaseArtifact release in releases) AssertTrue(Directory.Exists(release.Directory), "release kept");
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
            SkipTest(testName, "These cases assert Unix permission bits; Windows ACLs are covered by the Private Storage Backend suite.");
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

        private static async Task<List<SelfDeployReleaseArtifact>> CaptureAgedAsync(SelfDeployArtifactStore store, SelfDeployTestDirectory directory, int count)
        {
            List<SelfDeployReleaseArtifact> releases = new List<SelfDeployReleaseArtifact>();
            DateTime oldest = DateTime.UtcNow.AddHours(-count);
            for (int i = 0; i < count; i++)
            {
                SelfDeployReleaseArtifact release = await store.CaptureAsync(CreateSource(directory, "aged-" + i), "Armada.Server.dll");
                Directory.SetLastWriteTimeUtc(release.Directory, oldest.AddHours(i));
                releases.Add(release);
            }
            return releases;
        }

        private static SelfDeployRestartRecord Record(SelfDeployRestartStateEnum state, SelfDeployReleaseArtifact candidate, SelfDeployReleaseArtifact rollback)
        {
            SelfDeployRestartRecord record = new SelfDeployRestartRecord
            {
                OperationId = "sdo_" + Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Candidate = candidate,
                Rollback = rollback
            };
            record.MoveTo(state, "test");
            return record;
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
