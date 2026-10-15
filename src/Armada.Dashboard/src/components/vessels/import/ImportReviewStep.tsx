import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import type { Fleet, Pipeline, VesselImportCandidateStatus, VesselImportHint, VesselImportItem } from '../../../types/models';
import { useLocale } from '../../../context/LocaleContext';
import CodeStatusBadge from '../../shared/CodeStatusBadge';
import DataTable from '../../shared/DataTable';
import {
  CANDIDATE_STATUSES,
  CANDIDATE_STATUS_META,
  IMPORTABLE_STATUSES,
  candidateStatusBadge,
  hintLabel,
} from '../../../lib/vesselImportLabels';
import { IMPORT_LANDING_MODES } from '../../../lib/vesselImport';
import { usePersistedPageSize } from '../../../lib/usePersistedPageSize';

export interface ImportDefaults {
  fleetId: string;
  pipelineId: string;
  landingMode: string;
}

interface ImportReviewStepProps {
  candidates: VesselImportItem[];
  truncated: boolean;
  hints: VesselImportHint[];
  selected: string[];
  onSelectedChange: (paths: string[]) => void;
  fleets: Fleet[];
  pipelines: Pipeline[];
  defaults: ImportDefaults;
  onDefaultsChange: (defaults: ImportDefaults) => void;
}

/** Review step: candidate table with status badges and filter, selection, fleet picker and defaults. */
export default function ImportReviewStep({
  candidates,
  truncated,
  hints,
  selected,
  onSelectedChange,
  fleets,
  pipelines,
  defaults,
  onDefaultsChange,
}: ImportReviewStepProps) {
  const { t } = useLocale();
  const [statusFilter, setStatusFilter] = useState<VesselImportCandidateStatus | ''>('');
  const [search, setSearch] = useState('');
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = usePersistedPageSize('vessel-import-review', 50);

  const counts = useMemo(() => {
    const c: Partial<Record<VesselImportCandidateStatus, number>> = {};
    for (const item of candidates) c[item.candidateStatus] = (c[item.candidateStatus] ?? 0) + 1;
    return c;
  }, [candidates]);

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    return candidates.filter((c) => (!statusFilter || c.candidateStatus === statusFilter)
      && (!term || c.path.toLowerCase().includes(term) || c.proposedName.toLowerCase().includes(term)));
  }, [candidates, statusFilter, search]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
  const page = Math.min(pageNumber, totalPages);
  const visible = filtered.slice((page - 1) * pageSize, page * pageSize);
  const selectable = (c: VesselImportItem) => IMPORTABLE_STATUSES.includes(c.candidateStatus);
  const filteredSelectable = filtered.filter(selectable);
  const allFilteredSelected = filteredSelectable.length > 0 && filteredSelectable.every((c) => selected.includes(c.path));

  function toggle(path: string) {
    onSelectedChange(selected.includes(path) ? selected.filter((p) => p !== path) : [...selected, path]);
  }

  function toggleAllFiltered() {
    if (allFilteredSelected) onSelectedChange(selected.filter((p) => !filteredSelectable.some((c) => c.path === p)));
    else onSelectedChange(Array.from(new Set([...selected, ...filteredSelectable.map((c) => c.path)])));
  }

  function selectAllNew() {
    onSelectedChange(candidates.filter((c) => c.candidateStatus === 'New').map((c) => c.path));
  }

  return (
    <div className="import-review">
      {(hints.length > 0 || truncated) && (
        <div className="alert alert-info import-hints" role="note">
          {hints.map((h) => <p key={h.code}>{hintLabel(t, h.code, h.message)}</p>)}
          {truncated && !hints.some((h) => h.code === 'CandidateLimitReached') && <p>{hintLabel(t, 'CandidateLimitReached', '')}</p>}
        </div>
      )}

      <div className="import-status-chips" role="group" aria-label={t('Filter candidates by status')}>
        <button type="button" className={`chip${statusFilter === '' ? ' active' : ''}`} aria-pressed={statusFilter === ''} title={t('Show every candidate found, whatever its status.')} onClick={() => { setStatusFilter(''); setPageNumber(1); }}>
          {t('All')} <span className="chip-count">{candidates.length.toLocaleString()}</span>
        </button>
        {CANDIDATE_STATUSES.filter((s) => (counts[s] ?? 0) > 0).map((s) => (
          <button key={s} type="button" className={`chip${statusFilter === s ? ' active' : ''}`} aria-pressed={statusFilter === s} title={t(CANDIDATE_STATUS_META[s].description)} onClick={() => { setStatusFilter(s); setPageNumber(1); }}>
            {t(CANDIDATE_STATUS_META[s].label)} <span className="chip-count">{(counts[s] ?? 0).toLocaleString()}</span>
          </button>
        ))}
      </div>

      <div className="toolbar-row">
        <input type="search" value={search} onChange={(e) => { setSearch(e.target.value); setPageNumber(1); }} placeholder={t('Path or name contains...')} aria-label={t('Path or name contains...')} title={t('Filter the list to candidates whose folder path or proposed name contains this text, e.g. api. Not case sensitive. Does not change the selection.')} />
        <button type="button" className="btn btn-sm" onClick={selectAllNew} title={t('Select every candidate with status New (repositories that are not vessels yet) and nothing else, replacing the current selection.')}>{t('Select all new')}</button>
        <button type="button" className="btn btn-sm" onClick={() => onSelectedChange([])} disabled={selected.length === 0} title={t('Unselect every candidate, on all pages.')}>{t('Clear selection')}</button>
        <span className="text-dim toolbar-note" title={t('Candidates that will become vessels when you import, across all pages and filters.')}>{t('{count, plural, one {# selected} other {# selected}}', { count: selected.length })}</span>
      </div>

      <DataTable
        tableKey="vessel-import-review"
        wrapClassName="import-table-wrap"
        rows={visible}
        rowKey={(c) => c.id || c.path}
        onRowClick={(c) => { if (selectable(c)) toggle(c.path); }}
        isRowClickable={selectable}
        rowClassName={(c) => (selectable(c) ? undefined : 'row-muted')}
        pagination={{
          pageNumber: page,
          pageSize,
          totalPages,
          totalRecords: filtered.length,
          onPageChange: setPageNumber,
          onPageSizeChange: (size) => { setPageSize(size); setPageNumber(1); },
        }}
        emptyMessage={t('No candidates match the current filters.')}
        selection={{
          isSelected: (c) => selected.includes(c.path),
          isSelectable: selectable,
          onToggle: (c) => toggle(c.path),
          allSelected: allFilteredSelected,
          onToggleAll: () => toggleAllFiltered(),
          selectAllLabel: t('Select all importable candidates in this view'),
          rowLabel: (c) => t('Select {{name}}', { name: c.proposedName }),
        }}
        columns={[
          { key: 'name', label: t('Name'), headerTitle: t('Vessel name Armada will use, taken from the folder name. You can rename the vessel after import.'), required: true, render: (c) => <strong data-i18n-skip="true">{c.proposedName}</strong> },
          {
            // The existing-vessel link used to be a second line under the badge; it sits beside it on one line now.
            key: 'status', label: t('Status'), headerTitle: t('Whether the folder can be imported. Only New and Worktree candidates can be selected; hover a badge for details.'), cellClassName: 'cell-nowrap',
            render: (c) => (
              <>
                <CodeStatusBadge {...candidateStatusBadge(t, c.candidateStatus)} />
                {c.candidateStatus === 'AlreadyOnboarded' && c.existingVesselId && (
                  <>
                    {' '}
                    <Link to={`/vessels/${c.existingVesselId}`} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} title={t('Open the vessel that already uses this folder or remote, in a new tab.')}>{t('Open existing vessel')}</Link>
                  </>
                )}
              </>
            ),
          },
          {
            key: 'path', label: t('Path'), headerTitle: t('Folder on the Admiral host. It becomes the vessel working directory.'), cellClassName: 'mono', cellTitle: (c) => c.path,
            render: (c) => <span className="cell-clip" data-i18n-skip="true"><span>{c.path}</span></span>,
          },
          {
            key: 'remote', label: t('Remote'), headerTitle: t('The repository origin URL, used as the vessel Repository URL. Without an origin, the folder path is used instead.'), cellClassName: 'mono text-dim', cellTitle: (c) => c.remoteUrl ?? '',
            render: (c) => <span className="cell-clip" data-i18n-skip="true"><span>{c.remoteUrl || t('(no origin)')}</span></span>,
          },
          {
            key: 'branch', label: t('Branch'), headerTitle: t('Default branch detected in the repository, used as the vessel Default Branch.'), cellClassName: 'mono text-dim', cellTitle: (c) => c.defaultBranch || '',
            render: (c) => <span className="cell-clip" data-i18n-skip="true"><span>{c.defaultBranch || '-'}</span></span>,
          },
        ]}
      />

      <fieldset className="form-fieldset import-defaults">
        <legend className="form-label" title={t('Settings applied to every vessel this import creates. Each can be changed per vessel afterward.')}>{t('Defaults for the new vessels')}</legend>
        <div className="form-grid-3">
          <label className="form-field" title={t('Fleet every imported vessel joins. No fleet leaves them unassigned; fleet recommendations, if enabled, can assign them afterward.')}>
            <span className="form-label">{t('Fleet')}</span>
            <select title={t('Fleet every imported vessel joins. No fleet leaves them unassigned; fleet recommendations, if enabled, can assign them afterward.')} value={defaults.fleetId} onChange={(e) => onDefaultsChange({ ...defaults, fleetId: e.target.value })}>
              <option value="">{t('No fleet')}</option>
              {fleets.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
            </select>
          </label>
          <label className="form-field" title={t('Pipeline the new vessels use when a dispatch does not name one. None falls back to the fleet default, then a single Worker stage. You can change it per vessel later.')}>
            <span className="form-label">{t('Default Pipeline')}</span>
            <select title={t('Pipeline the new vessels use when a dispatch does not name one. None falls back to the fleet default, then a single Worker stage. You can change it per vessel later.')} value={defaults.pipelineId} onChange={(e) => onDefaultsChange({ ...defaults, pipelineId: e.target.value })}>
              <option value="">{t('None (WorkerOnly)')}</option>
              {pipelines.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
          <label className="form-field" title={t('How finished mission work lands on the new vessels: local merge into the discovered checkout, merge and push, pull request, merge queue, or manual. Default uses the global setting.')}>
            <span className="form-label">{t('Landing Mode')}</span>
            <select title={t('How finished mission work lands on the new vessels: local merge into the discovered checkout, merge and push, pull request, merge queue, or manual. Default uses the global setting.')} value={defaults.landingMode} onChange={(e) => onDefaultsChange({ ...defaults, landingMode: e.target.value })}>
              {IMPORT_LANDING_MODES.map((m) => <option key={m.value || 'default'} value={m.value}>{t(m.label)}</option>)}
            </select>
          </label>
        </div>
        <p className="text-dim form-help">{t('Imported vessels use the repository origin as the remote and the discovered folder as the working directory. The checkout is never deleted when a vessel is removed.')}</p>
      </fieldset>
    </div>
  );
}
