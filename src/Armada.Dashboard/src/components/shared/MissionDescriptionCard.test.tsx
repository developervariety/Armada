import { render, screen } from '@testing-library/react';
import MissionDescriptionCard from './MissionDescriptionCard';

const copyButtonTexts: string[] = [];

vi.mock('./CopyButton', () => ({
  default: ({ text, title }: { text: string; title?: string }) => {
    copyButtonTexts.push(text);
    return <button type="button" aria-label={title}>copy</button>;
  },
  copyToClipboard: vi.fn(() => Promise.resolve()),
}));

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

const description = '# Goal\n\nFix the **landing** gate.\n\n```bash\ndotnet build\n```';

describe('MissionDescriptionCard', () => {
  beforeEach(() => {
    copyButtonTexts.length = 0;
  });

  it('renders the mission description as markdown', () => {
    render(<MissionDescriptionCard description={description} />);

    expect(screen.getByRole('heading', { name: 'Description' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Goal' })).toBeInTheDocument();
    expect(screen.getByText('landing').tagName).toBe('STRONG');
    expect(screen.getByText('dotnet build').closest('pre')).not.toBeNull();
  });

  it('offers a copy of the raw markdown source', () => {
    render(<MissionDescriptionCard description={description} />);

    expect(screen.getByRole('button', { name: 'Copy raw markdown' })).toBeInTheDocument();
    expect(copyButtonTexts).toContain(description);
  });
});
