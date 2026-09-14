namespace Armada.Test.Automated
{
    using System;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;

    /// <summary>
    /// Stores captain rows that no API may create. Captain state, assignment and process are
    /// server-owned, so every create and update route refuses them; a fixture that needs a
    /// captain already at work writes the row through the running server's database.
    /// </summary>
    public static class ServerCaptainFixtures
    {
        #region Public-Methods

        /// <summary>
        /// Store a Working captain in the default tenant.
        /// </summary>
        /// <param name="server">Running in-process server.</param>
        /// <param name="captainId">Captain ID, or null for a generated one.</param>
        /// <param name="name">Captain name.</param>
        /// <param name="runtime">Agent runtime.</param>
        /// <param name="missionId">Mission the captain owns, or null.</param>
        /// <param name="processId">Process the captain owns, or null.</param>
        /// <param name="allowedPersonas">JSON array of allowed personas, or null for any.</param>
        /// <returns>The stored captain.</returns>
        public static async Task<Captain> CreateWorkingCaptainAsync(
            ArmadaServer server,
            string? captainId,
            string name,
            AgentRuntimeEnum runtime,
            string? missionId,
            int? processId,
            string? allowedPersonas)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            Captain captain = new Captain(name, runtime);
            if (!String.IsNullOrEmpty(captainId)) captain.Id = captainId;
            captain.TenantId = Armada.Core.Constants.DefaultTenantId;
            captain.UserId = Armada.Core.Constants.DefaultUserId;
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = missionId;
            captain.ProcessId = processId;
            captain.AllowedPersonas = allowedPersonas;
            DatabaseDriver database = (DatabaseDriver)typeof(ArmadaServer)
                .GetField("_Database", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!;
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        #endregion
    }
}
