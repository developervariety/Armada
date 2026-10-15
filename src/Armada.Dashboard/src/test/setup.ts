import '@testing-library/jest-dom/vitest';
import { configurePageSizeStore } from '../lib/pageSizePrefs';

// The dashboard registers localStorage and the route as the page-size store in main.tsx, which tests do not run.
configurePageSizeStore({
  get: (key) => localStorage.getItem(key),
  set: (key, value) => localStorage.setItem(key, value),
  scope: () => window.location.pathname,
});
