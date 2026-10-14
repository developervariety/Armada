namespace Armada.Core.Hosting
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The key of a Harbor menu shortcut, pressed with Command on macOS and Control elsewhere.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborMenuKeyEnum
    {
        /// <summary>
        /// No shortcut.
        /// </summary>
        None,

        /// <summary>
        /// Comma (Settings).
        /// </summary>
        Comma,

        /// <summary>
        /// A (Select All).
        /// </summary>
        A,

        /// <summary>
        /// C (Copy).
        /// </summary>
        C,

        /// <summary>
        /// D (Open Dashboard).
        /// </summary>
        D,

        /// <summary>
        /// I (Status).
        /// </summary>
        I,

        /// <summary>
        /// L (Logs).
        /// </summary>
        L,

        /// <summary>
        /// M (Minimize).
        /// </summary>
        M,

        /// <summary>
        /// Q (Quit).
        /// </summary>
        Q,

        /// <summary>
        /// R (Reconnect).
        /// </summary>
        R,

        /// <summary>
        /// V (Paste).
        /// </summary>
        V,

        /// <summary>
        /// W (Close).
        /// </summary>
        W,

        /// <summary>
        /// X (Cut).
        /// </summary>
        X,

        /// <summary>
        /// Z (Undo; with Shift, Redo).
        /// </summary>
        Z
    }
}
