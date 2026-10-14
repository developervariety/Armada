namespace Armada.Core.Hosting
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One entry of a Harbor menu as <see cref="HarborMenuLayout"/> defines it: a command, a submenu, a separator, or
    /// the tray's link status line. Harbor turns these into native menu items.
    /// </summary>
    public class HarborMenuEntry
    {
        #region Public-Members

        /// <summary>
        /// Text of the item or submenu; empty for a separator or the status line.
        /// </summary>
        public string Header { get; set; } = String.Empty;

        /// <summary>
        /// Command the item runs; null for a submenu, separator, or status line.
        /// </summary>
        public HarborMenuCommandEnum? Command { get; set; } = null;

        /// <summary>
        /// Shortcut key, pressed with Command on macOS and Control elsewhere.
        /// </summary>
        public HarborMenuKeyEnum Key { get; set; } = HarborMenuKeyEnum.None;

        /// <summary>
        /// True when the shortcut also takes Shift.
        /// </summary>
        public bool Shift { get; set; } = false;

        /// <summary>
        /// True for one choice of a group shown with a check mark on the current choice (the host reports which).
        /// </summary>
        public bool IsRadio { get; set; } = false;

        /// <summary>
        /// Items of a submenu; null for anything else.
        /// </summary>
        public List<HarborMenuEntry>? Children { get; set; } = null;

        /// <summary>
        /// True for a separator.
        /// </summary>
        public bool IsSeparator { get; set; } = false;

        /// <summary>
        /// True for the tray's disabled link status line, whose text the host supplies.
        /// </summary>
        public bool IsStatusLine { get; set; } = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public HarborMenuEntry()
        {
        }

        /// <summary>
        /// A command item.
        /// </summary>
        /// <param name="header">Text.</param>
        /// <param name="command">Command.</param>
        /// <param name="key">Shortcut key.</param>
        /// <param name="shift">True when the shortcut also takes Shift.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> is null or blank.</exception>
        public static HarborMenuEntry Item(string header, HarborMenuCommandEnum command, HarborMenuKeyEnum key = HarborMenuKeyEnum.None, bool shift = false)
        {
            if (String.IsNullOrWhiteSpace(header)) throw new ArgumentNullException(nameof(header));
            return new HarborMenuEntry { Header = header, Command = command, Key = key, Shift = shift };
        }

        /// <summary>
        /// One choice of a group, checked when the host reports it current.
        /// </summary>
        /// <param name="header">Text.</param>
        /// <param name="command">Command that selects the choice.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> is null or blank.</exception>
        public static HarborMenuEntry Radio(string header, HarborMenuCommandEnum command)
        {
            if (String.IsNullOrWhiteSpace(header)) throw new ArgumentNullException(nameof(header));
            return new HarborMenuEntry { Header = header, Command = command, IsRadio = true };
        }

        /// <summary>
        /// A submenu.
        /// </summary>
        /// <param name="header">Text.</param>
        /// <param name="children">Items.</param>
        /// <returns>The entry.</returns>
        public static HarborMenuEntry Submenu(string header, List<HarborMenuEntry> children)
        {
            if (String.IsNullOrWhiteSpace(header)) throw new ArgumentNullException(nameof(header));
            return new HarborMenuEntry { Header = header, Children = children ?? throw new ArgumentNullException(nameof(children)) };
        }

        /// <summary>
        /// A separator.
        /// </summary>
        /// <returns>The entry.</returns>
        public static HarborMenuEntry Separator()
        {
            return new HarborMenuEntry { IsSeparator = true };
        }

        /// <summary>
        /// The tray's link status line.
        /// </summary>
        /// <returns>The entry.</returns>
        public static HarborMenuEntry StatusLine()
        {
            return new HarborMenuEntry { IsStatusLine = true };
        }

        #endregion
    }
}
