import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { listPlaybooks } from '../../api/client';
import type { Playbook, PlaybookDeliveryMode, SelectedPlaybook } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

const DELIVERY_MODE_COPY: Record<PlaybookDeliveryMode, { label: string; description: string }> = {
  InlineFullContent: {
    label: 'Inline Full Content',
    description: 'Inject the complete markdown into the mission instructions.',
  },
  InstructionWithReference: {
    label: 'Instruction With Reference',
    description: 'Tell the model to read the materialized playbook path outside the worktree.',
  },
  AttachIntoWorktree: {
    label: 'Attach Into Worktree',
    description: 'Materialize the playbook in `.armada/playbooks/` and instruct the model to read it there.',
  },
};

interface PlaybookSelectorProps {
  value: SelectedPlaybook[];
  onChange: (next: SelectedPlaybook[]) => void;
  disabled?: boolean;
}

export default function PlaybookSelector({ value, onChange, disabled = false }: PlaybookSelectorProps) {
  const { t, formatRelativeTime } = useLocale();
  const [playbooks, setPlaybooks] = useState<Playbook[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [draftPlaybookId, setDraftPlaybookId] = useState('');
  const [draftDeliveryMode, setDraftDeliveryMode] = useState<PlaybookDeliveryMode>('InlineFullContent');

  useEffect(() => {
    let mounted = true;

    async function load() {
      try {
        setLoading(true);
        const result = await listPlaybooks({ pageSize: 9999 });
        if (!mounted) return;
        setPlaybooks(result.objects || []);
        setError('');
      } catch (err: unknown) {
        if (!mounted) return;
        setError(err instanceof Error ? err.message : t('Failed to load playbooks.'));
      } finally {
        if (mounted) setLoading(false);
      }
    }

    load();
    return () => { mounted = false; };
  }, [t]);

  const activePlaybooks = playbooks.filter((playbook) => playbook.active !== false);
  const selectedIds = new Set(value.map((item) => item.playbookId));
  const availableDraftPlaybooks = activePlaybooks.filter((playbook) => !selectedIds.has(playbook.id));

  useEffect(() => {
    const availableIds = new Set(availableDraftPlaybooks.map((playbook) => playbook.id));
    setDraftPlaybookId((current) => availableIds.has(current) ? current : '');
  }, [availableDraftPlaybooks]);

  function resolvePlaybook(playbookId: string): Playbook | undefined {
    return playbooks.find((playbook) => playbook.id === playbookId);
  }

  function getOptionsForRow(playbookId: string) {
    const otherSelectedIds = new Set(
      value
        .filter((item) => item.playbookId !== playbookId)
        .map((item) => item.playbookId),
    );
    const options = activePlaybooks.filter((playbook) => playbook.id === playbookId || !otherSelectedIds.has(playbook.id));
    const current = resolvePlaybook(playbookId);
    if (current && !options.some((playbook) => playbook.id === current.id)) {
      return [current, ...options];
    }

    return options;
  }

  function updateSelectedPlaybook(index: number, playbookId: string) {
    if (disabled || !playbookId) return;

    onChange(value.map((item, itemIndex) => (
      itemIndex === index
        ? {
            ...item,
            playbookId,
          }
        : item
    )));
  }

  function updateSelectedDeliveryMode(index: number, deliveryMode: PlaybookDeliveryMode) {
    if (disabled) return;

    onChange(value.map((item, itemIndex) => (
      itemIndex === index
        ? {
            ...item,
            deliveryMode,
          }
        : item
    )));
  }

  function removeSelectedPlaybook(index: number) {
    if (disabled) return;
    onChange(value.filter((_, itemIndex) => itemIndex !== index));
  }

  function addSelectedPlaybook() {
    if (disabled || !draftPlaybookId) return;

    onChange([
      ...value,
      {
        playbookId: draftPlaybookId,
        deliveryMode: draftDeliveryMode,
      },
    ]);
    setDraftPlaybookId('');
    setDraftDeliveryMode('InlineFullContent');
  }

  function renderSummary(playbook: Playbook | undefined, deliveryMode: PlaybookDeliveryMode) {
    if (!playbook) {
      return (
        <div className="playbook-selector-row-summary">
          <span className="text-dim">{t('This playbook is no longer available in the active catalog.')}</span>
        </div>
      );
    }

    return (
      <div className="playbook-selector-row-summary">
        <span>{playbook.description || t('No description')}</span>
        <span>{t('{{count}} chars', { count: playbook.content.length.toLocaleString() })}</span>
        <span>{t('Updated {{time}}', { time: formatRelativeTime(playbook.lastUpdateUtc) })}</span>
        <span>{t(DELIVERY_MODE_COPY[deliveryMode].description)}</span>
      </div>
    );
  }

  return (
    <section className="playbook-selector-shell">
      <div className="playbook-selector-head">
        <div>
          <h3>{t('Playbooks')}</h3>
          <p className="text-dim">
            {t('Attach reusable markdown guidance to every mission in this dispatch. Playbooks are applied in the order listed below.')}
          </p>
        </div>
        <div className="playbook-selector-meta">
          <span>
            {loading
              ? t('Loading playbooks...')
              : error
                ? t('Playbooks unavailable')
                : t('{{count}} available', { count: availableDraftPlaybooks.length })}
          </span>
          <span>{t('{{count}} selected', { count: value.length })}</span>
          <Link to="/playbooks" className="playbook-selector-link">{t('Manage playbooks')}</Link>
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      {loading ? (
        <div className="playbook-selector-empty">
          <strong>{t('Loading playbooks...')}</strong>
          <span>{t('Fetching reusable guidance from Armada.')}</span>
        </div>
      ) : activePlaybooks.length === 0 ? (
        <div className="playbook-selector-empty emphasized">
          <strong>{t('No active playbooks found.')}</strong>
          <span>{t('Create one from the Playbooks page, then return here to attach it.')}</span>
        </div>
      ) : (
        <div className="playbook-selector-list">
          {value.map((item, index) => {
            const playbook = resolvePlaybook(item.playbookId);
            const options = getOptionsForRow(item.playbookId);

            return (
              <div key={`${item.playbookId}-${index}`} className="playbook-selector-row">
                <div className="playbook-selector-row-main">
                  <label className="playbook-selector-field">
                    <span>{t('Playbook {{index}}', { index: index + 1 })}</span>
                    <select
                      value={item.playbookId}
                      disabled={disabled}
                      onChange={(event) => updateSelectedPlaybook(index, event.target.value)}
                    >
                      {options.map((option) => (
                        <option key={option.id} value={option.id}>
                          {option.fileName}
                        </option>
                      ))}
                      {!playbook && (
                        <option value={item.playbookId}>
                          {t('{{id}} (Unavailable)', { id: item.playbookId })}
                        </option>
                      )}
                    </select>
                  </label>

                  <label className="playbook-selector-field playbook-selector-mode-field">
                    <span>{t('Delivery Mode')}</span>
                    <select
                      value={item.deliveryMode}
                      disabled={disabled}
                      onChange={(event) => updateSelectedDeliveryMode(index, event.target.value as PlaybookDeliveryMode)}
                    >
                      {Object.entries(DELIVERY_MODE_COPY).map(([mode, copy]) => (
                        <option key={mode} value={mode}>
                          {t(copy.label)}
                        </option>
                      ))}
                    </select>
                  </label>

                  <div className="playbook-selector-row-actions">
                    <button
                      type="button"
                      className="icon-btn icon-btn-delete"
                      disabled={disabled}
                      aria-label={t('Remove playbook {{index}}', { index: index + 1 })}
                      title={t('Remove playbook {{index}}', { index: index + 1 })}
                      onClick={() => removeSelectedPlaybook(index)}
                    />
                  </div>
                </div>

                {renderSummary(playbook, item.deliveryMode)}
              </div>
            );
          })}

          {availableDraftPlaybooks.length > 0 && (
            <div className="playbook-selector-row playbook-selector-row-draft">
              <div className="playbook-selector-row-main">
                <label className="playbook-selector-field">
                  <span>{t('Add Playbook')}</span>
                  <select
                    value={draftPlaybookId}
                    disabled={disabled}
                    onChange={(event) => setDraftPlaybookId(event.target.value)}
                  >
                    <option value="">{t('Select a playbook...')}</option>
                    {availableDraftPlaybooks.map((playbook) => (
                      <option key={playbook.id} value={playbook.id}>
                        {playbook.fileName}
                      </option>
                    ))}
                  </select>
                </label>

                <label className="playbook-selector-field playbook-selector-mode-field">
                  <span>{t('Delivery Mode')}</span>
                  <select
                    value={draftDeliveryMode}
                    disabled={disabled}
                    onChange={(event) => setDraftDeliveryMode(event.target.value as PlaybookDeliveryMode)}
                  >
                    {Object.entries(DELIVERY_MODE_COPY).map(([mode, copy]) => (
                      <option key={mode} value={mode}>
                        {t(copy.label)}
                      </option>
                    ))}
                  </select>
                </label>

                <div className="playbook-selector-row-actions">
                  <button
                    type="button"
                    className="icon-btn icon-btn-add"
                    disabled={disabled || !draftPlaybookId}
                    aria-label={t('Add playbook')}
                    title={t('Add playbook')}
                    onClick={addSelectedPlaybook}
                  />
                </div>
              </div>

              {draftPlaybookId ? (
                renderSummary(resolvePlaybook(draftPlaybookId), draftDeliveryMode)
              ) : (
                <div className="playbook-selector-row-summary">
                  <span className="text-dim">
                    {t('Choose a playbook and delivery mode, then add it to the dispatch sequence.')}
                  </span>
                </div>
              )}
            </div>
          )}

          {availableDraftPlaybooks.length === 0 && value.length > 0 && (
            <div className="playbook-selector-helper text-dim">
              {t('All active playbooks are already attached to this request.')}
            </div>
          )}

          {value.length === 0 && (
            <div className="playbook-selector-helper text-dim">
              {t('Add only the reusable guidance you want automatically applied to every mission in this dispatch.')}
            </div>
          )}
        </div>
      )}
    </section>
  );
}
