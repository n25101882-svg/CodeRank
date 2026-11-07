import { DIFFICULTIES } from './problems-model.js';
import { errorMessage } from './validation.js';

function routeId(segment) {
  const id = Number(segment);
  return /^[1-9]\d*$/.test(segment) && Number.isSafeInteger(id) ? id : null;
}

export function matchOlympiadRoute(path) {
  const match = /^\/olympiads\/([^/]+)(?:\/problems\/([^/]+))?$/.exec(path);
  if (!match) return null;
  const [, olympiadId, problemId] = match;
  return { olympiadId: routeId(olympiadId), problemId: problemId === undefined ? undefined : routeId(problemId) };
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function problemMetadata(problem) {
  const difficulty = Object.hasOwn(DIFFICULTIES, problem.difficulty)
    ? DIFFICULTIES[problem.difficulty] : problem.difficulty || 'Не вказано';
  const meta = element('div', 'olympiad-problem-meta');
  meta.append(
    element('span', 'role-badge', `Складність: ${difficulty}`),
    element('span', 'role-badge', `Бали: ${problem.maxPoints}`),
  );
  return meta;
}

function problemList(olympiadId, problems) {
  const list = element('ul', 'olympiads-grid');
  list.setAttribute('aria-label', 'Задачі олімпіади');
  for (const problem of problems) {
    const item = element('li');
    const link = element('a', 'panel olympiad-card');
    link.href = `/olympiads/${olympiadId}/problems/${problem.id}`;
    link.dataset.route = '';
    link.setAttribute('aria-label', problem.title);
    link.append(element('h2', '', problem.title), problemMetadata(problem));
    item.append(link);
    list.append(item);
  }
  return list;
}

function exampleBlock(example, index) {
  const section = element('section', 'olympiad-problem-example');
  const values = element('dl', 'olympiad-example-values');
  for (const [label, text] of [['Введення', example.inputData], ['Виведення', example.expectedOutput]]) {
    const value = element('div');
    const data = element('dd');
    data.append(element('pre', '', text));
    value.append(element('dt', '', label), data);
    values.append(value);
  }
  section.append(element('h3', '', `Приклад ${index + 1}`), values);
  if (example.explanation) section.append(element('p', 'problem-statement', example.explanation));
  return section;
}

function problemContent(problem) {
  const article = element('article', 'panel olympiad-problem-detail');
  article.append(problemMetadata(problem));
  for (const [label, text] of [
    ['Умова', problem.statement],
    ['Формат введення', problem.inputFormat],
    ['Формат виведення', problem.outputFormat],
  ]) {
    const section = element('section');
    section.append(element('h2', '', label), element('p', 'problem-statement', text || 'Не зазначено'));
    article.append(section);
  }
  const examples = element('section');
  examples.append(element('h2', '', 'Приклади'));
  if (problem.examples.length === 0) examples.append(element('p', 'problem-statement', 'Поки немає прикладів.'));
  else problem.examples.forEach((example, index) => examples.append(exampleBlock(example, index)));
  article.append(examples);
  return article;
}

export function setupOlympiadProblems(root, api, { olympiadId, problemId }, signal) {
  const isDetail = problemId !== undefined;
  const back = root.querySelector('#olympiad-problems-back');
  if (isDetail && olympiadId !== null) {
    back.href = `/olympiads/${olympiadId}`;
    back.textContent = '← До задач олімпіади';
  }
  const title = root.querySelector('#olympiad-problems-title');
  const loading = root.querySelector('#olympiad-problems-loading');
  const empty = root.querySelector('#olympiad-problems-empty');
  const content = root.querySelector('#olympiad-problems-content');
  const errorPanel = root.querySelector('#olympiad-problems-error');
  const errorText = errorPanel.querySelector('p');
  const retry = root.querySelector('#retry-olympiad-problems');
  let busy = false;
  if (isDetail) {
    title.textContent = 'Умова задачі';
    loading.lastElementChild.textContent = 'Завантажуємо умову задачі…';
  }

  function unavailable() {
    errorText.textContent = isDetail && olympiadId !== null
      ? 'Задачу не знайдено або вона недоступна.'
      : 'Олімпіаду не знайдено або вона недоступна.';
    retry.hidden = true;
    errorPanel.hidden = false;
  }

  async function load() {
    if (busy || signal.aborted) return;
    busy = true;
    empty.hidden = content.hidden = errorPanel.hidden = true;
    loading.hidden = false;
    root.setAttribute('aria-busy', 'true');
    try {
      if (!api) throw new Error('Service unavailable');
      if (isDetail) {
        const problem = await api.getOlympiadProblem(olympiadId, problemId, { signal });
        if (signal.aborted) return;
        title.textContent = problem.title;
        content.replaceChildren(problemContent(problem));
        content.hidden = false;
      } else {
        const [olympiad, problems] = await Promise.all([
          api.getOlympiad(olympiadId, { signal }),
          api.listOlympiadProblems(olympiadId, { signal }),
        ]);
        if (signal.aborted) return;
        title.textContent = olympiad.title;
        if (problems.length === 0) empty.hidden = false;
        else {
          content.replaceChildren(problemList(olympiadId, problems));
          content.hidden = false;
        }
      }
    } catch (error) {
      if (signal.aborted) return;
      if (error.status === 404) unavailable();
      else {
        errorText.textContent = errorMessage(error, 'olympiads');
        errorPanel.hidden = false;
      }
    } finally {
      if (!signal.aborted) {
        busy = false;
        loading.hidden = true;
        root.removeAttribute('aria-busy');
      }
    }
  }

  if (olympiadId === null || problemId === null) {
    loading.hidden = true;
    unavailable();
    return;
  }
  retry.addEventListener('click', load);
  load();
}
