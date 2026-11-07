import { formatUtcDate } from './olympiads.js';
import { errorMessage } from './validation.js';
import { MAX_SOURCE_BYTES, readSubmissionFile, sourceSize, SUBMISSION_LANGUAGES, validateSubmission } from './submissions-model.js';

const byteFormatter = new Intl.NumberFormat('uk-UA');
const queuedLabel = 'Очікує перевірки';

function submissionRow(item) {
  const row = document.createElement('li');
  const number = document.createElement('strong');
  const language = document.createElement('span');
  const time = document.createElement('time');
  const status = document.createElement('span');
  number.textContent = '#' + item.id;
  language.textContent = SUBMISSION_LANGUAGES[item.language];
  time.dateTime = item.submittedAtUtc;
  time.textContent = formatUtcDate(item.submittedAtUtc);
  status.className = 'role-badge';
  status.textContent = queuedLabel;
  row.append(number, language, time, status);
  return row;
}

function submissionError(error) {
  if (error.status === 404) return 'Задача більше недоступна для надсилання. Онови сторінку.';
  if (error.status === 413) return 'Запит завеликий. Скороти вихідний код і повтори спробу.';
  if (error.problemCode === 'olympiad_not_started') return 'Олімпіада ще не почалася.';
  if (error.problemCode === 'olympiad_ended') return 'Олімпіада завершилася. Нові розв’язки вже не приймаються.';
  if (error.problemCode === 'submission_write_conflict') return 'Не вдалося зберегти розв’язок. Спробуй ще раз.';
  if (['network', 'timeout', 'response'].includes(error.code) || error.status >= 500) {
    return 'Не вдалося підтвердити надсилання. Онови історію перед повторною спробою.';
  }
  return errorMessage(error);
}

export function setupSubmissions(root, { api, olympiadId, problemId, sessionToken, signal, onSessionEnded }) {
  const guest = root.querySelector('#submission-guest');
  if (!sessionToken) {
    guest.hidden = false;
    return;
  }

  const form = root.querySelector('#submission-form');
  const fields = form.querySelector('fieldset');
  const language = form.elements.namedItem('language');
  const source = form.elements.namedItem('sourceCode');
  const file = form.querySelector('#submission-file');
  const fileError = form.querySelector('#submission-file-error');
  const size = form.querySelector('#submission-size');
  const working = form.querySelector('#submission-working');
  const notice = form.querySelector('#submission-notice');
  const history = root.querySelector('#submission-history');
  const loading = history.querySelector('#submission-history-loading');
  const empty = history.querySelector('#submission-history-empty');
  const errorPanel = history.querySelector('#submission-history-error');
  const rows = history.querySelector('.submission-history-list');
  const refresh = history.querySelector('#submission-refresh');
  const pagination = history.querySelector('#submission-pagination');
  const previous = history.querySelector('#submission-previous');
  const next = history.querySelector('#submission-next');
  let page = 1;
  let requestedPage = 1;
  let historyRequest = 0;
  let busy = false;
  form.hidden = history.hidden = false;

  function endSession(error) {
    if (error.status !== 401 && error.status !== 403) return false;
    sessionToken = null;
    form.hidden = history.hidden = true;
    form.reset();
    rows.replaceChildren();
    guest.querySelector('p').textContent = error.status === 401
      ? 'Сесія завершилася. Увійди, щоб продовжити.'
      : 'Надсилання доступне активним користувачам. Увійди з активного акаунта.';
    guest.hidden = false;
    onSessionEnded();
    return true;
  }

  async function loadHistory(targetPage) {
    if (signal.aborted || !sessionToken) return;
    const request = ++historyRequest;
    requestedPage = targetPage;
    empty.hidden = errorPanel.hidden = pagination.hidden = true;
    rows.replaceChildren();
    refresh.disabled = true;
    loading.hidden = false;
    const current = () => !signal.aborted && sessionToken && request === historyRequest;
    try {
      const result = await api.listSubmissions({ olympiadId, problemId, page: targetPage }, sessionToken, { signal });
      if (!current()) return;
      page = result.page;
      empty.hidden = result.items.length > 0;
      rows.replaceChildren(...result.items.map(submissionRow));
      pagination.hidden = page === 1 && !result.hasMore;
      history.querySelector('#submission-page').textContent = 'Сторінка ' + page;
      previous.disabled = page === 1;
      next.disabled = !result.hasMore;
    } catch (error) {
      if (!current() || endSession(error)) return;
      errorPanel.querySelector('p').textContent = errorMessage(error);
      errorPanel.hidden = false;
    } finally {
      if (current()) {
        refresh.disabled = false;
        loading.hidden = true;
      }
    }
  }

  function showFieldError(input, message) {
    form.querySelector('#' + input.id + '-error').textContent = message || '';
    if (message) input.setAttribute('aria-invalid', 'true');
    else input.removeAttribute('aria-invalid');
  }

  function updateSize() {
    const bytes = sourceSize(source.value);
    size.textContent = byteFormatter.format(bytes) + ' / ' + byteFormatter.format(MAX_SOURCE_BYTES) + ' байтів UTF-8';
    showFieldError(source, bytes > MAX_SOURCE_BYTES ? 'Код має займати не більше 64 КіБ у UTF-8.' : '');
  }

  function setBusy(message = '') {
    busy = Boolean(message);
    fields.disabled = busy;
    working.textContent = message;
    working.hidden = !busy;
  }

  function showNotice(message, success = false) {
    notice.querySelector('p').textContent = message;
    notice.classList.toggle('notice-success', success);
    notice.setAttribute('role', success ? 'status' : 'alert');
    notice.hidden = false;
  }

  source.addEventListener('input', updateSize);
  language.addEventListener('change', () => showFieldError(language));
  file.addEventListener('change', async () => {
    const selected = file.files[0];
    if (!selected || busy || !sessionToken) return;
    fileError.textContent = '';
    setBusy('Читаємо файл…');
    try {
      const code = await readSubmissionFile(selected);
      if (!signal.aborted && sessionToken) {
        source.value = code;
        updateSize();
      }
    } catch (error) {
      if (!signal.aborted) fileError.textContent = error.message;
    } finally {
      file.value = '';
      if (!signal.aborted) setBusy();
    }
  });

  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (busy || signal.aborted || !sessionToken) return;
    notice.hidden = true;
    const { body, errors } = validateSubmission({ language: language.value, sourceCode: source.value });
    showFieldError(language, errors.language);
    showFieldError(source, errors.sourceCode);
    if (errors.language || errors.sourceCode) {
      (errors.language ? language : source).focus();
      return;
    }

    setBusy('Надсилаємо розв’язок…');
    try {
      const receipt = await api.createSubmission(olympiadId, problemId, body, sessionToken, { signal });
      if (signal.aborted || !sessionToken) return;
      source.value = '';
      updateSize();
      showNotice('Розв’язок #' + receipt.id + ' збережено. ' + queuedLabel + '.', true);
      loadHistory(1);
    } catch (error) {
      if (!signal.aborted && sessionToken && !endSession(error)) showNotice(submissionError(error));
    } finally {
      if (!signal.aborted) setBusy();
    }
  });

  refresh.addEventListener('click', () => loadHistory(1));
  history.querySelector('#submission-retry').addEventListener('click', () => loadHistory(requestedPage));
  previous.addEventListener('click', () => loadHistory(page - 1));
  next.addEventListener('click', () => loadHistory(page + 1));
  loadHistory(1);
}
