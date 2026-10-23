namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one MCP seam for abbreviated record ids. Every registered tool's arguments pass through it: each
    /// argument named <c>id</c> or ending in <c>Id</c> or <c>Ids</c>, at any depth, whose value is an
    /// abbreviation resolves through <see cref="AbbreviatedIdResolver"/> to the one full id the caller can see,
    /// and the tool receives and stores the full id. No visible match returns <c>not_found</c> and two return
    /// <c>ambiguous_id</c>, and the tool does not run. A delete or purge tool takes full ids only: an
    /// abbreviation there is refused with <see cref="FullIdRequiredCode"/>.
    /// </summary>
    public static class McpAbbreviatedIdArguments
    {
        #region Public-Members

        /// <summary>
        /// Code returned when a delete or purge tool receives an abbreviated id.
        /// </summary>
        public const string FullIdRequiredCode = "full_id_required";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Wrap a registration delegate so every tool it registers resolves abbreviated id arguments.
        /// </summary>
        /// <param name="register">Registration delegate to wrap.</param>
        /// <param name="database">Database driver.</param>
        /// <returns>The wrapping delegate.</returns>
        public static RegisterToolDelegate Wrap(RegisterToolDelegate register, DatabaseDriver database)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            return (name, description, schema, handler) => register(name, description, schema, Wrap(name, handler, database));
        }

        /// <summary>
        /// Wrap one tool handler so its abbreviated id arguments resolve before it runs.
        /// </summary>
        /// <param name="toolName">Tool name; a name containing <c>delete</c> or <c>purge</c> takes full ids only.</param>
        /// <param name="handler">Tool handler.</param>
        /// <param name="database">Database driver.</param>
        /// <returns>The wrapped handler.</returns>
        public static Func<JsonElement?, Task<object>> Wrap(string toolName, Func<JsonElement?, Task<object>> handler, DatabaseDriver database)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            bool fullIdsOnly = toolName.Contains("delete", StringComparison.OrdinalIgnoreCase)
                || toolName.Contains("purge", StringComparison.OrdinalIgnoreCase);
            return async args =>
            {
                if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object) return await handler(args).ConfigureAwait(false);
                JsonNode? root = JsonNode.Parse(args.Value.GetRawText());
                List<JsonValue> abbreviated = new List<JsonValue>();
                CollectAbbreviatedIds(root, false, abbreviated);
                if (abbreviated.Count == 0) return await handler(args).ConfigureAwait(false);

                if (fullIdsOnly)
                {
                    return new
                    {
                        Error = "This tool takes full record ids. Use the full id instead of " + abbreviated[0].GetValue<string>() + ".",
                        Code = FullIdRequiredCode
                    };
                }

                AuthContext caller = McpCallerContext.Require();
                foreach (JsonValue value in abbreviated)
                {
                    string given = value.GetValue<string>();
                    AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, given).ConfigureAwait(false);
                    string notFound = DescribeRecord(given) + " not found";
                    if (resolved.Ambiguous) return McpScopeDenial.CreateAmbiguous(notFound, resolved.Candidates);
                    if (!resolved.Resolved) return McpScopeDenial.Create(notFound, false);
                    value.ReplaceWith(resolved.Id);
                }
                return await handler(JsonSerializer.SerializeToElement(root)).ConfigureAwait(false);
            };
        }

        #endregion

        #region Private-Methods

        private static void CollectAbbreviatedIds(JsonNode? node, bool idNamed, List<JsonValue> found)
        {
            if (node is JsonObject obj)
            {
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                    CollectAbbreviatedIds(property.Value, IsIdName(property.Key), found);
            }
            else if (node is JsonArray array)
            {
                foreach (JsonNode? item in array) CollectAbbreviatedIds(item, idNamed, found);
            }
            else if (idNamed && node is JsonValue value
                && value.TryGetValue<string>(out string? text)
                && AbbreviatedIdResolver.IsAbbreviated(text))
            {
                found.Add(value);
            }
        }

        private static bool IsIdName(string name)
        {
            return String.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Id", StringComparison.Ordinal)
                || name.EndsWith("Ids", StringComparison.Ordinal);
        }

        private static string DescribeRecord(string id)
        {
            switch (id.Substring(0, 4))
            {
                case "msn_": return "Mission";
                case "vyg_": return "Voyage";
                case "obj_": return "Objective";
                case "chk_": return "Check run";
                case "inc_": return "Incident";
                case "mrg_": return "Merge entry";
                case "sig_": return "Signal";
                case "cpt_": return "Captain";
                case "vsl_": return "Vessel";
                case "flt_": return "Fleet";
                case "dck_": return "Dock";
                case "rel_": return "Release";
                default: return "Deployment";
            }
        }

        #endregion
    }
}
