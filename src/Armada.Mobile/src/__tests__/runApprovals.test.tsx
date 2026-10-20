import { useState } from 'react';
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { DEFAULT_RUN_APPROVALS, RunApprovalFields, validRunApprovalOptions } from '../components/app/RunApprovalFields';
import { renderW4, resetW4 } from '../test/w4';
import { CliPermissionCountdown } from '../components/cliPermissions/CliPermissionCountdown';

jest.mock('@dashboard/api/client', () => require('../test/w4Client').autoMockClient());

function Form() {
  const [value, onChange] = useState({ ...DEFAULT_RUN_APPROVALS });
  return <RunApprovalFields value={value} onChange={onChange} />;
}

beforeEach(async () => { await resetW4(); });

it('starts at ten minutes / Deny and validates integer timeout bounds', () => {
  expect(DEFAULT_RUN_APPROVALS).toEqual({ approveAll: false, timeoutSeconds: 600, timeoutDecision: 'Deny' });
  for (const timeoutSeconds of [0, 9, 3601, 10.5, NaN]) expect(validRunApprovalOptions({ ...DEFAULT_RUN_APPROVALS, timeoutSeconds })).toBe(false);
  for (const timeoutSeconds of [10, 600, 3600]) expect(validRunApprovalOptions({ ...DEFAULT_RUN_APPROVALS, timeoutSeconds })).toBe(true);
});

it('allows an administrator to set automatic approval or a timeout answer', async () => {
  await renderW4(<Form />);
  await fireEvent.press(screen.getByTestId('run-approval-decision'));
  await fireEvent.press(screen.getByTestId('run-approval-decision-option-Approve'));
  expect(screen.getByText('Approve')).toBeTruthy();
  await fireEvent(screen.getByTestId('run-approve-all'), 'valueChange', true);
  expect(screen.queryByTestId('run-approval-timeout')).toBeNull();
});

it('prevents regular users from selecting automatic approval', async () => {
  await renderW4(<Form />, 'user');
  await waitFor(() => expect(screen.getByText('Automatic approval requires an administrator.')).toBeTruthy());
  await fireEvent(screen.getByTestId('run-approve-all'), 'valueChange', true);
  expect(screen.getByTestId('run-approval-timeout')).toBeTruthy();
  await fireEvent.press(screen.getByTestId('run-approval-decision'));
  expect(screen.queryByTestId('run-approval-decision-option-Approve')).toBeNull();
});

it('shows the actual timeout answer alongside the live countdown', async () => {
  await renderW4(<CliPermissionCountdown expiresUtc="2099-01-01T00:00:00Z" active timeoutDecision="Approve" />);
  expect(screen.getByTestId('cli-permission-countdown')).toHaveTextContent(/Default: Approve/);
});
