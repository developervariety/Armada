namespace Armada.Server
{
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Evaluates captain access to Armada's MCP tool catalog.
    /// </summary>
    public class CaptainToolService
    {
        private readonly MuxCliService _muxCli;
        private readonly List<CaptainToolSummary> _armadaTools;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="armadaTools">Armada MCP tool catalog.</param>
        public CaptainToolService(LoggingModule logging, IEnumerable<CaptainToolSummary> armadaTools)
        {
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (armadaTools == null) throw new ArgumentNullException(nameof(armadaTools));

            _muxCli = new MuxCliService(logging);
            _armadaTools = armadaTools
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            EnsureCaptainToolsEntry();
        }

        /// <summary>
        /// Describe the Armada MCP tools available to a captain.
        /// </summary>
        /// <param name="captain">Captain to inspect.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Availability summary and tool list.</returns>
        public async Task<CaptainToolAccessResult> DescribeAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            CaptainToolAccessResult result = new CaptainToolAccessResult
            {
                CaptainId = captain.Id,
                CaptainName = captain.Name,
                Runtime = captain.Runtime.ToString(),
                ArmadaToolCount = _armadaTools.Count,
                Tools = ShouldExposeCatalog(captain.Runtime)
                    ? CloneCatalog()
                    : new List<CaptainToolSummary>()
            };

            switch (captain.Runtime)
            {
                case AgentRuntimeEnum.Mux:
                    await PopulateMuxAvailabilityAsync(captain, result, token).ConfigureAwait(false);
                    break;
                case AgentRuntimeEnum.Custom:
                    result.ToolsAccessible = false;
                    result.AvailabilityVerified = false;
                    result.AvailabilitySource = "unsupported-runtime";
                    result.Summary = "Custom captains are not introspected by Armada, so Armada cannot verify or describe a runtime tool inventory for this captain.";
                    break;
                default:
                    result.ToolsAccessible = result.Tools.Count > 0;
                    result.AvailabilityVerified = false;
                    result.AvailabilitySource = "runtime-assumption";
                    result.Summary = "Armada is showing the current Armada MCP catalog for this server. Built-in runtimes are not actively probed per captain, so availability is inferred rather than verified.";
                    break;
            }

            return result;
        }

        private async Task PopulateMuxAvailabilityAsync(Captain captain, CaptainToolAccessResult result, CancellationToken token)
        {
            try
            {
                MuxProbeResult probe = await _muxCli.ProbeAsync(captain, token).ConfigureAwait(false);
                result.EndpointName = String.IsNullOrWhiteSpace(probe.EndpointName) ? null : probe.EndpointName;
                result.ToolsEnabled = probe.ToolsEnabled;
                result.EffectiveToolCount = probe.EffectiveToolCount;
                result.AvailabilitySource = "mux-probe";
                result.AvailabilityVerified = probe.Success;

                if (probe.Success)
                {
                    result.ToolsAccessible = probe.ToolsEnabled && probe.EffectiveToolCount > 0;
                    result.Summary = result.ToolsAccessible
                        ? "Mux probe succeeded and the endpoint reported tool calling enabled. Armada is showing the Armada MCP catalog this captain is expected to reach through the configured endpoint."
                        : "Mux probe succeeded, but the endpoint did not report a tool-enabled configuration for Armada missions.";
                }
                else
                {
                    result.ToolsAccessible = false;
                    result.Summary = String.IsNullOrWhiteSpace(probe.ErrorMessage)
                        ? "Mux probe did not succeed, so Armada could not verify runtime tool access."
                        : "Mux probe did not succeed: " + probe.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                result.ToolsAccessible = false;
                result.AvailabilityVerified = false;
                result.AvailabilitySource = "mux-probe-error";
                result.Summary = "Mux probe failed before Armada could verify tool access: " + ex.Message;
            }
        }

        private List<CaptainToolSummary> CloneCatalog()
        {
            return _armadaTools
                .Select(tool => new CaptainToolSummary
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchemaJson = tool.InputSchemaJson
                })
                .ToList();
        }

        private static bool ShouldExposeCatalog(AgentRuntimeEnum runtime)
        {
            return runtime != AgentRuntimeEnum.Custom;
        }

        private void EnsureCaptainToolsEntry()
        {
            if (_armadaTools.Any(t => String.Equals(t.Name, "get_captain_tools", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            object schema = new
            {
                type = "object",
                properties = new
                {
                    captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                },
                required = new[] { "captainId" }
            };

            _armadaTools.Add(new CaptainToolSummary
            {
                Name = "get_captain_tools",
                Description = "Describe the Armada MCP tools available to a specific captain.",
                InputSchemaJson = JsonSerializer.Serialize(schema)
            });

            _armadaTools.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        }
    }
}
