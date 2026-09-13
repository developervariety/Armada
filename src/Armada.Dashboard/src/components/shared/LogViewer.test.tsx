import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import LogViewer from './LogViewer';
import { copyToClipboard } from './CopyButton';

vi.mock('./CopyButton', () => ({
  default: () => null,
  copyToClipboard: vi.fn(() => Promise.resolve()),
}));

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string) => text };
  return { useLocale: () => locale };
});

const source = '## Plan\n\n- first step\n- second step\n\n<script>window.injected = true</script><img src="x" onerror="window.injected = true">';

function renderViewer(markdown?: boolean) {
  return render(
    <LogViewer open title="Instructions" content={source} completed markdown={markdown} onClose={() => undefined} />,
  );
}

describe('LogViewer', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders markdown content as formatted elements when markdown is set', () => {
    renderViewer(true);

    expect(screen.getByRole('heading', { name: 'Plan' })).toBeInTheDocument();
    expect(screen.getByRole('list')).toBeInTheDocument();
  });

  it('keeps plain text when markdown is not set', () => {
    renderViewer(false);

    expect(screen.queryByRole('heading', { name: 'Plan' })).not.toBeInTheDocument();
    expect(document.getElementById('log-viewer-content')?.textContent).toContain('## Plan');
  });

  it('never turns HTML in the content into live elements', () => {
    const { container } = renderViewer(true);

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect((window as unknown as { injected?: boolean }).injected).toBeUndefined();
  });

  it('copies the raw source, not the rendered text', async () => {
    renderViewer(true);

    fireEvent.click(screen.getByRole('button', { name: 'Copy' }));

    await waitFor(() => expect(copyToClipboard).toHaveBeenCalledWith(source));
  });
});
