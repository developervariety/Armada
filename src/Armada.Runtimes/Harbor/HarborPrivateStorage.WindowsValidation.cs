namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Runtime.Versioning;
    using System.Security.AccessControl;
    using System.Security.Principal;

    /// <summary>Validates protected Windows access rules for private Harbor paths.</summary>
    public static partial class HarborPrivateStorage
    {
        [SupportedOSPlatform("windows")]
        private static void ValidateWindowsDirectory(string path)
        {
            DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            ValidateWindowsRules(security, true);
        }

        [SupportedOSPlatform("windows")]
        private static void ValidateWindowsFile(string path)
        {
            FileSecurity security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            ValidateWindowsRules(security, false);
        }

        [SupportedOSPlatform("windows")]
        private static void ValidateWindowsRules(FileSystemSecurity security, bool directory)
        {
            SecurityIdentifier current = CurrentUserSid();
            SecurityIdentifier system = WellKnownSid(WellKnownSidType.LocalSystemSid);
            SecurityIdentifier administrators = WellKnownSid(WellKnownSidType.BuiltinAdministratorsSid);
            IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
            if (owner == null || !owner.Equals(current) || !security.AreAccessRulesProtected)
                throw new UnauthorizedAccessException("The private path owner or access-rule protection is invalid.");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
            bool currentCanRead = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow
                    || !IsTrusted(rule.IdentityReference, current, system, administrators))
                    throw new UnauthorizedAccessException("The private path grants access to an untrusted identity.");
                if (rule.IdentityReference.Equals(current) && CanRead(rule.FileSystemRights, directory)) currentCanRead = true;
                if (directory && (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    throw new UnauthorizedAccessException("The private directory rules do not protect child data.");
            }
            if (!currentCanRead) throw new UnauthorizedAccessException("The current user cannot read the private path.");
        }

        [SupportedOSPlatform("windows")]
        private static bool IsTrusted(IdentityReference identity, SecurityIdentifier current, SecurityIdentifier system, SecurityIdentifier administrators)
        {
            return identity.Equals(current) || identity.Equals(system) || identity.Equals(administrators);
        }

        [SupportedOSPlatform("windows")]
        private static bool CanRead(FileSystemRights rights, bool directory)
        {
            FileSystemRights readRights = directory
                ? FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes
                : FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes;
            return (rights & readRights) != 0 || (rights & FileSystemRights.Read) == FileSystemRights.Read;
        }
    }
}
