import { fireEvent, render, screen } from '@testing-library/react';
import MissionModeSelect from './MissionModeSelect';

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

describe('MissionModeSelect', () => {
  it('offers the three server mission modes and marks audit and research as read-only', () => {
    render(<MissionModeSelect value="Implementation" onChange={() => undefined} />);

    const select = screen.getByLabelText('Mode') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(['Implementation', 'Audit', 'Research']);
    expect(screen.getByRole('option', { name: 'Audit (read-only)' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Research (read-only)' })).toBeInTheDocument();
    expect(select).toHaveValue('Implementation');
  });

  it('reports the selected mode', () => {
    const onChange = vi.fn();
    render(<MissionModeSelect value="Implementation" onChange={onChange} />);

    fireEvent.change(screen.getByLabelText('Mode'), { target: { value: 'Audit' } });

    expect(onChange).toHaveBeenCalledWith('Audit');
  });

  it('explains that read-only modes produce no landing work', () => {
    render(<MissionModeSelect value="Research" onChange={() => undefined} />);

    expect(screen.getByText('Read-only: the captain reports findings and nothing is landed.')).toBeInTheDocument();
  });
});
