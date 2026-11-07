import policy from '../../shared/validation/registration-policy.json' with { type: 'json' };

const commonHashes = new Set(policy.commonPasswordSha256);
const comparisonTrim = new Set(policy.comparisonTrimCodePoints);

function matchesCount(digits, position, first, step, padding) {
  let terms = 1;
  for (let next = first + step; position < digits.length && next >= 0; next += step) {
    const term = String(next).padStart(padding, '0');
    if (!digits.startsWith(term, position)) return false;
    position += term.length;
    terms++;
  }
  return position === digits.length && terms >= 3;
}

function isCountingSequence(digits) {
  // Match the Backend's bounded first-term width and whole-password check.
  for (let width = 1; width <= Math.min(9, digits.length - 2); width++) {
    const first = Number(digits.slice(0, width));
    const padding = digits[0] === '0' ? width : 0;
    if (matchesCount(digits, width, first, 1, padding) || matchesCount(digits, width, first, -1, padding)) return true;
  }
  return false;
}

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
    if (isCountingSequence(digits.join(''))) return false;
  }
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(comparison));
  const hash = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  return !commonHashes.has(hash);
}
