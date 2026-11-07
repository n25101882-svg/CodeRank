import { isAcceptableRegistrationPassword } from './password-policy.js';

function validate(data, registration) {
  const values = {
    email: typeof data.email === 'string' ? data.email.trim() : '',
    password: typeof data.password === 'string' ? data.password : '',
  };
  const errors = {};
  if (!values.email) errors.email = 'Введи свій email.';
  else if (values.email.length > 256 || !/^[^\s@]+@[^\s@]+$/u.test(values.email)) {
    errors.email = 'Введи коректний email, до 256 символів.';
  }
  if (!values.password.trim()) errors.password = 'Введи пароль.';
  else if (values.password.length > 128) errors.password = 'Пароль має містити не більше 128 символів.';
  else if (registration && values.password.length < 12) errors.password = 'Пароль має містити щонайменше 12 символів.';
  if (registration) {
    for (const field of ['firstName', 'lastName']) {
      values[field] = typeof data[field] === 'string' ? data[field].trim() : '';
      if (!values[field]) errors[field] = field === 'firstName' ? 'Введи своє ім’я.' : 'Введи своє прізвище.';
      else if (values[field].length > 100) errors[field] = 'Використай не більше 100 символів.';
      else if (!/\p{L}/u.test(values[field].replace(/\u02bc/gu, ''))) errors[field] = 'Використай принаймні одну літеру. Можна вводити Unicode-літери, апострофи, дефіси та пробіли.';
    }
  }
  return { values, errors };
}

export const validateLogin = (data) => validate(data, false);
export async function validateRegistration(data) {
  const result = validate(data, true);
  if (!result.errors.password && !await isAcceptableRegistrationPassword(result.values.password)) {
    result.errors.password = 'Пароль занадто передбачуваний. Оберіть інший пароль або довшу фразу.';
  }
  return result;
}

export function errorMessage(error, mode) {
  if (error.name === 'SessionStorageError') return 'Браузер не дозволяє зберегти сесію. Дозволь зберігання даних для цього сайту й спробуй ще раз.';
  if (error.code === 'timeout') return 'CodeRank відповідає надто довго. Спробуй ще раз.';
  if (error.code === 'network') return 'Не вдалося під’єднатися до CodeRank. Перевір з’єднання та спробуй ще раз.';
  if (error.status === 401 && mode === 'login') return 'Неправильний email або пароль.';
  if (error.status === 409 && mode === 'register') return 'Цей email уже зареєстровано. Увійди у свій акаунт.';
  if (error.status === 400) return 'Перевір введені дані та спробуй ще раз.';
  if (error.status === 429) return 'Забагато спроб. Зачекай трохи й повтори запит.';
  return 'Зараз не вдалося виконати запит. Спробуй ще раз трохи пізніше.';
}
