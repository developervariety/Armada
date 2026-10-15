namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// A model endpoint's extra header with its value, in the shape stored in the database and shipped to a Harbor.
    /// Unlike <see cref="ModelEndpointHeader"/>, the value is serialized, so this type must never be returned by the API.
    /// </summary>
    public class ModelEndpointHeaderRecord
    {
        #region Public-Members

        /// <summary>
        /// Header name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Header value (secret).
        /// </summary>
        public string? Value { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelEndpointHeaderRecord()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Records for an endpoint's headers.
        /// </summary>
        /// <param name="headers">Headers, or null.</param>
        /// <returns>Records. Never null.</returns>
        public static List<ModelEndpointHeaderRecord> FromHeaders(IEnumerable<ModelEndpointHeader>? headers)
        {
            List<ModelEndpointHeaderRecord> records = new List<ModelEndpointHeaderRecord>();
            if (headers == null) return records;
            foreach (ModelEndpointHeader header in headers)
            {
                if (header == null) continue;
                records.Add(new ModelEndpointHeaderRecord { Name = header.Name, Value = header.Value });
            }

            return records;
        }

        /// <summary>
        /// Headers for stored or shipped records.
        /// </summary>
        /// <param name="records">Records, or null.</param>
        /// <returns>Headers. Never null.</returns>
        public static List<ModelEndpointHeader> ToHeaders(IEnumerable<ModelEndpointHeaderRecord>? records)
        {
            List<ModelEndpointHeader> headers = new List<ModelEndpointHeader>();
            if (records == null) return headers;
            foreach (ModelEndpointHeaderRecord record in records)
            {
                if (record == null) continue;
                headers.Add(new ModelEndpointHeader(record.Name, record.Value));
            }

            return headers;
        }

        /// <summary>
        /// Serialize an endpoint's headers, values included, for the database.
        /// </summary>
        /// <param name="headers">Headers, or null.</param>
        /// <returns>JSON, or null when there are no headers.</returns>
        public static string? SerializeHeaders(IEnumerable<ModelEndpointHeader>? headers)
        {
            List<ModelEndpointHeaderRecord> records = FromHeaders(headers);
            return records.Count == 0 ? null : JsonSerializer.Serialize(records);
        }

        /// <summary>
        /// Deserialize headers stored by <see cref="SerializeHeaders"/>.
        /// </summary>
        /// <param name="json">JSON, or null.</param>
        /// <returns>Headers. Never null.</returns>
        public static List<ModelEndpointHeader> DeserializeHeaders(string? json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new List<ModelEndpointHeader>();
            return ToHeaders(JsonSerializer.Deserialize<List<ModelEndpointHeaderRecord>>(json!));
        }

        #endregion
    }
}
