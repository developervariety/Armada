import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import DataTable, { type DataTableColumn } from './DataTable';

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (value: string) => value }) }));

type Row = { title: string; status: string };
const columns: DataTableColumn<Row>[] = [
  { key: 'title', label: 'Title', required: true, render: row => row.title },
  { key: 'status', label: 'Status', render: row => row.status },
  { key: 'actions', label: 'Actions', fixed: true, render: () => <button>More</button> },
];
const props = {
  tableKey: 'voyages', columns, rows: [{ title: 'Voyage One', status: 'Open' }],
  rowKey: (row: Row) => row.title,
};

beforeEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

afterEach(() => vi.restoreAllMocks());

test('persists a visibility choice across unmount and remount', () => {
  const first = render(<DataTable {...props} />);
  fireEvent.click(screen.getByText('Columns'));
  expect(screen.getByLabelText('Title (Required)')).toBeDisabled();
  fireEvent.click(screen.getByLabelText('Status'));
  expect(screen.queryByText('Open')).not.toBeInTheDocument();
  first.unmount();

  render(<DataTable {...props} />);
  expect(screen.queryByText('Open')).not.toBeInTheDocument();
  expect(JSON.parse(localStorage.getItem('armada_columns_voyages') || '{}').hidden).toEqual(['status']);
});

test('ignores corrupt and stale choices and always shows required and fixed columns', () => {
  localStorage.setItem('armada_columns_voyages', JSON.stringify({ hidden: ['title', 'actions', 'removed'] }));
  const view = render(<DataTable {...props} />);
  expect(screen.getByText('Voyage One')).toBeInTheDocument();
  expect(screen.getByText('More')).toBeInTheDocument();
  fireEvent.click(screen.getByText('Columns'));
  expect(screen.getByLabelText('Title (Required)')).toBeDisabled();
  expect(screen.getByLabelText('Title (Required)')).toBeChecked();
  expect(screen.queryByLabelText(/Actions/)).not.toBeInTheDocument();
  view.unmount();

  localStorage.setItem('armada_columns_voyages', '{invalid json');
  render(<DataTable {...props} />);
  expect(screen.getByText('Open')).toBeInTheDocument();
});

test('keeps the current visibility choice when browser storage fails', () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage unavailable'); });
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('storage unavailable'); });
  render(<DataTable {...props} />);
  fireEvent.click(screen.getByText('Columns'));
  fireEvent.click(screen.getByLabelText('Status'));
  expect(screen.queryByText('Open')).not.toBeInTheDocument();
});
