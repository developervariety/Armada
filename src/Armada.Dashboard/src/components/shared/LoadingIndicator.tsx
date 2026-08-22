interface LoadingIndicatorProps {
  /** Text shown under the spinner. Pass a translated string; defaults to "Loading...". */
  label?: string;
  /** Fill a large vertical area (used for full-page / route loading). */
  fullHeight?: boolean;
}

/**
 * Centered spinner + label used as a visible "still working" affordance while a page chunk or its data
 * loads, so loading never reads as a blank screen.
 */
export default function LoadingIndicator({ label = 'Loading...', fullHeight = false }: LoadingIndicatorProps) {
  return (
    <div className={`loading-indicator${fullHeight ? ' loading-indicator-full' : ''}`} role="status" aria-live="polite">
      <span className="loading-indicator-spinner" aria-hidden="true" />
      <span className="loading-indicator-label">{label}</span>
    </div>
  );
}
