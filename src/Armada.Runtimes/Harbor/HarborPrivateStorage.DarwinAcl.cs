namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;

    /// <summary>Removes or rejects extended Darwin ACLs on private Harbor paths.</summary>
    public static partial class HarborPrivateStorage
    {
        private const int DarwinAclExtended = 0x00000100;
        private const int DarwinNoAclErrno = 2;

        [SupportedOSPlatform("macos")]
        private static void RemoveDarwinAcl(string path)
        {
            IntPtr emptyAcl = acl_init(0);
            if (emptyAcl == IntPtr.Zero) throw new UnauthorizedAccessException("An empty macOS ACL could not be created for a private Harbor path.");
            try
            {
                if (acl_set_file(path, DarwinAclExtended, emptyAcl) != 0)
                    throw new UnauthorizedAccessException("The macOS extended ACL could not be cleared from a private Harbor path.");
            }
            finally { acl_free(emptyAcl); }
        }

        [SupportedOSPlatform("macos")]
        private static void ValidateDarwinAcl(string path)
        {
            IntPtr acl = acl_get_file(path, DarwinAclExtended);
            if (acl == IntPtr.Zero)
            {
                if (Marshal.GetLastPInvokeError() == DarwinNoAclErrno && (File.Exists(path) || Directory.Exists(path))
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return;
                throw new UnauthorizedAccessException("The macOS extended ACL state could not be verified.");
            }
            acl_free(acl);
            throw new UnauthorizedAccessException("A private Harbor path has an extended ACL.");
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "acl_init")]
        private static extern IntPtr acl_init(int count);

        [DllImport("libc", SetLastError = true, EntryPoint = "acl_set_file")]
        private static extern int acl_set_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type, IntPtr acl);

        [DllImport("libc", SetLastError = true, EntryPoint = "acl_get_file")]
        private static extern IntPtr acl_get_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);


        [DllImport("libc", EntryPoint = "acl_free")]
        private static extern int acl_free(IntPtr acl);
    }
}
