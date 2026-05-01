import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PlanningDispatchCard from './PlanningDispatchCard';

const t = (value: string) => value;

describe('PlanningDispatchCard', () => {
  it('invokes summarize, open, and dispatch actions when enabled', async () => {
    const user = userEvent.setup();
    const onSummarize = vi.fn();
    const onOpenInDispatch = vi.fn();
    const onDispatch = vi.fn();

    render(
      <PlanningDispatchCard
        t={t}
        selectedMessage={{
          id: 'psm_123',
          planningSessionId: 'psn_123',
          tenantId: null,
          userId: null,
          role: 'Assistant',
          sequence: 2,
          content: 'Draft content',
          isSelectedForDispatch: false,
          createdUtc: '2026-04-29T00:00:00Z',
          lastUpdateUtc: '2026-04-29T00:00:00Z',
        }}
        dispatchTitle="Planning Draft"
        dispatchDescription="Turn this into a voyage."
        canSummarize
        canOpenInDispatch
        canDispatch
        summarizing={false}
        dispatching={false}
        onDispatchTitleChange={() => undefined}
        onDispatchDescriptionChange={() => undefined}
        onSummarize={onSummarize}
        onOpenInDispatch={onOpenInDispatch}
        onDispatch={onDispatch}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Summarize Draft' }));
    await user.click(screen.getByRole('button', { name: 'Open In Dispatch' }));
    await user.click(screen.getByRole('button', { name: 'Dispatch' }));

    expect(onSummarize).toHaveBeenCalledTimes(1);
    expect(onOpenInDispatch).toHaveBeenCalledTimes(1);
    expect(onDispatch).toHaveBeenCalledTimes(1);
  });

  it('disables actions when the draft cannot be used yet', () => {
    render(
      <PlanningDispatchCard
        t={t}
        selectedMessage={null}
        dispatchTitle=""
        dispatchDescription=""
        canSummarize={false}
        canOpenInDispatch={false}
        canDispatch={false}
        summarizing={false}
        dispatching={false}
        onDispatchTitleChange={() => undefined}
        onDispatchDescriptionChange={() => undefined}
        onSummarize={() => undefined}
        onOpenInDispatch={() => undefined}
        onDispatch={() => undefined}
      />,
    );

    expect(screen.getByRole('button', { name: 'Summarize Draft' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Open In Dispatch' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Dispatch' })).toBeDisabled();
  });
});
