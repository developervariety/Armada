import { useMemo, useState } from 'react';
import type { Vessel, WorkspaceStatusResult } from '../../types/models';

interface WorkspaceVesselPickerProps {
  vessels: Vessel[];
  recentVesselIds: string[];
  statusByVesselId: Record<string, WorkspaceStatusResult | undefined>;
  onOpen: (vesselId: string) => void;
}

export default function WorkspaceVesselPicker(props: WorkspaceVesselPickerProps) {
  const { vessels, recentVesselIds, statusByVesselId, onOpen } = props;
  const [query, setQuery] = useState('');

  const recentVessels = useMemo(
    () => recentVesselIds
      .map((id) => vessels.find((vessel) => vessel.id === id))
      .filter((vessel): vessel is Vessel => !!vessel),
    [recentVesselIds, vessels],
  );

  const filtered = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    const source = vessels.filter((vessel) => !recentVesselIds.includes(vessel.id));
    if (!normalizedQuery) return source;
    return source.filter((vessel) =>
      vessel.name.toLowerCase().includes(normalizedQuery)
      || vessel.id.toLowerCase().includes(normalizedQuery)
      || (vessel.workingDirectory || '').toLowerCase().includes(normalizedQuery),
    );
  }, [query, recentVesselIds, vessels]);

  function renderCard(vessel: Vessel) {
    const status = statusByVesselId[vessel.id];
    return (
      <button
        key={vessel.id}
        type="button"
        className="workspace-picker-card"
        onClick={() => onOpen(vessel.id)}
      >
        <div className="workspace-picker-card-title">
          <strong>{vessel.name}</strong>
          <span className={`workspace-picker-badge${status?.hasWorkingDirectory === false ? ' warning' : ''}`}>
            {status?.hasWorkingDirectory === false ? 'No working directory' : 'Workspace ready'}
          </span>
        </div>
        <div className="workspace-picker-card-meta">{vessel.id}</div>
        <div className="workspace-picker-card-meta">{vessel.workingDirectory || 'No working directory configured'}</div>
        {status && (
          <div className="workspace-picker-card-status">
            <span>{status.branchName || 'No branch info'}</span>
            <span>{status.activeMissionCount} active mission(s)</span>
            <span>
              {typeof status.commitsAhead === 'number' ? `${status.commitsAhead} ahead` : 'No git sync data'}
              {typeof status.commitsBehind === 'number' ? ` / ${status.commitsBehind} behind` : ''}
            </span>
          </div>
        )}
      </button>
    );
  }

  return (
    <div className="workspace-picker">
      <div className="page-header">
        <div>
          <h2>Workspace</h2>
          <p className="text-muted">
            Open a vessel as a browsable, editable repository workspace inside Armada.
          </p>
        </div>
      </div>

      <div className="card workspace-picker-shell">
        <div className="workspace-picker-toolbar">
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Find a vessel by name, id, or working directory"
          />
        </div>

        {recentVessels.length > 0 && (
          <section className="workspace-picker-section">
            <h3>Recent Vessels</h3>
            <div className="workspace-picker-grid">
              {recentVessels.map(renderCard)}
            </div>
          </section>
        )}

        <section className="workspace-picker-section">
          <h3>All Vessels</h3>
          <div className="workspace-picker-grid">
            {filtered.map(renderCard)}
            {filtered.length === 0 && (
              <div className="workspace-picker-empty">No vessels match the current filter.</div>
            )}
          </div>
        </section>
      </div>
    </div>
  );
}
