namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Idempotent startup bootstrap that ensures every vessel has a learned-facts playbook
    /// and that the playbook is attached through DefaultPlaybooks.
    /// </summary>
    public class ReflectionMemoryBootstrapService : IReflectionMemoryBootstrapService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private const string _Header = "[ReflectionMemoryBootstrapService] ";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ReflectionMemoryBootstrapService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task BootstrapAsync(CancellationToken token = default)
        {
            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            _Logging.Info(_Header + "bootstrapping learned playbooks for " + vessels.Count + " vessels");

            foreach (Vessel vessel in vessels)
            {
                if (String.IsNullOrEmpty(vessel.TenantId)) continue;

                try
                {
                    await BootstrapVesselAsync(vessel, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to bootstrap vessel " + vessel.Id + ": " + ex.Message);
                }
            }
        }

        #endregion

        #region Private-Methods

        private async Task BootstrapVesselAsync(Vessel vessel, CancellationToken token)
        {
            string sanitizedName = SanitizeName(vessel.Name);
            string fileName = "vessel-" + sanitizedName + "-learned.md";

            Playbook? existing = await _Database.Playbooks.ReadByFileNameAsync(vessel.TenantId!, fileName, token).ConfigureAwait(false);
            if (existing == null)
            {
                Playbook playbook = new Playbook(fileName, "# Vessel Learned Facts\n\nNo accepted reflection facts yet.");
                playbook.TenantId = vessel.TenantId;
                playbook.Description = "Learned facts for vessel " + vessel.Name + ". Updated by accepted reflection missions.";
                existing = await _Database.Playbooks.CreateAsync(playbook, token).ConfigureAwait(false);
                _Logging.Info(_Header + "created learned playbook " + existing.Id + " for vessel " + vessel.Id);
            }

            List<SelectedPlaybook> defaults = vessel.GetDefaultPlaybooks();
            bool alreadyPresent = defaults.Exists(sp => sp.PlaybookId == existing.Id);
            if (alreadyPresent) return;

            defaults.Add(new SelectedPlaybook
            {
                PlaybookId = existing.Id,
                DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference
            });
            vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults, _JsonOptions);
            await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
            _Logging.Info(_Header + "attached learned playbook " + existing.Id + " to vessel " + vessel.Id);
        }

        private static string SanitizeName(string name)
        {
            string lower = name.ToLowerInvariant();
            string replaced = Regex.Replace(lower, "[^a-z0-9]+", "-");
            return replaced.Trim('-');
        }

        #endregion
    }
}
