export const TOKEN_KEY = 'coderank.accessToken';

export class SessionStorageError extends Error {
  constructor() { super('Session storage unavailable'); this.name = 'SessionStorageError'; }
}

// A lazy accessor also handles browsers that throw when reading window.sessionStorage itself.
export function createSessionStore(storageAccessor) {
  let loggedOut = false;
  return {
    read() {
      if (loggedOut) return null;
      try {
        const token = storageAccessor().getItem(TOKEN_KEY);
        if (typeof token === 'string' && token && token.length <= 16_384 && !/\s/.test(token)) return token;
        if (token !== null) this.clear();
      } catch { /* A blocked store behaves like an unauthenticated session. */ }
      return null;
    },
    save(token) {
      if (typeof token !== 'string' || !token || token.length > 16_384 || /\s/.test(token)) {
        throw new SessionStorageError();
      }
      try { storageAccessor().setItem(TOKEN_KEY, token); loggedOut = false; }
      catch { throw new SessionStorageError(); }
    },
    clear() {
      loggedOut = true;
      try { storageAccessor().removeItem(TOKEN_KEY); } catch { /* Do not retain an in-memory session. */ }
    },
  };
}
