namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Server.Mcp;

    /// <summary>MCP tools for discovering and importing existing local vessel checkouts.</summary>
    public static class McpVesselCheckoutTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Register local checkout tools, including named unavailable responses when no service is supplied.</summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="service">Shared local checkout import service, or null on standalone hosts.</param>
        public static void Register(RegisterToolDelegate register, VesselCheckoutImportService? service)
        {
            register(
                "armada_discover_vessel_checkouts",
                "List configured local checkouts that can be imported as vessels",
                new { type = "object", properties = new { }, additionalProperties = false },
                async _ =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!VesselCheckoutImportService.IsUnscopedGlobalAdminCaller(caller)) return (object)new { Error = "forbidden", Message = "Local checkout discovery requires a global administrator operator." };
                    if (service == null) return McpToolUnavailable.Create("local checkout import");
                    try
                    {
                        return (object)await service.DiscoverAsync(caller, McpCallerContext.CallToken).ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return (object)new { Error = "forbidden", Message = "Local checkout discovery requires a global administrator operator." };
                    }
                });

            register(
                "armada_import_vessel_checkout",
                "Import an existing local checkout as an Armada vessel",
                new
                {
                    type = "object",
                    properties = new
                    {
                        checkoutPath = new { type = "string", description = "Canonical path of a discovered local checkout" },
                        name = new { type = "string", description = "Vessel display name" },
                        fleetId = new { type = "string", description = "Optional fleet ID" }
                    },
                    required = new[] { "checkoutPath", "name" },
                    additionalProperties = false
                },
                async args =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!VesselCheckoutImportService.IsUnscopedGlobalAdminCaller(caller)) return (object)new { Error = "forbidden", Message = "Local checkout import requires a global administrator operator." };
                    if (service == null) return McpToolUnavailable.Create("local checkout import");
                    VesselCheckoutImportRequest? request;
                    try
                    {
                        request = args.HasValue
                            ? JsonSerializer.Deserialize<VesselCheckoutImportRequest>(args.Value, _JsonOptions)
                            : null;
                    }
                    catch (JsonException)
                    {
                        return (object)new { Error = "bad_request", Message = "The local checkout import request is invalid." };
                    }
                    if (request == null) return (object)new { Error = "bad_request", Message = "The local checkout import request is required." };
                    try
                    {
                        return (object)await service.ImportAsync(caller, request, McpCallerContext.CallToken).ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return (object)new { Error = "forbidden", Message = "Local checkout import requires a global administrator operator." };
                    }
                    catch (ArgumentException)
                    {
                        return (object)new { Error = "bad_request", Message = "The local checkout path or import request is invalid." };
                    }
                    catch (VesselCheckoutImportUnavailableException)
                    {
                        return McpToolUnavailable.Create("local checkout import");
                    }
                    catch (InvalidOperationException)
                    {
                        return (object)new { Error = "conflict", Message = "The local checkout conflicts with an existing vessel." };
                    }
                });
        }

    }
}
