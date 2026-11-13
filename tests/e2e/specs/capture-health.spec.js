const { test, expect, login, F } = require('./helpers');

test('Живой Uploader без Capture не подтверждает запись экрана', async ({ page }) => {
  await login(page);
  const host = F.hosts.find(h => h.name === 'kassa-01');
  const heartbeat = async captures => {
    const response = await page.request.post('/api/heartbeat', {
      headers: { Authorization: 'Bearer e2e-agent-token' },
      data: { host: host.name, ip: host.ip, version: host.version, resources: host.resources, captures },
    });
    expect(response.ok()).toBeTruthy();
  };
  try {
    await heartbeat([]);
    await page.goto('/#/hosts');
    const row = page.locator('tbody tr', { has: page.locator('a.hostlink', { hasText: /^kassa-01$/ }) });
    await expect(row.locator('.dot')).toHaveClass(/online/);
    await expect(row).toContainText('Запись не подтверждена');
    await heartbeat([{ user: 'test', recording: true, at: new Date().toISOString() }]);
    await page.getByRole('button', { name: 'Обновить', exact: true }).click();
    await expect(row).toContainText('Процесс записи на связи');
    await heartbeat([{ user: 'test', recording: true, at: new Date(Date.now() - 6 * 60_000).toISOString() }]);
    await page.getByRole('button', { name: 'Обновить', exact: true }).click();
    await expect(row).toContainText('Запись не подтверждена');
  } finally {
    await heartbeat(null);
  }
});
