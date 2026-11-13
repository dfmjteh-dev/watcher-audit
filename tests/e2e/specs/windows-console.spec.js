const { test, expect, login } = require('./helpers');

// Задача F2: запись консоли Windows-агента — транскрипт PowerShell без .timing:
// текст вместо проигрывателя, фильтр по ОС. Хост — сидовый kassa-01 (без своих
// консольных записей), чтобы не менять ни список хостов, ни выборки других спек.
// 0.12: ввод и вывод одной сессии (auditSession = PID PowerShell) — одна строка.
test('Windows: транскрипт PowerShell открывается текстом, фильтр по ОС', async ({ page, request }) => {
  const headers = { Authorization: 'Bearer e2e-agent-token' };
  const t0 = new Date(Date.now() - 120_000).toISOString();
  const t1 = new Date(Date.now() - 90_000).toISOString();
  const common = { host: 'kassa-01', sessionUser: 'winadmin', remoteAddr: '10.1.0.99', startedAt: t0, endedAt: t1, durationSec: 30, source: 'win-transcript', shell: 'Windows PowerShell 5.1' };
  const transcript = '\uFEFF**********************\nНачало записи сценария Windows PowerShell\n**********************\nPS C:\\Users\\winadmin> Get-Service spooler\nRunning  spooler  Print Spooler\n';
  let r = await request.post('/api/upload-terminal', { headers, multipart: {
    meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ ...common, kind: 'terminal-input', sessionId: 7001, input: 'Get-Service spooler' })) },
  } });
  expect(r.ok()).toBeTruthy();
  r = await request.post('/api/upload-terminal', { headers, multipart: {
    meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ ...common, kind: 'terminal-output', outFile: 'e2e-win.txt', timingFile: '', auditSession: 7001 })) },
    out: { name: 'e2e-win.txt', mimeType: 'text/plain', buffer: Buffer.from(transcript) },
  } });
  expect(r.ok()).toBeTruthy();

  await login(page);
  await page.goto('/#/console');
  await page.locator('.field:has(label:text-is("ОС")) select').selectOption('windows');
  const rows = page.locator('tbody tr');
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText('Windows');

  await page.fill('input[placeholder="например, rm -rf"]', 'spooler');
  await page.press('input[placeholder="например, rm -rf"]', 'Enter');
  await page.click('button:has-text("транскрипт")');
  const view = page.locator('.term-transcript');
  await expect(view).toContainText('Print Spooler');
  await expect(view.locator('mark').first()).toHaveText(/spooler/i);
  await expect(page.locator('.term-panel .xterm')).toHaveCount(0);
  await expect(page.locator('.term-in')).toContainText('Команды (из транскрипта)');
  await expect(page.locator('.term-panel')).toContainText('с 10.1.0.99');
});
