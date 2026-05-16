import { useLocale } from '../../context/LocaleContext';
import type { CaptainToolAccessResult } from '../../types/models';

interface CaptainToolViewerProps {
  open: boolean;
  captainName: string;
  loading: boolean;
  error: string;
  data: CaptainToolAccessResult | null;
  onClose: () => void;
}

export default function CaptainToolViewer({ open, captainName, loading, error, data, onClose }: CaptainToolViewerProps) {
  const { t } = useLocale();

  if (!open) return null;

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal captain-tool-viewer" onClick={(event) => event.stopPropagation()}>
        <div className="captain-tool-viewer-header">
          <div>
            <h3>{t('Available Tools')}</h3>
            <p className="text-dim">{captainName}</p>
          </div>
          <button type="button" className="btn btn-sm" onClick={onClose}>
            {t('Close')}
          </button>
        </div>

        {loading && <p className="text-dim">{t('Loading captain tool access...')}</p>}
        {!loading && error && <p className="text-dim">{error}</p>}

        {!loading && !error && data && (
          <>
            <p className="text-dim captain-tool-viewer-summary">{data.summary}</p>
            <div className="captain-tool-viewer-meta">
              <span className={`tag ${data.toolsAccessible ? 'complete' : 'failed'}`}>
                {data.toolsAccessible ? t('Accessible') : t('Unavailable')}
              </span>
              <span className={`tag ${data.availabilityVerified ? 'working' : 'review'}`}>
                {data.availabilityVerified ? t('Verified') : t('Inferred')}
              </span>
              <span className="tag idle">{data.runtime}</span>
              <span className="tag idle">{t('{{count}} Armada tools', { count: data.armadaToolCount })}</span>
              {data.endpointName && <span className="tag idle">{data.endpointName}</span>}
              {typeof data.effectiveToolCount === 'number' && (
                <span className="tag idle">{t('{{count}} runtime tools', { count: data.effectiveToolCount })}</span>
              )}
            </div>

            {data.tools.length === 0 ? (
              <p className="text-dim">{t('No Armada tool catalog is available for this captain runtime.')}</p>
            ) : (
              <div className="table-wrap captain-tool-viewer-table">
                <table>
                  <thead>
                    <tr>
                      <th>{t('Tool')}</th>
                      <th>{t('Description')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.tools.map((tool) => (
                      <tr key={tool.name}>
                        <td className="mono">{tool.name}</td>
                        <td>{tool.description}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
