import { useState } from 'react';
import { useLocale } from '../../context/LocaleContext';

interface PaginationProps {
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  totalRecords: number;
  totalMs?: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (size: number) => void;
}

const PAGE_SIZES = [10, 25, 50, 100, 250];

export default function Pagination({
  pageNumber,
  pageSize,
  totalPages,
  totalRecords,
  totalMs,
  onPageChange,
  onPageSizeChange,
}: PaginationProps) {
  const { t } = useLocale();
  const [pageInput, setPageInput] = useState(String(pageNumber));
  const [shownPage, setShownPage] = useState(pageNumber);

  // The page number is owned by the parent: a filter change, a page-size change or a
  // shrinking list moves it without the pager's buttons, so the box follows every change.
  if (shownPage !== pageNumber) {
    setShownPage(pageNumber);
    setPageInput(String(pageNumber));
  }

  const handlePageInputChange = (val: string) => {
    setPageInput(val);
  };

  const handlePageInputSubmit = () => {
    const num = parseInt(pageInput, 10);
    if (num >= 1 && num <= totalPages) {
      onPageChange(num);
    } else {
      setPageInput(String(pageNumber));
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') handlePageInputSubmit();
  };

  return (
    <div className="pagination-bar">
      <div className="pagination-info">
        <span className="pagination-records">
          {t(totalRecords === 1 ? '{{count}} record' : '{{count}} records', {
            count: totalRecords.toLocaleString(),
          })}
        </span>
        {totalMs !== undefined && (
          <span className="pagination-timing">{totalMs}ms</span>
        )}
      </div>
      <div className="pagination-controls">
        <button
          className="btn btn-sm"
          disabled={pageNumber <= 1}
          onClick={() => onPageChange(pageNumber - 1)}
        >
          {t('Prev')}
        </button>
        <div className="pagination-page">
          <input
            type="number"
            className="page-input"
            value={pageInput}
            onChange={e => handlePageInputChange(e.target.value)}
            onBlur={handlePageInputSubmit}
            onKeyDown={handleKeyDown}
            min={1}
            max={totalPages}
          />
          <span>{t('of {{totalPages}}', { totalPages: totalPages.toLocaleString() })}</span>
        </div>
        <button
          className="btn btn-sm"
          disabled={pageNumber >= totalPages}
          onClick={() => onPageChange(pageNumber + 1)}
        >
          {t('Next')}
        </button>
        <select
          className="page-size-select"
          value={pageSize}
          onChange={e => onPageSizeChange(parseInt(e.target.value, 10))}
        >
          {PAGE_SIZES.map(size => (
            <option key={size} value={size}>
              {t('{{size}} / page', { size: size.toLocaleString() })}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}
