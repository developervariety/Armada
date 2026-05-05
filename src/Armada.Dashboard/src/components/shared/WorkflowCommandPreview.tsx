import type { WorkflowProfileCommandPreview } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';

interface WorkflowCommandPreviewProps {
  commands: WorkflowProfileCommandPreview[] | null | undefined;
  emptyMessage?: string;
}

export default function WorkflowCommandPreview({ commands, emptyMessage }: WorkflowCommandPreviewProps) {
  const { t } = useLocale();

  if (!commands || commands.length === 0) {
    return <p className="text-dim">{emptyMessage || t('No resolved commands available.')}</p>;
  }

  return (
    <div className="table-wrap workflow-command-preview-table">
      <table>
        <thead>
          <tr>
            <th>{t('Check Type')}</th>
            <th>{t('Environment')}</th>
            <th>{t('Command')}</th>
          </tr>
        </thead>
        <tbody>
          {commands.map((command, index) => (
            <tr key={`${command.checkType}-${command.environmentName || 'base'}-${index}`}>
              <td>{command.checkType}</td>
              <td className="text-dim">{command.environmentName || t('Base')}</td>
              <td><code className="workflow-command-preview-code">{command.command}</code></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
