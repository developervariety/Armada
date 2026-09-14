namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.AccessControl;
    using System.Security.Principal;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Platform selection and fail-closed contract of owner-only private storage, plus a real ACL check on Windows.
    /// </summary>
    public sealed class SelfDeployPrivateStorageBackendTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Private Storage Backend";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SelectBackend_WindowsUsesAclBackend_OtherPlatformsUseUnixModes", () =>
            {
                AssertEqual("windows-acl", SelfDeployPrivateFile.SelectBackend(true).Name, "Windows selects the ACL backend");
                AssertEqual("unix-mode", SelfDeployPrivateFile.SelectBackend(false).Name, "other platforms select Unix modes");
                AssertEqual(OperatingSystem.IsWindows() ? "windows-acl" : "unix-mode", SelfDeployPrivateFile.Backend.Name, "this host uses its platform backend");
            });

            await RunTest("CreateDirectory_AclBackend_CreatesAndVerifiesEachMissingLevel", async () =>
            {
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (BackendScope scope = new BackendScope(new RecordingAclBackend()))
                {
                    string nested = Path.Combine(directory.Root, "a", "b", "c");
                    SelfDeployPrivateFile.CreateDirectory(nested);
                    string file = Path.Combine(nested, "settings.json");
                    await SelfDeployPrivateFile.WriteTextAsync(file, "{}", CancellationToken.None);
                    SelfDeployPrivateFile.RestrictFile(file);

                    List<string> expected = new List<string>
                    {
                        "create:" + Path.Combine(directory.Root, "a"), "verify:" + Path.Combine(directory.Root, "a"),
                        "create:" + Path.Combine(directory.Root, "a", "b"), "verify:" + Path.Combine(directory.Root, "a", "b"),
                        "create:" + nested, "verify:" + nested,
                        "file:" + file, "restrict:" + file
                    };
                    AssertEqual(String.Join("|", expected), String.Join("|", scope.Backend.Calls), "every missing level is created then verified, and files go through the backend");
                    AssertEqual("{}", File.ReadAllText(file), "file content written through the backend stream");
                }
            });

            await RunTest("CreateDirectory_AclVerificationFails_FailsClosedAndLeavesExistingDirectoryUnchanged", () =>
            {
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (BackendScope scope = new BackendScope(new RecordingAclBackend { FailVerification = true }))
                {
                    string existing = Path.Combine(directory.Root, "existing");
                    Directory.CreateDirectory(existing);
                    string reason = CaptureReason(() => SelfDeployPrivateFile.CreateDirectory(existing));
                    AssertEqual("private_storage_acl_unverified", reason, "unverified existing directory fails closed");
                    AssertEqual("verify:" + existing, String.Join("|", scope.Backend.Calls), "an existing directory is verified, never modified");

                    string created = Path.Combine(directory.Root, "created");
                    AssertEqual("private_storage_acl_unverified", CaptureReason(() => SelfDeployPrivateFile.CreateDirectory(created)), "unverified new directory fails closed");
                }
            });

            if (!OperatingSystem.IsWindows())
            {
                SkipTest("WindowsAcl_RealDirectoryAndFile_AreOwnerOnlyAndPublicDirectoryIsRefused",
                    "Real Windows ACL test runs only on Windows; it was not executed on this host.");
            }
            else
            {
                await RunTest("WindowsAcl_RealDirectoryAndFile_AreOwnerOnlyAndPublicDirectoryIsRefused", async () =>
                {
                    if (!OperatingSystem.IsWindows()) return;
                    using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                    {
                        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
                        string nested = Path.Combine(directory.Root, "private", "backups");
                        SelfDeployPrivateFile.CreateDirectory(nested);
                        DirectorySecurity directoryAcl = new DirectoryInfo(nested).GetAccessControl();
                        AssertTrue(directoryAcl.AreAccessRulesProtected, "inheritance removed");
                        AssertOwnerOnly(directoryAcl, user, "directory");

                        string file = Path.Combine(nested, "settings.json");
                        await SelfDeployPrivateFile.WriteTextAsync(file, "{}", CancellationToken.None);
                        AssertOwnerOnly(new FileInfo(file).GetAccessControl(), user, "file");

                        string ordinary = Path.Combine(directory.Root, "ordinary");
                        Directory.CreateDirectory(ordinary);
                        AssertEqual("private_storage_acl_unverified", CaptureReason(() => SelfDeployPrivateFile.CreateDirectory(ordinary)),
                            "a directory with inherited access is refused");
                    }
                });
            }
        }

        private void AssertOwnerOnly(FileSystemSecurity security, SecurityIdentifier user, string label)
        {
            if (!OperatingSystem.IsWindows()) return;
            AssertTrue(user.Equals(security.GetOwner(typeof(SecurityIdentifier))), label + " owner is the current user");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            AssertTrue(rules.Count > 0, label + " has an access rule");
            foreach (FileSystemAccessRule rule in rules)
            {
                AssertEqual(AccessControlType.Allow, rule.AccessControlType, label + " rule is allow");
                AssertTrue(user.Equals(rule.IdentityReference), label + " rule belongs to the current user only");
            }
        }

        private static string CaptureReason(Action action)
        {
            try
            {
                action();
                return String.Empty;
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                return ex.FailureReason;
            }
        }

        private sealed class BackendScope : IDisposable
        {
            private readonly ISelfDeployPrivateStorageBackend _Previous;

            public BackendScope(RecordingAclBackend backend)
            {
                _Previous = SelfDeployPrivateFile.Backend;
                Backend = backend;
                SelfDeployPrivateFile.Backend = backend;
            }

            public RecordingAclBackend Backend { get; }

            public void Dispose()
            {
                SelfDeployPrivateFile.Backend = _Previous;
            }
        }

        // Stands in for the Windows ACL backend on any host: performs plain filesystem work, records the contract
        // calls in order, and can report a failed ACL read-back.
        private sealed class RecordingAclBackend : ISelfDeployPrivateStorageBackend
        {
            public List<string> Calls { get; } = new List<string>();
            public bool FailVerification { get; set; }
            public string Name => "recording-acl";

            public void CreatePrivateDirectory(string path)
            {
                Calls.Add("create:" + path);
                Directory.CreateDirectory(path);
            }

            public void VerifyPrivateDirectory(string path)
            {
                Calls.Add("verify:" + path);
                if (FailVerification) throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            }

            public FileStream CreatePrivateFile(string path)
            {
                Calls.Add("file:" + path);
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }

            public void RestrictFile(string path)
            {
                Calls.Add("restrict:" + path);
            }
        }
    }
}
