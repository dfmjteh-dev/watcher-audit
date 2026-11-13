const { test, expect, login, navItems, F } = require('./helpers');

test('operator: парк и настройки, без записей', async ({ page }) => {
  await login(page, F.operator.username);
  expect(await navItems(page)).toEqual(['Обзор', 'Хосты', 'Настройки']);
  await page.goto('/#/hosts/buh-01');
  await expect(page.locator('.tabs a')).toHaveText(['Конфиг', 'Инфо']);
  await page.goto('/#/settings');
  await expect(page.locator('.tabs a')).toHaveText(['Группы и конфиги', 'Хранилище']);
});

test('viewer: только хосты своих групп, без управления', async ({ page }) => {
  await login(page, F.viewer.username);
  expect(await navItems(page)).toEqual(['Обзор', 'Хосты', 'Консоль', 'Отчёты']);
  await page.click('.topnav >> text=Хосты');
  await expect(page.locator('a.hostlink')).toHaveText(F.groupHosts);
  await expect(page.locator('tbody input[type=checkbox]')).toHaveCount(0);

  page.expectHttpError(403, '/api/inventory/buh-01');
  await page.goto('/#/hosts/buh-01/info');
  await expect(page.locator('.empty')).toContainText('не найден среди хостов ваших групп');
});

test('admin: массовое назначение группы', async ({ page }) => {
  await login(page);
  await page.goto('/#/settings/groups');
  await page.fill('input[placeholder="название"]', 'Бухгалтерия');
  await page.click('text=Добавить');
  await expect(page.locator('tbody tr')).toHaveCount(2);

  await page.click('.topnav >> text=Хосты');
  await page.fill('input[placeholder="имя, IP или пользователь"]', 'buh');
  await expect(page.locator('a.hostlink')).toHaveText(['buh-01']);
  await page.locator('thead input[type=checkbox]').check();
  await page.locator('.bulkbar select').selectOption({ label: 'Бухгалтерия' });
  await page.click('text=Назначить группу');
  await expect(page.locator('.bulkbar')).toHaveCount(0);
  // Колонки: флажок, статус, хост, пользователь, сотрудник, IP, группа.
  await expect(page.locator('tbody tr td:nth-child(7) select')).toHaveValue(/\d+/);
  await expect(page.locator('tbody tr td:nth-child(7) select option:checked')).toHaveText('Бухгалтерия');
});

// H7: право выгрузки — отдельно от роли. Admin выдаёт его в «Пользователях»,
// у viewer появляется вкладка «Выгрузка», zip скачивается обычной ссылкой.
test('выгрузка: право выдаёт admin, viewer скачивает zip', async ({ page, browser }) => {
  await login(page);
  await page.goto('/#/settings/users');
  const row = page.locator('tbody tr', { hasText: F.viewer.username });
  await row.locator('input[aria-label="Право выгрузки"]').check();
  await expect(row.locator('input[aria-label="Право выгрузки"]')).toBeChecked();

  const viewerPage = await browser.newPage();
  try {
    await login(viewerPage, F.viewer.username);
    await viewerPage.goto('/#/hosts/kassa-01');
    await expect(viewerPage.locator('.tabs a')).toHaveText(['Видео', 'Консоль', 'Инфо', 'Выгрузка']);
    await viewerPage.click('.tabs >> text=Выгрузка');
    // По умолчанию — сегодня; seed у полуночи кладёт записи во вчера.
    await viewerPage.locator('input[type=datetime-local]').first().fill('2000-01-01T00:00');
    await expect(viewerPage.locator('.panel dl.kv')).toContainText('1 сегм.');
    const [download] = await Promise.all([
      viewerPage.waitForEvent('download'),
      viewerPage.click('text=Скачать zip'),
    ]);
    expect(download.suggestedFilename()).toMatch(/^watcher-export-\d+-kassa-01-\d{8}\.zip$/);
  } finally {
    await viewerPage.close();
  }

  // Право снимается — вкладка пропадает (флаг читается из БД, без перелогина).
  await row.locator('input[aria-label="Право выгрузки"]').uncheck();
  await expect(row.locator('input[aria-label="Право выгрузки"]')).not.toBeChecked();
});
