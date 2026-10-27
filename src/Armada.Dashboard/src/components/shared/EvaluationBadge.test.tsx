import { readFileSync } from 'node:fs';
import { render, screen } from '@testing-library/react';
import EvaluationBadge, { EVALUATION_BADGE_TITLE } from './EvaluationBadge';
import { translateTemplate } from '../../i18n/runtime';

const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
};

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => localeValue }));

describe('EvaluationBadge', () => {
  it('shows a small Evaluation tag with an explanatory title when the flag is set', () => {
    render(<EvaluationBadge isEvaluation />);
    const badge = screen.getByText('Evaluation');
    expect(badge).toHaveClass('tag', 'evaluation-badge');
    expect(badge).toHaveAttribute('title', EVALUATION_BADGE_TITLE);
  });

  it.each([false, null, undefined])('renders nothing when the flag is %s', (flag) => {
    const { container } = render(<EvaluationBadge isEvaluation={flag} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('has a translation of its title in every locale', () => {
    const catalog = JSON.parse(readFileSync('../Armada.Server/wwwroot/i18n/armada.json', 'utf8')) as { locales: Record<string, { phrases: Record<string, string> }> };
    for (const [code, locale] of Object.entries(catalog.locales)) {
      expect(locale.phrases[EVALUATION_BADGE_TITLE], code).toBeTruthy();
      expect(locale.phrases['Evaluation'], code).toBeTruthy();
    }
  });
});
