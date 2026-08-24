namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Shrinks an MCP tool result by previewing its long free-text fields.
    /// <para>
    /// A tool that returns a collection returns its members whole, and one long field
    /// on each member is almost always the whole payload: a Description, an
    /// AcceptanceCriteria block, a captured Output. Measured on one live fleet,
    /// <c>list_objectives</c> returned 388,594 characters and
    /// <c>armada_campaign_status</c> 245,233, both far past the caller's tool output
    /// limit. An agent then spends turns spilling the payload to a file and parsing it
    /// back, which is visible in the autonomous cycle transcripts.
    /// </para>
    /// <para>
    /// This is deliberately OPT-IN per tool rather than applied to every result. Some
    /// results must never be trimmed -- a mission log, a diff, a note addressed to the
    /// caller -- and a blanket truncation would hide exactly the payload someone asked
    /// for. Truncating the board once hid five complete reports; the lesson is that the
    /// decision belongs to the tool, not to the transport.
    /// </para>
    /// </summary>
    public static class McpResultPreview
    {
        #region Public-Members

        /// <summary>
        /// Characters of a long field kept when previewing.
        /// <para>
        /// This bounds the result, so it is chosen against the page size rather than by
        /// taste. These tools page at 50 records, and a record carries two or three long
        /// fields, so 250 characters holds a full page near 40 KB. Raising it to 400 put
        /// the same page back over the caller's limit. The record's Id and Title are
        /// separate short fields and are never trimmed, so a preview only has to say
        /// enough to decide whether to fetch the rest.
        /// </para>
        /// </summary>
        public const int DefaultPreviewChars = 250;

        /// <summary>
        /// Elements of a long primitive array kept when previewing.
        /// <para>
        /// The bulk of a rich record is usually not one long string but MANY SHORT ONES:
        /// measured on one page of objectives, EvidenceLinks held 33,967 characters and
        /// AcceptanceCriteria 28,782, while every individual element was far below the
        /// string preview limit and so survived untouched. Capping the array is what
        /// actually shrinks those records.
        /// </para>
        /// <para>
        /// Only arrays of PRIMITIVES are capped. An array of objects is the collection
        /// itself -- the records the caller asked for -- and dropping members of it would
        /// silently lose data rather than abbreviate it.
        /// </para>
        /// </summary>
        public const int DefaultPreviewItems = 5;

        /// <summary>
        /// Page size used by MCP list tools when the caller does not ask for one.
        /// <para>
        /// Previewing fields and capping arrays takes a page of objectives from 388,594
        /// characters to 185,301, and no further: what remains is simply fifty rich
        /// records at roughly 3.7 KB each. The record COUNT is then the only lever left.
        /// Measured on the same fleet: 50 records give 185,301 characters, 25 give
        /// 101,099, 20 give 79,596, 15 give 57,247 -- which is level with the size that
        /// was already failing -- and 10 give 37,710. Ten is therefore the largest page
        /// with real headroom.
        /// </para>
        /// <para>
        /// This applies to the MCP surface only, where the output limit exists. REST
        /// callers keep their own default. Every response carries TotalRecords and
        /// TotalPages, and an explicit pageSize is always honoured.
        /// </para>
        /// </summary>
        public const int DefaultMcpPageSize = 10;

        /// <summary>
        /// True when the caller did not supply <c>pageSize</c>, so a list tool should
        /// apply <see cref="DefaultMcpPageSize"/> rather than the service default.
        /// </summary>
        /// <param name="args">Raw tool arguments.</param>
        /// <returns>True when no explicit page size was given.</returns>
        public static bool WantsDefaultPageSize(JsonElement? args)
        {
            if (args == null || !args.HasValue) return true;
            if (args.Value.ValueKind != JsonValueKind.Object) return true;
            return !args.Value.TryGetProperty("pageSize", out JsonElement size)
                || size.ValueKind == JsonValueKind.Null;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return <paramref name="payload"/> with every string longer than
        /// <paramref name="previewChars"/> replaced by a preview, each gaining a
        /// companion <c>&lt;name&gt;Length</c> property carrying the original length,
        /// plus a top-level <c>TruncatedFieldCount</c> so a reader can see at a glance
        /// whether anything was trimmed.
        /// </summary>
        /// <param name="payload">Result object to shrink.</param>
        /// <param name="includeFullContent">When true, the payload is returned unchanged.</param>
        /// <param name="previewChars">Characters to keep. Defaults to <see cref="DefaultPreviewChars"/>.</param>
        /// <returns>The shrunk payload, or the original when nothing needed trimming.</returns>
        public static object Apply(
            object payload,
            bool includeFullContent,
            int previewChars = DefaultPreviewChars,
            int previewItems = DefaultPreviewItems)
        {
            if (payload == null) return payload!;
            if (includeFullContent) return payload;
            if (previewChars < 1) previewChars = DefaultPreviewChars;
            if (previewItems < 1) previewItems = DefaultPreviewItems;

            JsonNode? root;
            try
            {
                root = JsonSerializer.SerializeToNode(payload);
            }
            catch (NotSupportedException)
            {
                // A payload the serializer refuses is returned untouched rather than
                // lost. Shrinking is a courtesy; the caller's result is not.
                return payload;
            }

            if (root == null) return payload;

            int truncated = Shrink(root, previewChars, previewItems);
            if (truncated == 0) return payload;

            if (root is JsonObject rootObject)
            {
                rootObject["TruncatedFieldCount"] = truncated;
                return rootObject;
            }

            // A bare array has nowhere to carry the count, so it is wrapped once. The
            // count matters more than the shape: a reader who cannot see that fields
            // were trimmed will read a preview as the whole record.
            return new JsonObject
            {
                ["Items"] = root,
                ["TruncatedFieldCount"] = truncated
            };
        }

        #endregion

        #region Private-Methods

        private static bool IsPrimitiveArray(JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject || item is JsonArray) return false;
            }

            return true;
        }

        private static int Shrink(JsonNode node, int previewChars, int previewItems)
        {
            int truncated = 0;

            if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item != null) truncated += Shrink(item, previewChars, previewItems);
                }

                return truncated;
            }

            if (node is not JsonObject obj) return 0;

            // Collected first: the companion length properties are added to the same
            // object being enumerated, and mutating during enumeration would throw.
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> longFields =
                new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            System.Collections.Generic.List<JsonNode> children =
                new System.Collections.Generic.List<JsonNode>();
            System.Collections.Generic.List<string> longArrays =
                new System.Collections.Generic.List<string>();

            foreach (System.Collections.Generic.KeyValuePair<string, JsonNode?> property in obj)
            {
                if (property.Value == null) continue;

                if (property.Value is JsonValue value
                    && value.TryGetValue(out string? text)
                    && text != null
                    && text.Length > previewChars)
                {
                    longFields.Add(new System.Collections.Generic.KeyValuePair<string, string>(property.Key, text));
                }
                else if (property.Value is JsonArray childArray
                    && childArray.Count > previewItems
                    && IsPrimitiveArray(childArray))
                {
                    longArrays.Add(property.Key);
                }
                else if (property.Value is JsonObject || property.Value is JsonArray)
                {
                    children.Add(property.Value);
                }
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> field in longFields)
            {
                obj[field.Key] = field.Value.Substring(0, previewChars);
                obj[field.Key + "Length"] = field.Value.Length;
                truncated++;
            }

            foreach (string key in longArrays)
            {
                JsonArray original = (JsonArray)obj[key]!;
                int total = original.Count;
                JsonArray kept = new JsonArray();
                for (int i = 0; i < previewItems; i++)
                {
                    JsonNode? element = original[i];
                    kept.Add(element == null ? null : JsonNode.Parse(element.ToJsonString()));
                }

                obj[key] = kept;
                obj[key + "Count"] = total;
                truncated++;
            }

            foreach (JsonNode child in children) truncated += Shrink(child, previewChars, previewItems);

            return truncated;
        }

        #endregion
    }
}
