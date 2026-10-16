import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import WorkspaceDiff from './WorkspaceDiff';
import { getWorkspaceDiff } from '../../api/client';
import { translateTemplate } from '../../i18n/runtime';

vi.mock('../../api/client', () => ({ getWorkspaceDiff: vi.fn() }));
vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string, params?: Record<string, string | number>) => translateTemplate('en', text, null, params) };
  return { useLocale: () => locale };
});

const DIFF = 'diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\ndiff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-x\n+y\n';

beforeEach(() => {
  localStorage.clear();
  vi.mocked(getWorkspaceDiff).mockReset();
});

describe('WorkspaceDiff', () => {
  it('is one collapsed line until loaded', () => {
    render(<WorkspaceDiff vesselId="vsl_1" />);
    expect(screen.getByRole('button', { name: 'Review Diff' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByText('Not loaded')).toBeInTheDocument();
  });

  it('says there are no changes on its header line and stays collapsed', async () => {
    vi.mocked(getWorkspaceDiff).mockResolvedValue({ diff: '', error: null } as never);
    render(<WorkspaceDiff vesselId="vsl_1" />);
    fireEvent.click(screen.getByRole('button', { name: 'Load diff' }));
    expect(await screen.findByText('No tracked changes against HEAD.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Diff' })).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens to show a loaded diff and collapses from its header', async () => {
    vi.mocked(getWorkspaceDiff).mockResolvedValue({ diff: DIFF, error: null } as never);
    render(<WorkspaceDiff vesselId="vsl_1" />);
    fireEvent.click(screen.getByRole('button', { name: 'Load diff' }));
    expect(await screen.findByText('2 file(s) changed')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Review Diff' });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('+new')).toBeInTheDocument();
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('+new')).not.toBeInTheDocument();
    expect(screen.getByText('2 file(s) changed')).toBeInTheDocument();
  });
});
