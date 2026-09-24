import { useEffect, useState, useCallback } from 'react';
import { createTenant, updateTenant, deleteTenant, listAllTenants } from '../../api/client';
import type { TenantMetadata } from '../../types/models';
import Pagination from '../../components/shared/Pagination';
import ActionMenu from '../../components/shared/ActionMenu';
import ConfirmDialog from '../../components/shared/ConfirmDialog';
import JsonViewer from '../../components/shared/JsonViewer';
import CopyButton from '../../components/shared/CopyButton';
import RefreshButton from '../../components/shared/RefreshButton';
import AutoRefreshSelect from '../../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../../lib/useAutoRefresh';
import { useResourceTable } from '../../lib/useResourceTable';
import { useAuth } from '../../context/AuthContext';
import ErrorModal from '../../components/shared/ErrorModal';
import { useLocale } from '../../context/LocaleContext';
import { useNotifications } from '../../context/NotificationContext';
import { useProxySessionContext } from '../../lib/useProxySessionContext';

// Column values for filtering and sorting; names compare without case.
const TENANT_COLUMNS: Record<string, (tenant: TenantMetadata) => string | number> = {
  name: tenant => tenant.name.toLowerCase(),
  active: tenant => (tenant.active ? 1 : 0),
  createdUtc: tenant => tenant.createdUtc,
};

export default function Tenants() {
  const { user, isAdmin } = useAuth();
  const { t, formatRelativeTime, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();
  const proxyContext = useProxySessionContext();
  const [items, setItems] = useState<TenantMetadata[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<TenantMetadata | null>(null);
  const [form, setForm] = useState({ name: '', active: true });
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; resourceName?: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });
  const remoteProxyMode = Boolean(proxyContext?.selectedInstanceId);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      if (isAdmin) {
        // The server returns 10 rows without a page size; read every page at its 1000-row maximum.
        setItems(await listAllTenants());
      } else {
        setItems(user?.tenant ? [user.tenant] : []);
      }
      setError('');
    } catch { setError(t('Failed to load tenants.')); }
    finally { setLoading(false); }
  }, [isAdmin, t, user?.tenant]);

  useEffect(() => { load(); }, [load]);
  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('tenants', load);

  const {
    colFilters, setColFilter, handleSort, sortIcon, pageSize, setPageNumber, setPageSize, totalPages, currentPage,
    sorted, paginated, selected, setSelected, toggleSelect, allSelected, selectAll, clearSelection,
  } = useResourceTable<TenantMetadata>({ rows: items, getId: tenant => tenant.id, columnValues: TENANT_COLUMNS, initialSortField: 'name' });

  function openCreate() {
    if (remoteProxyMode) return;
    setForm({ name: '', active: true });
    setEditing(null);
    setShowForm(true);
  }
  function openEdit(tenant: TenantMetadata) {
    if (remoteProxyMode) {
      setJsonData({ open: true, title: `Tenant: ${tenant.name}`, data: tenant });
      return;
    }
    setForm({ name: tenant.name, active: tenant.active });
    setEditing(tenant);
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    try {
      if (editing) await updateTenant(editing.id, form);
      else await createTenant(form);
      setShowForm(false);
      pushToast('success', editing
        ? t('Tenant "{{name}}" saved.', { name: form.name })
        : t('Tenant "{{name}}" created.', { name: form.name }));
      load();
    } catch { setError(t('Save failed.')); }
  }

  function handleDelete(id: string, name: string) {
    setConfirm({
      open: true, title: t('Delete Tenant'),
      message: t('Delete tenant "{{name}}"? This is destructive and cannot be undone.', { name }),
      resourceName: name,
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        try {
          await deleteTenant(id);
          pushToast('warning', t('Tenant "{{name}}" deleted.', { name }));
          load();
        } catch { setError(t('Delete failed.')); }
      },
    });
  }

  function handleBulkDelete() {
    setConfirm({
      open: true, title: t('Delete Selected Tenants'),
      message: t('Delete {{count}} tenant(s)? This cannot be undone.', { count: selected.length }),
      resourceName: `${selected.length} tenant(s)`,
      onConfirm: async () => {
        setConfirm(c => ({ ...c, open: false }));
        const ids = [...selected]; setSelected([]);
        let failed = 0;
        for (const id of ids) { try { await deleteTenant(id); } catch { failed++; } }
        const success = ids.length - failed;
        if (success > 0) {
          pushToast(failed > 0 ? 'warning' : 'success', failed > 0
            ? t('Deleted {{success}} tenants. {{failed}} failed.', { success, failed })
            : t('Deleted {{success}} tenants.', { success }));
        }
        if (failed > 0) setError(t('Deleted {{success}}, {{failed}} failed.', { success: ids.length - failed, failed }));
        load();
      },
    });
  }

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Tenants')}</h2>
          <p className="text-dim view-subtitle">
            {isAdmin
              ? t('Manage tenants in the system. Each tenant is an isolated organizational unit.')
              : t('View your tenant information.')}
          </p>
        </div>
        <div className="view-actions">
          {isAdmin && !remoteProxyMode && selected.length > 0 && <button className="btn btn-sm btn-danger" onClick={handleBulkDelete}>{t('Delete Selected')} ({selected.length})</button>}
          {isAdmin && !remoteProxyMode && <button className="btn btn-primary btn-sm" onClick={openCreate}>+ {t('Tenant')}</button>}
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title="Refresh tenants" />
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      {remoteProxyMode && (
        <div className="alert alert-warning" style={{ marginBottom: '1rem' }}>
          {t(
            'This page is connected through Armada.Proxy for {{instanceId}}. Tenant create, edit, and delete actions are blocked in remote mode.',
            { instanceId: proxyContext?.selectedInstanceId ?? t('the selected deployment') },
          )}
        </div>
      )}

      {showForm && (
        <div className="modal-overlay" onClick={() => setShowForm(false)}>
          <form className="modal" onClick={e => e.stopPropagation()} onSubmit={handleSubmit}>
            <h3>{editing ? t('Edit Tenant') : t('Create Tenant')}</h3>
            <label>{t('Name')}<input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} required /></label>
            {editing && (
              <label className="checkbox-label"><input type="checkbox" checked={form.active} onChange={e => setForm({ ...form, active: e.target.checked })} /> {t('Active')}</label>
            )}
            <div className="modal-actions">
              <button type="submit" className="btn btn-primary">{t('Save')}</button>
              <button type="button" className="btn" onClick={() => setShowForm(false)}>{t('Cancel')}</button>
            </div>
          </form>
        </div>
      )}

      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message} resourceName={confirm.resourceName} danger requireDeleteConfirm onConfirm={confirm.onConfirm} onCancel={() => setConfirm(c => ({ ...c, open: false }))} />

      {loading && items.length === 0 && <p className="text-dim">{t('Loading...')}</p>}
      {!loading && items.length === 0 && <p className="text-dim">{t('No tenants found.')}</p>}

      {items.length > 0 && (
        <>
          <Pagination pageNumber={currentPage} pageSize={pageSize} totalPages={totalPages} totalRecords={sorted.length}
            onPageChange={p => setPageNumber(p)} onPageSizeChange={s => { setPageSize(s); setPageNumber(1); }} />
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="col-checkbox"><input type="checkbox" checked={allSelected} onChange={e => e.target.checked ? selectAll() : clearSelection()} title={t('Select all tenants')} /></th>
                  <th className="sortable" onClick={() => handleSort('name')}>{t('Name')}{sortIcon('name')}</th>
                  <th>{t('ID')}</th>
                  <th className="sortable" onClick={() => handleSort('active')}>{t('Active')}{sortIcon('active')}</th>
                  <th className="sortable" onClick={() => handleSort('createdUtc')}>{t('Created')}{sortIcon('createdUtc')}</th>
                  <th>{t('Last Updated')}</th>
                  <th className="text-right">{t('Actions')}</th>
                </tr>
                <tr className="column-filter-row">
                  <td></td>
                  <td><input type="text" className="col-filter" value={colFilters.name ?? ''} onChange={e => setColFilter('name', e.target.value)} placeholder={t('Search...')} /></td>
                  <td></td><td></td><td></td><td></td><td></td>
                </tr>
              </thead>
              <tbody>
                {paginated.map(tenant => (
                  <tr
                    key={tenant.id}
                    className="clickable"
                    onClick={() => isAdmin && !remoteProxyMode
                      ? openEdit(tenant)
                      : setJsonData({ open: true, title: `Tenant: ${tenant.name}`, data: tenant })}
                  >
                    <td className="col-checkbox" onClick={e => e.stopPropagation()}><input type="checkbox" checked={selected.includes(tenant.id)} onChange={() => toggleSelect(tenant.id)} title={t('Select this tenant')} /></td>
                    <td><strong>{tenant.name}</strong></td>
                    <td className="mono text-dim table-id-cell">
                      <span className="id-display">
                        <span className="id-value" title={tenant.id}>{tenant.id}</span>
                        <CopyButton text={tenant.id} onClick={e => e.stopPropagation()} />
                      </span>
                    </td>
                    <td>{tenant.active ? t('Yes') : t('No')}</td>
                    <td className="text-dim" title={formatDateTime(tenant.createdUtc)}>{formatRelativeTime(tenant.createdUtc)}</td>
                    <td className="text-dim" title={formatDateTime(tenant.lastUpdateUtc)}>{formatRelativeTime(tenant.lastUpdateUtc)}</td>
                    <td className="text-right" onClick={e => e.stopPropagation()}>
                      <ActionMenu id={tenant.id} items={[
                        ...(isAdmin && !remoteProxyMode ? [{ label: 'Edit', onClick: () => openEdit(tenant) }] : []),
                        { label: 'View JSON', onClick: () => setJsonData({ open: true, title: `Tenant: ${tenant.name}`, data: tenant }) },
                        ...(isAdmin && !remoteProxyMode ? [{ label: 'Delete', danger: true as const, onClick: () => handleDelete(tenant.id, tenant.name) }] : []),
                      ]} />
                    </td>
                  </tr>
                ))}
                {paginated.length === 0 && <tr><td colSpan={7} className="text-dim">{t('No tenants match filters.')}</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
