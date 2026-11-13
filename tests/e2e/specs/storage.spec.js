const { test, expect, login, F } = require('./helpers');

// H6: сроки хранения по группам и legal hold — раздел «Хранилище», только admin.
test('хранилище: срок группы и legal hold', async ({ page }) => {
  await login(page);
  await page.goto('/#/settings/storage');

  const row = page.locator('tbody tr', { hasText: F.group });
  await row.locator('input[type=number]').fill('90');
  await row.locator('button', { hasText: 'Сохранить' }).click();
  await expect(page.locator(`input[aria-label="Срок хранения ${F.group}"]`)).toHaveValue('90');

  await page.locator('.field', { hasText: 'Хост' }).locator('input').fill('buh-01');
  await page.locator('.field', { hasText: 'Причина' }).locator('input').fill('служебная проверка № 7');
  await page.click('text=Установить');
  const hold = page.locator('tbody tr', { hasText: 'служебная проверка № 7' });
  await expect(hold).toContainText('действует');

  await page.goto('/#/hosts/buh-01/info');
  await expect(page.locator('.panel', { hasText: 'Legal hold' })).toContainText('служебная проверка № 7');

  await page.goto('/#/settings/storage');
  // Диалог причины подтверждает helpers.js (пустой ответ — причина необязательна).
  await hold.locator('button', { hasText: 'Снять' }).click();
  await expect(hold).toContainText('снят admin');
  await expect(hold).not.toContainText('действует');
});

test('хранилище: operator не видит сроков групп и legal hold', async ({ page }) => {
  await login(page, F.operator.username);
  await page.goto('/#/settings/storage');
  await expect(page.locator('.panel h3').first()).toHaveText('Сейчас');
  await expect(page.locator('.panel h3', { hasText: 'Legal hold' })).toHaveCount(0);
  await expect(page.locator('.panel h3', { hasText: 'Сроки по группам' })).toHaveCount(0);
});
