import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import UsageRoutingEditor, { emptyUsageRouting } from './UsageRoutingEditor';
import { previewUsageRouting } from '../api/client';
vi.mock('../api/client', () => ({ previewUsageRouting: vi.fn() }));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));

describe('Usage routing editor', () => {
  it('keeps draft edits separate and previews the requested persona and priority', async () => {
    vi.mocked(previewUsageRouting).mockResolvedValue({ reason: 'preferred_eligible_route_with_allowance', candidates: [] });
    const onChange = vi.fn();
    render(<UsageRoutingEditor value={JSON.stringify(emptyUsageRouting)} onChange={onChange} statuses={[]} />);
    fireEvent.change(screen.getByLabelText('Persona'), { target: { value: 'Judge' } });
    fireEvent.change(screen.getByLabelText('Priority'), { target: { value: '10' } });
    fireEvent.click(screen.getByText('Preview usage routing'));
    await waitFor(() => expect(previewUsageRouting).toHaveBeenCalledWith({ persona: 'Judge', priority: 10, preferredModel: null, usageRouting: emptyUsageRouting }));
    expect(onChange).not.toHaveBeenCalled();
    expect(await screen.findByRole('status')).toHaveTextContent('preferred_eligible_route_with_allowance');
  });
  it('blocks preview of invalid JSON and shows unknown data explicitly', () => {
    render(<UsageRoutingEditor value="{" onChange={vi.fn()} statuses={[{ accountId: 'example', state: 'Unknown', source: 'none', reason: 'required_usage_window_unknown_or_stale', windows: [] }]} />);
    expect(screen.getByText('Preview usage routing')).toBeDisabled();
    expect(screen.getAllByText('Unknown').length).toBeGreaterThan(0);
  });
});
