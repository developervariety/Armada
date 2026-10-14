namespace Armada.Core.Hosting
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Every command the Harbor menus offer. The macOS menu bar, the in-window menu on Windows and Linux, and the tray
    /// menu are all built from these (see <see cref="HarborMenuLayout"/>), so each surface offers the same commands with
    /// the same enabled state, and each opens the same windows.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborMenuCommandEnum
    {
        /// <summary>
        /// Show and focus the main Harbor window.
        /// </summary>
        ShowWindow,

        /// <summary>
        /// Show the About window.
        /// </summary>
        About,

        /// <summary>
        /// Open the Settings window.
        /// </summary>
        Settings,

        /// <summary>
        /// Open the Status window on its overview.
        /// </summary>
        Status,

        /// <summary>
        /// Open the Status window on its Activity tab.
        /// </summary>
        Activity,

        /// <summary>
        /// Open the Status window on its Logs tab.
        /// </summary>
        Logs,

        /// <summary>
        /// Start the link loop.
        /// </summary>
        Connect,

        /// <summary>
        /// Stop the link loop.
        /// </summary>
        Disconnect,

        /// <summary>
        /// Drop the current link and dial again.
        /// </summary>
        Reconnect,

        /// <summary>
        /// Copy the Harbor identifier.
        /// </summary>
        CopyHarborId,

        /// <summary>
        /// Copy the MCP URL the Admiral advertised.
        /// </summary>
        CopyMcpUrl,

        /// <summary>
        /// Open the Harbor settings folder (~/.armada-harbor).
        /// </summary>
        OpenHarborFolder,

        /// <summary>
        /// Open the dashboard in the browser.
        /// </summary>
        OpenDashboard,

        /// <summary>
        /// Minimize the active Harbor window.
        /// </summary>
        MinimizeWindow,

        /// <summary>
        /// Close the active Harbor window (the main window hides; Harbor keeps running in the tray).
        /// </summary>
        CloseWindow,

        /// <summary>
        /// Zoom (maximize or restore) the active Harbor window.
        /// </summary>
        ZoomWindow,

        /// <summary>
        /// Bring every open Harbor window to the front.
        /// </summary>
        BringAllToFront,

        /// <summary>
        /// Undo the last edit in the focused text field.
        /// </summary>
        Undo,

        /// <summary>
        /// Redo the last undone edit in the focused text field.
        /// </summary>
        Redo,

        /// <summary>
        /// Cut the selection in the focused text field.
        /// </summary>
        Cut,

        /// <summary>
        /// Copy the selection in the focused text field.
        /// </summary>
        Copy,

        /// <summary>
        /// Paste into the focused text field.
        /// </summary>
        Paste,

        /// <summary>
        /// Select all text in the focused text field.
        /// </summary>
        SelectAll,

        /// <summary>
        /// Follow the operating system's color scheme.
        /// </summary>
        AppearanceSystem,

        /// <summary>
        /// Always use the light color scheme.
        /// </summary>
        AppearanceLight,

        /// <summary>
        /// Always use the dark color scheme.
        /// </summary>
        AppearanceDark,

        /// <summary>
        /// Open the Armada documentation.
        /// </summary>
        Documentation,

        /// <summary>
        /// Copy versions, paths, link state, and recent errors for a bug report.
        /// </summary>
        CopyDiagnostics,

        /// <summary>
        /// Open a new Armada issue in the browser.
        /// </summary>
        ReportIssue,

        /// <summary>
        /// Quit Harbor.
        /// </summary>
        Quit
    }
}
