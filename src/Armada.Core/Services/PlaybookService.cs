namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Authorization;
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

        /// <summary>
        /// Read a playbook by id within the caller's scope: a global administrator reads any playbook, anyone
        /// else only the playbooks of its own tenant.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Playbook identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The playbook, or null when absent or outside the caller's scope.</returns>
        public Task<Playbook?> ReadForCallerAsync(AuthContext caller, string? id, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return Task.FromResult<Playbook?>(null);
            if (caller.IsAdmin) return _Database.Playbooks.ReadAsync(id!.Trim(), token);
            return _Database.Playbooks.ReadAsync(OwnershipPolicy.TenantOf(caller), id!.Trim(), token);
        }

        /// <summary>
        /// Create a playbook in the caller's tenant. The server generates the id and timestamps; the file name
        /// must end in .md and be unique inside the tenant.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Playbook>> CreateAsync(AuthContext caller, PlaybookWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (request == null) request = new PlaybookWriteRequest();

            string fileName = (request.FileName ?? "").Trim();
            if (fileName.Length == 0) return RecordWriteResult<Playbook>.Invalid("fileName is required");
            if (String.IsNullOrWhiteSpace(request.Content)) return RecordWriteResult<Playbook>.Invalid("content is required");

            Playbook playbook = new Playbook(fileName, request.Content!);
            playbook.TenantId = OwnershipPolicy.TenantOf(caller);
            playbook.UserId = OwnershipPolicy.UserOf(caller);
            playbook.Description = String.IsNullOrEmpty(request.Description) ? null : request.Description;
            if (request.Active.HasValue) playbook.Active = request.Active.Value;

            string? invalid = ValidationError(playbook);
            if (invalid != null) return RecordWriteResult<Playbook>.Invalid(invalid);
            if (await _Database.Playbooks.ExistsByFileNameAsync(playbook.TenantId, playbook.FileName, token).ConfigureAwait(false))
                return RecordWriteResult<Playbook>.Conflict("A playbook with that file name already exists.");

            Playbook created = await _Database.Playbooks.CreateAsync(playbook, token).ConfigureAwait(false);
            return RecordWriteResult<Playbook>.Success(created);
        }

        /// <summary>
        /// Update a playbook within the caller's scope. Only supplied fields change.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Playbook identifier.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Playbook>> UpdateAsync(AuthContext caller, string? id, PlaybookWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return RecordWriteResult<Playbook>.Invalid("id is required");
            if (request == null) request = new PlaybookWriteRequest();

            Playbook? existing = await ReadForCallerAsync(caller, id, token).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Playbook>.NotFound("Playbook not found: " + id);

            if (request.FileName != null)
            {
                string fileName = request.FileName.Trim();
                if (fileName.Length == 0) return RecordWriteResult<Playbook>.Invalid("fileName must not be empty");
                existing.FileName = fileName;
            }

            if (request.Content != null)
            {
                if (String.IsNullOrWhiteSpace(request.Content)) return RecordWriteResult<Playbook>.Invalid("content must not be empty");
                existing.Content = request.Content;
            }

            if (request.DescriptionSupplied) existing.Description = String.IsNullOrEmpty(request.Description) ? null : request.Description;
            if (request.Active.HasValue) existing.Active = request.Active.Value;
            existing.TenantId ??= Constants.DefaultTenantId;
            existing.UserId ??= Constants.DefaultUserId;

            string? invalid = ValidationError(existing);
            if (invalid != null) return RecordWriteResult<Playbook>.Invalid(invalid);
            Playbook? duplicate = await _Database.Playbooks.ReadByFileNameAsync(existing.TenantId, existing.FileName, token).ConfigureAwait(false);
            if (duplicate != null && !String.Equals(duplicate.Id, existing.Id, StringComparison.Ordinal))
                return RecordWriteResult<Playbook>.Conflict("A playbook with that file name already exists.");

            Playbook updated = await _Database.Playbooks.UpdateAsync(existing, token).ConfigureAwait(false);
            return RecordWriteResult<Playbook>.Success(updated);
        }

        /// <summary>
        /// Delete a playbook within the caller's scope. Existing mission snapshots remain unchanged.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Playbook identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result carrying the deleted playbook.</returns>
        public async Task<RecordWriteResult<Playbook>> DeleteAsync(AuthContext caller, string? id, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return RecordWriteResult<Playbook>.Invalid("id is required");
            Playbook? existing = await ReadForCallerAsync(caller, id, token).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Playbook>.NotFound("Playbook not found: " + id);

            await _Database.Playbooks.DeleteAsync(existing.Id, token).ConfigureAwait(false);
            return RecordWriteResult<Playbook>.Success(existing);
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
                // Inactive playbooks are skipped rather than failing dispatch: a playbook can be
                // toggled off at any time and voyages must still dispatch without it.
                if (!playbook.Active)
                    continue;

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
                // Skip inactive playbooks instead of failing dispatch (see ResolveSelectionsAsync).
                if (!playbook.Active)
                    continue;

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

        private string? ValidationError(Playbook playbook)
        {
            try
            {
                Validate(playbook);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }
    }
}
