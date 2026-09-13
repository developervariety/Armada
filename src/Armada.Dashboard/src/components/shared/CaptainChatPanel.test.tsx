import { act, fireEvent, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CaptainChatPanel, { type ChatTurn } from './CaptainChatPanel';

interface Geometry {
  scrollHeight: number;
  clientHeight: number;
  scrollTop: number;
}

// jsdom does no layout, so the transcript window gets a controllable scroll geometry.
function stubGeometry(el: HTMLElement, geometry: Geometry) {
  Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => geometry.scrollHeight });
  Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => geometry.clientHeight });
  Object.defineProperty(el, 'scrollTop', {
    configurable: true,
    get: () => geometry.scrollTop,
    set: (value: number) => { geometry.scrollTop = value; },
  });
}

function panel(turns: ChatTurn[]) {
  return (
    <CaptainChatPanel
      t={(value) => value}
      turns={turns}
      input=""
      onInputChange={() => {}}
      onSend={() => {}}
      busy={false}
      canSend
    />
  );
}

const answer: ChatTurn = { id: 'a1', role: 'assistant', text: 'Checking the fleet.' };
const withTool: ChatTurn = { ...answer, tools: [{ id: 't1', name: 'list_vessels', status: 'running' }] };

function transcript(container: HTMLElement): HTMLElement {
  const el = container.querySelector('.ask-chat-window');
  if (!(el instanceof HTMLElement)) throw new Error('transcript window not rendered');
  return el;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('CaptainChatPanel autoscroll', () => {
  it('follows a tool card that grows the transcript without new reply text', async () => {
    const { container, rerender } = render(panel([answer]));
    const geometry: Geometry = { scrollHeight: 300, clientHeight: 200, scrollTop: 100 };
    stubGeometry(transcript(container), geometry);

    geometry.scrollHeight = 600;
    rerender(panel([withTool]));

    await waitFor(() => expect(geometry.scrollTop).toBe(600));
  });

  it('does not pull a reader back down after they scroll up', async () => {
    const { container, rerender } = render(panel([answer]));
    const el = transcript(container);
    const geometry: Geometry = { scrollHeight: 1000, clientHeight: 200, scrollTop: 100 };
    stubGeometry(el, geometry);
    fireEvent.scroll(el);

    geometry.scrollHeight = 1400;
    rerender(panel([withTool]));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });

    expect(geometry.scrollTop).toBe(100);
  });

  it('follows the conversation again when a new turn is added', async () => {
    const { container, rerender } = render(panel([answer]));
    const el = transcript(container);
    const geometry: Geometry = { scrollHeight: 1000, clientHeight: 200, scrollTop: 100 };
    stubGeometry(el, geometry);
    fireEvent.scroll(el);

    geometry.scrollHeight = 1800;
    rerender(panel([answer, { id: 'u2', role: 'user', text: 'And the voyages?' }]));

    await waitFor(() => expect(geometry.scrollTop).toBe(1800));
  });

  it('stops observing the transcript when it unmounts', () => {
    const disconnect = vi.spyOn(MutationObserver.prototype, 'disconnect');
    const { unmount } = render(panel([answer]));

    unmount();

    expect(disconnect).toHaveBeenCalled();
  });
});
