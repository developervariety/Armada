namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The MCP servers document Mux reads: its config directory's mcp-servers.json and the file a
    /// `mux print --mcp-config` run names share this one shape. Armada writes it for captain launches and
    /// for `armada mcp install`, and reads it back to describe the tools a Mux captain receives, so the
    /// field names live here once. Mux ignores a field it does not know, so a misspelled auth field
    /// silently sends no credential.
    /// </summary>
    public sealed class MuxMcpServersDocument
    {
        #region Public-Members

        /// <summary>
        /// The configured servers, in order.
        /// </summary>
        [JsonPropertyName("servers")]
        public List<MuxMcpServer> Servers { get; set; } = new List<MuxMcpServer>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse a Mux MCP servers document. A document without a servers array yields no servers.
        /// </summary>
        /// <param name="json">Document text.</param>
        /// <returns>The parsed document.</returns>
        /// <exception cref="JsonException">The text is not a JSON document of this shape.</exception>
        public static MuxMcpServersDocument Parse(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new MuxMcpServersDocument();
            MuxMcpServersDocument? document = JsonSerializer.Deserialize<MuxMcpServersDocument>(json, _ReadOptions);
            if (document == null) return new MuxMcpServersDocument();
            if (document.Servers == null) document.Servers = new List<MuxMcpServer>();
            return document;
        }

        /// <summary>
        /// Serialize the document as indented JSON, omitting fields that are not set.
        /// </summary>
        /// <returns>The document text.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, WriteOptions);
        }

        #endregion

        #region Internal-Members

        internal static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        #endregion
    }
}
