namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Runtime.Versioning;
    using System.Security.AccessControl;
    using System.Security.Principal;

    /// <summary>
    /// Owner-only private storage through Windows ACLs. Each directory and file gets a protected descriptor
    /// (inheritance from the parent removed) whose owner is the current user and whose only access rule grants
    /// that user full control, inherited by children. SYSTEM and Administrators are not granted: the storage
    /// belongs to the account running Armada, and that account is SYSTEM when Armada runs as SYSTEM. After every
    /// change the descriptor is read back; anything other than that exact shape fails closed with
    /// <c>private_storage_acl_unverified</c>. An existing directory is verified and never modified.
    /// </summary>
    internal sealed class WindowsSelfDeployPrivateStorage : ISelfDeployPrivateStorageBackend
    {
        /// <inheritdoc />
        public string Name => "windows-acl";

        /// <inheritdoc />
        public void CreatePrivateDirectory(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            SecurityIdentifier user = CurrentUser();
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            try
            {
                new DirectoryInfo(path).Create(security);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is InvalidOperationException || ex is PrivilegeNotHeldException)
            {
                throw new SelfDeployPrivateStorageException("private_storage_acl_apply_failed");
            }
        }

        /// <inheritdoc />
        public void VerifyPrivateDirectory(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            DirectorySecurity security;
            try
            {
                security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is InvalidOperationException)
            {
                throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            }
            RequireOwnerOnly(security, CurrentUser());
        }

        /// <inheritdoc />
        public FileStream CreatePrivateFile(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            SecurityIdentifier user = CurrentUser();
            FileStream stream;
            try
            {
                stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.SequentialScan, OwnerOnlyFile(user));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is PrivilegeNotHeldException)
            {
                throw new SelfDeployPrivateStorageException("private_storage_acl_apply_failed");
            }
            try
            {
                RequireOwnerOnly(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), user);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public void RestrictFile(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            SecurityIdentifier user = CurrentUser();
            try
            {
                FileInfo file = new FileInfo(path);
                file.SetAccessControl(OwnerOnlyFile(user));
                RequireOwnerOnly(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), user);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is InvalidOperationException || ex is PrivilegeNotHeldException)
            {
                throw new SelfDeployPrivateStorageException("private_storage_acl_apply_failed");
            }
        }

        [SupportedOSPlatform("windows")]
        private static FileSecurity OwnerOnlyFile(SecurityIdentifier user)
        {
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return security;
        }

        [SupportedOSPlatform("windows")]
        private static SecurityIdentifier CurrentUser()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return identity.User ?? throw new SelfDeployPrivateStorageException("private_storage_acl_user_unknown");
            }
        }

        [SupportedOSPlatform("windows")]
        private static void RequireOwnerOnly(FileSystemSecurity security, SecurityIdentifier user)
        {
            if (!security.AreAccessRulesProtected) throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
            if (owner == null || !owner.Equals(user)) throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (rules.Count == 0) throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow || !rule.IdentityReference.Equals(user))
                    throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            }
        }
    }
}
