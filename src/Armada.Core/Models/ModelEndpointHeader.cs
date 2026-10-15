namespace Armada.Core.Models
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// An extra HTTP header a model endpoint sends with every request (for example the two Cloudflare Access headers
    /// in front of a self-hosted Ollama). Values are treated as secrets: accepted on create and update, stored, and
    /// used for calls, but never returned; reads carry <see cref="HasValue"/> instead.
    /// </summary>
    public class ModelEndpointHeader
    {
        #region Public-Members

        /// <summary>
        /// Header name, for example <c>CF-Access-Client-Id</c>.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => _Name = (value ?? String.Empty).Trim();
        }

        /// <summary>
        /// Header value. Never serialized in read responses.
        /// </summary>
        [JsonIgnore]
        public string? Value
        {
            get => _Value;
            set
            {
                _Value = value;
                _HasValue = null;
            }
        }

        /// <summary>
        /// Write-only JSON input for the value. Null or omitted on update keeps the stored value of the header with
        /// the same name.
        /// </summary>
        [JsonPropertyName("value")]
        public string? ValueInput
        {
            get => null;
            set => Value = value;
        }

        /// <summary>
        /// Whether a value is stored. Serialized in read responses; the value itself is not.
        /// </summary>
        [JsonInclude]
        public bool HasValue
        {
            get => _HasValue ?? !String.IsNullOrEmpty(_Value);
            private set => _HasValue = value;
        }

        #endregion

        #region Private-Members

        private string _Name = String.Empty;
        private string? _Value = null;
        private bool? _HasValue = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelEndpointHeader()
        {
        }

        /// <summary>
        /// Instantiate with a name and value.
        /// </summary>
        /// <param name="name">Header name.</param>
        /// <param name="value">Header value.</param>
        public ModelEndpointHeader(string name, string? value)
        {
            Name = name;
            Value = value;
        }

        #endregion
    }
}
