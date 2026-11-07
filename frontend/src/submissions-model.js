export const MAX_SOURCE_BYTES = 64 * 1024;
export const SUBMISSION_LANGUAGES = Object.freeze({ cpp17: 'C++17', python3: 'Python 3' });

const encoder = new TextEncoder();
export const sourceSize = (value) => encoder.encode(value).byteLength;
export const isSubmissionLanguage = (value) => typeof value === 'string' && Object.hasOwn(SUBMISSION_LANGUAGES, value);

export function validateSubmission({ language, sourceCode }) {
  const errors = {};
  if (!isSubmissionLanguage(language)) errors.language = 'Обери C++17 або Python 3.';
  if (typeof sourceCode !== 'string' || /^\p{White_Space}*$/u.test(sourceCode)) {
    errors.sourceCode = 'Введи вихідний код.';
  } else if (sourceSize(sourceCode) > MAX_SOURCE_BYTES) {
    errors.sourceCode = 'Код має займати не більше 64 КіБ у UTF-8.';
  }
  return { body: { language, sourceCode }, errors };
}

export async function readSubmissionFile(file) {
  if (!/\.(cpp|py|txt)$/i.test(file.name)) throw new Error('Обери текстовий файл .cpp, .py або .txt.');
  if (file.size > MAX_SOURCE_BYTES) throw new Error('Файл має займати не більше 64 КіБ.');
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer());
  } catch {
    throw new Error('Не вдалося прочитати файл. Збережи його в UTF-8 і спробуй ще раз.');
  }
}
