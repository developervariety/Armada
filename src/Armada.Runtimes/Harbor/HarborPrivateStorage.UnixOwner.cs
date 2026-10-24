namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;

    /// <summary>Reads the owner UID through documented Linux and Darwin filesystem APIs.</summary>
    public static partial class HarborPrivateStorage
    {
        private const uint LinuxStatxUid = 0x00000008;
        private const int AtCurrentWorkingDirectory = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint DarwinCommonOwnerId = 0x00008000;
        private const uint DarwinNoFollow = 0x00000001;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatxOwner
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(20)] public uint UserId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DarwinAttributeList
        {
            public ushort BitmapCount;
            public ushort Reserved;
            public uint CommonAttributes;
            public uint VolumeAttributes;
            public uint DirectoryAttributes;
            public uint FileAttributes;
            public uint ForkAttributes;
        }

        [UnsupportedOSPlatform("windows")]
        private static void ValidateUnixOwner(string path)
        {
            uint owner = OperatingSystem.IsLinux() ? ReadLinuxOwner(path)
                : OperatingSystem.IsMacOS() ? ReadDarwinOwner(path)
                : throw new PlatformNotSupportedException("Private Harbor storage owner checks require Linux or macOS.");
            uint current = geteuid();
            if (owner != current) throw new UnauthorizedAccessException("A private Harbor path is not owned by the current user.");
        }

        [SupportedOSPlatform("linux")]
        private static uint ReadLinuxOwner(string path)
        {
            try
            {
                if (statx(AtCurrentWorkingDirectory, path, AtSymlinkNoFollow, LinuxStatxUid, out LinuxStatxOwner status) != 0
                    || (status.Mask & LinuxStatxUid) == 0)
                    throw new UnauthorizedAccessException("The Linux filesystem did not return a file owner UID.");
                return status.UserId;
            }
            catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException("The Linux statx owner API is unavailable.", exception);
            }
        }

        [SupportedOSPlatform("macos")]
        private static uint ReadDarwinOwner(string path)
        {
            try
            {
                DarwinAttributeList attributes = new DarwinAttributeList { BitmapCount = 5, CommonAttributes = DarwinCommonOwnerId };
                byte[] result = new byte[8];
                if (getattrlist(path, ref attributes, result, (nuint)result.Length, DarwinNoFollow) != 0
                    || BitConverter.ToUInt32(result, 0) < 8)
                    throw new UnauthorizedAccessException("The macOS filesystem did not return a file owner UID.");
                return BitConverter.ToUInt32(result, 4);
            }
            catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException("The macOS getattrlist owner API is unavailable.", exception);
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
        private static extern int statx(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatxOwner status);

        [DllImport("libc", SetLastError = true, EntryPoint = "getattrlist")]
        private static extern int getattrlist([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref DarwinAttributeList attributes,
            [Out] byte[] attributeBuffer, nuint bufferSize, uint options);

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint geteuid();
    }
}
