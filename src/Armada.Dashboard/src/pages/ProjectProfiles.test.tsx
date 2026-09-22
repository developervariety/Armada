import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import { PICKER_RECORD_COUNT, optionValues, pickerRecords, serverDefaultPage } from '../test/pickerRecords';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string) => text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: true, user: null }),
}));

vi.mock('../api/client', () => ({
  createProjectProfile: vi.fn(),
  deleteProjectProfile: vi.fn(),
  listProjectProfiles: vi.fn(),
  listFleets: vi.fn(),
  listVessels: vi.fn(),
  listAllFleets: vi.fn(),
  listAllVessels: vi.fn(),
  updateProjectProfile: vi.fn(),
}));

import { listAllFleets, listAllVessels, listFleets, listProjectProfiles, listVessels } from '../api/client';
import ProjectProfiles from './ProjectProfiles';

const fleets = pickerRecords('flt');
const vessels = pickerRecords('vsl');

beforeEach(() => {
  vi.mocked(listProjectProfiles).mockResolvedValue(serverDefaultPage([]) as never);
  vi.mocked(listFleets).mockResolvedValue(serverDefaultPage(fleets) as never);
  vi.mocked(listVessels).mockResolvedValue(serverDefaultPage(vessels) as never);
  vi.mocked(listAllFleets).mockResolvedValue(fleets as never);
  vi.mocked(listAllVessels).mockResolvedValue(vessels as never);
});

test('offers every fleet and vessel in the create form scope pickers, not only the first server page', async () => {
  render(<MemoryRouter><ProjectProfiles /></MemoryRouter>);

  fireEvent.click(await screen.findByText(/Project Profile$/, { selector: 'button' }));
  const scope = await screen.findByDisplayValue('Global');
  fireEvent.change(scope, { target: { value: 'Fleet' } });
  await waitFor(() => expect(optionValues(screen.getByDisplayValue('Select a fleet...'))).toHaveLength(PICKER_RECORD_COUNT));

  fireEvent.change(scope, { target: { value: 'Vessel' } });
  await waitFor(() => expect(optionValues(screen.getByDisplayValue('Select a vessel...'))).toHaveLength(PICKER_RECORD_COUNT));
});
