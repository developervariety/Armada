import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import Pagination from './Pagination';

vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({
    t: (text: string, vars?: Record<string, string>) =>
      text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => vars?.[key] ?? ''),
  }),
}));

function pager(pageNumber: number, totalPages: number, onPageChange = vi.fn()) {
  return (
    <Pagination
      pageNumber={pageNumber}
      pageSize={25}
      totalPages={totalPages}
      totalRecords={totalPages * 25}
      onPageChange={onPageChange}
      onPageSizeChange={vi.fn()}
    />
  );
}

describe('Pagination', () => {
  it('shows the new page number when the parent resets the page', () => {
    const { rerender } = render(pager(5, 5));
    expect(screen.getByRole('spinbutton')).toHaveValue(5);

    // A filter or page-size change resets the page to 1 outside the pager.
    rerender(pager(1, 2));

    expect(screen.getByRole('spinbutton')).toHaveValue(1);
  });

  it('shows a clamped page number after the list shrinks', () => {
    const { rerender } = render(pager(4, 4));
    rerender(pager(2, 2));
    expect(screen.getByRole('spinbutton')).toHaveValue(2);
  });

  it('keeps a page number the user is typing until it is submitted', () => {
    const onPageChange = vi.fn();
    render(pager(1, 9, onPageChange));
    const input = screen.getByRole('spinbutton');
    fireEvent.change(input, { target: { value: '7' } });
    expect(input).toHaveValue(7);
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onPageChange).toHaveBeenCalledWith(7);
  });
});
