import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import CodeIndex from './CodeIndex';

const api = vi.hoisted(() => ({
  listAllVessels: vi.fn(),
  getCodeIndexStatus: vi.fn(),
  updateCodeIndex: vi.fn(),
  searchCodeIndex: vi.fn(),
  searchCodeGraphSymbols: vi.fn(),
  getCodeGraphFiles: vi.fn(),
  exploreCodeGraph: vi.fn(),
}));

vi.mock('../api/client', () => api);
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ t: (value: string) => value }) }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));

const vessel = { id: 'vsl-1', name: 'Test vessel' };
const secondVessel = { id: 'vsl-2', name: 'Second vessel' };
const status = {
  vesselId: 'vsl-1', vesselName: 'Test vessel', defaultBranch: 'main', indexedCommitSha: 'abcdef012345',
  currentCommitSha: 'abcdef012345', indexedAtUtc: '2026-10-14T12:00:00Z', freshness: 'Fresh',
  documentCount: 12, chunkCount: 20, indexDirectory: '', lastError: null,
};
const availableSearch = {
  status, query: 'sample search', available: true, unavailableReason: null, message: null,
  results: [{ score: 5.2, record: { path: 'src/main.ts', language: 'typescript', startLine: 2, endLine: 4 }, excerpt: 'match' }],
};

describe('Code Index page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.listAllVessels.mockResolvedValue([vessel]);
    api.getCodeIndexStatus.mockResolvedValue(status);
    api.updateCodeIndex.mockResolvedValue(status);
    api.searchCodeIndex.mockResolvedValue(availableSearch);
    api.searchCodeGraphSymbols.mockResolvedValue({ results: [], warnings: [] });
    api.getCodeGraphFiles.mockResolvedValue({ files: [], warnings: [] });
    api.exploreCodeGraph.mockResolvedValue({ files: [], warnings: [] });
  });

  it('loads all vessels, updates the index, and runs a bounded lexical search', async () => {
    render(<MemoryRouter><CodeIndex /></MemoryRouter>);
    expect(await screen.findByText('Freshness')).toBeInTheDocument();
    expect(api.listAllVessels).toHaveBeenCalledOnce();
    await waitFor(() => expect(api.getCodeIndexStatus).toHaveBeenCalledWith('vsl-1'));

    fireEvent.click(screen.getByRole('button', { name: 'Update Index' }));
    await waitFor(() => expect(api.updateCodeIndex).toHaveBeenCalledWith('vsl-1'));

    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'sample search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByText('src/main.ts')).toBeInTheDocument();
    expect(api.searchCodeIndex).toHaveBeenCalledWith('vsl-1', {
      query: 'sample search', limit: 20, pathPrefix: null, language: null, includeContent: false,
    });
  });

  it('uses the complete vessel list when the list is larger than one server page', async () => {
    const allVessels = Array.from({ length: 1001 }, (_, index) => ({ id: `vsl-${index + 1}`, name: `Vessel ${index + 1}` }));
    api.listAllVessels.mockResolvedValue(allVessels);
    render(<MemoryRouter><CodeIndex /></MemoryRouter>);

    const selector = await screen.findByRole('combobox');
    fireEvent.change(selector, { target: { value: 'vsl-1001' } });
    await waitFor(() => expect(api.getCodeIndexStatus).toHaveBeenCalledWith('vsl-1001'));
    expect(screen.getByRole('option', { name: 'Vessel 1001' })).toBeInTheDocument();
  });

  it('shows the search unavailable message and reason instead of an empty result state', async () => {
    api.searchCodeIndex.mockResolvedValue({
      status, query: 'sample search', available: false, unavailableReason: 'index_not_ready',
      message: 'Search is unavailable until indexing completes.', results: [],
    });
    render(<MemoryRouter><CodeIndex /></MemoryRouter>);
    await screen.findByText('Freshness');
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'sample search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Search is unavailable until indexing completes. — index_not_ready');
    expect(screen.queryByText('No results')).not.toBeInTheDocument();
  });

  it('clears prior results and ignores a search response after the vessel changes', async () => {
    let resolvePendingSearch: ((value: typeof availableSearch) => void) | undefined;
    api.listAllVessels.mockResolvedValue([vessel, secondVessel]);
    api.searchCodeIndex
      .mockResolvedValueOnce(availableSearch)
      .mockImplementationOnce(() => new Promise((resolve) => { resolvePendingSearch = resolve; }));
    render(<MemoryRouter><CodeIndex /></MemoryRouter>);
    await screen.findByText('Freshness');

    const query = screen.getByLabelText('Query');
    fireEvent.change(query, { target: { value: 'first search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByText('src/main.ts')).toBeInTheDocument();

    fireEvent.change(query, { target: { value: 'pending search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => expect(api.searchCodeIndex).toHaveBeenCalledTimes(2));

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'vsl-2' } });
    await waitFor(() => expect(api.getCodeIndexStatus).toHaveBeenCalledWith('vsl-2'));
    expect(screen.queryByText('src/main.ts')).not.toBeInTheDocument();

    resolvePendingSearch?.({
      ...availableSearch,
      results: [{ ...availableSearch.results[0], record: { ...availableSearch.results[0].record, path: 'old-vessel/result.ts' } }],
    });
    await waitFor(() => expect(screen.queryByText('old-vessel/result.ts')).not.toBeInTheDocument());
    expect(screen.queryByText('No results')).toBeInTheDocument();
  });

  it('ignores an old status response after selecting another vessel', async () => {
    let resolveFirstStatus: ((value: typeof status) => void) | undefined;
    api.listAllVessels.mockResolvedValue([vessel, secondVessel]);
    api.getCodeIndexStatus.mockImplementation((id: string) => {
      if (id === 'vsl-1') return new Promise((resolve) => { resolveFirstStatus = resolve; });
      return Promise.resolve({ ...status, vesselId: 'vsl-2', vesselName: 'Second vessel', currentCommitSha: '222222222222' });
    });
    render(<MemoryRouter><CodeIndex /></MemoryRouter>);
    await waitFor(() => expect(api.getCodeIndexStatus).toHaveBeenCalledWith('vsl-1'));

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'vsl-2' } });
    await waitFor(() => expect(screen.getByText(/2222222222/)).toBeInTheDocument());
    resolveFirstStatus?.({ ...status, currentCommitSha: '111111111111' });

    await waitFor(() => expect(screen.getByText(/2222222222/)).toBeInTheDocument());
    expect(screen.queryByText(/1111111111/)).not.toBeInTheDocument();
  });
});
