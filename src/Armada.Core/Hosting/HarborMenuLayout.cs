namespace Armada.Core.Hosting
{
    using System.Collections.Generic;

    /// <summary>
    /// The Harbor menus, defined once: the macOS application menu, the window menu (the macOS menu bar, or the
    /// in-window menu bar on Windows and Linux), and the tray menu. Every entry point opens the same three windows: the
    /// main window, Status (with its Logs tab), and Settings.
    /// </summary>
    public static class HarborMenuLayout
    {
        #region Public-Methods

        /// <summary>
        /// The macOS application menu (the bold "Armada Harbor" menu). The platform appends Services, Hide, and Quit.
        /// </summary>
        /// <returns>Entries.</returns>
        public static List<HarborMenuEntry> ApplicationMenu()
        {
            return new List<HarborMenuEntry>
            {
                HarborMenuEntry.Item("About Armada Harbor", HarborMenuCommandEnum.About),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Settings...", HarborMenuCommandEnum.Settings, HarborMenuKeyEnum.Comma)
            };
        }

        /// <summary>
        /// The window menu bar: Harbor, Edit (macOS only), View, Window (macOS only), and Help, in the order macOS
        /// apps use. Without an application menu (Windows and Linux), Settings and Quit go in the Harbor menu and About
        /// in Help; text fields there handle their own editing shortcuts, so there is no Edit menu. Open Dashboard is in the
        /// Harbor menu, next to the other items that open something outside the app.
        /// </summary>
        /// <param name="isMacOS">True on macOS.</param>
        /// <returns>Entries (submenus).</returns>
        public static List<HarborMenuEntry> WindowMenu(bool isMacOS)
        {
            List<HarborMenuEntry> harbor = new List<HarborMenuEntry>
            {
                HarborMenuEntry.Item("Connect", HarborMenuCommandEnum.Connect),
                HarborMenuEntry.Item("Disconnect", HarborMenuCommandEnum.Disconnect),
                HarborMenuEntry.Item("Reconnect", HarborMenuCommandEnum.Reconnect, HarborMenuKeyEnum.R),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Copy Harbor ID", HarborMenuCommandEnum.CopyHarborId),
                HarborMenuEntry.Item("Copy MCP URL", HarborMenuCommandEnum.CopyMcpUrl),
                HarborMenuEntry.Item("Open Harbor Folder", HarborMenuCommandEnum.OpenHarborFolder),
                HarborMenuEntry.Item("Open Dashboard", HarborMenuCommandEnum.OpenDashboard, HarborMenuKeyEnum.D)
            };

            if (isMacOS)
            {
                harbor.Add(HarborMenuEntry.Separator());
                harbor.Add(HarborMenuEntry.Item("Close Window", HarborMenuCommandEnum.CloseWindow, HarborMenuKeyEnum.W));
            }
            else
            {
                harbor.Add(HarborMenuEntry.Separator());
                harbor.Add(HarborMenuEntry.Item("Settings...", HarborMenuCommandEnum.Settings, HarborMenuKeyEnum.Comma));
                harbor.Add(HarborMenuEntry.Separator());
                harbor.Add(HarborMenuEntry.Item("Quit", HarborMenuCommandEnum.Quit, HarborMenuKeyEnum.Q));
            }

            List<HarborMenuEntry> view = new List<HarborMenuEntry>
            {
                HarborMenuEntry.Item("Status", HarborMenuCommandEnum.Status, HarborMenuKeyEnum.I),
                HarborMenuEntry.Item("Activity", HarborMenuCommandEnum.Activity),
                HarborMenuEntry.Item("Logs", HarborMenuCommandEnum.Logs, HarborMenuKeyEnum.L),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Submenu("Appearance", new List<HarborMenuEntry>
                {
                    HarborMenuEntry.Radio("System", HarborMenuCommandEnum.AppearanceSystem),
                    HarborMenuEntry.Radio("Light", HarborMenuCommandEnum.AppearanceLight),
                    HarborMenuEntry.Radio("Dark", HarborMenuCommandEnum.AppearanceDark)
                })
            };

            List<HarborMenuEntry> help = new List<HarborMenuEntry>
            {
                HarborMenuEntry.Item("Armada Documentation", HarborMenuCommandEnum.Documentation),
                HarborMenuEntry.Item("Report an Issue...", HarborMenuCommandEnum.ReportIssue),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Copy Diagnostics", HarborMenuCommandEnum.CopyDiagnostics)
            };

            if (!isMacOS)
            {
                help.Add(HarborMenuEntry.Separator());
                help.Add(HarborMenuEntry.Item("About Armada Harbor", HarborMenuCommandEnum.About));
            }

            List<HarborMenuEntry> menu = new List<HarborMenuEntry>
            {
                HarborMenuEntry.Submenu("Harbor", harbor)
            };

            if (isMacOS)
            {
                menu.Add(HarborMenuEntry.Submenu("Edit", new List<HarborMenuEntry>
                {
                    HarborMenuEntry.Item("Undo", HarborMenuCommandEnum.Undo, HarborMenuKeyEnum.Z),
                    HarborMenuEntry.Item("Redo", HarborMenuCommandEnum.Redo, HarborMenuKeyEnum.Z, true),
                    HarborMenuEntry.Separator(),
                    HarborMenuEntry.Item("Cut", HarborMenuCommandEnum.Cut, HarborMenuKeyEnum.X),
                    HarborMenuEntry.Item("Copy", HarborMenuCommandEnum.Copy, HarborMenuKeyEnum.C),
                    HarborMenuEntry.Item("Paste", HarborMenuCommandEnum.Paste, HarborMenuKeyEnum.V),
                    HarborMenuEntry.Item("Select All", HarborMenuCommandEnum.SelectAll, HarborMenuKeyEnum.A)
                }));
            }

            menu.Add(HarborMenuEntry.Submenu("View", view));

            if (isMacOS)
            {
                menu.Add(HarborMenuEntry.Submenu("Window", new List<HarborMenuEntry>
                {
                    HarborMenuEntry.Item("Minimize", HarborMenuCommandEnum.MinimizeWindow, HarborMenuKeyEnum.M),
                    HarborMenuEntry.Item("Zoom", HarborMenuCommandEnum.ZoomWindow),
                    HarborMenuEntry.Separator(),
                    HarborMenuEntry.Item("Armada Harbor", HarborMenuCommandEnum.ShowWindow),
                    HarborMenuEntry.Separator(),
                    HarborMenuEntry.Item("Bring All to Front", HarborMenuCommandEnum.BringAllToFront)
                }));
            }

            menu.Add(HarborMenuEntry.Submenu("Help", help));
            return menu;
        }

        /// <summary>
        /// The tray (menu bar extra) menu: the link status, the three windows, connect and disconnect, the dashboard,
        /// and Quit. Tray items carry no shortcuts: those work only while a Harbor window is focused.
        /// </summary>
        /// <returns>Entries.</returns>
        public static List<HarborMenuEntry> TrayMenu()
        {
            return new List<HarborMenuEntry>
            {
                HarborMenuEntry.StatusLine(),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Open Armada Harbor", HarborMenuCommandEnum.ShowWindow),
                HarborMenuEntry.Item("Status", HarborMenuCommandEnum.Status),
                HarborMenuEntry.Item("Logs", HarborMenuCommandEnum.Logs),
                HarborMenuEntry.Item("Settings...", HarborMenuCommandEnum.Settings),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Connect", HarborMenuCommandEnum.Connect),
                HarborMenuEntry.Item("Disconnect", HarborMenuCommandEnum.Disconnect),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Open Dashboard", HarborMenuCommandEnum.OpenDashboard),
                HarborMenuEntry.Separator(),
                HarborMenuEntry.Item("Quit Armada Harbor", HarborMenuCommandEnum.Quit)
            };
        }

        #endregion
    }
}
