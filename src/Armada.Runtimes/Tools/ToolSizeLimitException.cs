namespace Armada.Runtimes.Tools
{
    using System.IO;

    /// <summary>Indicates that a tool input or file exceeded its configured size limit.</summary>
    internal sealed class ToolSizeLimitException : IOException
    {
        /// <summary>Instantiate a size limit error.</summary>
        /// <param name="message">Safe description of the exceeded limit.</param>
        public ToolSizeLimitException(string message) : base(message) { }
    }
}
