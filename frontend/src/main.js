import './styles.css';
import { createApiClient } from './api.js';
import { createSessionStore } from './session.js';
import { createDraftStore, DRAFT_FIELDS } from './drafts.js';
import { errorMessage, validateLogin, validateRegistration } from './validation.js';
import { setupProblems } from './problems.js';

const main = document.querySelector('#main');
const session = createSessionStore(() => window.sessionStorage);
const drafts = createDraftStore(() => window.sessionStorage);
let routeController = new AbortController();
let pendingNotice = null;
let api;
try {
  api = createApiClient({ baseUrl: import.meta.env.VITE_API_BASE_URL || 'https://localhost:7160' });
} catch { /* Render a safe configuration error below, without exposing configuration values. */ }

function clone(id) { return document.getElementById(id).content.cloneNode(true); }

function navigate(path, { replace = false, notice = null } = {}) {
  pendingNotice = notice;
  window.history[replace ? 'replaceState' : 'pushState'](null, '', path);
  renderRoute();
}

function showNotice(container, text, success = false) {
  container.hidden = false;
  container.classList.toggle('notice-success', success);
  container.setAttribute('role', success ? 'status' : 'alert');
  container.setAttribute('tabindex', '-1');
  container.querySelector('p').textContent = text;
  container.querySelector('.notice-symbol').textContent = success ? '✓' : '!';
}

function setFieldErrors(form, errors) {
  for (const input of form.querySelectorAll('input[name]')) {
    const message = form.querySelector(`#${input.name}-error`);
    message.textContent = errors[input.name] || '';
    if (errors[input.name]) input.setAttribute('aria-invalid', 'true');
    else input.removeAttribute('aria-invalid');
  }
}

function setupForm(mode, signal, notice) {
  const form = main.querySelector('#auth-form');
  const banner = main.querySelector('#form-notice');
  const fieldset = form.querySelector('fieldset');
  const button = form.querySelector('[type=submit]');
  const label = button.querySelector('.submit-label');
  const originalLabel = label.textContent;
  const restored = drafts.read(mode);
  for (const field of DRAFT_FIELDS[mode]) form.elements.namedItem(field).value = restored[field] || '';
  function saveDraft(event) {
    if (!DRAFT_FIELDS[mode].includes(event.target.name)) return;
    const values = {};
    for (const field of DRAFT_FIELDS[mode]) values[field] = form.elements.namedItem(field).value;
    drafts.save(mode, values);
  }
  form.addEventListener('change', saveDraft);
  let busy = false;
  if (notice) showNotice(banner, notice.text, notice.success);
  if (!api) {
    showNotice(banner, 'CodeRank тимчасово недоступний. Спробуй зайти пізніше.');
    fieldset.disabled = true;
    return;
  }
  form.querySelector('.password-toggle').addEventListener('click', (event) => {
    const input = form.elements.namedItem('password');
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    event.currentTarget.setAttribute('aria-pressed', String(show));
    event.currentTarget.setAttribute('aria-label', show ? 'Приховати пароль' : 'Показати пароль');
  });
  form.addEventListener('input', (event) => {
    saveDraft(event);
    const input = event.target;
    if (!input.name) return;
    input.removeAttribute('aria-invalid');
    form.querySelector(`#${input.name}-error`).textContent = '';
    if (!busy) banner.hidden = true;
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (busy) return;
    busy = true;
    banner.hidden = true;
    const raw = Object.fromEntries(new FormData(form));
    let validated;
    try { validated = await (mode === 'register' ? validateRegistration : validateLogin)(raw); }
    catch {
      busy = false;
      if (!signal.aborted) showNotice(banner, 'Не вдалося перевірити дані. Онови сторінку та спробуй ще раз.');
      return;
    }
    if (signal.aborted) return;
    const { values, errors } = validated;
    setFieldErrors(form, errors);
    if (Object.keys(errors).length) {
      busy = false;
      form.querySelector('[aria-invalid=true]').focus();
      return;
    }
    fieldset.disabled = true;
    form.setAttribute('aria-busy', 'true');
    button.classList.add('is-loading');
    label.textContent = mode === 'register' ? 'Створюємо акаунт…' : 'Перевіряємо…';
    try {
      if (mode === 'register') {
        await api.register(values, { signal });
        if (signal.aborted) return;
        session.clear();
        drafts.clear('register');
        form.elements.namedItem('password').value = '';
        navigate('/login', { replace: true, notice: { text: 'Акаунт створено. Увійди за допомогою свого email і пароля.', success: true } });
      } else {
        const result = await api.login(values, { signal });
        if (signal.aborted) return;
        session.save(result.accessToken);
        drafts.clearAll();
        form.elements.namedItem('password').value = '';
        navigate('/dashboard', { replace: true });
      }
    } catch (error) {
      if (signal.aborted) return;
      if (error.status === 401) session.clear();
      const serverErrors = {};
      for (const field of error.fields || []) {
        const name = ['email', 'password', 'firstName', 'lastName'].find((known) => known.toLowerCase() === field.replace(/^\$\./, '').toLowerCase());
        if (name && form.elements.namedItem(name)) serverErrors[name] = 'Перевір це поле та спробуй ще раз.';
      }
      setFieldErrors(form, serverErrors);
      showNotice(banner, errorMessage(error, mode));
      banner.focus();
    } finally {
      if (!signal.aborted) {
        busy = false;
        fieldset.disabled = false;
        form.removeAttribute('aria-busy');
        button.classList.remove('is-loading');
        label.textContent = originalLabel;
      }
    }
  });
}

function setupDashboard(signal) {
  const content = main.querySelector('#profile-content');
  const loading = main.querySelector('#profile-loading');
  const errorPanel = main.querySelector('#dashboard-error');
  let busy = false;
  async function loadProfile() {
    if (busy || signal.aborted) return;
    const token = session.read();
    if (!token) { navigate('/login', { replace: true }); return; }
    busy = true;
    content.hidden = true;
    errorPanel.hidden = true;
    loading.hidden = false;
    main.setAttribute('aria-busy', 'true');
    try {
      if (!api) throw new Error('Service unavailable');
      const user = await api.me(token, { signal });
      if (signal.aborted) return;
      for (const field of ['email', 'firstName', 'lastName', 'role']) {
        main.querySelector(`#profile-${field}`).textContent = user[field] || '—';
      }
      main.querySelector('#profile-welcome').textContent = user.firstName ? `Привіт, ${user.firstName}!` : 'Привіт!';
      main.querySelector('#profile-avatar').textContent = [user.firstName, user.lastName]
        .filter(Boolean).map((name) => [...name.trim()][0] || '').join('').toLocaleUpperCase('uk-UA') || '{ }';
      content.hidden = false;
    } catch (error) {
      if (signal.aborted) return;
      if (error.status === 401) {
        session.clear();
        navigate('/login', { replace: true, notice: { text: 'Сесія завершилася. Увійди ще раз, щоб продовжити.', success: false } });
        return;
      }
      errorPanel.hidden = false;
      errorPanel.querySelector('p').textContent = errorMessage(error, 'dashboard');
    } finally {
      if (!signal.aborted) {
        busy = false;
        loading.hidden = true;
        main.removeAttribute('aria-busy');
      }
    }
  }
  main.querySelector('#retry-profile').addEventListener('click', loadProfile);
  main.querySelector('#refresh-profile').addEventListener('click', loadProfile);
  loadProfile();
}

function renderRoute() {
  routeController.abort();
  routeController = new AbortController();
  const { signal } = routeController;
  main.replaceChildren();
  main.removeAttribute('aria-busy');
  const path = window.location.pathname.replace(/\/+$/, '') || '/';
  if (!['/login', '/register', '/dashboard', '/problems'].includes(path)) {
    navigate(session.read() ? '/dashboard' : '/login', { replace: true });
    return;
  }
  if (['/dashboard', '/problems'].includes(path) && !session.read()) {
    session.clear();
    navigate('/login', { replace: true });
    return;
  }
  const dashboard = path === '/dashboard';
  const problems = path === '/problems';
  document.querySelector('#auth-nav').hidden = dashboard || problems;
  document.querySelector('#session-nav').hidden = !(dashboard || problems);
  document.querySelector('#workspace-nav').hidden = !(dashboard || problems);
  for (const link of document.querySelectorAll('.site-header [data-route]')) {
    if (link.getAttribute('href') === path) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  document.title = `${problems ? 'Мої задачі' : dashboard ? 'Мій профіль' : path === '/login' ? 'Вхід' : 'Реєстрація'} — CodeRank`;
  const notice = pendingNotice;
  pendingNotice = null;
  if (problems) {
    main.append(clone('problems-view'));
    setupProblems(main.querySelector('.problems-page'), { api, session, signal, onUnauthorized: () => {
      session.clear();
      navigate('/login', { replace: true, notice: { text: 'Сесія завершилася. Увійди ще раз, щоб продовжити.', success: false } });
    } });
  } else if (dashboard) {
    main.append(clone('dashboard-view'));
    setupDashboard(signal);
  } else {
    main.append(clone('auth-shell'));
    main.querySelector('#auth-content').append(clone(path === '/register' ? 'register-view' : 'login-view'));
    setupForm(path.slice(1), signal, notice);
  }
  main.focus({ preventScroll: true });
  window.scrollTo(0, 0);
}

document.addEventListener('click', (event) => {
  const link = event.target.closest('a[data-route]');
  if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  navigate(link.getAttribute('href'));
});
document.querySelector('#logout').addEventListener('click', () => {
  session.clear();
  drafts.clearAll();
  navigate('/login', { replace: true, notice: { text: 'Ти вийшов із сесії у цій вкладці.', success: true } });
});
window.addEventListener('popstate', renderRoute);
window.addEventListener('pageshow', (event) => { if (event.persisted) renderRoute(); });
window.addEventListener('storage', (event) => { if (event.storageArea === window.sessionStorage) renderRoute(); });
renderRoute();
