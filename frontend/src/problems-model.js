export const PROBLEM_FIELDS = Object.freeze(['title', 'statement', 'inputFormat', 'outputFormat', 'difficulty']);
export const DIFFICULTIES = Object.freeze({ Easy: 'Легка', Medium: 'Середня', Hard: 'Складна' });

export function validateProblem(data) {
  const values = {
    title: typeof data.title === 'string' ? data.title.trim() : '',
    statement: typeof data.statement === 'string' ? data.statement : '',
    inputFormat: typeof data.inputFormat === 'string' && data.inputFormat !== '' ? data.inputFormat : null,
    outputFormat: typeof data.outputFormat === 'string' && data.outputFormat !== '' ? data.outputFormat : null,
    difficulty: data.difficulty === '' || data.difficulty == null ? null : data.difficulty,
  };
  const errors = {};
  if (!values.title) errors.title = 'Введи назву задачі.';
  else if (values.title.length > 200) errors.title = 'Назва має містити не більше 200 символів.';
  if (!values.statement.trim()) errors.statement = 'Введи умову задачі.';
  for (const field of ['inputFormat', 'outputFormat']) {
    if (data[field] != null && typeof data[field] !== 'string') errors[field] = 'Введи текст або залиш поле порожнім.';
  }
  if (values.difficulty !== null && (typeof values.difficulty !== 'string' || !Object.hasOwn(DIFFICULTIES, values.difficulty))) {
    errors.difficulty = 'Обери складність зі списку або залиш її невизначеною.';
  }
  return { values, errors };
}

export function isProblemResponse(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    && Number.isSafeInteger(value.id) && value.id > 0
    && typeof value.title === 'string' && Boolean(value.title.trim()) && value.title.length <= 200
    && typeof value.statement === 'string' && Boolean(value.statement.trim())
    && ['inputFormat', 'outputFormat'].every((field) => value[field] === null || typeof value[field] === 'string')
    && (value.difficulty === null || (typeof value.difficulty === 'string' && Object.hasOwn(DIFFICULTIES, value.difficulty)))
    && typeof value.createdAtUtc === 'string' && Number.isFinite(Date.parse(value.createdAtUtc));
}
