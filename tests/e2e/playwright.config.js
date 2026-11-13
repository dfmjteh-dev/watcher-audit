// Задача E6 (docs/roadmap-next.md). Сервер поднимается настоящий (собранный
// Watcher.Server, не мок) на отдельном порту и с чистым DataDir; данные
// заливаются через те же API, что используют агенты (seed.js), — тест видит
// ровно то, что увидел бы пользователь после работы агентов.
const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const { defineConfig } = require('@playwright/test');

const ROOT = path.resolve(__dirname, '../..');
const DATA = path.join(__dirname, '.server-data');

// H2: ключ обновлений для e2e — открытый в env сервера, закрытый спеки берут
// из того же файла для подписи пакета. Конфиг читается и в главном процессе, и
// в воркерах — файл создаётся один раз, иначе ключи разошлись бы.
const UPDATE_KEY_FILE = path.join(__dirname, '.update-key.json');
if (!fs.existsSync(UPDATE_KEY_FILE)) {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  fs.writeFileSync(UPDATE_KEY_FILE, JSON.stringify({
    publicKey: publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
    privateKey: privateKey.export({ type: 'pkcs8', format: 'pem' }),
  }), { mode: 0o600 });
}
const UPDATE_PUBLIC_KEY = JSON.parse(fs.readFileSync(UPDATE_KEY_FILE, 'utf8')).publicKey;
const MAINTENANCE_KEY_FILE = path.join(__dirname, '.maintenance-key.pem');
if (!fs.existsSync(MAINTENANCE_KEY_FILE)) {
  const { privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  fs.writeFileSync(MAINTENANCE_KEY_FILE, privateKey.export({type:'pkcs8',format:'pem'}), {mode:0o600});
}

const PORT = Number(process.env.E2E_PORT || 5091);
// CI собирает Release (ci.yml), локально по умолчанию Debug.
const CONFIGURATION = process.env.E2E_CONFIGURATION || 'Debug';

module.exports = defineConfig({
  testDir: './specs',
  globalSetup: require.resolve('./seed.js'),
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    // Сутки в плеере (E3) — локальные браузера; фиксируем зону, чтобы seed.js
    // мог гарантировать, что записи не разъедутся по разным суткам.
    timezoneId: 'UTC',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `rm -rf "${DATA}" && mkdir -p "${DATA}" && dotnet run --project "${ROOT}/src/Watcher.Server" -c ${CONFIGURATION} --no-build -- --contentRoot "${ROOT}/src/Watcher.Server"`,
    url: `http://127.0.0.1:${PORT}/login.html`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: {
      // Время в UI — зона сервера (0.14.4); та же, что timezoneId браузера ниже.
      TZ: 'UTC',
      Server__DataDir: DATA,
      Server__DbPath: path.join(DATA, 'watcher.db'),
      Server__AdminUser: 'admin',
      Server__AdminPassword: 'e2e-admin-password',
      Server__ApiToken: 'e2e-agent-token',
      Server__CookieSecure: 'false',
      Server__UpdatePublicKey: UPDATE_PUBLIC_KEY,
      Server__MaintenanceSigningKeyPath: MAINTENANCE_KEY_FILE,
      // Комплект агентов — его кладёт спека agentkit.spec.js (каталог внутри DATA).
      Server__AgentKitDir: path.join(DATA, 'kit'),
      Kestrel__Endpoints__Http__Url: `http://127.0.0.1:${PORT}`,
    },
  },
});
