import type { PlanningSessionMessage } from '../../types/models';

interface PlanningDispatchCardProps {
  t: (value: string, vars?: Record<string, string | number>) => string;
  selectedMessage: PlanningSessionMessage | null;
  dispatchTitle: string;
  dispatchDescription: string;
  canSummarize: boolean;
  canOpenInDispatch: boolean;
  canDispatch: boolean;
  summarizing: boolean;
  dispatching: boolean;
  onDispatchTitleChange: (value: string) => void;
  onDispatchDescriptionChange: (value: string) => void;
  onSummarize: () => void;
  onOpenInDispatch: () => void;
  onDispatch: () => void;
}

export default function PlanningDispatchCard(props: PlanningDispatchCardProps) {
  const {
    t,
    selectedMessage,
    dispatchTitle,
    dispatchDescription,
    canSummarize,
    canOpenInDispatch,
    canDispatch,
    summarizing,
    dispatching,
    onDispatchTitleChange,
    onDispatchDescriptionChange,
    onSummarize,
    onOpenInDispatch,
    onDispatch,
  } = props;

  return (
    <div className="card" style={{ padding: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'start', marginBottom: '0.75rem' }}>
        <div>
          <h3 title={t('Turn a captain reply from this session into real work: a voyage with missions on the session\'s vessel.')}>{t('Dispatch From Session')}</h3>
          <p className="text-muted">
            {t('Select an assistant response, optionally summarize it into a cleaner draft, then dispatch or open the draft in the main dispatch page.')}
          </p>
        </div>
        {selectedMessage && (
          <span className="text-dim mono" style={{ fontSize: '0.8rem' }} title={t('ID of the transcript message the dispatch draft is based on.')}>
            {selectedMessage.id}
          </span>
        )}
      </div>

      <div className="dispatch-form">
        <div className="form-group">
          <label title={t('Name for the voyage this creates. Leave blank to use the session title or a server-chosen name.')}>{t('Voyage Title')}</label>
          <input aria-label={t('Voyage Title')}
            title={t('Name for the voyage this creates. Leave blank to use the session title or a server-chosen name.')}
            value={dispatchTitle}
            onChange={(event) => onDispatchTitleChange(event.target.value)}
            placeholder={t('Optional override for the resulting voyage title')}
          />
        </div>

        <div className="form-group">
          <label title={t('Instructions the captain will work from. Seeded from the reply you selected; edit it freely before dispatching. Required.')}>{t('Mission Description')}</label>
          <textarea aria-label={t('Mission Description')}
            title={t('Instructions the captain will work from. Seeded from the reply you selected; edit it freely before dispatching. Required.')}
            value={dispatchDescription}
            onChange={(event) => onDispatchDescriptionChange(event.target.value)}
            rows={10}
            placeholder={t('Select a planning response to seed the dispatch description.')}
          />
        </div>

        <div className="form-actions" style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
          <button type="button" className="btn" disabled={!canSummarize} onClick={onSummarize} title={canSummarize ? t('Have Armada rewrite the selected reply into a clean title and mission description. Nothing is dispatched yet.') : t('Select a captain reply in the transcript first.')}>
            {summarizing ? t('Summarizing...') : t('Summarize Draft')}
          </button>
          <button type="button" className="btn" disabled={!canOpenInDispatch} onClick={onOpenInDispatch} title={canOpenInDispatch ? t('Ends this session (releasing the captain), then opens the Dispatch page with this draft filled in so you can adjust settings before dispatching.') : t('Write or seed a mission description first.')}>
            {t('Open In Dispatch')}
          </button>
          <button type="button" className="btn-primary" disabled={!canDispatch} onClick={onDispatch} title={canDispatch ? t('Create a voyage on this session\'s vessel from the description above and queue it for captains now.') : t('Select a captain reply and make sure the mission description is not empty.')}>
            {dispatching ? t('Dispatching...') : t('Dispatch')}
          </button>
        </div>
      </div>
    </div>
  );
}
