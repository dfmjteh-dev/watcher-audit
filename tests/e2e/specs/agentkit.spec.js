const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { test, expect, login, F } = require('./helpers');

// «Установка агентов»: комплект install-server.sh (каталог Server__AgentKitDir,
// playwright.config.js) — состояние токена, ссылки на скачивание, команды.
const KIT = path.join(__dirname, '..', '.server-data', 'kit');

test('установка агентов: комплект, токен, скачивание MSI', async ({ page }) => {
  await login(page);
  // Пока комплекта нет — подсказка, как собрать.
  await page.goto('/#/settings/install');
  await expect(page.locator('.panel')).toContainText('Комплекта ещё нет');
  await expect(page.locator('.cmd pre')).toHaveText('sudo ./install-server.sh --rebuild-kit');

  const { token } = await page.evaluate(async () => (await fetch('/api/agents/enroll-tokens', {
    method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: 'install-kit-e2e', ttlHours: 24 }),
  })).json());
  const msi = Buffer.from('e2e-msi');
  const tgz = Buffer.from('e2e-tgz');
  const tgzName = `watcher-agent-linux-${F.agentVersion}.tar.gz`;
  fs.mkdirSync(path.join(KIT, 'windows'), { recursive: true });
  fs.mkdirSync(path.join(KIT, 'linux'), { recursive: true });
  fs.writeFileSync(path.join(KIT, 'windows', 'watcher-agent.msi'), msi);
  fs.writeFileSync(path.join(KIT, 'linux', tgzName), tgz);
  const entry = (p, b) => ({ path: p, size: b.length, sha256: crypto.createHash('sha256').update(b).digest('hex') });
  fs.writeFileSync(path.join(KIT, 'kit.json'), JSON.stringify({
    version: F.agentVersion,
    serverUrl: 'https://watcher.e2e',
    builtAt: new Date().toISOString(),
    updateKey: 'MFkwE2E',
    enrollTokenSha256: crypto.createHash('sha256').update(token).digest('hex'),
    files: [entry('windows/watcher-agent.msi', msi), entry('linux/' + tgzName, tgz)],
  }));

  await page.reload();
  const kit = page.locator('.panel', { hasText: 'Комплект' }).first();
  await expect(kit).toContainText('https://watcher.e2e');
  await expect(kit).toContainText('install-kit-e2e');
  await expect(kit.locator('.badge.ok')).toHaveText('действует');
  const win = page.locator('.panel', { hasText: 'Windows' });
  await expect(win.locator('.cmd pre').first()).toContainText('msiexec /i');
  // 0.13: на Windows — только MSI (без .cmd и zip для install.ps1), Linux — один архив.
  await expect(win.locator('a.button')).toHaveCount(1);
  const linux = page.locator('.panel', { hasText: 'Linux' });
  await expect(linux.locator('a.button')).toHaveText(new RegExp(tgzName.replace(/\./g, '\\.')));
  await expect(linux.locator('.cmd pre').first()).toHaveText(`tar xzf ${tgzName}\nsudo sh watcher-agent-${F.agentVersion.split('+')[0]}/install.sh`);

  const href = await page.locator('a', { hasText: 'watcher-agent.msi' }).first().getAttribute('href');
  const r = await page.request.get(href);
  expect(r.status()).toBe(200);
  expect(Buffer.from(await r.body()).toString()).toBe('e2e-msi');

  // Скачивание — в журнале доступа.
  await page.goto('/#/settings/audit');
  await expect(page.locator('tbody')).toContainText('agent-kit.download');
});
