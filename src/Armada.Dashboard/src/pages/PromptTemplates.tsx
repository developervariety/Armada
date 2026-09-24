import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { createPromptTemplate, resetPromptTemplate, listAllPromptTemplates } from '../api/client';
import type { PromptTemplate } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { canEditOwned, canWrite, resolveCreateScope, viewerFromAuth, OWNED_RECORD_WRITE_LEVEL } from '../lib/scoping';
import ScopeBadge from '../components/shared/ScopeBadge';
import Pagination from '../components/shared/Pagination';
import ActionMenu from '../components/shared/ActionMenu';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import JsonViewer from '../components/shared/JsonViewer';
import RecordDetailModal from '../components/shared/RecordDetailModal';
import StatusBadge from '../components/shared/StatusBadge';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import PageHeader from '../components/shared/PageHeader';
import ErrorModal from '../components/shared/ErrorModal';
import { useAutoRefresh } from '../lib/useAutoRefresh';
import { useResourceTable } from '../lib/useResourceTable';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import { buildPromptTemplateDuplicatePayload } from '../lib/duplicates';

// Column values for filtering and sorting; text compares without case.
const TEMPLATE_COLUMNS: Record<string, (template: PromptTemplate) => string | number> = {
  name: template => template.name.toLowerCase(),
  description: template => (template.description ?? '').toLowerCase(),
  category: template => template.category.toLowerCase(),
  isBuiltIn: template => (template.isBuiltIn ? 1 : 0),
  contentLength: template => (template.content ?? '').length,
  active: template => (template.active ? 1 : 0),
  lastUpdateUtc: template => template.lastUpdateUtc ?? '',
};

const CATEGORY_OPTIONS = ['all', 'mission', 'persona', 'structure', 'commit', 'landing', 'agent'] as const;

export default function PromptTemplates() {
  const navigate = useNavigate();
  const { t: translate, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const viewer = viewerFromAuth(useAuth());
  const writeLevel = OWNED_RECORD_WRITE_LEVEL.promptTemplates;
  const canCreate = canWrite(viewer, writeLevel);
  const [templates, setTemplates] = useState<PromptTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // JSON viewer
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });

  // Row-click view modal
  const [viewRecord, setViewRecord] = useState<Record<string, unknown> | null>(null);

  // Confirm dialog
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });

  // Category tab bar filter
  const [categoryFilter, setCategoryFilter] = useState('all');
  const categoryRowFilter = useCallback(
    (template: PromptTemplate) => categoryFilter === 'all' || template.category.toLowerCase() === categoryFilter.toLowerCase(),
    [categoryFilter],
  );

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const result = await listAllPromptTemplates();
      setTemplates(result);
      setError('');
    } catch {
      setError(translate('Failed to load prompt templates.'));
    } finally {
      setLoading(false);
    }
  }, [translate]);

  useEffect(() => { load(); }, [load]);
  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('prompttemplates', load);

  const {
    colFilters, setColFilter, handleSort, sortIcon, pageSize, setPageNumber, setPageSize, totalPages, currentPage,
    sorted, paginated,
  } = useResourceTable<PromptTemplate>({
    rows: templates,
    getId: template => template.name,
    columnValues: TEMPLATE_COLUMNS,
    rowFilter: categoryRowFilter,
    initialSortField: 'category',
  });

  // Actions
  function handleResetToDefault(name: string) {
    setConfirm({
      open: true,
      title: translate('Reset to Default'),
      message: translate('Reset template "{{name}}" to its built-in default content? Any custom edits will be lost.', { name }),
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await resetPromptTemplate(name);
          pushToast('success', translate('Template "{{name}}" reset to default.', { name }));
          load();
        } catch { setError(translate('Reset failed.')); }
      },
    });
  }

  async function handleDuplicate(template: PromptTemplate) {
    try {
      const created = await createPromptTemplate({ ...buildPromptTemplateDuplicatePayload(template), ownershipScope: resolveCreateScope(viewer, template.ownershipScope) });
      pushToast('success', translate('Template "{{name}}" duplicated.', { name: created.name }));
      navigate(`/prompt-templates/${encodeURIComponent(created.name)}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : translate('Duplicate failed.'));
    }
  }

  return (
    <div>
      <PageHeader
        title={translate('Prompt Templates')}
        subtitle={translate('Prompt templates define the instructions and structure used when generating prompts for captains and missions.')}
        actions={(
          <>
            <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
            <RefreshButton onRefresh={load} title={translate('Refresh prompt template data')} />
            {canCreate && (
              <button className="btn btn-primary btn-sm" onClick={() => navigate('/prompt-templates/create')}>
                + {translate('Prompt Template')}
              </button>
            )}
          </>
        )}
      />

      <ErrorModal error={error} onClose={() => setError('')} />

      {/* JSON Viewer */}
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />

      {/* Row-click View Modal */}
      <RecordDetailModal
        open={!!viewRecord}
        title={typeof viewRecord?.name === 'string' ? viewRecord.name : translate('Prompt Template')}
        subtitle={translate('Prompt Template')}
        record={viewRecord}
        sizeClassName="modal-large modal-prompt-template"
        onClose={() => setViewRecord(null)}
        onEdit={() => { const r = viewRecord; setViewRecord(null); navigate(`/prompt-templates/${encodeURIComponent((r as { name: string }).name)}`); }}
        editLabel={translate('Open Details')}
      />

      {/* Confirm Dialog */}
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message}
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />

      {/* Category tab bar */}
      {templates.length > 0 && (
        <div style={{ display: 'flex', gap: '0.25rem', marginBottom: '1rem', flexWrap: 'wrap' }}>
          {CATEGORY_OPTIONS.map(cat => (
            <button
              key={cat}
              className={`btn btn-sm${categoryFilter === cat ? ' btn-primary' : ''}`}
              onClick={() => { setCategoryFilter(cat); setPageNumber(1); }}
              style={{ textTransform: 'capitalize', padding: '0.25rem 0.75rem', fontSize: '0.85rem' }}
            >
              {cat === 'all' ? translate('All') : translate(cat)}
            </button>
          ))}
        </div>
      )}

      {loading && templates.length === 0 && <p className="text-dim">{translate('Loading...')}</p>}
      {!loading && templates.length === 0 && <p className="text-dim">{translate('No prompt templates found.')}</p>}

      {templates.length > 0 && (
        <>
          <Pagination pageNumber={currentPage} pageSize={pageSize} totalPages={totalPages}
            totalRecords={sorted.length}
            onPageChange={p => setPageNumber(p)} onPageSizeChange={s => { setPageSize(s); setPageNumber(1); }} />

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="sortable" onClick={() => handleSort('name')} title={translate('Template name -- click to sort')}>
                    {translate('Name')}{sortIcon('name')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('description')} title={translate('Description -- click to sort')}>
                    {translate('Description')}{sortIcon('description')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('category')} title={translate('Category -- click to sort')}>
                    {translate('Category')}{sortIcon('category')}
                  </th>
                  <th>{translate('Visibility')}</th>
                  <th className="sortable" onClick={() => handleSort('isBuiltIn')} title={translate('Built-in -- click to sort')}>
                    {translate('Built-in')}{sortIcon('isBuiltIn')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('contentLength')} title={translate('Content length -- click to sort')}>
                    {translate('Content Length')}{sortIcon('contentLength')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('active')} title={translate('Active -- click to sort')}>
                    {translate('Active')}{sortIcon('active')}
                  </th>
                  <th className="sortable" onClick={() => handleSort('lastUpdateUtc')} title={translate('Last updated -- click to sort')}>
                    {translate('Last Updated')}{sortIcon('lastUpdateUtc')}
                  </th>
                  <th className="text-right">{translate('Actions')}</th>
                </tr>
                <tr className="column-filter-row">
                  <td><input type="text" className="col-filter" value={colFilters.name ?? ''} onChange={e => setColFilter('name', e.target.value)} placeholder={translate('Filter...')} /></td>
                  <td><input type="text" className="col-filter" value={colFilters.description ?? ''} onChange={e => setColFilter('description', e.target.value)} placeholder={translate('Filter...')} /></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                </tr>
              </thead>
              <tbody>
                {paginated.map(template => (
                  <tr key={template.id} className="clickable" onClick={() => setViewRecord(template as unknown as Record<string, unknown>)}>
                    <td><strong>{template.name}</strong></td>
                    <td className="text-dim">{template.description || '-'}</td>
                    <td><StatusBadge status={template.category} /></td>
                    <td><ScopeBadge scope={template.ownershipScope} /></td>
                    <td>{template.isBuiltIn ? <StatusBadge status="Built-in" /> : '-'}</td>
                    <td className="mono text-dim">{(template.content ?? '').length.toLocaleString()} {translate('chars')}</td>
                    <td><StatusBadge status={template.active !== false ? 'Active' : 'Inactive'} /></td>
                    <td className="text-dim" title={formatDateTime(template.lastUpdateUtc)}>{formatRelativeTime(template.lastUpdateUtc)}</td>
                    <td className="text-right" onClick={e => e.stopPropagation()}>
                      <ActionMenu id={`template-${template.id}`} items={[
                        canEditOwned(viewer, template, writeLevel)
                          ? { label: 'Edit', onClick: () => navigate(`/prompt-templates/${encodeURIComponent(template.name)}`) }
                          : { label: 'Open', onClick: () => navigate(`/prompt-templates/${encodeURIComponent(template.name)}`) },
                        ...(canCreate ? [{ label: 'Duplicate', onClick: () => void handleDuplicate(template) }] : []),
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `${translate('Template')}: ${template.name}`, data: template }) },
                        ...(template.isBuiltIn && canEditOwned(viewer, template, writeLevel) ? [{ label: 'Reset to Default', danger: true as const, onClick: () => handleResetToDefault(template.name) }] : []),
                      ]} />
                    </td>
                  </tr>
                ))}
                {paginated.length === 0 && (
                  <tr><td colSpan={9} className="text-dim">{translate('No prompt templates match the current filters.')}</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
