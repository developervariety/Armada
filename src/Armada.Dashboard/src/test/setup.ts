import '@testing-library/jest-dom/vitest';

// Some Node versions expose a global `localStorage` binding that is undefined
// unless launched with --localstorage-file, which shadows the jsdom-provided
// storage. Install a minimal in-memory polyfill when that happens so tests
// calling localStorage.clear/getItem/setItem behave.
if (typeof globalThis.localStorage === 'undefined') {
  const store = new Map<string, string>();

  const storage: Storage = {
    get length(): number {
      return store.size;
    },
    clear(): void {
      store.clear();
    },
    getItem(key: string): string | null {
      return store.has(key) ? store.get(key)! : null;
    },
    key(index: number): string | null {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key: string): void {
      store.delete(key);
    },
    setItem(key: string, value: string): void {
      store.set(key, String(value));
    },
  };

  (globalThis as { localStorage: Storage }).localStorage = storage;
}
