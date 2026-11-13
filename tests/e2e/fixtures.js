// Общие данные seed.js и спеков.
const fs = require('fs');
const path = require('path');

// Версии — из Directory.Build.props (раздельные: ServerVersion и AgentVersion).
// «Актуальные» агенты (kassa-01, buh-01) — версии агентов текущего релиза,
// комплект — тоже; строкой ('0.8.0') тесты ломались на бампе версии.
const props = fs.readFileSync(path.join(__dirname, '..', '..', 'Directory.Build.props'), 'utf8');
const serverVersion = props.match(/<ServerVersion>([^<]+)<\/ServerVersion>/)[1];
const agentVersion = props.match(/<AgentVersion>([^<]+)<\/AgentVersion>/)[1];

module.exports = {
  serverVersion,
  agentVersion,
  hosts: [
    // resources — 0.13 (PR9б): CPU/память агента и очередь, как шлёт агент.
    { name: 'kassa-01', ip: '10.1.0.11', version: agentVersion,
      resources: { cpuPercent: 2.5, memBytes: 157286400, processes: 3, queueBytes: 52428800, queueFiles: 4 } },
    { name: 'kassa-02', ip: '10.1.0.12', version: '0.1.0', dropped: 3 },
    { name: 'buh-01', ip: '10.2.0.21', version: agentVersion },
  ],
  // offsetSec — от общей базы seed.js (несколько минут назад, не через полночь
  // UTC). buh-01: три сегмента, между 2-м и 3-м пауза 2 минуты — для проверки
  // непрерывного воспроизведения (E3). Видео — media/clip-3s.mp4: VP9, потому
  // что Chromium из Playwright собран без H.264.
  segmentSeconds: 3,
  segments: [
    { host: 'buh-01', offsetSec: 0, user: 'ivan', motion: 2 },
    { host: 'buh-01', offsetSec: 3, user: 'petr' },
    // H9: смены активного окна — подписи в плеере и поиск по окнам.
    { host: 'buh-01', offsetSec: 123, user: 'ivan', motion: 9, activity: [
      { t: 0, process: 'WINWORD', title: 'Договор поставки.docx - Word' },
      { t: 1.5, process: 'chrome', title: 'Почта — Входящие', site: 'mail.example.com' },
    ] },
    // 0.13: два монитора в одном кадре — выбор монитора в плеере (клип 3 с — любого
    // 160×90 — два «монитора» по 80×90).
    { host: 'kassa-01', offsetSec: 30, monitors: [{ x: 0, y: 0, w: 80, h: 90, primary: true }, { x: 80, y: 0, w: 80, h: 90, primary: false }] },
  ],
  // Этап 4 Android: телефоны регистрируются своей учёткой (kind android — /api/events
  // анонимно не принимает). phone-01 — с событиями за сегодня (вкладка
  // «Устройство»), phone-02 — без доступа к статистике («агент ослеп»).
  phones: ['phone-01', 'phone-02'],
  phoneApps: { game: 'com.example.game', mail: 'com.example.mail', sideload: 'com.example.sideload' },
  group: 'Кассы',
  groupHosts: ['kassa-01', 'kassa-02'],
  terminalHost: 'buh-01',
  // terminal-input сервер не режет по набору символов (это ввод пользователя,
  // см. Program.cs) — настоящий путь, по которому HTML от агента доходит до UI.
  hostileInput: '<img src=x onerror="window.__xss=1">echo ok',
  // Запись script(1) как её пишет util-linux 2.38 (watcher-term-wrap.sh):
  // строка-заголовок с размером терминала, куски вывода с задержками из
  // .timing, строка «Script done» в конце (в .timing не входит). Задача E4.
  terminalCols: 100,
  terminalRows: 20,
  terminalChunks: [
    [0.2, '$ '],
    [1.0, 'deploy --prod\r\n'],
    [4.0, '\x1b[32mdeploy finished\x1b[0m\r\n'],
    [0.5, '$ '],
  ],
  operator: { username: 'e2e-operator', password: 'operator-password-1' },
  viewer: { username: 'e2e-viewer', password: 'viewer-password-12' },
  adminPassword: 'e2e-admin-password',
  authDir: require('path').join(__dirname, '.auth'),
  // H2: тестовая пара ключей обновлений — генерируется при первом запуске
  // (playwright.config.js), не хранится в репозитории.
  updateKeyFile: require('path').join(__dirname, '.update-key.json'),
};
