const { test, expect, login, F } = require('./helpers');

// 0.13 (п.1): отчёты — активность ПК, приложения, пользователи; CSV.
test('отчёты: активность, приложения, пользователи, CSV', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Отчёты');
  await expect(page.locator('.page-title')).toHaveText('Отчёты');
  // Лента активности: линия и площадь; стрелка клавиатуры показывает подсказку.
  const timeline = page.locator('.panel', { hasText: 'Активность за период' });
  await expect(timeline.locator('path.tl-line')).toHaveCount(1);
  await timeline.locator('svg').focus();
  await page.keyboard.press('ArrowLeft');
  await expect(timeline.locator('.tl-tip')).toBeVisible();
  await expect(timeline.locator('.tl-tip b')).not.toBeEmpty();
  const hosts = page.locator('.panel').filter({has:page.getByRole('heading',{name:'Хосты',exact:true})}).locator('tbody tr');
  await expect(hosts.first()).toBeVisible();
  await expect(page.locator('.panel').filter({has:page.getByRole('heading',{name:'Хосты',exact:true})})).toContainText('buh-01');

  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('.panel').filter({has:page.getByRole('heading',{name:'Хосты',exact:true})}).locator('button', { hasText: 'CSV' }).click()]);
  expect(download.suggestedFilename()).toMatch(/^watcher-activity-\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.csv$/);

  await page.click('.tabs >> text=Приложения');
  await expect(page.locator('.panel', { hasText: 'Самые используемые приложения' })).toContainText('WINWORD');
  await page.click('.tabs >> text=Пользователи');
  const users = page.locator('.panel', { hasText: 'Самые активные пользователи' });
  await expect(users).toContainText('ivan');
  await expect(users).toContainText('petr');

  // Фильтр по пользователю — остальные пропадают.
  await page.fill('input[list="rep-users"]', 'petr');
  await expect(users.locator('tbody tr')).toHaveCount(1);
});

test('отчёты: operator не видит раздел, API — 403', async ({ page }) => {
  await login(page, F.operator.username);
  await expect(page.locator('.topnav')).not.toContainText('Отчёты');
  page.expectHttpError(403, '/api/reports/activity');
  const status = await page.evaluate(async () => (await fetch('/api/reports/activity?from=2026-10-01&to=2026-10-14')).status);
  expect(status).toBe(403);
});

// 0.14.3: «Сайты и документы» по типу программы; тип меняется в «Настройки → Программы».
test('отчёты: сайты и документы, тип программы из настроек', async ({ page }) => {
  await login(page);
  await page.goto('/#/reports?t=subjects');
  const panel = page.locator('.panel', { hasText: 'Сайты и документы' });
  await expect(panel).toContainText('mail.example.com');          // домен от агента
  await expect(panel).toContainText('Договор поставки.docx');      // без « - Word»
  await expect(panel).not.toContainText(' - Word');

  await page.goto('/#/settings/apps');
  const row = page.getByRole('row').filter({has:page.getByRole('cell',{name:/^chrome(?:\s|$)/})});
  await expect(row.locator('select')).toHaveValue('browser');
  await expect(row.locator('option[value="browser"]')).toHaveText('Браузер (стандартно)');
  await row.locator('select').selectOption('app');
  await page.click('button:has-text("Сохранить")');
  await expect(page.locator('.hint.ok-text')).toContainText('Сохранено');
  await expect(row).toContainText('изменено');

  await page.goto('/#/reports?t=subjects');
  await expect(panel).toContainText('Договор поставки.docx');
  await expect(panel).not.toContainText('mail.example.com');      // chrome теперь «программа»

  // Вернуть как было — остальные тесты видят стандартные типы.
  await page.goto('/#/settings/apps');
  await page.getByRole('row').filter({has:page.getByRole('cell',{name:/^chrome(?:\s|$)/})}).locator('select').selectOption('browser');
  await page.click('button:has-text("Сохранить")');
  await expect(page.locator('.hint.ok-text')).toContainText('Сохранено');
});

test('настройки программ: только admin', async ({ page }) => {
  await login(page, F.operator.username);
  await page.goto('/#/settings');
  await expect(page.locator('.tabs')).not.toContainText('Программы');
  page.expectHttpError(403, '/api/settings/app-kinds');
  const status = await page.evaluate(async () => (await fetch('/api/settings/app-kinds')).status);
  expect(status).toBe(403);
});
