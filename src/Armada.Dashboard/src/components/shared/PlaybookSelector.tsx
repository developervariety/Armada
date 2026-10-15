import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { listPlaybooks } from '../../api/client';
import { listAll } from '../../api/paging';
import type { Playbook, PlaybookDeliveryMode, SelectedPlaybook } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import { activePlaybooksOf, availablePlaybooks, DEFAULT_PLAYBOOK_DELIVERY_MODE, PLAYBOOK_DELIVERY_MODES as DELIVERY_MODE_COPY, playbookOptionsForRow } from '../../lib/playbookSelection';

interface PlaybookSelectorProps {
  value: SelectedPlaybook[];
  onChange: (next: SelectedPlaybook[]) => void;
  disabled?: boolean;
}

export default function PlaybookSelector({ value, onChange, disabled = false }: PlaybookSelectorProps) {
  const { t } = useLocale();
  const [playbooks, setPlaybooks] = useState<Playbook[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [draftPlaybookId, setDraftPlaybookId] = useState('');
  const [draftDeliveryMode, setDraftDeliveryMode] = useState<PlaybookDeliveryMode>(DEFAULT_PLAYBOOK_DELIVERY_MODE);

  useEffect(() => {
    let mounted = true;

    async function load() {
      try {
        setLoading(true);
        const result = await listAll(listPlaybooks);
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

  const activePlaybooks = activePlaybooksOf(playbooks);
  const availableDraftPlaybooks = availablePlaybooks(playbooks, value);

  useEffect(() => {
    const availableIds = new Set(availableDraftPlaybooks.map((playbook) => playbook.id));
    setDraftPlaybookId((current) => availableIds.has(current) ? current : '');
  }, [availableDraftPlaybooks]);

  function resolvePlaybook(playbookId: string): Playbook | undefined {
    return playbooks.find((playbook) => playbook.id === playbookId);
  }

  function getOptionsForRow(playbookId: string) {
    return playbookOptionsForRow(playbooks, value, playbookId);
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
    setDraftDeliveryMode(DEFAULT_PLAYBOOK_DELIVERY_MODE);
  }

  return (
    <section className="playbook-selector-shell">
      <div className="form-label-row">
        <label className="playbook-selector-section-label" title={t('Markdown guidance documents to give the captain with this work, such as coding standards or checklists. Optional. Each one is snapshotted onto the mission when it is dispatched.')}>{t('Playbooks')}</label>
        <Link to="/playbooks" className="form-label-link" title={t('Open the Playbooks page to create or edit playbooks. Unsaved choices here are lost if you leave.')}>{t('Manage playbooks')}</Link>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      {loading ? (
        <div className="text-dim">{t('Loading playbooks...')}</div>
      ) : activePlaybooks.length === 0 && value.length === 0 ? (
        <div className="playbook-selector-empty emphasized">
          <strong>{t('No active playbooks found.')}</strong>
          <span>{t('Create one from the Playbooks page, then return here to attach it.')}</span>
        </div>
      ) : (
        <div className="playbook-selector-list" role="group" aria-label={t('Playbooks')}>
          <div className="playbook-selector-column-head">
            <span title={t('Which playbook to attach. Only active playbooks are offered.')}>{t('Playbook')}</span>
            <span title={t('How the playbook reaches the captain: pasted into the prompt, given as a file path outside the worktree, or copied into the worktree under .armada/playbooks/.')}>{t('Delivery Mode')}</span>
            <span title={t('Add or remove playbooks.')}>{t('Actions')}</span>
          </div>

          {value.map((item, index) => {
            const playbook = resolvePlaybook(item.playbookId);
            const options = getOptionsForRow(item.playbookId);

            return (
              <div key={`${item.playbookId}-${index}`} className="playbook-selector-inline-row">
                <select
                  aria-label={t('Playbook {{index}}', { index: index + 1 })}
                  title={playbook?.description ? t('{{description}} Choose a different playbook to swap it.', { description: playbook.description }) : t('The attached playbook. Choose a different one to swap it.')}
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

                <select
                  aria-label={t('Delivery mode for playbook {{index}}', { index: index + 1 })}
                  title={t(DELIVERY_MODE_COPY[item.deliveryMode].description)}
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

                <div className="playbook-selector-row-actions">
                  <button
                    type="button"
                    className="icon-btn icon-btn-delete"
                    disabled={disabled}
                    aria-label={t('Remove playbook {{index}}', { index: index + 1 })}
                    title={t('Remove this playbook from the selection. The playbook itself is not deleted.')}
                    onClick={() => removeSelectedPlaybook(index)}
                  />
                </div>
              </div>
            );
          })}

          {availableDraftPlaybooks.length > 0 && (
            <div className="playbook-selector-inline-row">
              <select
                aria-label={t('Add Playbook')}
                title={t('Pick another active playbook to attach, then click the add button.')}
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

              <select
                aria-label={t('Delivery mode for new playbook')}
                title={t(DELIVERY_MODE_COPY[draftDeliveryMode].description)}
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

              <div className="playbook-selector-row-actions">
                <button
                  type="button"
                  className="icon-btn icon-btn-add"
                  disabled={disabled || !draftPlaybookId}
                  aria-label={t('Add playbook')}
                  title={t('Attach the chosen playbook with the chosen delivery mode.')}
                  onClick={addSelectedPlaybook}
                />
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
