namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for validating and snapshotting playbooks.
    /// </summary>
    public class PlaybookService : IPlaybookService
    {
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlaybookService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public void Validate(Playbook playbook)
        {
            if (playbook == null) throw new ArgumentNullException(nameof(playbook));
            if (String.IsNullOrWhiteSpace(playbook.FileName))
                throw new InvalidOperationException("Playbook filename is required.");
            if (!playbook.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Playbook filename must end with .md.");
            if (String.IsNullOrWhiteSpace(playbook.Content))
                throw new InvalidOperationException("Playbook content is required.");

            playbook.FileName = playbook.FileName.Trim();
            playbook.LastUpdateUtc = DateTime.UtcNow;
        }

        /// <inheritdoc />
        public async Task<List<Playbook>> ResolveSelectionsAsync(string tenantId, List<SelectedPlaybook> selections, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (selections == null) return new List<Playbook>();

            List<Playbook> resolved = new List<Playbook>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (SelectedPlaybook selection in selections)
            {
                if (selection == null || String.IsNullOrWhiteSpace(selection.PlaybookId))
                    throw new InvalidOperationException("Every selected playbook must include a playbookId.");

                if (!seen.Add(selection.PlaybookId))
                    throw new InvalidOperationException("Duplicate playbook selection: " + selection.PlaybookId);

                // Inline-content selections bypass DB validation entirely -- they ship
                // a compile-time playbook (recovery flows) and intentionally have no
                // curated playbook row.
                if (!String.IsNullOrEmpty(selection.InlineFullContent)) continue;

                Playbook? playbook = await _Database.Playbooks.ReadAsync(tenantId, selection.PlaybookId, token).ConfigureAwait(false);
                if (playbook == null)
                    throw new InvalidOperationException("Playbook not found: " + selection.PlaybookId);
                if (!playbook.Active)
                    throw new InvalidOperationException("Playbook is inactive: " + playbook.FileName);

                resolved.Add(playbook);
            }

            return resolved;
        }

        /// <inheritdoc />
        public async Task<List<MissionPlaybookSnapshot>> CreateSnapshotsAsync(string tenantId, List<SelectedPlaybook>? selections, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (selections == null || selections.Count == 0) return new List<MissionPlaybookSnapshot>();

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<MissionPlaybookSnapshot> snapshots = new List<MissionPlaybookSnapshot>();

            foreach (SelectedPlaybook selection in selections)
            {
                if (selection == null || String.IsNullOrWhiteSpace(selection.PlaybookId))
                    throw new InvalidOperationException("Every selected playbook must include a playbookId.");
                if (!seen.Add(selection.PlaybookId))
                    throw new InvalidOperationException("Duplicate playbook selection: " + selection.PlaybookId);

                if (!String.IsNullOrEmpty(selection.InlineFullContent))
                {
                    snapshots.Add(new MissionPlaybookSnapshot
                    {
                        PlaybookId = selection.PlaybookId,
                        FileName = selection.PlaybookId + ".md",
                        Description = null,
                        Content = selection.InlineFullContent,
                        DeliveryMode = selection.DeliveryMode,
                        SourceLastUpdateUtc = null
                    });
                    continue;
                }

                Playbook? playbook = await _Database.Playbooks.ReadAsync(tenantId, selection.PlaybookId, token).ConfigureAwait(false);
                if (playbook == null)
                    throw new InvalidOperationException("Playbook not found: " + selection.PlaybookId);
                if (!playbook.Active)
                    throw new InvalidOperationException("Playbook is inactive: " + playbook.FileName);

                snapshots.Add(new MissionPlaybookSnapshot
                {
                    PlaybookId = playbook.Id,
                    FileName = playbook.FileName,
                    Description = playbook.Description,
                    Content = playbook.Content,
                    DeliveryMode = selection.DeliveryMode,
                    SourceLastUpdateUtc = playbook.LastUpdateUtc
                });
            }

            return snapshots;
        }
    }
}
