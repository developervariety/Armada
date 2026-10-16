namespace Armada.Core.Hosting
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Builds the shell commands a desktop app uses to open a folder or document, reveal a file in the file manager,
    /// open a text file for editing, or open a terminal in a folder: open on macOS, explorer.exe, Windows Terminal, or
    /// the shell association on Windows, xdg-open and the installed terminal emulator on Linux.
    /// </summary>
    public static class DesktopShellCommandBuilder
    {
        #region Public-Methods

        /// <summary>
        /// Open a folder, document, or URL with its default handler.
        /// </summary>
        /// <param name="platform">Target platform.</param>
        /// <param name="target">Path or URL.</param>
        /// <returns>The launch.</returns>
        /// <exception cref="ArgumentNullException">target is null or empty.</exception>
        /// <exception cref="PlatformNotSupportedException">platform is unsupported.</exception>
        public static ShellLaunch BuildOpen(HostPlatformEnum platform, string target)
        {
            if (String.IsNullOrWhiteSpace(target)) throw new ArgumentNullException(nameof(target));

            switch (platform)
            {
                case HostPlatformEnum.MacOS:
                    return new ShellLaunch { FileName = "open", Arguments = new List<string> { target } };
                case HostPlatformEnum.Linux:
                    return new ShellLaunch { FileName = "xdg-open", Arguments = new List<string> { target } };
                case HostPlatformEnum.Windows:
                    return new ShellLaunch { FileName = target, UseShellExecute = true };
                default:
                    throw new PlatformNotSupportedException("Desktop shell commands are not supported on " + platform + ".");
            }
        }

        /// <summary>
        /// Show a file or folder selected in the file manager (Finder, Explorer). Linux has no portable way to
        /// select an item, so it opens the containing folder.
        /// </summary>
        /// <param name="platform">Target platform.</param>
        /// <param name="path">File or folder path.</param>
        /// <returns>The launch.</returns>
        /// <exception cref="ArgumentNullException">path is null or empty.</exception>
        /// <exception cref="PlatformNotSupportedException">platform is unsupported.</exception>
        public static ShellLaunch BuildReveal(HostPlatformEnum platform, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            switch (platform)
            {
                case HostPlatformEnum.MacOS:
                    return new ShellLaunch { FileName = "open", Arguments = new List<string> { "-R", path } };
                case HostPlatformEnum.Windows:
                    // explorer.exe parses its own command line: the path must be quoted after the comma, not as a whole.
                    return new ShellLaunch { FileName = "explorer.exe", RawArguments = "/select,\"" + path + "\"" };
                case HostPlatformEnum.Linux:
                    string? parent = TargetPath.GetDirectoryName(path, HostPlatformEnum.Linux);
                    return BuildOpen(platform, String.IsNullOrEmpty(parent) ? path : parent);
                default:
                    throw new PlatformNotSupportedException("Desktop shell commands are not supported on " + platform + ".");
            }
        }

        /// <summary>
        /// Open a text file (settings, logs) in a text editor: the default text editor on macOS (open -t), the file
        /// association on Windows (see <see cref="BuildTextEditorFallback"/> when there is none), and xdg-open on Linux.
        /// </summary>
        /// <param name="platform">Target platform.</param>
        /// <param name="path">File path.</param>
        /// <returns>The launch.</returns>
        /// <exception cref="ArgumentNullException">path is null or empty.</exception>
        /// <exception cref="PlatformNotSupportedException">platform is unsupported.</exception>
        public static ShellLaunch BuildOpenInTextEditor(HostPlatformEnum platform, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (platform == HostPlatformEnum.MacOS)
                return new ShellLaunch { FileName = "open", Arguments = new List<string> { "-t", path } };
            return BuildOpen(platform, path);
        }

        /// <summary>
        /// Open a terminal window in a folder. Returns the candidates to try in order, since which terminal exists
        /// varies: Terminal.app on macOS (open -a Terminal, waited on so a failure is reported); Windows Terminal
        /// (wt.exe -d), then a Command Prompt window started in the folder on Windows; the Debian default
        /// (x-terminal-emulator), GNOME Terminal, Konsole, Xfce Terminal, and xterm on Linux. Every candidate also starts
        /// in the folder.
        /// </summary>
        /// <param name="platform">Target platform.</param>
        /// <param name="path">Folder path.</param>
        /// <returns>The launches to try, first preferred; never empty.</returns>
        /// <exception cref="ArgumentNullException">path is null or empty.</exception>
        /// <exception cref="PlatformNotSupportedException">platform is unsupported.</exception>
        public static List<ShellLaunch> BuildOpenTerminal(HostPlatformEnum platform, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            switch (platform)
            {
                case HostPlatformEnum.MacOS:
                    return new List<ShellLaunch>
                    {
                        new ShellLaunch { FileName = "open", Arguments = new List<string> { "-a", "Terminal", path }, WorkingDirectory = path, WaitForExitMs = 10000 }
                    };
                case HostPlatformEnum.Windows:
                    return new List<ShellLaunch>
                    {
                        new ShellLaunch { FileName = "wt.exe", Arguments = new List<string> { "-d", path }, WorkingDirectory = path },
                        // start opens a new console window; /D sets its folder. The empty title keeps start from reading
                        // the quoted path as the window title.
                        new ShellLaunch { FileName = "cmd.exe", RawArguments = "/c start \"\" /D \"" + path + "\" cmd.exe", WorkingDirectory = path }
                    };
                case HostPlatformEnum.Linux:
                    return new List<ShellLaunch>
                    {
                        new ShellLaunch { FileName = "x-terminal-emulator", WorkingDirectory = path },
                        new ShellLaunch { FileName = "gnome-terminal", Arguments = new List<string> { "--working-directory=" + path }, WorkingDirectory = path },
                        new ShellLaunch { FileName = "konsole", Arguments = new List<string> { "--workdir", path }, WorkingDirectory = path },
                        new ShellLaunch { FileName = "xfce4-terminal", Arguments = new List<string> { "--working-directory=" + path }, WorkingDirectory = path },
                        new ShellLaunch { FileName = "xterm", WorkingDirectory = path }
                    };
                default:
                    throw new PlatformNotSupportedException("Desktop shell commands are not supported on " + platform + ".");
            }
        }

        /// <summary>
        /// Text editor to use when <see cref="BuildOpenInTextEditor"/> fails because the file type has no handler.
        /// Only Windows has one (Notepad, which every installation includes); other platforms return null.
        /// </summary>
        /// <param name="platform">Target platform.</param>
        /// <param name="path">File path.</param>
        /// <returns>The fallback launch, or null.</returns>
        /// <exception cref="ArgumentNullException">path is null or empty.</exception>
        public static ShellLaunch? BuildTextEditorFallback(HostPlatformEnum platform, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (platform != HostPlatformEnum.Windows) return null;
            return new ShellLaunch { FileName = "notepad.exe", Arguments = new List<string> { path } };
        }

        #endregion
    }
}
