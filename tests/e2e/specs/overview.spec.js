const { test, expect, login } = require('./helpers');

// 0.13 (PR9): загрузка сервера на «Обзоре» и автообновление страницы.
test('обзор: панель «Сервер» и автообновление', async ({ page }) => {
  await page.clock.install();
  let requests = 0;
  page.on('request', r => { if (new URL(r.url()).pathname === '/api/overview') requests++; });
  await login(page);
  const panel = page.locator('.panel', { has: page.locator('h3', { hasText: /^Сервер$/ }) });
  await expect(panel).toBeVisible();
  // До первого замера (15 с после старта) — пояснение, после — CPU/память/сеть.
  await expect(panel).toContainText(/Первый замер|CPU/);
  const before = requests;
  await page.clock.runFor(16_000);
  await expect.poll(() => requests).toBeGreaterThan(before);
  // Фоновое обновление не сбрасывает страницу в «Загрузка…».
  await expect(page.locator('.empty.loading')).toHaveCount(0);
});
