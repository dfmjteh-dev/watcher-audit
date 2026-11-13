const { test, expect, login, F } = require('./helpers');

// Задача E4 (docs/roadmap-next.md): плеер консоли в браузере. Запись — из
// seed.js: настоящий формат script(1) (заголовок с COLUMNS/LINES, цвета,
// задержки из .timing). Хелпер валит тест на любом нарушении CSP — это и
// проверка правки xterm.js под CSP без 'unsafe-inline' (vendor/README.md).

const screen = page => page.locator('.term-screen .xterm-rows');

async function openOutput(page) {
  await login(page);
  await page.click('.topnav >> text=Консоль');
  await page.click('button:has-text("воспроизвести")');
  await expect(page.locator('.term-panel .xterm')).toBeVisible();
}

test('вывод проигрывается в браузере, ввод той же сессии — рядом', async ({ page }) => {
  await openOutput(page);
  // Размер терминала — из заголовка записи, а не дефолтные 80×24.
  await expect(page.locator('.term-panel .hint')).toContainText(`${F.terminalCols}×${F.terminalRows}`);
  // 0.14.4: вся ширина записи влезает в панель — шрифт подбирается, без прокрутки вбок.
  await expect.poll(() => page.locator('.term-screen').evaluate(el =>
    el.querySelector('.xterm-screen').offsetWidth <= el.clientWidth)).toBe(true);
  // Паузы сжимаются до 2 с — вся запись ~4 с.
  await expect(screen(page)).toContainText('deploy finished', { timeout: 10_000 });
  await expect(page.locator('.player-now')).toContainText('/ 00:03');
  // Ввод связан по host+user+времени и показан текстом (A1 — тот же HTML).
  await expect(page.locator('.term-input-full')).toHaveText(F.hostileInput);
  await expect(page.locator('.term-panel img')).toHaveCount(0);
  // Стили xterm дошли через constructable stylesheet, не <style>.
  expect(await page.evaluate(() => document.adoptedStyleSheets.length)).toBeGreaterThan(0);
  await expect(page.locator('.term-panel style')).toHaveCount(0);
});

test('перемотка, пауза, заголовок script(1) не попадает на экран', async ({ page }) => {
  await openOutput(page);
  await page.click('.term-panel button.play'); // пауза
  await page.locator('.term-seek').fill('0');
  await expect(screen(page)).not.toContainText('deploy');
  await page.locator('.term-seek').fill('1.5');
  await expect(screen(page)).toContainText('deploy --prod');
  await expect(screen(page)).not.toContainText('deploy finished');
  const end = await page.locator('.term-seek').getAttribute('max');
  await page.locator('.term-seek').fill(end);
  await expect(screen(page)).toContainText('deploy finished');
  await expect(screen(page)).not.toContainText('Script started');
  await expect(screen(page)).not.toContainText('Script done');
  // Назад — терминал переигрывается с начала.
  await page.locator('.term-seek').fill('0.5');
  await expect(screen(page)).not.toContainText('deploy --prod');
});

test('совпадения поиска: в выводе — моменты на шкале, во вводе — подсветка', async ({ page }) => {
  await login(page);
  await page.goto('/#/console');
  await page.fill('input[placeholder="например, rm -rf"]', 'finished');
  await page.press('input[placeholder="например, rm -rf"]', 'Enter');
  await expect(page.locator('tbody tr')).toHaveCount(1);
  await page.click('button:has-text("воспроизвести")');
  const chip = page.locator('.term-matches button');
  await expect(chip).toHaveText(['00:03']);
  await chip.click();
  await expect(screen(page)).toContainText('deploy finished');
  await expect(page.locator('.term-panel button.play')).toHaveText('▶'); // встали на паузу

  await page.fill('input[placeholder="например, rm -rf"]', 'ECHO');
  await page.press('input[placeholder="например, rm -rf"]', 'Enter');
  await expect(page.locator('tbody .term-input mark')).toHaveText('echo');
});

test('ссылка на сессию открывает её, адрес обновляется без перезагрузки', async ({ page }) => {
  await login(page);
  await page.goto('/#/console');
  await page.click('button:has-text("воспроизвести")');
  await expect(page).toHaveURL(/#\/console\?s=\d+$/);
  const url = page.url();
  await page.click('.term-panel button[title="закрыть"]');
  await expect(page.locator('.term-panel')).toHaveCount(0);
  await page.goto('/#/');
  await page.goto(url);
  await expect(page.locator('.term-panel .xterm')).toBeVisible();
  await expect(page.locator('.term-input-full')).toHaveText(F.hostileInput);
});

test('строка ввода тоже открывает панель с плеером вывода', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/buh-01/console');
  // У admin первая колонка — галочка выбора (0.13), открываем по «Началу».
  await page.click('tbody tr:has(.term-input) td[data-label="Начало"]');
  await expect(page.locator('.term-panel .xterm')).toBeVisible();
  await expect(screen(page)).toContainText('deploy finished', { timeout: 10_000 });
});

// Живой терминал: Enter в readline — CR, pam_tty_audit пишет ввод как есть.
// Команды в списке и в панели ввода — каждая на своей строке, не слитно.
test('ввод с CR (живой терминал) — команды по строкам', async ({ page, request }) => {
  const t = new Date(Date.now() - 20 * 60_000).toISOString();
  const r = await request.post('/api/upload-terminal', {
    multipart: { meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({
      kind: 'terminal-input', host: F.terminalHost, sessionUser: 'crlf-user', startedAt: t, endedAt: t,
      durationSec: 3, sessionId: 200, input: 'uptime\rdf -h /\r\nexit\r',
    })) } },
  });
  expect(r.ok()).toBeTruthy();
  await login(page);
  await page.goto('/#/console');
  await page.locator('.filters .field', { hasText: 'Пользователь' }).locator('input').fill('crlf-user');
  const cell = page.locator('tbody tr').first().locator('.term-input');
  await expect(cell).toBeVisible();
  expect(await cell.innerText()).toContain('uptime\ndf -h /\nexit');
  await page.locator('tbody tr').first().click();
  expect(await page.locator('.term-input-full').first().innerText()).toContain('uptime\ndf -h /\nexit');
});

// 0.13 (п.7): служебные подключения — «Скрыть похожие» из открытой сессии,
// по умолчанию такие сессии скрыты, показанные свёрнуты в «×N».
test('служебные подключения: скрыть похожие и свёртка ×N', async ({ page, request }) => {
  const now = Date.now();
  for (let i = 0; i < 3; i++) {
    const t = new Date(now - (10 - i) * 60_000).toISOString();
    const r = await request.post('/api/upload-terminal', {
      multipart: { meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({
        kind: 'terminal-input', host: F.terminalHost, sessionUser: 'lldp', remoteAddr: '10.20.30.40', sessionId: 100 + i,
        startedAt: t, endedAt: t, durationSec: 1, source: 'linux-exec', input: `${t.slice(11, 19)}Z /root$ lldpctl -f json\n`,
      })) } },
    });
    expect(r.ok()).toBeTruthy();
  }
  await login(page);
  const host = (await (await page.request.get('/api/inventory/' + F.terminalHost)).json()).host;
  const own = (await (await page.request.get(`/api/fleet/${host.id}/config`)).json()).config;
  try {
    await page.goto('/#/console');
    const userFilter = page.locator('.filters .field', { hasText: 'Пользователь' }).locator('input');
    await userFilter.fill('lldp');
    const rows = page.locator('tbody tr');
    await expect(rows).toHaveCount(3);
    await rows.first().click();
    await page.click('.term-panel button:has-text("Скрыть похожие")');
    const form = page.locator('.service-form');
    await expect(form.locator('.field', { hasText: 'Каждая команда' }).locator('input')).toHaveValue('lldpctl -f json');
    await form.locator('.field', { hasText: 'Где' }).locator('select').selectOption('host');
    await form.locator('button[type=submit]').click();
    await expect(page.locator('.term-panel .hint', { hasText: 'Правило добавлено хосту' })).toContainText('Пометка изменена у сессий: 3');
    await expect(page.locator('.term-panel .badge', { hasText: 'служебное' })).toBeVisible();

    await expect(page.locator('.empty', { hasText: 'Консольных сессий нет' })).toBeVisible();
    await page.locator('.filters .field', { hasText: 'Служебные' }).locator('select').selectOption('');
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText('×3');
    await rows.first().click();
    await expect(rows).toHaveCount(4);
  } finally {
    const headers = { 'X-Requested-With': 'XMLHttpRequest' };
    if (own) await page.request.put(`/api/fleet/${host.id}/config`, { headers, data: { config: own } });
    else await page.request.delete(`/api/fleet/${host.id}/config`, { headers });
  }
});

// 0.13 (проверка RC): admin удаляет выбранные записи хоста — с подтверждением,
// в журнал доступа.
test('удаление выбранной консольной сессии', async ({ page, request }) => {
  const t = new Date(Date.now() - 3 * 60_000).toISOString();
  const r = await request.post('/api/upload-terminal', {
    multipart: { meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({
      kind: 'terminal-input', host: 'kassa-01', sessionUser: 'deluser', sessionId: 900, startedAt: t, endedAt: t,
      durationSec: 1, input: 'echo delete-me\n',
    })) } },
  });
  expect(r.ok()).toBeTruthy();
  await login(page);
  await page.goto('/#/hosts/kassa-01/console');
  const row = page.locator('tbody tr', { hasText: 'delete-me' });
  await expect(row).toHaveCount(1);
  await row.locator('input[type=checkbox]').check();
  await page.click('.selbar button:has-text("Удалить выбранное")');   // confirm принимает helpers
  await expect(page.locator('tbody tr', { hasText: 'delete-me' })).toHaveCount(0);
  await page.goto('/#/settings/audit');
  await expect(page.locator('tbody')).toContainText('records.delete');
});
