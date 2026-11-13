const { test, expect, login, F } = require('./helpers');
const { check } = require('../i18n-check.js');

// 0.14.4: язык интерфейса RU/EN (wwwroot/i18n.js). Перевод — словарь по русской
// строке; здесь — что у каждой строки t() есть перевод и в коде нет русского
// текста мимо t(), и что весь интерфейс на английском открывается без ошибок.

test('перевод: у каждой строки интерфейса есть английский вариант', () => {
  const { missing, stray } = check();
  expect(missing, 'строки t() без перевода в i18n.en.js').toEqual([]);
  expect(stray, 'русский текст в коде мимо t()').toEqual([]);
});

const PAGES = [
  ['/#/', 'Overview'],
  ['/#/hosts', 'Hosts'],
  ['/#/hosts/buh-01/video', 'Video'],
  ['/#/hosts/buh-01/info', 'Agent'],
  ['/#/hosts/buh-01/config', 'Screen'],
  ['/#/hosts/phone-01/device', 'Foreground apps'],
  ['/#/console', 'Console'],
  ['/#/reports', 'Reports'],
  ['/#/reports?t=subjects', 'Sites and documents'],
  ['/#/settings/groups', 'Groups and configs'],
  ['/#/settings/users', 'What each role can do'],
  ['/#/settings/ldap', 'Connection'],
  ['/#/settings/install', 'Kit'],
  ['/#/settings/agents', 'Enrollment tokens'],
  ['/#/settings/apps', 'Programs'],
  ['/#/settings/updates', 'How to update agents'],
  ['/#/settings/server', 'Server'],
  ['/#/settings/audit', 'Action'],
  ['/#/settings/storage', 'Retention'],
];

test('английский интерфейс: все разделы открываются, подписи на английском', async ({ page }) => {
  await login(page);
  await page.evaluate(() => localStorage.setItem('watcher.lang', 'en'));
  await page.reload();
  await page.locator('.topnav a').first().waitFor();
  for (const [url, text] of PAGES) {
    await page.goto(url);
    await expect(page.locator('main'), url).toContainText(text);
    await expect(page.locator('.empty.loading'), url).toHaveCount(0);
  }
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page.locator('.topnav')).toHaveText(/Overview.*Hosts.*Console.*Reports.*Settings/);
});

test('язык переключается в меню пользователя и на странице входа, выбор запоминается', async ({ page, browser }) => {
  await login(page);
  await page.click('.usermenu > button');
  await page.click('.usermenu .seg button:text-is("EN")');
  await expect(page.locator('.topnav a').first()).toHaveText('Overview');
  await page.reload();
  await expect(page.locator('.topnav a').first()).toHaveText('Overview');
  await page.click('.usermenu > button');
  await page.click('.usermenu .seg button:text-is("RU")');
  await expect(page.locator('.topnav a').first()).toHaveText('Обзор');

  const anon = await browser.newPage();
  try {
    await anon.goto('/login.html');
    await expect(anon.locator('#submit')).toHaveText('ВОЙТИ');
    await anon.click('.lang button[data-lang="en"]');
    await expect(anon.locator('#submit')).toHaveText('SIGN IN');
    await anon.fill('#username', 'admin');
    await anon.fill('#password', 'wrong-password-x');
    await anon.click('#submit');
    await expect(anon.locator('#error')).toHaveText('Wrong login or password');
  } finally { await anon.close(); }
});

// Сообщения с сервера переводятся на язык интерфейса (i18n.srv.js, ts()).
test('сообщения сервера — на языке интерфейса', async ({ page }) => {
  await login(page);
  const tsIn = (lang, msg) => page.evaluate(async ([l, m]) => {
    localStorage.setItem('watcher.lang', l);
    const mod = await import('/i18n.js?' + l + Math.random());
    return mod.ts(m);
  }, [lang, msg]);
  expect(await tsIn('en', 'обновление уже поставлено в очередь')).toBe('an update is already queued');
  expect(await tsIn('en', 'версия 0.1.0 старше текущей 0.2.0 — откат через веб не поддерживается (схема БД)'))
    .toBe('version 0.1.0 is older than the current 0.2.0 — rolling back via the web is not supported (DB schema)');
  expect(await tsIn('ru', 'crf: must be in 0..51')).toBe('crf: от 0 до 51');
  expect(await tsIn('ru', 'username already exists')).toBe('пользователь с таким именем уже есть');
  await page.evaluate(() => localStorage.removeItem('watcher.lang'));

  // Ошибка API с сообщением — в тексте ошибки, переведённом (настройки программ, неверное имя).
  await page.evaluate(() => localStorage.setItem('watcher.lang', 'en'));
  page.expectHttpError(400, '/api/settings/app-kinds');
  const r = await page.evaluate(async () => {
    const res = await fetch('/api/settings/app-kinds', { method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
      body: JSON.stringify({ overrides: { 'chrome': 'nonsense' } }) });
    const body = await res.json();
    const { ts } = await import('/i18n.js?err');
    return ts(body.error);
  });
  expect(r).toBe('unknown type “nonsense” for “chrome”');
  await page.evaluate(() => localStorage.removeItem('watcher.lang'));
});
