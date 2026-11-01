export const DRAFT_KEYS = Object.freeze({
  login: 'coderank.draft.login',
  register: 'coderank.draft.register',
});
export const DRAFT_FIELDS = Object.freeze({
  login: Object.freeze(['email']),
  register: Object.freeze(['firstName', 'lastName', 'email']),
});
const limits = { email: 256, firstName: 100, lastName: 100 };

function selectFields(mode, values) {
  const result = {};
  if (!values || typeof values !== 'object' || Array.isArray(values)) return result;
  for (const field of DRAFT_FIELDS[mode]) {
    if (!Object.hasOwn(values, field)) continue;
    const value = values[field];
    if (typeof value === 'string' && value.length > 0 && value.length <= limits[field]) result[field] = value;
  }
  return result;
}

// Drafts are optional. Storage failure must not prevent entering data or using the API.
// Both reads and writes use an allowlist; never serialize a form or a request DTO here.
export function createDraftStore(storageAccessor) {
  const validMode = (mode) => Object.hasOwn(DRAFT_KEYS, mode);
  function clear(mode) {
    if (!validMode(mode)) return;
    try { storageAccessor().removeItem(DRAFT_KEYS[mode]); } catch { /* Best effort for blocked storage. */ }
  }
  function save(mode, values) {
    if (!validMode(mode)) return false;
    try {
      const fields = selectFields(mode, values);
      const storage = storageAccessor();
      if (Object.keys(fields).length) storage.setItem(DRAFT_KEYS[mode], JSON.stringify(fields));
      else storage.removeItem(DRAFT_KEYS[mode]);
      return true;
    } catch { return false; }
  }
  function read(mode) {
    if (!validMode(mode)) return {};
    try {
      const raw = storageAccessor().getItem(DRAFT_KEYS[mode]);
      if (raw === null) return {};
      if (raw.length > 4096) { clear(mode); return {}; }
      const fields = selectFields(mode, JSON.parse(raw));
      // Remove unknown fields or corrupted/obsolete data rather than round-tripping them.
      if (JSON.stringify(fields) !== raw || !Object.keys(fields).length) save(mode, fields);
      return fields;
    } catch { clear(mode); return {}; }
  }
  return { read, save, clear, clearAll() { clear('login'); clear('register'); } };
}
