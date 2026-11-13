// Данные для e2e — только через настоящие агентские и админские API (не SQL в
// обход сервера): heartbeat, загрузка сегментов и консольных сессий, группы,
// пользователи. Значения, которые проверяют спеки, — в fixtures.js.
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { request } = require('@playwright/test');
const config = require('./playwright.config.js');
const F = require('./fixtures.js');

const iso = d => d.toISOString();

module.exports = async () => {
  const baseURL = config.use.baseURL;
  const agent = await request.newContext({ baseURL });
  const ok = async (resp, what) => {
    if (!resp.ok()) throw new Error(`${what}: HTTP ${resp.status()} ${await resp.text()}`);
    return resp.headers()['content-type']?.includes('json') ? resp.json() : null;
  };

  for (const h of F.hosts) {
    await ok(await agent.post('/api/heartbeat', { data: { host: h.name, ip: h.ip, version: h.version, droppedSegments: h.dropped || 0, resources: h.resources } }), 'heartbeat ' + h.name);
  }

  const now = Date.now();
  // База — 5 минут назад; если записи (до +3 мин) пересекли бы полночь UTC —
  // сдвигаем всё на 23:40 предыдущих суток (браузер в тестах в UTC).
  let base = now - 5 * 60_000;
  const day = t => Math.floor(t / 86_400_000);
  if (day(base) !== day(base + 4 * 60_000)) base = day(base + 4 * 60_000) * 86_400_000 - 20 * 60_000;
  const clip = fs.readFileSync(path.join(__dirname, 'media', 'clip-3s.mp4'));
  for (const [i, s] of F.segments.entries()) {
    const started = new Date(base + s.offsetSec * 1000);
    const meta = { host: s.host, startedAt: iso(started), endedAt: iso(new Date(started.getTime() + F.segmentSeconds * 1000)), durationSec: F.segmentSeconds, frames: 12, motionEvents: s.motion || 0, startReason: 'motion', sessionUser: s.user, activity: s.activity || [], monitors: s.monitors };
    await ok(await agent.post('/api/upload', {
      multipart: {
        video: { name: `${s.host}_${i}.mp4`, mimeType: 'video/mp4', buffer: clip },
        meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(meta)) },
      },
    }), 'upload segment');
  }

  const t = iso(new Date(now - 60_000));
  await ok(await agent.post('/api/upload-terminal', {
    multipart: {
      meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({
        kind: 'terminal-input', host: F.terminalHost, sessionUser: 'root', startedAt: t, endedAt: t, durationSec: 5, sessionId: 1, input: F.hostileInput,
      })) },
    },
  }), 'upload terminal-input');
  // Вывод той же сессии (тот же хост, пользователь и время, что у ввода выше)
  // — плеер (E4) должен связать их и показать рядом.
  const out = Buffer.concat([
    Buffer.from(`Script started on 2026-10-08 10:00:00+00:00 [TERM="xterm-256color" TTY="/dev/pts/0" COLUMNS="${F.terminalCols}" LINES="${F.terminalRows}"]\n`),
    ...F.terminalChunks.map(([, text]) => Buffer.from(text)),
    Buffer.from('\nScript done on 2026-10-08 10:00:06+00:00 [COMMAND_EXIT_CODE="2"]\n'),
  ]);
  const timing = F.terminalChunks.map(([delay, text]) => `${delay} ${Buffer.byteLength(text)}`).join('\n') + '\n';
  await ok(await agent.post('/api/upload-terminal', {
    multipart: {
      meta: { name: 'meta.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({
        kind: 'terminal-output', host: F.terminalHost, sessionUser: 'root', startedAt: t, endedAt: t, durationSec: 5, exitCode: 2, outFile: 'e2e.out', timingFile: 'e2e.timing',
      })) },
      out: { name: 'e2e.out', mimeType: 'text/plain', buffer: out },
      timing: { name: 'e2e.timing', mimeType: 'text/plain', buffer: Buffer.from(timing) },
    },
  }), 'upload terminal-output');
  await agent.dispose();

  const admin = await request.newContext({ baseURL, extraHTTPHeaders: { 'X-Requested-With': 'XMLHttpRequest' } });
  await ok(await admin.post('/api/login', { data: { username: 'admin', password: 'e2e-admin-password' } }), 'login');
  const group = await ok(await admin.post('/api/groups', { data: { name: F.group } }), 'group');
  const fleet = await ok(await admin.get('/api/fleet?limit=100'), 'fleet');
  const ids = fleet.items.filter(h => F.groupHosts.includes(h.name)).map(h => h.id);
  await ok(await admin.post('/api/fleet/assign-group', { data: { hostIds: ids, groupId: group.id } }), 'assign');
  await ok(await admin.post('/api/users', { data: { username: F.operator.username, role: 'operator', password: F.operator.password } }), 'operator');
  const viewer = await ok(await admin.post('/api/users', { data: { username: F.viewer.username, role: 'viewer', password: F.viewer.password } }), 'viewer');
  await ok(await admin.put(`/api/users/${viewer.id}/groups`, { data: { groupIds: [group.id] } }), 'viewer groups');

  // Этап 4 Android: регистрация по токену (как агент на телефоне), heartbeat с
  // платформой android и события за сегодня — у базы видео (несколько минут назад).
  const A = F.phoneApps;
  const at = sec => iso(new Date(base + sec * 1000));
  const ev = (sec, type, pkg, detail) => ({ eventId: crypto.randomUUID(), ts: at(sec), type, package: pkg ?? null, detail: detail ?? null });
  const phoneEvents = {
    'phone-01': [
      ev(0, 'device_state', null, { osVersion: '14', securityPatch: '2026-09-01', networkType: 'wifi', batteryPercent: 80, charging: false, screenOn: true, usageAccess: true }),
      ev(10, 'app_foreground', A.game), ev(130, 'app_background', A.game),
      ev(140, 'app_foreground', A.mail), ev(170, 'app_background', A.mail),
      ev(150, 'app_installed', A.sideload, { installer: 'com.google.android.packageinstaller' }),
      ev(160, 'app_traffic', A.game, { rxBytes: 5 * 1048576, txBytes: 1048576, intervalMs: 60000, networkType: 'mobile' }),
      ev(165, 'app_inventory', null, { snapshotId: crypto.randomUUID(), part: 0, parts: 1, packages: [
        { package: 'android', versionCode: 34, system: true },
        { package: A.game, versionCode: 3, versionName: '3.0', system: false, installer: 'com.android.vending', label: 'Игра Пример' },
        { package: A.sideload, versionCode: 1, versionName: '1.0', system: false, installer: 'com.google.android.packageinstaller', label: 'Сайдлоад' },
      ] }),
      // Этап 6: сетевой журнал и журнал безопасности (как присылает агент-владелец).
      ev(166, 'net_dns', null, { domain: 'ads.example.com', package: A.game, count: 7 }),
      ev(167, 'net_dns', null, { domain: 'mail.example.com', package: A.mail, count: 2 }),
      ev(168, 'net_connect', null, { ip: '203.0.113.9', port: 443, package: A.game, count: 3 }),
      ev(169, 'security', null, { tag: 210002, name: 'adb_shell_command', data: '["pm list packages"]' }),
    ],
    'phone-02': [ev(0, 'device_state', null, { osVersion: '10', usageAccess: false })],
  };
  for (const name of F.phones) {
    const token = await ok(await admin.post('/api/agents/enroll-tokens', { data: { name: 'e2e-' + name, ttlHours: 1 } }), 'enroll token');
    const anon = await request.newContext({ baseURL });
    const { secret } = await ok(await anon.post('/api/enroll', { data: { token: token.token, host: name, kind: 'android' } }), 'enroll ' + name);
    await anon.dispose();
    const phone = await request.newContext({ baseURL, extraHTTPHeaders: { Authorization: 'Bearer ' + secret } });
    // Этап 5: phone-01 — владелец устройства, phone-02 — обычное приложение.
    await ok(await phone.post('/api/heartbeat', { data: { host: name, version: F.agentVersion, platform: 'android', ownerActive: name === 'phone-01' } }), 'heartbeat ' + name);
    await ok(await phone.post('/api/events', { data: { events: phoneEvents[name] } }), 'events ' + name);
    await phone.dispose();
  }
  await admin.dispose();

  // Сессии ролей — один вход на роль на весь прогон: у сервера лимит входов
  // 20 / 5 мин с одного IP (A3), и спеки, логинясь каждая через форму, в него
  // упирались. Через форму входят только тесты самой формы (auth.spec.js).
  fs.mkdirSync(F.authDir, { recursive: true });
  for (const [username, password] of [['admin', F.adminPassword], [F.operator.username, F.operator.password], [F.viewer.username, F.viewer.password]]) {
    const ctx = await request.newContext({ baseURL });
    await ok(await ctx.post('/api/login', { data: { username, password } }), 'login ' + username);
    await ctx.storageState({ path: path.join(F.authDir, username + '.json') });
    await ctx.dispose();
  }
};
