namespace Armada.Server.WebSocket
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// The error text and code of a refused shared voyage dispatch, lifted to the top level of a
    /// WebSocket <c>command.error</c>. The full refusal body travels beside it unchanged.
    /// </summary>
    public sealed class WebSocketDispatchRefusal
    {
        #region Public-Members

        /// <summary>
        /// Human-readable refusal message.
        /// </summary>
        public string Error { get; set; } = String.Empty;

        /// <summary>
        /// Machine-readable refusal code, or null when the refusal body carries none.
        /// </summary>
        public string? Code { get; set; } = null;

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _ReadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the error and code from a failed dispatch result.
        /// </summary>
        /// <param name="result">Failed dispatch result.</param>
        /// <returns>The refusal; its error names the HTTP-equivalent status when the body has no error text.</returns>
        public static WebSocketDispatchRefusal From(VoyageDispatchResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            string json = JsonSerializer.Serialize(result.Value, result.Value.GetType());
            WebSocketDispatchRefusal refusal;
            try
            {
                refusal = JsonSerializer.Deserialize<WebSocketDispatchRefusal>(json, _ReadOptions) ?? new WebSocketDispatchRefusal();
            }
            catch (JsonException)
            {
                // A body that is not an object carries no error field; it is still returned whole as the detail.
                refusal = new WebSocketDispatchRefusal { Error = "Voyage dispatch refused with status " + result.StatusCode + ": " + json };
            }
            if (String.IsNullOrWhiteSpace(refusal.Error))
                refusal.Error = "Voyage dispatch refused with status " + result.StatusCode + ".";
            return refusal;
        }

        #endregion
    }
}
