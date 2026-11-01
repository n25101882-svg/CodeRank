export const API_ROOT = 'https://localhost:7160';
export const cors = {
  'access-control-allow-origin': `http://localhost:${process.env.CODERANK_TEST_PORT || 5173}`,
  'access-control-allow-methods': 'GET, POST, PUT, DELETE',
  'access-control-allow-headers': 'content-type, authorization',
};

export const respond = (route, json, status = 200) => status === 204
  ? route.fulfill({ status, headers: cors }) : route.fulfill({ status, json, headers: cors });

export async function mock(page, path, handler) {
  await page.route(`${API_ROOT}/api/v1/${path}`, (route) => route.request().method() === 'OPTIONS'
    ? route.fulfill({ status: 204, headers: cors }) : handler(route));
}

export async function isolateRequests(page, baseURL) {
  const local = new URL(baseURL).origin;
  await page.route('**/*', (route) => {
    const origin = new URL(route.request().url()).origin;
    if (origin === local) return route.continue();
    if (origin === API_ROOT) return route.request().method() === 'OPTIONS'
      ? route.fulfill({ status: 204, headers: cors }) : respond(route, {}, 503);
    return route.abort('blockedbyclient');
  });
}
