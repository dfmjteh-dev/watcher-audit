const { test, expect, login } = require('./helpers');

// 0.12: тема и зона времени — в меню пользователя (клик по имени справа вверху).
async function pref(page, label) {
  await page.click('.usermenu > button');
  await page.click(`.usermenu .seg button:text-is("${label}")`);
  await page.keyboard.press('Escape');
}

// Задача E5 (docs/roadmap-next.md): тема, зона времени, узкий экран.

// 0.14.4: время — всегда в зоне сервера (/api/me timeZone; в e2e сервер в UTC),
// не браузера: смотрящий из другого пояса видит те же часы и сутки.
test.describe('зона времени', () => {
  test.use({ timezoneId: 'Asia/Yekaterinburg' });

  test('время на страницах — сервера, а не браузера; переключателя нет', async ({ page }) => {
    await login(page);
    await page.goto('/#/console');
    const firstTime = page.locator('tbody tr td').first();
    const iso = await page.evaluate(async () => (await (await fetch('/api/terminal-sessions?limit=1')).json())[0].startedAt);
    const zone = await page.evaluate(async () => (await (await fetch('/api/me')).json()).timeZone);
    expect(zone).toBe('UTC');
    const fmt = z => page.evaluate(([v, tz]) => new Date(v).toLocaleString('ru-RU', { hour12: false, timeZone: tz }), [iso, z]);
    await expect(firstTime).toHaveText(await fmt('UTC'));
    expect(await fmt('UTC')).not.toBe(await fmt('Asia/Yekaterinburg'));
    await page.click('.usermenu > button');
    await expect(page.locator('.usermenu .menu')).toContainText('Время сервера: UTC');
    await expect(page.locator('.usermenu .seg button:text-is("UTC")')).toHaveCount(0);
  });
});

test('«N мин назад» с абсолютным временем в подсказке', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts');
  const ago = page.locator('tbody .ago').first();
  await expect(ago).toContainText('назад');
  await expect(ago).toHaveAttribute('title', /\d{2}\.\d{2}\.\d{4}, \d{2}:\d{2}:\d{2}/);
});

test('тема: светлая/тёмная вручную, запоминается', async ({ page }) => {
  await login(page);
  const bg = () => page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  await pref(page, 'светлая');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  const light = await bg();
  await pref(page, 'тёмная');
  const dark = await bg();
  expect(light).not.toEqual(dark);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await pref(page, 'авто');
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/);
});

test.describe('узкий экран', () => {
  test.use({ viewport: { width: 390, height: 800 } });

  test('таблицы — карточками, без горизонтальной прокрутки', async ({ page }) => {
    await login(page);
    await page.goto('/#/hosts');
    await expect(page.locator('a.hostlink').first()).toBeVisible();
    await expect(page.locator('table.responsive thead')).toBeHidden();
    const cell = page.locator('tbody tr').first().locator('td[data-label="IP"]');
    expect(await cell.evaluate(td => getComputedStyle(td, '::before').content)).toBe('"IP"');
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  });
});

test('ошибка API — текстом, а не пустой таблицей', async ({ page }) => {
  await login(page);
  await page.route(url => url.pathname === '/api/inventory', route => route.fulfill({ status: 500, body: '' }));
  page.expectHttpError(500, '/api/inventory');
  await page.goto('/#/hosts');
  await expect(page.locator('.empty.error')).toHaveText('Ошибка сервера (500)');
});
