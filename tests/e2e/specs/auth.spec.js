const { test, expect, loginViaForm, F } = require('./helpers');

test('без входа — на страницу логина, неверный пароль — ошибка, верный — Обзор', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login\.html$/);

  page.expectHttpError(401, '/api/login');
  await page.fill('#username', 'admin');
  await page.fill('#password', 'wrong-password');
  await page.click('#submit');
  await expect(page.locator('#error')).toHaveText('Неверный логин или пароль');

  await loginViaForm(page);
  await expect(page.locator('.page-title')).toHaveText('Обзор');
});

test('выход возвращает на страницу логина, статика не открывается из кэша', async ({ page }) => {
  await loginViaForm(page);
  await page.click('.usermenu > button');
  await page.click('.usermenu >> text=Выйти');
  await expect(page).toHaveURL(/\/login\.html$/);
  // Без Cache-Control: no-cache «/» отдавался из кэша браузера мимо AuthGate.
  await page.goto('/#/hosts');
  await expect(page).toHaveURL(/\/login\.html#\/hosts$/);
});

test('глубокая ссылка без сессии — после входа туда же', async ({ page }) => {
  await page.goto('/#/hosts/buh-01/info');
  await expect(page).toHaveURL(/\/login\.html#\/hosts\/buh-01\/info$/);
  await page.fill('#username', 'admin');
  await page.fill('#password', F.adminPassword);
  await page.click('#submit');
  await expect(page).toHaveURL(/\/#\/hosts\/buh-01\/info$/);
  await expect(page.locator('.tabs a.current')).toHaveText('Инфо');
});

test('строгий CSP отдаётся сервером', async ({ page }) => {
  const resp = await page.goto('/login.html');
  const csp = resp.headers()['content-security-policy'];
  expect(csp).toContain("script-src 'self'");
  expect(csp).not.toContain('unsafe-inline');
});

// 0.13 (PR9): внизу страницы входа — версия сервера и подпись, без надписи о журнале.
test('страница входа: версия сервера и подпись', async ({ page }) => {
  await page.goto('/login.html');
  await expect(page.locator('#version')).toHaveText('Watcher ' + F.serverVersion.split('+')[0]);
  await expect(page.locator('.foot')).toContainText('Who. What. When. In detail.');
  await expect(page.locator('.foot')).not.toContainText('журнал');
});
