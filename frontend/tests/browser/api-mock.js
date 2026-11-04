export const apiRoot = 'https://localhost:7160';
const cors = {
  'access-control-allow-origin': `http://localhost:${process.env.CODERANK_TEST_PORT || 5173}`,
  'access-control-allow-methods': 'GET, POST',
  'access-control-allow-headers': 'content-type, authorization',
};

export const respond = (route, json, status = 200) => route.fulfill({ status, json, headers: cors });

export async function mock(page, path, handler) {
  await page.route(`${apiRoot}/api/v1/${path}`, (route) => route.request().method() === 'OPTIONS'
    ? route.fulfill({ status: 204, headers: cors }) : handler(route));
}

export async function isolateRequests(page, baseURL) {
  const frontendOrigin = new URL(baseURL).origin;
  await page.route('**/*', (route) => {
    const origin = new URL(route.request().url()).origin;
    if (origin === frontendOrigin) return route.continue(); // Only local Vite assets use the network.
    if (origin === apiRoot) {
      return route.request().method() === 'OPTIONS'
        ? route.fulfill({ status: 204, headers: cors })
        : respond(route, { title: 'Unconfigured test endpoint' }, 503);
    }
    return route.abort('blockedbyclient');
  });
}
