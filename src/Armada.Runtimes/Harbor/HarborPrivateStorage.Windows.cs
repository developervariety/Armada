namespace Armada.Runtimes.Harbor
{
    using System.IO;
    using System.Runtime.Versioning;
    using System.Security.AccessControl;
    using System.Security.Principal;

    /// <summary>Creates protected Windows access rules for private Harbor paths.</summary>
    public static partial class HarborPrivateStorage
    {
        [SupportedOSPlatform("windows")]
        private static void SecureWindowsDirectory(string path)
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            AddDirectoryRule(security, CurrentUserSid(), FileSystemRights.FullControl);
            AddDirectoryRule(security, WellKnownSid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl);
            AddDirectoryRule(security, WellKnownSid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl);
            new DirectoryInfo(path).SetAccessControl(security);
            ValidateWindowsDirectory(path);
        }

        [SupportedOSPlatform("windows")]
        private static void SecureWindowsFile(string path)
        {
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            AddFileRule(security, CurrentUserSid(), FileSystemRights.FullControl);
            AddFileRule(security, WellKnownSid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl);
            AddFileRule(security, WellKnownSid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl);
            new FileInfo(path).SetAccessControl(security);
            ValidateWindowsFile(path);
        }

        [SupportedOSPlatform("windows")]
        private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        [SupportedOSPlatform("windows")]
        private static void AddFileRule(FileSecurity security, SecurityIdentifier sid, FileSystemRights rights)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        }

        [SupportedOSPlatform("windows")]
        private static SecurityIdentifier CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.User ?? throw new System.UnauthorizedAccessException("The current Windows user has no security identifier.");
        }

        [SupportedOSPlatform("windows")]
        private static SecurityIdentifier WellKnownSid(WellKnownSidType sidType)
        {
            return new SecurityIdentifier(sidType, null);
        }
    }
}
