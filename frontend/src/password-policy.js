import policy from '../../shared/validation/registration-policy.json' with { type: 'json' };

const commonHashes = new Set(policy.commonPasswordSha256);
const comparisonTrim = new Set(policy.comparisonTrimCodePoints);

export async function isAcceptableRegistrationPassword(password) {
  if (password.length < 12 || password.length > 128 || !password.isWellFormed()) return false;
  // Comparison only: never replace, trim or normalize the submitted password.
  const normalized = [...password.normalize('NFKC')];
  while (normalized.length && comparisonTrim.has(normalized[0].codePointAt(0))) normalized.shift();
  while (normalized.length && comparisonTrim.has(normalized.at(-1).codePointAt(0))) normalized.pop();
  const comparison = normalized.join('').replace(/[A-Z]/g, (character) => character.toLowerCase());
  const characters = [...comparison];
  if (!characters.length) return false;
  for (let period = 1; period <= Math.min(8, Math.floor(characters.length / 2)); period++) {
    if (characters.length % period === 0 && characters.every((char, i) => char === characters[i % period])) return false;
  }
  const digits = characters.map((char) => {
    const code = char.codePointAt(0);
    const start = policy.decimalDigitStarts.find((zero) => code >= zero && code <= zero + 9);
    return start === undefined ? -1 : code - start;
  });
  if (digits.length >= 3 && digits.every((digit) => digit >= 0)) {
    const step = (digits[1] - digits[0] + 10) % 10;
    if (digits.slice(1).every((digit, i) => (digit - digits[i] + 10) % 10 === step)) return false;
  }
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(comparison));
  const hash = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  return !commonHashes.has(hash);
}
