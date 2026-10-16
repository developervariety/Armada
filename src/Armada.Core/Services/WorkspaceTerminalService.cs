namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Hosting;
    using Armada.Core.Models;

    /// <summary>
    /// The Workspace's Open in Terminal: opens a terminal window at a vessel's checkout on the computer that holds it.
    /// A checkout on a Harbor opens on that Harbor's desktop (a Harbor that advertises
    /// <see cref="HarborProtocol.OpenTerminalCapability"/>; the Harbor checks the folder is its checkout of the vessel).
    /// A working directory on the Admiral host opens on the Admiral's desktop only when the request came from that
    /// computer and the Admiral can reach a desktop. Every expected reason not to open is an answer, not an exception, so
    /// the caller can offer the path instead.
    /// </summary>
    public class WorkspaceTerminalService
    {
        #region Public-Members

        /// <summary>
        /// How long to wait for a Harbor to answer.
        /// </summary>
        public int HarborTimeoutMs { get; set; } = 15000;

        /// <summary>
        /// Opens a terminal on the Admiral's desktop in a folder and returns null, or returns why not. Defaults to
        /// <see cref="DesktopShellLauncher.OpenTerminal"/>; tests replace it.
        /// </summary>
        public Func<string, string?> LocalLauncher
        {
            get { return _LocalLauncher; }
            set { _LocalLauncher = value ?? throw new ArgumentNullException(nameof(LocalLauncher)); }
        }

        #endregion

        #region Private-Members

        private readonly VesselHostResolver _Hosts;
        private Func<string, string?> _LocalLauncher = (string path) => DesktopShellLauncher.OpenTerminal(path, "The Admiral");

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="hosts">Finds where a vessel's checkout lives.</param>
        /// <exception cref="ArgumentNullException">Thrown when hosts is null.</exception>
        public WorkspaceTerminalService(VesselHostResolver hosts)
        {
            _Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Open a terminal at the vessel's checkout.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="vessel">Vessel (already checked visible to the caller).</param>
        /// <param name="requestFromAdmiralHost">Whether the request came from the Admiral's own computer (loopback).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Whether it opened, where, and why not.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller or vessel is null.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when the caller may not open a terminal on the host that
        /// holds the checkout: on a Harbor, someone other than its owner or an admin of its tenant; on the Admiral host,
        /// a non-admin.</exception>
        public async Task<WorkspaceOpenTerminalResult> OpenAsync(AuthContext caller, Vessel vessel, bool requestFromAdmiralHost, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            VesselHostResolution resolution = await _Hosts.TryResolveAsync(vessel, caller.UserId, token).ConfigureAwait(false);
            VesselHost? host = resolution.Host;
            if (host == null)
            {
                return new WorkspaceOpenTerminalResult
                {
                    Opened = false,
                    Location = WorkspaceTerminalLocationEnum.None,
                    Path = String.IsNullOrWhiteSpace(vessel.WorkingDirectory) ? null : vessel.WorkingDirectory,
                    Message = resolution.Message ?? ("Vessel " + vessel.Name + " has no checkout to open a terminal in.")
                };
            }

            if (host.IsHarbor) return await OpenOnHarborAsync(caller, vessel, host, token).ConfigureAwait(false);
            return OpenOnAdmiralHost(caller, host, requestFromAdmiralHost);
        }

        #endregion

        #region Private-Methods

        private async Task<WorkspaceOpenTerminalResult> OpenOnHarborAsync(AuthContext caller, Vessel vessel, VesselHost host, CancellationToken token)
        {
            string harborId = host.HarborId!;
            string name = host.HarborDisplayName ?? harborId;
            WorkspaceOpenTerminalResult result = new WorkspaceOpenTerminalResult
            {
                Location = WorkspaceTerminalLocationEnum.Harbor,
                Path = host.WorkingDirectory,
                HostName = name
            };

            HarborConnectionManager? harbors = _Hosts.Harbors;
            HarborConnection? connection = null;
            if (harbors == null || !harbors.TryGetConnection(harborId, out connection) || connection == null)
            {
                result.Message = "Harbor " + name + " is offline, so it cannot open a terminal.";
                return result;
            }

            if (!MayUseHarbor(caller, connection))
                throw new UnauthorizedAccessException("Only the Harbor's owner or an admin of its tenant can open a terminal on Harbor " + name + ".");

            if (!connection.OpensTerminals)
            {
                result.Message = "Update the Harbor on " + name + " to open a terminal from the dashboard.";
                return result;
            }

            HarborFileRequest request = new HarborFileRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Operation = HarborFileOperationEnum.OpenTerminal,
                Root = host.WorkingDirectory,
                Path = String.Empty,
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                RepoUrl = String.IsNullOrWhiteSpace(vessel.RepoUrl) ? null : vessel.RepoUrl
            };

            HarborFileResult? answer = await harbors.SendFileAsync(harborId, request, HarborTimeoutMs, token).ConfigureAwait(false);
            if (answer == null)
            {
                result.Message = "Harbor " + name + " did not answer within " + (HarborTimeoutMs / 1000) + " seconds.";
                return result;
            }

            if (!answer.Success)
            {
                string why = String.IsNullOrWhiteSpace(answer.Message) ? "it did not say why" : answer.Message!.TrimEnd('.');
                result.Message = "Harbor " + name + " could not open a terminal: " + why + ".";
                return result;
            }

            result.Opened = true;
            return result;
        }

        private WorkspaceOpenTerminalResult OpenOnAdmiralHost(AuthContext caller, VesselHost host, bool requestFromAdmiralHost)
        {
            if (!caller.IsAdmin && !caller.IsTenantAdmin)
                throw new UnauthorizedAccessException("Only an admin can open a terminal on the Admiral's computer.");

            WorkspaceOpenTerminalResult result = new WorkspaceOpenTerminalResult
            {
                Location = WorkspaceTerminalLocationEnum.AdmiralHost,
                Path = host.WorkingDirectory,
                HostName = Environment.MachineName
            };

            if (!requestFromAdmiralHost)
            {
                result.Message = "The working directory is on the Admiral's computer and this browser is elsewhere, so a terminal opened there would not be seen.";
                return result;
            }

            string? error = _LocalLauncher(host.WorkingDirectory);
            if (error != null)
            {
                result.Message = error;
                return result;
            }

            result.Opened = true;
            return result;
        }

        private static bool MayUseHarbor(AuthContext caller, HarborConnection connection)
        {
            if (caller.IsAdmin) return true;
            if (!String.IsNullOrEmpty(connection.UserId) && String.Equals(connection.UserId, caller.UserId, StringComparison.Ordinal)) return true;
            return caller.IsTenantAdmin
                && !String.IsNullOrEmpty(connection.TenantId)
                && String.Equals(connection.TenantId, caller.TenantId, StringComparison.Ordinal);
        }

        #endregion
    }
}
