namespace Test.Shared.Infrastructure
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Guards the text payload of an MCP tool result before a suite helper deserializes it into the
    /// entity it expected.
    ///
    /// A refused tool call answers with a result object carrying an <c>Error</c> and a <c>Code</c>, not
    /// with a transport failure, so a seeding helper that deserializes it as the entity gets a blank id
    /// and hands that id to the rest of the case. Every later step then fails on the blank id, and the
    /// run reports the consequence ("Mission not found", on every case that seeds a mission) rather than
    /// the one refusal that caused it.
    /// </summary>
    public static class McpToolResults
    {
        #region Public-Methods

        /// <summary>
        /// Return the result text unchanged, or fail naming the tool and the refusal it carried.
        /// </summary>
        /// <param name="toolName">MCP tool that produced the result.</param>
        /// <param name="resultText">Text payload of the tool result.</param>
        /// <returns>The same text, when it is not a refusal.</returns>
        public static string RequireSuccess(string toolName, string resultText)
        {
            if (String.IsNullOrWhiteSpace(toolName)) throw new ArgumentNullException(nameof(toolName));
            if (resultText == null) throw new ArgumentNullException(nameof(resultText));

            McpToolErrorPayload? payload;
            try
            {
                payload = JsonHelper.Deserialize<McpToolErrorPayload>(resultText);
            }
            catch (JsonException)
            {
                // Not a JSON object; the caller's own deserialization owns that failure.
                return resultText;
            }

            if (payload == null || String.IsNullOrWhiteSpace(payload.Error)) return resultText;

            string code = String.IsNullOrWhiteSpace(payload.Code) ? "" : " [" + payload.Code + "]";
            throw new AssertionException(toolName + " was refused" + code + ": " + payload.Error);
        }

        #endregion
    }
}
