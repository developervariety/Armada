namespace Armada.Harbor
{
    using System;
    using System.Runtime.InteropServices;
    using Avalonia.Threading;

    /// <summary>
    /// Switches the macOS activation policy so Harbor behaves as a menu-bar app: a Dock icon and app menu only
    /// while its window is open (NSApplicationActivationPolicyRegular), and only the menu-bar (tray) icon while
    /// it runs in the background (NSApplicationActivationPolicyAccessory). The bundle's Info.plist sets
    /// LSUIElement so a launch from the login item starts without a Dock icon. Does nothing on other platforms.
    /// Thread safety: not thread safe; call from the UI thread.
    /// </summary>
    public static class MacActivationPolicy
    {
        #region Private-Members

        private const string _ObjCLibrary = "/usr/lib/libobjc.A.dylib";
        private const string _DockBundleId = "com.apple.dock";
        private const long _PolicyRegular = 0;
        private const long _PolicyAccessory = 1;
        private const ulong _ActivateIgnoringOtherApps = 2;
        private static readonly TimeSpan _ReactivateDelay = TimeSpan.FromMilliseconds(150);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Show Harbor in the Dock and the app switcher (call before showing the window). Never throws.
        /// </summary>
        /// <returns>True when the policy was applied.</returns>
        public static bool ShowInDock()
        {
            return Apply(_PolicyRegular);
        }

        /// <summary>
        /// Remove Harbor from the Dock and the app switcher, leaving the menu-bar icon (call after hiding the
        /// window). Never throws.
        /// </summary>
        /// <returns>True when the policy was applied.</returns>
        public static bool HideFromDock()
        {
            return Apply(_PolicyAccessory);
        }

        /// <summary>
        /// Make Harbor the active application, so its window comes to the front and the menu bar shows Harbor's menus.
        /// Started from a terminal (scripts/macos/run-harbor.sh, dotnet run), Harbor's window otherwise opens while
        /// the terminal stays active and keeps the menu bar. Never throws.
        /// </summary>
        /// <returns>True when the request was sent.</returns>
        public static bool Activate()
        {
            if (!OperatingSystem.IsMacOS()) return false;

            try
            {
                IntPtr application = SharedApplication();
                if (application == IntPtr.Zero) return false;
                SendBool(application, sel_registerName("activateIgnoringOtherApps:"), true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when this process runs from inside a macOS .app bundle (Contents/MacOS), rather than from
        /// dotnet run or a bare executable.
        /// </summary>
        /// <returns>True inside a bundle.</returns>
        public static bool IsRunningFromBundle()
        {
            if (!OperatingSystem.IsMacOS()) return false;
            string? path = Environment.ProcessPath;
            if (String.IsNullOrEmpty(path)) return false;
            return path.Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
        }

        #endregion

        #region Private-Methods

        private static bool Apply(long policy)
        {
            if (!OperatingSystem.IsMacOS()) return false;

            try
            {
                IntPtr application = SharedApplication();
                if (application == IntPtr.Zero) return false;

                // Setting the policy again, even to the same value, while Harbor is active leaves the menu bar showing
                // the previous app's menus until Harbor is deactivated and reactivated; so change it only when it differs.
                if (SendReturnLong(application, sel_registerName("activationPolicy")) == policy) return true;

                if (policy != _PolicyRegular)
                {
                    return SendLong(application, sel_registerName("setActivationPolicy:"), policy);
                }

                // Becoming a regular app: AppKit only installs the menu bar on the next activation, so hand activation
                // to the Dock, switch, and take it back (the same sequence Avalonia uses at launch).
                ActivateDock();
                bool applied = SendLong(application, sel_registerName("setActivationPolicy:"), policy);
                DispatcherTimer.RunOnce(() => Activate(), _ReactivateDelay);
                return applied;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IntPtr SharedApplication()
        {
            return Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        }

        private static void ActivateDock()
        {
            IntPtr bundleId = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), _DockBundleId);
            if (bundleId == IntPtr.Zero) return;
            IntPtr apps = SendPointer(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationsWithBundleIdentifier:"), bundleId);
            if (apps == IntPtr.Zero) return;
            IntPtr dock = Send(apps, sel_registerName("firstObject"));
            if (dock == IntPtr.Zero) return;
            SendULong(dock, sel_registerName("activateWithOptions:"), _ActivateIgnoringOtherApps);
        }

        [DllImport(_ObjCLibrary)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(_ObjCLibrary)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern long SendReturnLong(IntPtr receiver, IntPtr selector);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendLong(IntPtr receiver, IntPtr selector, long argument);

        [DllImport(_ObjCLibrary, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendULong(IntPtr receiver, IntPtr selector, ulong argument);

        #endregion
    }
}
