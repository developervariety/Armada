import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import RunApprovalFields, { DEFAULT_RUN_APPROVALS } from './RunApprovalFields';

const auth = { isAdmin: true, isTenantAdmin: false };
vi.mock('../../context/AuthContext', () => ({ useAuth: () => auth }));
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));
function Form() {
  const [value, setValue] = useState({ ...DEFAULT_RUN_APPROVALS });
  return <><RunApprovalFields value={value} onChange={setValue} /><output>{JSON.stringify(value)}</output></>;
}
describe('Per-run approvals', () => {
  it('defaults to a ten-minute countdown with Deny, and permits choosing Approve', () => {
    render(<Form />);
    expect(screen.getByLabelText('Approve all')).not.toBeChecked();
    expect(screen.getByLabelText('Approval timeout (seconds)')).toHaveValue(600);
    fireEvent.change(screen.getByLabelText('Approval timeout (seconds)'), { target: { value: '45' } });
    fireEvent.change(screen.getByLabelText('Default answer on timeout'), { target: { value: 'Approve' } });
    expect(screen.getByRole('status')).toHaveTextContent('"timeoutSeconds":45');
    expect(screen.getByRole('status')).toHaveTextContent('"timeoutDecision":"Approve"');
  });
  it('hides timeout controls while approve all is selected and preserves them when toggled back', () => {
    render(<Form />);
    fireEvent.click(screen.getByLabelText('Approve all'));
    expect(screen.queryByLabelText('Approval timeout (seconds)')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('"approveAll":true');
    fireEvent.click(screen.getByLabelText('Approve all'));
    expect(screen.getByLabelText('Approval timeout (seconds)')).toHaveValue(600);
  });
  it('restricts automatic approval choices to administrators', () => {
    auth.isAdmin = false;
    try {
      render(<Form />);
      expect(screen.getByLabelText('Approve all')).toBeDisabled();
      expect(screen.queryByRole('option', { name: 'Approve' })).not.toBeInTheDocument();
    } finally { auth.isAdmin = true; }
  });
});
