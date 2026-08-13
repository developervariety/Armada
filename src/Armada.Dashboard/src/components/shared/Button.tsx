import { forwardRef, useState, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from 'react';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** Force the busy state (for callers that already track their own async/submit state). */
  busy?: boolean;
  children?: ReactNode;
}

/**
 * Shared button with a built-in busy state. When its `onClick` returns a promise the button immediately
 * disables itself and shows an inline spinner until the promise settles, so an action that hits the server
 * never leaves the user staring at a dead-looking control. Callers that manage their own async state
 * (e.g. a form submit handled by `onSubmit`) can drive the same treatment with the explicit `busy` prop.
 * Drop-in for existing `<button className="btn ...">` usage -- pass the same className.
 */
const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { busy, children, className = 'btn', disabled, onClick, type, ...rest },
  ref,
) {
  const [pending, setPending] = useState(false);
  const effectiveBusy = Boolean(busy) || pending;

  async function handleClick(event: MouseEvent<HTMLButtonElement>) {
    if (!onClick) return;
    const result = onClick(event) as unknown;
    if (result && typeof (result as Promise<unknown>).then === 'function') {
      setPending(true);
      try {
        await result;
      } finally {
        setPending(false);
      }
    }
  }

  return (
    <button
      ref={ref}
      type={type ?? 'button'}
      className={`${className}${effectiveBusy ? ' is-busy' : ''}`}
      disabled={disabled || effectiveBusy}
      aria-busy={effectiveBusy || undefined}
      onClick={handleClick}
      {...rest}
    >
      {effectiveBusy && <span className="btn-spinner" aria-hidden="true" />}
      {children}
    </button>
  );
});

export default Button;
