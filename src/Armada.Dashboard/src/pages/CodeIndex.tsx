import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  exploreCodeGraph,
  getCodeGraphFiles,
  getCodeIndexStatus,
  listAllVessels,
  searchCodeGraphSymbols,
  searchCodeIndex,
  updateCodeIndex,
} from '../api/client';
import type {
  CodeGraphExploreResponse,
  CodeGraphFileStructureResponse,
  CodeGraphSymbolSearchResponse,
  CodeIndexStatus,
  CodeSearchResponse,
  Vessel,
} from '../types/models';
import RefreshButton from '../components/shared/RefreshButton';
import StatusBadge from '../components/shared/StatusBadge';
import ErrorModal from '../components/shared/ErrorModal';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';

type Tab = 'search' | 'symbols' | 'files' | 'explore';

export default function CodeIndex() {
  const { t } = useLocale();
  const { pushToast } = useNotifications();
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [vesselId, setVesselId] = useState('');
  const [status, setStatus] = useState<CodeIndexStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [updating, setUpdating] = useState(false);
  const [error, setError] = useState('');
  const [tab, setTab] = useState<Tab>('search');
  const selectedVesselId = useRef(vesselId);

  const [query, setQuery] = useState('');
  const [pathPrefix, setPathPrefix] = useState('');
  const [language, setLanguage] = useState('');
  const [searchResults, setSearchResults] = useState<CodeSearchResponse | null>(null);
  const [symbolResults, setSymbolResults] = useState<CodeGraphSymbolSearchResponse | null>(null);
  const [filesResult, setFilesResult] = useState<CodeGraphFileStructureResponse | null>(null);
  const [exploreResult, setExploreResult] = useState<CodeGraphExploreResponse | null>(null);

  const currentVessel = useMemo(() => vessels.find((v) => v.id === vesselId) || null, [vessels, vesselId]);

  const selectVessel = useCallback((id: string) => {
    selectedVesselId.current = id;
    setVesselId(id);
    setStatus(null);
    setSearchResults(null);
    setSymbolResults(null);
    setFilesResult(null);
    setExploreResult(null);
    setError('');
  }, []);

  const loadVessels = useCallback(async () => {
    const result = await listAllVessels();
    setVessels(result);
    if (!selectedVesselId.current && result[0]) selectVessel(result[0].id);
  }, [selectVessel]);

  const loadStatus = useCallback(async (id: string) => {
    if (!id) {
      setStatus(null);
      return;
    }
    const result = await getCodeIndexStatus(id);
    if (selectedVesselId.current === id) setStatus(result);
  }, []);

  const refresh = useCallback(async () => {
    try {
      setLoading(true);
      await loadVessels();
      const selectedId = selectedVesselId.current;
      if (selectedId) await loadStatus(selectedId);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : t('Failed to load code index.'));
    } finally {
      setLoading(false);
    }
  }, [loadStatus, loadVessels, t, vesselId]);

  useEffect(() => {
    void refresh();
  }, []);

  useEffect(() => {
    if (!vesselId) return;
    void loadStatus(vesselId).catch((err) => setError(err instanceof Error ? err.message : t('Failed to load index status.')));
  }, [loadStatus, t, vesselId]);

  async function handleUpdate() {
    if (!vesselId) return;
    const requestedVesselId = vesselId;
    try {
      setUpdating(true);
      const next = await updateCodeIndex(requestedVesselId);
      if (selectedVesselId.current !== requestedVesselId) return;
      setStatus(next);
      pushToast('success', t('Index refreshed.'));
    } catch (err) {
      if (selectedVesselId.current === requestedVesselId) setError(err instanceof Error ? err.message : t('Index refresh failed.'));
    } finally {
      setUpdating(false);
    }
  }

  async function runSearch() {
    if (!vesselId || !query.trim()) return;
    const requestedVesselId = vesselId;
    try {
      const response = await searchCodeIndex(requestedVesselId, {
        query,
        limit: 20,
        pathPrefix: pathPrefix || null,
        language: language || null,
        includeContent: false,
      });
      if (selectedVesselId.current !== requestedVesselId) return;
      setSearchResults(response);
      setError('');
    } catch (err) {
      if (selectedVesselId.current === requestedVesselId) setError(err instanceof Error ? err.message : t('Search failed.'));
    }
  }

  async function runSymbols() {
    if (!vesselId || !query.trim()) return;
    const requestedVesselId = vesselId;
    try {
      const response = await searchCodeGraphSymbols(requestedVesselId, query, 30, undefined, pathPrefix || undefined);
      if (selectedVesselId.current !== requestedVesselId) return;
      setSymbolResults(response);
      setError('');
    } catch (err) {
      if (selectedVesselId.current === requestedVesselId) setError(err instanceof Error ? err.message : t('Symbol search failed.'));
    }
  }

  async function runFiles() {
    if (!vesselId) return;
    const requestedVesselId = vesselId;
    try {
      const response = await getCodeGraphFiles(requestedVesselId, pathPrefix, 100, true);
      if (selectedVesselId.current !== requestedVesselId) return;
      setFilesResult(response);
      setError('');
    } catch (err) {
      if (selectedVesselId.current === requestedVesselId) setError(err instanceof Error ? err.message : t('File graph load failed.'));
    }
  }

  async function runExplore() {
    if (!vesselId || !query.trim()) return;
    const requestedVesselId = vesselId;
    try {
      const response = await exploreCodeGraph(requestedVesselId, query, 2, 25, true);
      if (selectedVesselId.current !== requestedVesselId) return;
      setExploreResult(response);
      setError('');
    } catch (err) {
      if (selectedVesselId.current === requestedVesselId) setError(err instanceof Error ? err.message : t('Graph explore failed.'));
    }
  }

  function runActiveTab() {
    if (tab === 'search') return runSearch();
    if (tab === 'symbols') return runSymbols();
    if (tab === 'files') return runFiles();
    return runExplore();
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <h2>{t('Code Index')}</h2>
          <div className="text-muted">{currentVessel?.name || t('No vessel selected')}</div>
        </div>
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
          <select className="form-control" value={vesselId} onChange={(e) => selectVessel(e.target.value)} disabled={loading || updating} style={{ minWidth: 240 }}>
            {vessels.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}
          </select>
          <RefreshButton onRefresh={refresh} />
          <button type="button" className="btn btn-primary" onClick={() => void handleUpdate()} disabled={!vesselId || updating}>
            {updating ? t('Updating...') : t('Update Index')}
          </button>
        </div>
      </div>

      {status && (
        <div className="card-grid">
          <div className="card">
            <div className="card-label">{t('Freshness')}</div>
            <div className="card-value" style={{ fontSize: '1.2rem' }}><StatusBadge status={status.freshness} /></div>
            <div className="card-detail text-muted">{formatShortSha(status.indexedCommitSha)} / {formatShortSha(status.currentCommitSha)}</div>
          </div>
          <div className="card">
            <div className="card-label">{t('Files')}</div>
            <div className="card-value">{status.documentCount}</div>
            <div className="card-detail text-muted">{t('Chunks')}: {status.chunkCount}</div>
          </div>
          <div className="card">
            <div className="card-label">{t('Indexed')}</div>
            <div className="card-value" style={{ fontSize: '1rem' }}>{formatDate(status.indexedAtUtc)}</div>
            <div className="card-detail text-muted">{status.defaultBranch || 'main'}</div>
          </div>
        </div>
      )}

      {status?.lastError && <div className="alert alert-error" style={{ marginTop: '1rem' }}>{status.lastError}</div>}

      <div className="table-wrap" style={{ marginTop: '1rem', padding: '1rem' }}>
        <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'end', marginBottom: '1rem' }}>
          <label>
            {t('Query')}
            <input className="form-control" value={query} onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') void runActiveTab(); }} />
          </label>
          <label>
            {t('Path')}
            <input className="form-control" value={pathPrefix} onChange={(e) => setPathPrefix(e.target.value)} placeholder="src/" />
          </label>
          <label>
            {t('Language')}
            <input className="form-control" value={language} onChange={(e) => setLanguage(e.target.value)} placeholder="typescript" />
          </label>
          <button type="button" className="btn btn-primary" onClick={() => void runActiveTab()} disabled={!vesselId || (tab !== 'files' && !query.trim())}>
            {t('Run')}
          </button>
        </div>

        <div className="tabs" style={{ marginBottom: '1rem' }}>
          {(['search', 'symbols', 'files', 'explore'] as Tab[]).map((item) => (
            <button key={item} type="button" className={`tab ${tab === item ? 'active' : ''}`} onClick={() => setTab(item)}>
              {tabLabel(item)}
            </button>
          ))}
        </div>

        {tab === 'search' && <SearchResults response={searchResults} />}
        {tab === 'symbols' && <SymbolResults response={symbolResults} />}
        {tab === 'files' && <FileResults response={filesResult} />}
        {tab === 'explore' && <ExploreResults response={exploreResult} />}
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
    </div>
  );
}

function SearchResults({ response }: { response: CodeSearchResponse | null }) {
  if (!response) return <EmptyState />;
  if (!response.available) {
    const details = [response.message, response.unavailableReason].filter(Boolean).join(' — ');
    return <div role="status" className="alert alert-warning">{details || 'Code search is unavailable.'}</div>;
  }
  return (
    <div className="table-wrap">
      <table>
        <thead><tr><th>Score</th><th>Path</th><th>Language</th><th>Lines</th><th>Excerpt</th></tr></thead>
        <tbody>
          {response.results.map((result, index) => (
            <tr key={`${result.record.path}-${index}`}>
              <td>{result.score.toFixed(1)}</td>
              <td className="table-url-cell">{result.record.path}</td>
              <td>{result.record.language}</td>
              <td>{result.record.startLine}-{result.record.endLine}</td>
              <td>{result.excerpt}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SymbolResults({ response }: { response: CodeGraphSymbolSearchResponse | null }) {
  if (!response) return <EmptyState />;
  return (
    <>
      <Warnings warnings={response.warnings} />
      <div className="table-wrap">
        <table>
          <thead><tr><th>Score</th><th>Kind</th><th>Symbol</th><th>Path</th><th>Tags</th></tr></thead>
          <tbody>
            {response.results.map((result, index) => (
              <tr key={`${result.symbol.qualifiedName}-${index}`}>
                <td>{result.score.toFixed(1)}</td>
                <td>{result.symbol.kind}</td>
                <td>{result.symbol.qualifiedName || result.symbol.simpleName}</td>
                <td className="table-url-cell">{result.symbol.path}:{result.symbol.startLine}</td>
                <td>{[result.symbol.framework, ...(result.symbol.tags || [])].filter(Boolean).join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function FileResults({ response }: { response: CodeGraphFileStructureResponse | null }) {
  if (!response) return <EmptyState />;
  return (
    <>
      <Warnings warnings={response.warnings} />
      <div className="table-wrap">
        <table>
          <thead><tr><th>Path</th><th>Language</th><th>Symbols</th><th>Top Symbols</th></tr></thead>
          <tbody>
            {response.files.map((file) => (
              <tr key={file.path}>
                <td className="table-url-cell">{file.path}</td>
                <td>{file.language}</td>
                <td>{file.symbolCount}</td>
                <td>{file.symbols.slice(0, 5).map((s) => s.simpleName).join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function ExploreResults({ response }: { response: CodeGraphExploreResponse | null }) {
  if (!response) return <EmptyState />;
  return (
    <>
      <Warnings warnings={response.warnings} />
      <div className="table-wrap">
        <table>
          <thead><tr><th>Score</th><th>Path</th><th>Symbols</th><th>Source</th></tr></thead>
          <tbody>
            {response.files.map((file) => (
              <tr key={file.path}>
                <td>{file.score.toFixed(1)}</td>
                <td className="table-url-cell">{file.path}</td>
                <td>{file.symbols.map((s) => s.simpleName).join(', ')}</td>
                <td>{file.sourceSections.map((s) => `${s.startLine}-${s.endLine}`).join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function Warnings({ warnings }: { warnings: string[] }) {
  if (!warnings?.length) return null;
  return <div className="alert alert-warning" style={{ marginBottom: '1rem' }}>{warnings.join('; ')}</div>;
}

function EmptyState() {
  return <div className="text-muted">No results</div>;
}

function tabLabel(tab: Tab) {
  if (tab === 'search') return 'Search';
  if (tab === 'symbols') return 'Symbols';
  if (tab === 'files') return 'Files';
  return 'Explore';
}

function formatShortSha(value: string | null) {
  return value ? value.slice(0, 10) : 'none';
}

function formatDate(value: string | null) {
  if (!value) return 'never';
  return new Date(value).toLocaleString();
}
