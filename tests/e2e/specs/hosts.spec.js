const { test, expect, login, F } = require('./helpers');

test('Обзор: состояние парка и проблемные хосты', async ({ page }) => {
  await login(page);
  const main = page.locator('.main');
  await expect(main.locator('.card').first()).toContainText(`из ${F.hosts.length + F.phones.length}`);
  // kassa-02: агент 0.1.0 и 3 потерянных сегмента.
  await expect(main.locator('.panel', { hasText: 'Потери сегментов' })).toContainText('kassa-02');
  // Версии сервера и агентов раздельные: агент 0.1.0 без версии, назначенной
  // группе, — не проблема (релиз только сервера не делает агентов устаревшими).
  await expect(main.locator('.panel', { hasText: 'Агент не обновился' })).toHaveCount(0);
});

test('Хосты: список, поиск, фильтр по группе', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Хосты');
  const names = page.locator('a.hostlink');
  await expect(names).toHaveText(['buh-01', 'kassa-01', 'kassa-02', ...F.phones]);
  await page.fill('input[placeholder="имя, IP или пользователь"]', '10.1.');
  await expect(names).toHaveText(['kassa-01', 'kassa-02']);
  // Последний пользователь хоста — в колонке и в поиске (у buh-01 свежее всего
  // консольная сессия root, видео ivan — раньше).
  await page.fill('input[placeholder="имя, IP или пользователь"]', 'root');
  await expect(names).toHaveText(['buh-01']);
  await expect(page.locator('tbody tr').first()).toContainText('root');
  await page.fill('input[placeholder="имя, IP или пользователь"]', '');
  await page.locator('.filters select').first().selectOption({ label: F.group });
  await expect(names).toHaveText(['kassa-01', 'kassa-02']);
});

test('группы: переименование по месту, без кнопки «Сохранить»', async ({ page }) => {
  await login(page);
  await page.goto('/#/settings/groups');
  await page.fill('input[placeholder="название"]', 'Временная');
  await page.click('text=Добавить');
  const row = page.locator('tbody tr', { hasText: 'Временная' });
  await expect(row.locator('button', { hasText: 'Сохранить' })).toHaveCount(0);
  await row.locator('button.link').click();
  // В режиме правки в строке только поле — текста «Временная» в ней уже нет.
  const input = page.locator('input[aria-label="Название группы"]');
  await input.fill('Временная-2');
  await input.press('Enter');
  const renamed = page.locator('tbody tr', { hasText: 'Временная-2' });
  await expect(renamed.locator('button.link')).toContainText('Временная-2');
  await renamed.locator('button', { hasText: 'Удалить' }).click();
  await expect(page.locator('tbody tr', { hasText: 'Временная' })).toHaveCount(0);
});

test('страница хоста: видео, консоль, конфиг, инфо', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/buh-01');
  await expect(page.locator('.tabs a')).toHaveText(['Видео', 'Консоль', 'Конфиг', 'Инфо']);

  // Видео: два пользователя (RDS) — есть фильтр; клик по строке открывает плеер.
  const rows = page.locator('tbody tr');
  await expect(rows).toHaveCount(3);
  await rows.first().click();
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', /\/api\/segments\/\d+\/video(\?v=[0-9a-f]+)?$/);
  // 0.14.4: поиск по набранному — подстрока, сразу при вводе.
  await page.locator('.filters input[list="video-users"]').fill('PET');
  await expect(rows).toHaveCount(1);
  await page.locator('.filters input[list="video-users"]').fill('');
  await expect(rows).toHaveCount(3);
  await page.locator('.filters input[list="video-users"]').fill('petr');
  await expect(rows).toHaveCount(1);

  await page.click('.tabs >> text=Консоль');
  await expect(page.locator('text=exit 2')).toBeVisible();
  await expect(page.locator('button', { hasText: 'воспроизвести' })).toBeVisible();

  await page.click('.tabs >> text=Инфо');
  const agentInfo = page.locator('.panel').filter({
    has: page.getByRole('heading', { name: 'Агент', exact: true }),
  });
  await expect(agentInfo.locator('.kv')).toContainText('10.2.0.21');
  await expect(page.locator('.kv', { hasText: 'Последний пользователь' })).toContainText('root');
  await expect(page.locator('.presence .cell.on')).not.toHaveCount(0);
});

test('конфиг хоста сохраняется, пустые поля не уходят нулями', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/kassa-01/config');
  await page.locator('.field', { hasText: 'Кадров в секунду в файле' }).locator('input').fill('3');
  await page.locator('.field', { hasText: 'Активное окно' }).locator('select').selectOption('process');
  await page.click('text=Сохранить');
  await expect(page.locator('.status')).toHaveText('сохранено');

  const cfg = await page.evaluate(async () => {
    const fleet = await (await fetch('/api/fleet?limit=100')).json();
    const id = fleet.items.find(h => h.name === 'kassa-01').id;
    return (await (await fetch(`/api/fleet/${id}/config`)).json()).config;
  });
  expect(cfg.outputFps).toBe(3);
  expect(cfg.activity).toBe('process');   // H9
  // Пустые списки и невыбранный preset не уходят: у хоста они перекрыли бы группу.
  expect(cfg).not.toHaveProperty('excludeUsers');
  expect(cfg).not.toHaveProperty('preset');
});

// H8: расписание записи и исключённые окна — массивами в конфиге. 0.13 (п.4):
// расписание сеткой (текстом — по кнопке), программы — фишками.
test('конфиг хоста: расписание и исключённые окна', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/kassa-02/config');
  const field = label => page.locator('.field', { hasText: label });
  const sched = field('Записывать только в эти часы');
  await expect(sched.locator('.sched-c.on')).toHaveCount(7 * 24);   // пусто = всегда
  await sched.locator('button', { hasText: 'текстом' }).click();
  await sched.locator('input').fill('пн 9:00-18:00');
  await page.click('text=Сохранить');
  await expect(page.locator('.status')).toContainText('расписание: не понял');

  await sched.locator('input').fill('пн-пт 09:00-18:00; сб 10:00-14:00');
  const excl = field('Пауза при активной программе').locator('input');
  await excl.fill('KeePass'); await excl.press('Enter');
  await excl.fill('1cv8'); await excl.press('Enter');
  // 0.13: заголовки — выбором (подсказки из активности), по строке на значение:
  // запятая в заголовке значение не делит.
  const titles = field('Пауза, когда заголовок').locator('input');
  await titles.fill('Сбербанк Онлайн'); await titles.press('Enter');
  await titles.fill('Личная почта, входящие'); await titles.press('Enter');
  await page.click('text=Сохранить');
  await expect(page.locator('.status')).toHaveText('сохранено');

  const config = async () => page.evaluate(async () => {
    const fleet = await (await fetch('/api/fleet?limit=100')).json();
    const id = fleet.items.find(h => h.name === 'kassa-02').id;
    return (await (await fetch(`/api/fleet/${id}/config`)).json()).config;
  });
  let cfg = await config();
  expect(cfg.schedule).toEqual([{ days: [1, 2, 3, 4, 5], from: '09:00', to: '18:00' }, { days: [6], from: '10:00', to: '14:00' }]);
  expect(cfg.excludeProcesses).toEqual(['KeePass', '1cv8']);
  expect(cfg.excludeTitles).toEqual(['Сбербанк Онлайн', 'Личная почта, входящие']);
  expect(cfg).not.toHaveProperty('captureIntervalMs');
  expect(cfg).not.toHaveProperty('downscaleWidth');

  // После перезагрузки — сеткой: 5 × 9 ч + 4 ч в субботу; клик — воскресенье 10:00.
  await page.reload();
  await expect(sched.locator('.sched-c.on')).toHaveCount(49);
  await sched.locator('.sched-c[title="вс 10:00–11:00"]').click();
  await field('Пауза при активной программе').locator('.chip', { hasText: 'KeePass' }).locator('button').click();
  await page.click('text=Сохранить');
  await expect(page.locator('.status')).toHaveText('сохранено');
  cfg = await config();
  expect(cfg.schedule).toContainEqual({ days: [7], from: '10:00', to: '11:00' });
  expect(cfg.excludeProcesses).toEqual(['1cv8']);
});

test('глубокая ссылка ?t= открывает нужный сегмент', async ({ page }) => {
  await login(page);
  const seg = await page.evaluate(async () => (await (await fetch('/api/segments?host=buh-01')).json())
    .sort((a, b) => a.startedAt.localeCompare(b.startedAt))[0]);
  const t = new Date(new Date(seg.startedAt).getTime() + 1500).toISOString();
  await page.goto(`/#/hosts/buh-01/video?t=${t}`);
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', new RegExp(`^/api/segments/${seg.id}/video(\\?v=[0-9a-f]+)?$`));
  await expect(page.locator('tbody tr.active')).toHaveCount(1);
});

test('старые адреса переадресуются', async ({ page }) => {
  await login(page);
  for (const [from, to] of [
    ['/#/fleet', /#\/hosts$/],
    ['/#/terminal', /#\/console$/],
    ['/#/h/buh-01', /#\/hosts\/buh-01\/video$/],
    ['/#/groups', /#\/settings\/groups$/],
    ['/#/audit?user=admin', /#\/settings\/audit\?user=admin$/],
  ]) {
    await page.goto(from);
    await expect(page).toHaveURL(to);
  }
});

// 0.13: журнал потерь вместо счётчика и статус конфига на агенте.
test('потери: что потеряно и почему, «Принято»; статус конфига', async ({ page }) => {
  await login(page);
  // IP и версия — как в seed: другие спеки ищут kassa-01 по ним.
  const hb = body => page.evaluate(async b => (await fetch('/api/heartbeat', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer e2e-agent-token' }, body: JSON.stringify(b),
  })).json(), { ip: '10.1.0.11', version: F.agentVersion, ...body });
  const first = await hb({ host: 'kassa-01', losses: [{ file: 'kassa-01_20261013_113107.mp4', recordedAt: '2026-10-13T08:31:07Z', sizeBytes: 2048, reason: 'age' }] });
  expect(first.lossesAccepted).toBe(1);
  await hb({ host: 'kassa-01', configHash: first.configHash, losses: [] });

  await page.goto('/#/hosts/kassa-01/info');
  const panel = page.locator('.panel.losses');
  await expect(panel).toContainText('kassa-01_20261013_113107.mp4');
  await expect(panel).toContainText('не выгружено дольше срока хранения');
  await expect(page.locator('.kv').first()).toContainText('конфиг: применён');
  await panel.locator('button', { hasText: 'Принято' }).click();
  await expect(panel).toContainText('Непринятых потерь нет');

  await page.goto('/#/hosts/kassa-01/config');
  await expect(page.locator('.config-status')).toContainText('получил текущий');
});

// 0.13: «Снять с записи» кнопкой в шапке хоста — значок в «Хостах», вернуть.
test('снять с записи и вернуть', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/kassa-02/info');
  await page.click('.page-actions >> text=Снять с записи');
  await page.fill('.pause-form input[placeholder^="причина"]', 'ремонт кассы');
  await page.click('.pause-form button[type=submit]');
  await expect(page.locator('.page-actions')).toContainText('экран и консоль не записываются — ремонт кассы');

  await page.click('.topnav >> text=Хосты');
  await expect(page.locator('tbody tr', { hasText: 'kassa-02' }).locator('.badge', { hasText: 'не записывается' })).toBeVisible();

  await page.goto('/#/hosts/kassa-02/info');
  await page.click('.page-actions >> text=Вернуть запись');
  await expect(page.locator('.page-actions')).toContainText('Снять с записи');
});

// 0.13 (п.4): пользователи и программы хоста — в «Инфо» и подсказками в конфиге;
// «записывать только при программе».
test('пользователи и приложения хоста, выбор в конфиге', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/buh-01/info');
  const users = page.locator('.panel', { hasText: 'Пользователи' });
  await expect(users).toContainText('ivan');
  await expect(users).toContainText('petr');
  await expect(page.locator('.panel', { hasText: 'Приложения за 30 дней' })).toContainText('WINWORD');

  await page.goto('/#/hosts/buh-01/config');
  const inc = page.locator('.field', { hasText: 'Записывать только при активной программе' });
  await inc.locator('.picker-known button', { hasText: 'WINWORD' }).click();
  await expect(inc.locator('.chip')).toHaveText(['WINWORD×']);
  const allow = page.locator('.field', { hasText: 'Записывать только этих пользователей' });
  await expect(allow.locator('.picker-known')).toContainText('ivan');
  await page.click('text=Сохранить');
  await expect(page.locator('.status')).toHaveText('сохранено');
  const cfg = await page.evaluate(async () => {
    const fleet = await (await fetch('/api/fleet?limit=100')).json();
    const id = fleet.items.find(h => h.name === 'buh-01').id;
    return (await (await fetch(`/api/fleet/${id}/config`)).json()).config;
  });
  expect(cfg.includeProcesses).toEqual(['WINWORD']);
  // Чтобы не влиять на другие спеки — снять.
  await page.click('text=Снять настройки хоста');
});

// 0.13 (PR9б): сколько станции стоит агент — колонка «Агент» и строка в «Инфо».
test('ресурсы агента: в списке хостов и в «Инфо»', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Хосты');
  const row = page.locator('tbody tr', { hasText: 'kassa-01' });
  await expect(row.locator('td[data-label="Агент"]')).toContainText('3% · 150.0 MB');
  await expect(row.locator('td[data-label="Агент"] .badge')).toContainText('50.0 MB');
  await expect(page.locator('tbody tr', { hasText: 'kassa-02' }).locator('td[data-label="Агент"]')).toHaveText('—');
  await page.goto('/#/hosts/kassa-01/info');
  const dd = page.locator('dt:has-text("Нагрузка агента") + dd');
  await expect(dd).toContainText('CPU 3% · память 150.0 MB · процессов 3');
  await expect(dd).toContainText('очередь 50.0 MB (4 ф.)');
});
