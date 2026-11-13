const { test, expect, login, F } = require('./helpers');

// Задача G1 (roadmap-next.md): токен регистрации показывается один раз, агент
// меняет его на учётку, учётку можно отозвать; хосты на общем токене помечены.
test('Агенты: токен регистрации → учётка → отзыв', async ({ page, request }) => {
  await login(page);
  await page.goto('/#/settings/agents');
  const tokens = page.locator('.panel', { hasText: 'Токены регистрации' });
  await tokens.locator('input[placeholder="кассы, октябрь"]').fill('e2e-раскатка');
  await tokens.locator('button', { hasText: 'Создать' }).click();
  const secret = tokens.locator('.notice .secret');
  await expect(secret).toHaveText(/^wenr_/);
  const token = (await secret.textContent()).trim();
  await expect(tokens.locator('tbody tr', { hasText: 'e2e-раскатка' })).toContainText('0 ');

  // Агент: существующий сид-хост, чтобы не менять список хостов других спек.
  const enroll = await request.post('/api/enroll', { data: { token, host: 'buh-01', kind: 'video' } });
  expect(enroll.ok()).toBeTruthy();
  const { secret: agentSecret } = await enroll.json();
  // ip/версия — как у сида: heartbeat перезаписывает их, другие спеки на них смотрят.
  const seed = F.hosts.find(h => h.name === 'buh-01');
  const hb = await request.post('/api/heartbeat', { data: { host: 'buh-01', ip: seed.ip, version: seed.version }, headers: { Authorization: 'Bearer ' + agentSecret } });
  expect(hb.ok()).toBeTruthy();

  await page.reload();
  const creds = page.locator('.panel', { hasText: 'Учётки агентов' });
  const row = creds.locator('tbody tr', { hasText: 'buh-01' });
  await expect(row).toContainText('экран');
  await expect(row).toContainText('e2e-раскатка');
  await expect(row).toContainText('действует');
  // Значение токена после перезагрузки больше нигде не показывается.
  await expect(page.locator('body')).not.toContainText(token);

  await row.locator('button', { hasText: 'Отозвать' }).click();
  await expect(row).toContainText('отозвана');
  const after = await request.post('/api/heartbeat', { data: { host: 'buh-01' }, headers: { Authorization: 'Bearer ' + agentSecret } });
  expect(after.status()).toBe(401);
});

test('Хосты без учётки агента помечены', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Хосты');
  await expect(page.locator('tbody tr', { hasText: 'kassa-01' })).toContainText('без учётки');
});
