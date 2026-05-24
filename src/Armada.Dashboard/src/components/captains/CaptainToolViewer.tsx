import { useLocale } from '../../context/LocaleContext';
import type { CaptainToolAccessResult, CaptainToolServerSummary, CaptainToolSummary } from '../../types/models';

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

  const configuredMcpServers = data?.servers.filter((server) => server.sourceKind === 'McpServer') ?? [];
  const runtimeSources = data?.servers.filter((server) => server.sourceKind !== 'McpServer') ?? [];
  const runtimeInternalTools = data?.tools.filter((tool) => tool.sourceKind === 'RuntimeBuiltIn') ?? [];
  const mcpTools = data?.tools.filter((tool) => tool.sourceKind === 'McpServer') ?? [];
  const runtimeReportedToolCount = runtimeSources.reduce((total, source) => total + Math.max(0, source.toolCount), 0);

  function formatEndpoint(server: CaptainToolServerSummary) {
    return server.url || server.command || server.target || '-';
  }

  function formatServerNotes(server: CaptainToolServerSummary) {
    const notes: string[] = [];
    if (server.workingDirectory) notes.push(t('WD: {{path}}', { path: server.workingDirectory }));
    if (server.startupTimeoutSeconds > 0 || server.toolTimeoutSeconds > 0) {
      notes.push(t('{{startup}}s startup / {{tool}}s tool', { startup: server.startupTimeoutSeconds, tool: server.toolTimeoutSeconds }));
    }
    if (server.headerCount > 0) notes.push(t('{{count}} header(s)', { count: server.headerCount }));
    if (server.environmentVariableCount > 0) notes.push(t('{{count}} env var(s)', { count: server.environmentVariableCount }));
    if (server.enabledToolFilterCount > 0) notes.push(t('{{count}} allow filter(s)', { count: server.enabledToolFilterCount }));
    if (server.disabledToolFilterCount > 0) notes.push(t('{{count}} deny filter(s)', { count: server.disabledToolFilterCount }));
    if (server.errorMessage) notes.push(server.errorMessage);
    return notes.length > 0 ? notes.join(' | ') : '-';
  }

  function renderServerTable(title: string, servers: CaptainToolServerSummary[], emptyMessage: string) {
    return (
      <section className="captain-tool-viewer-section">
        <h4>{title}</h4>
        {servers.length === 0 ? (
          <p className="text-dim">{emptyMessage}</p>
        ) : (
          <div className="table-wrap captain-tool-viewer-table captain-tool-viewer-server-table">
            <table>
              <thead>
                <tr>
                  <th>{t('Source')}</th>
                  <th>{t('Transport')}</th>
                  <th>{t('Endpoint / Target')}</th>
                  <th>{t('Status')}</th>
                  <th>{t('Tools')}</th>
                  <th>{t('Notes')}</th>
                </tr>
              </thead>
              <tbody>
                {servers.map((server) => (
                  <tr key={`${server.sourceKind}:${server.name}`}>
                    <td>
                      <strong>{server.name}</strong>
                    </td>
                    <td>{server.transport || '-'}</td>
                    <td className="mono captain-tool-viewer-endpoint-cell" title={formatEndpoint(server)}>
                      {formatEndpoint(server)}
                    </td>
                    <td>
                      <span className={`tag ${server.reachable ? 'complete' : server.enabled ? 'review' : 'idle'}`}>
                        {server.status}
                      </span>
                    </td>
                    <td>{server.toolCount}</td>
                    <td className="text-dim captain-tool-viewer-notes-cell" title={formatServerNotes(server)}>
                      {formatServerNotes(server)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    );
  }

  function renderToolTable(title: string, tools: CaptainToolSummary[], emptyMessage: string) {
    return (
      <section className="captain-tool-viewer-section">
        <h4>{title}</h4>
        {tools.length === 0 ? (
          <p className="text-dim">{emptyMessage}</p>
        ) : (
          <div className="table-wrap captain-tool-viewer-table">
            <table>
              <thead>
                <tr>
                  <th>{t('Tool')}</th>
                  <th>{t('Server / Source')}</th>
                  <th>{t('Description')}</th>
                </tr>
              </thead>
              <tbody>
                {tools.map((tool) => (
                  <tr key={`${tool.registrationSource || 'internal'}:${tool.name}`}>
                    <td className="mono captain-tool-viewer-tool-name">{tool.name}</td>
                    <td className="captain-tool-viewer-source">
                      <span className="tag idle">{tool.registrationSource || t('Internal')}</span>
                    </td>
                    <td>{tool.description}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    );
  }

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
              {data.configuredServerCount > 0 && (
                <span className="tag idle">{t('{{count}} sources', { count: data.configuredServerCount })}</span>
              )}
              {data.reachableServerCount > 0 && (
                <span className="tag idle">{t('{{count}} reachable', { count: data.reachableServerCount })}</span>
              )}
              {data.endpointName && <span className="tag idle">{data.endpointName}</span>}
              {typeof data.effectiveToolCount === 'number' && (
                <span className="tag idle">{t('{{count}} listed tools', { count: data.effectiveToolCount })}</span>
              )}
            </div>
            {data.configuredServerCount > data.reachableServerCount && (
              <p className="text-dim captain-tool-viewer-summary">
                {t('This is a point-in-time snapshot. Configured MCP servers that did not respond may simply be offline right now.')}
              </p>
            )}

            {renderServerTable(
              t('Configured MCP Servers'),
              configuredMcpServers,
              t('No external MCP servers are configured for this captain runtime.'),
            )}

            {runtimeSources.length > 0 &&
              renderServerTable(
                t('Runtime Sources'),
                runtimeSources,
                t('No runtime-managed sources were reported for this captain runtime.'),
              )}

            {renderToolTable(
              t('Runtime Internal Tools'),
              runtimeInternalTools,
              runtimeReportedToolCount > 0
                ? t('This runtime reports {{count}} internal tool(s), but it does not expose individual tool names for every internal source.', { count: runtimeReportedToolCount })
                : t('No named runtime-internal tools are currently exposed for this captain runtime.'),
            )}

            {renderToolTable(
              t('MCP Tools'),
              mcpTools,
              configuredMcpServers.length > 0
                ? t('No named MCP tools were returned from the captain runtime\'s reachable MCP servers.')
                : t('No MCP tool inventory is available for this captain runtime.'),
            )}
          </>
        )}
      </div>
    </div>
  );
}
