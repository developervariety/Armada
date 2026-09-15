import { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  getStatus,
  getVoyageMissionSummary,
  listMissionSummaries,
  listVessels,
  listCaptains,
  listFleets,
  deleteMission,
  restartMission,
} from '../api/client';
import type { MissionSummary, Vessel, Captain, Fleet } from '../types/models';
import { useWebSocket } from '../context/WebSocketContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import type { WebSocketMessage } from '../types/models';
import StatusBadge from '../components/shared/StatusBadge';
import ActionMenu from '../components/shared/ActionMenu';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import CopyButton, { copyToClipboard } from '../components/shared/CopyButton';
import JsonViewer from '../components/shared/JsonViewer';
import FilterBar from '../components/shared/FilterBar';
import MissionHistoryChart from '../components/MissionHistoryChart';
import { useLocale } from '../context/LocaleContext';
import { useAutoRefresh } from '../lib/useAutoRefresh';

interface VoyageProgress {
  voyage: {
    id: string;
    title: string;
    status: string;
  };
  totalMissions: number;
  completedMissions: number;
  failedMissions: number;
}

interface StatusData {
  totalCaptains: number;
  idleCaptains: number;
  workingCaptains: number;
  stalledCaptains: number;
  activeVoyages: number;
  /** Pending missions whose last admission evaluation deferred them for host resource pressure (current count). */
  missionsWaitingForResourcePressure?: number;
  missionsByStatus: Record<string, number>;
  voyages: VoyageProgress[];
  recentSignals: Array<{
    id: string;
    type: string;
    payload?: string;
    message?: string;
    createdUtc: string;
  }>;
}

/** Number of newest missions shown on the home page. */
const RECENT_MISSION_COUNT = 10;
/** Vessel IDs read per voyage; the voyage summary endpoint caps a page at 100. */
const VOYAGE_VESSEL_PAGE_SIZE = 100;
/** Home keeps its previous 30-second cadence unless the operator chooses another interval. */
const HOME_DEFAULT_REFRESH_SECONDS = 30;

function voyagePercent(vp: VoyageProgress): number {
  if (!vp.totalMissions) return 0;
  return Math.round((vp.completedMissions / vp.totalMissions) * 100);
}

export default function Dashboard() {
  const { subscribe } = useWebSocket();
  const navigate = useNavigate();
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const openSetupWizard = useCallback(() => {
    window.dispatchEvent(new CustomEvent('armada:open-setup-wizard'));
  }, []);

  const [status, setStatus] = useState<StatusData | null>(null);
  const [recentMissions, setRecentMissions] = useState<MissionSummary[]>([]);
  const [voyageVesselIds, setVoyageVesselIds] = useState<Record<string, string[]>>({});
  // Advances on every home refresh so the mission history chart reloads on the same cycle.
  const [historyRefreshKey, setHistoryRefreshKey] = useState(0);
  const homeLoadedRef = useRef(false);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [error, setError] = useState('');
  // Fleet status is a global-administrator read. A refusal is a role boundary, not a failure: it is named once
  // and the route is not requested again for this page, so a narrower session never polls into repeated 403s.
  const [statusRefused, setStatusRefused] = useState(false);
  const statusRefusedRef = useRef(false);
  const [loading, setLoading] = useState(true);
  const [jsonViewer, setJsonViewer] = useState<{ open: boolean; title: string; data: unknown }>({
    open: false,
    title: '',
    data: null,
  });
  const [confirm, setConfirm] = useState<{
    open: boolean;
    title: string;
    message: string;
    onConfirm: () => void;
  }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => undefined,
  });

  // Recent mission filters
  const [statusFilter, setStatusFilter] = useState('');
  const [vesselFilter, setVesselFilter] = useState('');
  const [captainFilter, setCaptainFilter] = useState('');

  const vesselName = useCallback(
    (id: string | null | undefined) => {
      if (!id) return '-';
      const v = vessels.find((x) => x.id === id);
      return v ? v.name : id.slice(0, 8);
    },
    [vessels],
  );

  const voyageVesselNames = useCallback(
    (voyageId: string | null | undefined) => {
      if (!voyageId) return '-';
      const ids = voyageVesselIds[voyageId];
      if (!ids || ids.length === 0) return '-';
      return ids.map((id) => vesselName(id)).join(', ');
    },
    [voyageVesselIds, vesselName],
  );

  const captainName = useCallback(
    (id: string | null | undefined) => {
      if (!id) return '-';
      const c = captains.find((x) => x.id === id);
      return c ? c.name : id.slice(0, 8);
    },
    [captains],
  );

  const fetchAll = useCallback(async () => {
    try {
      const [statusRes, missionRes, vesselRes, captainRes, fleetRes] = await Promise.all([
        statusRefusedRef.current
          ? Promise.resolve(null)
          : getStatus().catch((err: unknown) => {
              if ((err as { status?: number } | null)?.status === 403) {
                statusRefusedRef.current = true;
                setStatusRefused(true);
              }
              return null;
            }),
        listMissionSummaries({ pageSize: RECENT_MISSION_COUNT }).catch(() => null),
        listVessels({ pageSize: 9999 }).catch(() => null),
        listCaptains({ pageSize: 9999 }).catch(() => null),
        listFleets({ pageSize: 9999 }).catch(() => null),
      ]);
      const statusData = statusRes as unknown as StatusData | null;
      if (statusData) setStatus(statusData);
      // The summaries endpoint returns the newest missions first, so no client-side sort is needed.
      if (missionRes) setRecentMissions(missionRes.objects);
      if (vesselRes) setVessels(vesselRes.objects);
      if (fleetRes) setFleets(fleetRes.objects);
      if (captainRes) setCaptains(captainRes.objects);
      if (!statusRes && !missionRes) setError(t('Failed to load dashboard data.'));

      // Voyage vessels come from the voyage's own mission summary, so a voyage whose missions are older than
      // the recent slice still shows its vessels.
      const voyageIds = (statusData?.voyages ?? [])
        .map((vp) => vp.voyage?.id)
        .filter((id): id is string => Boolean(id));
      const summaries = await Promise.all(
        voyageIds.map((id) =>
          getVoyageMissionSummary(id, { pageSize: VOYAGE_VESSEL_PAGE_SIZE })
            .then((summary) => ({ id, vesselIds: summary.vessels?.objects ?? [] }))
            .catch(() => null),
        ),
      );
      const next: Record<string, string[]> = {};
      for (const entry of summaries) {
        if (entry) next[entry.id] = entry.vesselIds;
      }
      setVoyageVesselIds(next);
      // The chart loads itself on mount, so only later refreshes advance its key.
      if (homeLoadedRef.current) setHistoryRefreshKey((key) => key + 1);
      homeLoadedRef.current = true;
    } catch {
      setError(t('Failed to load dashboard data.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  // One refresh path. A trigger that arrives while a load is running is folded into a single follow-up load,
  // so bursts of events, timer ticks and clicks never start overlapping requests.
  const inFlightRef = useRef<Promise<void> | null>(null);
  const pendingRef = useRef(false);
  const loadAll = useCallback((): Promise<void> => {
    if (inFlightRef.current) {
      pendingRef.current = true;
      return inFlightRef.current;
    }
    const run = async () => {
      do {
        pendingRef.current = false;
        await fetchAll();
      } while (pendingRef.current);
    };
    const promise = run().finally(() => {
      inFlightRef.current = null;
    });
    inFlightRef.current = promise;
    return promise;
  }, [fetchAll]);

  useEffect(() => {
    loadAll();
  }, [loadAll]);

  // Refresh on WebSocket messages through the same coalesced path.
  useEffect(() => {
    const unsubscribe = subscribe((_msg: WebSocketMessage) => {
      loadAll();
    });
    return unsubscribe;
  }, [subscribe, loadAll]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('dashboard', loadAll, HOME_DEFAULT_REFRESH_SECONDS);

  // Compute alerts from status data
  const alerts = useMemo(() => {
    if (!status) return [];
    const result: Array<{ level: 'error' | 'warning'; message: string; action?: string; link?: string }> = [];
    const ms = status.missionsByStatus || {};
    const stalledCount = status.stalledCaptains ?? 0;
    const failedCount = (ms['Failed'] ?? 0);
    const landingFailedCount = (ms['LandingFailed'] ?? 0);
    const pendingCount = (ms['Pending'] ?? 0);
    const idleCount = status.idleCaptains ?? 0;
    const workingCount = status.workingCaptains ?? 0;
    const totalCaptains = status.totalCaptains ?? 0;

    if (stalledCount > 0) {
      result.push({
        level: 'error',
        message: `${stalledCount} captain(s) stalled -- recovery attempts exhausted.`,
        action: 'Stop and restart stalled captains to resume work.',
        link: '/captains',
      });
    }

    if (failedCount > 0) {
      result.push({
        level: 'warning',
        message: `${failedCount} mission(s) failed.`,
        action: 'Review and restart failed missions.',
        link: '/missions',
      });
    }

    if (landingFailedCount > 0) {
      result.push({
        level: 'warning',
        message: `${landingFailedCount} mission(s) failed to land -- work was produced but could not be merged.`,
        action: 'Retry landing or restart these missions.',
        link: '/missions',
      });
    }

    if (pendingCount > 0 && idleCount > 0 && workingCount === 0) {
      result.push({
        level: 'warning',
        message: `${pendingCount} pending mission(s) but no captains are working. ${idleCount} captain(s) idle.`,
        action: 'Vessels may have concurrent mission limits blocking dispatch, or missions may be assigned to a vessel with an active mission.',
      });
    }

    if (totalCaptains === 0 && pendingCount > 0) {
      result.push({
        level: 'error',
        message: `${pendingCount} pending mission(s) but no captains exist.`,
        action: 'Create a captain to start processing missions.',
        link: '/captains',
      });
    }

    return result;
  }, [status]);

  const totalMissions = useMemo(() => {
    if (!status?.missionsByStatus) return 0;
    return Object.values(status.missionsByStatus).reduce((sum, n) => sum + n, 0);
  }, [status]);

  const filteredRecentMissions = useMemo(() => {
    return recentMissions.filter((m) => {
      if (statusFilter && m.status !== statusFilter) return false;
      if (vesselFilter && m.vesselId !== vesselFilter) return false;
      if (captainFilter && m.captainId !== captainFilter) return false;
      return true;
    });
  }, [recentMissions, statusFilter, vesselFilter, captainFilter]);

  const handleDeleteMission = async (id: string) => {
    try {
      await deleteMission(id);
      loadAll();
    } catch {
      setError(t('Failed to delete mission.'));
    }
  };

  const copyId = (id: string) => {
    copyToClipboard(id);
  };

  const missionStatuses = [
    'Pending',
    'Assigned',
    'InProgress',
    'Testing',
    'Review',
    'Complete',
    'Failed',
    'Cancelled',
  ];

  if (loading) {
    return (
      <div>
        <h2>{t('System Status')}</h2>
        <p className="text-dim">{t('Loading dashboard...')}</p>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={t('System Status')}
        subtitle={t('Overview of fleet health, active missions, and recent activity.')}
        actions={(
          <>
            <button
              className="btn btn-sm"
              onClick={openSetupWizard}
              title={t('Open the setup wizard for onboarding and next-step guidance')}
              aria-label={t('Open Setup Wizard')}
              style={{ display: 'inline-flex', alignItems: 'center', gap: '0.4rem' }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="m12 3-1.9 4.8L5 9.7l4.1 2.1L12 17l2.9-5.2L19 9.7l-5.1-1.9Z" />
                <path d="M5 3v4" />
                <path d="M3 5h4" />
                <path d="M19 16v5" />
                <path d="M16.5 18.5h5" />
              </svg>
              <span>{t('Setup Wizard')}</span>
            </button>
            <button className="btn btn-primary btn-sm" onClick={() => navigate('/dispatch')} title={t('Smart dispatch with NLP task parsing')}>
              + {t('Dispatch')}
            </button>
            <button className="btn btn-primary btn-sm" onClick={() => navigate('/voyages/create')} title={t('Create a new voyage with multiple missions')}>
              + {t('Voyage')}
            </button>
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <RefreshButton onRefresh={loadAll} title="Refresh all dashboard data" />
          </>
        )}
      />

      {/* Ask Armada lead band -- the primary workflow interface */}
      <div className="ask-hero">
        <div className="ask-hero-copy">
          <h2 className="ask-hero-title">{t('Ask Armada')}</h2>
          <p className="ask-hero-sub">{t('Ask about fleet state in plain language and dispatch work straight from the conversation.')}</p>
        </div>
        <div className="ask-hero-actions">
          <button className="btn btn-primary" onClick={() => navigate('/ask')}>
            {t('Ask Armada')} &rarr;
          </button>
          <button className="btn btn-sm" onClick={() => navigate('/inbox')} title={t('Reviews, failures, and stalls awaiting you')}>
            {t('Needs You')}
          </button>
          <button className="btn btn-sm" onClick={() => navigate('/dispatch')} title={t('Send work to vessels')}>
            {t('Dispatch')}
          </button>
          <button className="btn btn-sm" onClick={() => navigate('/server?tab=diagnostics')} title={t('System health diagnostics')}>
            {t('Diagnostics')}
          </button>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      {statusRefused && (
        <div className="dashboard-alerts">
          <div className="dashboard-alert dashboard-alert-warning" role="status">
            <div className="dashboard-alert-content">
              <strong>{t('Fleet status is available to global administrators.')}</strong>
              <span className="dashboard-alert-action"> {t('Your own fleets, vessels, captains and missions are listed below.')}</span>
            </div>
          </div>
        </div>
      )}
      <ConfirmDialog
        open={confirm.open}
        title={confirm.title}
        message={confirm.message}
        confirmLabel={t('Delete')}
        cancelLabel={t('Cancel')}
        danger
        onConfirm={confirm.onConfirm}
        onCancel={() => setConfirm((current) => ({ ...current, open: false }))}
      />

      {/* Alert Banners */}
      {alerts.length > 0 && (
        <div className="dashboard-alerts">
          {alerts.map((alert, i) => (
            <div key={i} className={`dashboard-alert dashboard-alert-${alert.level}`}>
              <div className="dashboard-alert-content">
                <strong>{alert.message}</strong>
                {alert.action && <span className="dashboard-alert-action"> {alert.action}</span>}
              </div>
              {alert.link && (
                <button className="btn btn-sm" onClick={() => navigate(alert.link!)}>{t('View')}</button>
              )}
            </div>
          ))}
        </div>
      )}

      {/* KPI Cards */}
      <div className="cards">
        <div
          className="card clickable"
          onClick={() => navigate('/captains')}
          title={t('Click to view all captains')}
        >
          <div className="card-label">{t('Captains')}</div>
          <div className="card-value">{status?.totalCaptains ?? 0}</div>
          <div className="card-detail">
            <span className="tag idle">{status?.idleCaptains ?? 0} idle</span>
            <span className="tag working">{status?.workingCaptains ?? 0} working</span>
            {(status?.stalledCaptains ?? 0) > 0 && (
              <span className="tag stalled">{status?.stalledCaptains ?? 0} stalled</span>
            )}
          </div>
        </div>

        <div
          className="card clickable"
          onClick={() => navigate('/missions?tab=voyages')}
          title={t('Click to view all voyages')}
        >
          <div className="card-label">{t('Active Voyages')}</div>
          <div className="card-value">{status?.activeVoyages ?? 0}</div>
          {(status?.missionsWaitingForResourcePressure ?? 0) > 0 && (
            <div className="card-detail">
              <span
                className="tag stalled"
                title={t('Pending missions whose last admission check deferred them for host resource pressure')}
              >
                {t('{{count}} waiting for resource pressure', { count: status?.missionsWaitingForResourcePressure ?? 0 })}
              </span>
            </div>
          )}
        </div>

        <div
          className="card clickable"
          onClick={() => navigate('/missions')}
          title={t('Click to view all missions')}
        >
          <div className="card-label">{t('Missions')}</div>
          <div className="card-value">{totalMissions}</div>
          <div className="card-detail">
            {status?.missionsByStatus &&
              Object.entries(status.missionsByStatus).map(([key, val]) => (
                <span key={key} className={`tag ${key.toLowerCase()}`}>
                  {val} {key}
                </span>
              ))}
          </div>
        </div>
      </div>

      {/* Mission History Chart */}
      <MissionHistoryChart vessels={vessels} fleets={fleets} onRefresh={loadAll} refreshKey={historyRefreshKey} />

      {/* Voyage Progress */}
      {status?.voyages && status.voyages.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <h3>{t('Voyage Progress')}</h3>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th title={t('Voyage name and unique identifier')}>{t('Voyage')}</th>
                  <th title={t('Current voyage lifecycle state')}>{t('Status')}</th>
                  <th title={t('Vessel(s) targeted by this voyage\'s missions')}>{t('Vessel')}</th>
                  <th title={t('Percentage of missions completed')}>{t('Progress')}</th>
                  <th title={t('Completed vs total missions in this voyage')}>{t('Missions')}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {status.voyages.map((vp) => (
                  <tr
                    key={vp.voyage?.id}
                    className="clickable"
                    onClick={() => navigate(`/voyages/${vp.voyage?.id}`)}
                  >
                    <td>
                      <strong>{vp.voyage?.title || vp.voyage?.id}</strong>
                      <div className="text-dim id-display">
                        <span className="mono">{vp.voyage?.id}</span>
                        <CopyButton text={vp.voyage?.id || ''} onClick={e => e.stopPropagation()} />
                      </div>
                    </td>
                    <td>
                      <StatusBadge status={vp.voyage?.status || ''} />
                    </td>
                    <td className="text-dim">{voyageVesselNames(vp.voyage?.id)}</td>
                    <td>
                      <div className="progress-bar">
                        <div
                          className="progress-fill"
                          style={{ width: `${voyagePercent(vp)}%` }}
                        />
                      </div>
                      <span className="text-dim">{voyagePercent(vp)}%</span>
                    </td>
                    <td>
                      <span>
                        {vp.completedMissions}/{vp.totalMissions} {t('done')}
                      </span>
                      {vp.failedMissions > 0 && (
                        <span className="text-dim">, {vp.failedMissions} {t('failed')}</span>
                      )}
                    </td>
                    <td onClick={(e) => e.stopPropagation()}>
                      <ActionMenu
                        id={`voyage-action-${vp.voyage?.id}`}
                        items={[
                          {
                            label: 'View JSON',
                            onClick: () =>
                              setJsonViewer({
                                open: true,
                                title: `Voyage: ${vp.voyage?.title || vp.voyage?.id}`,
                                data: vp.voyage,
                              }),
                          },
                        ]}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Recent Missions */}
      <div style={{ marginTop: '1.5rem' }}>
        <div className="view-header">
          <h3>{t('Recent Missions')}</h3>
          <div className="view-actions filter-bar">
            <FilterBar
              filters={[
                {
                  key: 'status',
                  label: t('All Statuses'),
                  type: 'select',
                  options: missionStatuses.map((s) => ({ value: s, label: t(s) })),
                },
                {
                  key: 'vessel',
                  label: t('All Vessels'),
                  type: 'select',
                  options: vessels.map((v) => ({ value: v.id, label: v.name })),
                },
                {
                  key: 'captain',
                  label: t('All Captains'),
                  type: 'select',
                  options: captains.map((c) => ({ value: c.id, label: c.name })),
                },
              ]}
              values={{ status: statusFilter, vessel: vesselFilter, captain: captainFilter }}
              onChange={(key, value) => {
                if (key === 'status') setStatusFilter(value);
                else if (key === 'vessel') setVesselFilter(value);
                else if (key === 'captain') setCaptainFilter(value);
              }}
            />
            <button
              className="btn btn-sm"
              onClick={() => navigate('/missions')}
              title={t('Go to full missions list')}
            >
              {t('View All')} &rarr;
            </button>
          </div>
        </div>

        {filteredRecentMissions.length > 0 ? (
          <div className="table-wrap">
            <table className="table mission-table">
              <thead>
                <tr>
                  <th title={t('Mission title')}>{t('Mission')}</th>
                  <th title={t('Mission ID')}>{t('ID')}</th>
                  <th title={t('Current mission lifecycle state')}>{t('Status')}</th>
                  <th title={t('Target repository for this mission')}>{t('Vessel')}</th>
                  <th title={t('AI captain assigned to execute this mission')}>{t('Captain')}</th>
                  <th title={t('When this mission was created')}>{t('Created')}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {filteredRecentMissions.map((m) => (
                  <tr
                    key={m.id}
                    className="clickable"
                    onClick={() => navigate(`/missions/${m.id}`)}
                  >
                    <td className="truncate-cell" title={m.title}>
                      <strong>{m.title}</strong>
                    </td>
                    <td className="mono text-dim">
                      <span className="id-display">
                        <span>{m.id}</span>
                        <CopyButton text={m.id} onClick={e => e.stopPropagation()} />
                      </span>
                    </td>
                    <td>
                      <StatusBadge status={m.status} />
                    </td>
                    <td>{vesselName(m.vesselId)}</td>
                    <td>{captainName(m.captainId)}</td>
                    <td className="text-dim" title={formatDateTime(m.createdUtc)}>
                      {formatRelativeTime(m.createdUtc)}
                    </td>
                    <td onClick={(e) => e.stopPropagation()}>
                      <ActionMenu
                        id={`mission-action-${m.id}`}
                        items={[
                          {
                            label: 'View Detail',
                            onClick: () => navigate(`/missions/${m.id}`),
                          },
                          {
                            label: 'View JSON',
                            onClick: () =>
                              setJsonViewer({ open: true, title: `Mission: ${m.title}`, data: m }),
                          },
                          ...(m.status === 'Failed' || m.status === 'Cancelled' || m.status === 'LandingFailed' ? [{
                            label: 'Restart',
                            onClick: async () => { try { await restartMission(m.id); loadAll(); } catch { /* ignore */ } },
                          }] : []),
                          {
                            label: 'Delete',
                            danger: true,
                            onClick: () => setConfirm({
                              open: true,
                              title: t('Delete Mission'),
                              message: t('Delete this mission?'),
                              onConfirm: async () => {
                                setConfirm((current) => ({ ...current, open: false }));
                                await handleDeleteMission(m.id);
                              },
                            }),
                          },
                        ]}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="text-dim">{t('No missions found.')}</p>
        )}
      </div>

      {/* Recent Signals */}
      {status?.recentSignals && status.recentSignals.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <h3>{t('Recent Signals')}</h3>
          <div className="signal-list">
            {status.recentSignals.slice(0, 5).map((sig) => (
              <div key={sig.id} className="signal-item">
                <StatusBadge status={sig.type || ''} />
                <span>{sig.payload || sig.message}</span>
                <span className="text-dim" title={formatDateTime(sig.createdUtc)}>
                  {formatRelativeTime(sig.createdUtc)}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* JSON Viewer Modal */}
      {jsonViewer.open && (
        <JsonViewer
          open={jsonViewer.open}
          title={jsonViewer.title}
          data={jsonViewer.data}
          onClose={() => setJsonViewer({ open: false, title: '', data: null })}
        />
      )}
    </div>
  );
}
