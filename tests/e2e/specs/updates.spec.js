const fs = require('fs');
const crypto = require('crypto');
const { test, expect, login, F } = require('./helpers');

// H2: обновление агентов с сервера — пакет с подписью владельца, целевая
// версия группы, предложение агенту в heartbeat.
test('обновления: пакет с подписью → целевая версия группы → агент получает предложение', async ({ page }) => {
  const { privateKey } = JSON.parse(fs.readFileSync(F.updateKeyFile, 'utf8'));
  const content = Buffer.from('e2e-agent-package-0.99.0');
  const sha = crypto.createHash('sha256').update(content).digest('hex');
  const sign = (platform, version) =>
    crypto.sign('sha256', Buffer.from(`watcher-agent-update/1\n${platform}\n${version}\n${sha}\n`), privateKey).toString('base64');

  await login(page);
  await page.goto('/#/settings/updates');
  const upload = (version, signature) => page.evaluate(async ({ version, signature, bytes }) => {
    const fd = new FormData();
    fd.append('platform', 'windows-msi');
    fd.append('version', version);
    fd.append('signature', signature);
    fd.append('file', new Blob([new Uint8Array(bytes)]), 'pkg.msi');
    return (await fetch('/api/agent-packages', { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest' }, body: fd })).status;
  }, { version, signature, bytes: [...content] });

  // Подпись под другую версию сервер не принимает.
  page.expectHttpError(400, '/api/agent-packages');
  expect(await upload('0.99.0', sign('windows-msi', '0.99.1'))).toBe(400);

  // Через форму UI (0.12: свёрнута — нужна только для ключа вне сервера).
  await page.click('summary:has-text("Пакеты на сервере и ручная загрузка")');
  // 0.13: по умолчанию — Windows MSI; пакет здесь — zip.
  await page.locator('.field', { hasText: 'Платформа' }).locator('select').selectOption('windows-msi');
  await page.locator('.field', { hasText: 'Версия' }).locator('input').fill('0.99.0');
  await page.locator('.field', { hasText: 'Подпись' }).locator('input').fill(sign('windows-msi', '0.99.0'));
  await page.locator('input[type=file]').setInputFiles({ name: 'pkg.msi', mimeType: 'application/octet-stream', buffer: content });
  await page.click('button:has-text("Загрузить")');
  await expect(page.locator('details.fold', { hasText: 'Пакеты на сервере' }).locator('tbody tr')).toContainText(['0.99.0']);

  await page.locator(`select[aria-label="Целевая версия ${F.group}"]`).selectOption('0.99.0');
  await expect(page.locator(`select[aria-label="Целевая версия ${F.group}"]`)).toHaveValue('0.99.0');

  // Агент группы на другой версии получает предложение со ссылкой и подписью.
  // Версию шлём текущую (как в seed) — другие спеки смотрят на версию kassa-01.
  const offer = await page.evaluate(async version => {
    const r = await fetch('/api/heartbeat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer e2e-agent-token' },
      body: JSON.stringify({ host: 'kassa-01', ip: '10.1.0.11', version, platform: 'windows-msi' }),
    });
    return (await r.json()).update;
  }, F.agentVersion);
  expect(offer.version).toBe('0.99.0');
  expect(offer.sha256).toBe(sha);
  expect(crypto.verify('sha256', Buffer.from(`watcher-agent-update/1\nwindows-msi\n0.99.0\n${sha}\n`), crypto.createPublicKey(privateKey), Buffer.from(offer.signature, 'base64'))).toBe(true);

  // Снять цель — чтобы остальные спеки видели парк без обновлений.
  await page.locator(`select[aria-label="Целевая версия ${F.group}"]`).selectOption('');
  await expect(page.locator(`select[aria-label="Целевая версия ${F.group}"]`)).toHaveValue('');
});
