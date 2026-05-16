import { createRef } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PlanningTranscriptCard from './PlanningTranscriptCard';

vi.mock('../shared/StatusBadge', () => ({
  default: ({ status }: { status: string }) => <span>{status}</span>,
}));

const t = (value: string) => value;
const formatDateTime = (value: string) => value;
const formatRelativeTime = () => 'just now';

describe('PlanningTranscriptCard', () => {
  it('renders waiting assistant output and allows selecting a completed assistant reply', async () => {
    const user = userEvent.setup();
    const onSelectMessage = vi.fn();

    render(
      <PlanningTranscriptCard
        t={t}
        transcriptRef={createRef<HTMLDivElement>()}
        title="Session title"
        captainName="Planner"
        captainRuntime="Codex"
        vesselName="Vessel"
        branchName="feature/planning"
        pipelineName="FullPipeline"
        playbookCount={1}
        updatedUtc="2026-04-29T00:00:03Z"
        messages={[
          {
            id: 'psm_user',
            planningSessionId: 'psn_123',
            tenantId: null,
            userId: null,
            role: 'User',
            sequence: 1,
            content: 'Help me plan this.',
            isSelectedForDispatch: false,
            createdUtc: '2026-04-29T00:00:00Z',
            lastUpdateUtc: '2026-04-29T00:00:00Z',
          },
          {
            id: 'psm_waiting',
            planningSessionId: 'psn_123',
            tenantId: null,
            userId: null,
            role: 'Assistant',
            sequence: 2,
            content: '',
            isSelectedForDispatch: false,
            createdUtc: '2026-04-29T00:00:01Z',
            lastUpdateUtc: '2026-04-29T00:00:01Z',
          },
          {
            id: 'psm_ready',
            planningSessionId: 'psn_123',
            tenantId: null,
            userId: null,
            role: 'Assistant',
            sequence: 3,
            content: 'Structured draft output',
            isSelectedForDispatch: false,
            createdUtc: '2026-04-29T00:00:02Z',
            lastUpdateUtc: '2026-04-29T00:00:02Z',
          },
        ]}
        selectedMessageId=""
        currentStatus="Active"
        composer=""
        sending={false}
        canSend={false}
        canStop
        stopping={false}
        deleting={false}
        formatDateTime={formatDateTime}
        formatRelativeTime={formatRelativeTime}
        onSelectMessage={onSelectMessage}
        onComposerChange={() => undefined}
        onSend={() => undefined}
        onStop={() => undefined}
        onDelete={() => undefined}
      />,
    );

    expect(screen.getByText('Waiting for response...')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Use For Dispatch' }));
    expect(onSelectMessage).toHaveBeenCalledWith('psm_ready');
  });

  it('sends the composed message when the session is active', async () => {
    const user = userEvent.setup();
    const onComposerChange = vi.fn();
    const onSend = vi.fn();

    render(
      <PlanningTranscriptCard
        t={t}
        transcriptRef={createRef<HTMLDivElement>()}
        title="Session title"
        captainName="Planner"
        captainRuntime="Codex"
        vesselName="Vessel"
        branchName="feature/planning"
        pipelineName="FullPipeline"
        playbookCount={1}
        updatedUtc="2026-04-29T00:00:03Z"
        messages={[]}
        selectedMessageId=""
        currentStatus="Active"
        composer="Add a migration checklist"
        sending={false}
        canSend
        canStop
        stopping={false}
        deleting={false}
        formatDateTime={formatDateTime}
        formatRelativeTime={formatRelativeTime}
        onSelectMessage={() => undefined}
        onComposerChange={onComposerChange}
        onSend={onSend}
        onStop={() => undefined}
        onDelete={() => undefined}
      />,
    );

    await user.type(screen.getByRole('textbox', { name: 'Send Message' }), ' now');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(onComposerChange).toHaveBeenCalled();
    expect(onSend).toHaveBeenCalledTimes(1);
  });
});
