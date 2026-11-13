const { test, expect, login, F } = require('./helpers');

// Регрессия A1 (docs/roadmap-next.md): HTML из данных агента и из URL
// показывается текстом и не исполняется. Хелпер дополнительно проверяет
// window.__xss после каждого теста.
test('ввод терминала с HTML показывается текстом', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Консоль');
  // Строка именно этой сессии: в списке могут быть и сессии других спеков.
  const cell = page.locator('.term-input', { hasText: 'echo ok' });
  await expect(cell).toHaveText(F.hostileInput);
  await expect(page.locator('.term-input img')).toHaveCount(0);
});

test('HTML в адресе страницы хоста не исполняется', async ({ page }) => {
  await login(page);
  const hostile = '<img src=x onerror="window.__xss=1">';
  page.expectHttpError(404, '/api/inventory/');
  page.expectHttpError(404, '/api/segments?');
  await page.goto('/#/hosts/' + encodeURIComponent(hostile));
  await expect(page.locator('.page-title')).toHaveText(hostile);
  await expect(page.locator('.main img')).toHaveCount(0);
});

test('имя из неудачного входа в журнале доступа — текстом', async ({ page }) => {
  const hostileUser = '<img src=x onerror="window.__xss=1">';
  page.expectHttpError(401, '/api/login');
  await page.goto('/login.html');
  await page.fill('#username', hostileUser);
  await page.fill('#password', 'whatever-password');
  await page.click('#submit');
  await expect(page.locator('#error')).toBeVisible();

  await login(page);
  await page.goto('/#/settings/audit');
  await page.locator('.filters select').selectOption('login');
  await expect(page.locator('tbody td', { hasText: hostileUser }).first()).toBeVisible();
  await expect(page.locator('tbody img')).toHaveCount(0);
});
