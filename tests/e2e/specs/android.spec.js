const { test, expect, login, F } = require('./helpers');

// Этап 4 Android (docs/android-plan.md): вкладка «Устройство» и признаки
// Android-агента во флоте. Данные — seed.js (phone-01 с событиями, phone-02 «ослеп»).
const A = F.phoneApps;

test('Android: вкладка «Устройство» — приложения, установки, трафик, перечень', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/phone-01');
  // У телефона нет видео и консоли — вместо них «Устройство», она и открывается.
  await expect(page.locator('.tabs a')).toHaveText(['Устройство', 'Конфиг', 'Инфо']);
  await expect(page.locator('.tabs a.current')).toHaveText('Устройство');
  await expect(page.locator('.page-title')).toContainText('Android');

  const apps = page.locator('.panel', { hasText: 'Приложения на экране' }).locator('tbody tr');
  await expect(apps).toHaveCount(2);
  // Сортировка по времени на экране: игра 2 мин, почта 30 с. Показывается ИМЯ приложения.
  await expect(apps.nth(0)).toContainText('Игра Пример');
  await expect(apps.nth(0)).toContainText(A.game);
  await expect(apps.nth(0)).toContainText('2 мин');
  await expect(apps.nth(1)).toContainText(A.mail);

  const changes = page.locator('.panel', { hasText: 'Установки и удаления' });
  await expect(changes.locator('tbody tr')).toHaveCount(1);
  await expect(changes).toContainText(A.sideload);
  await expect(changes.locator('.badge.warn')).toHaveText('APK вручную');

  const traffic = page.locator('.panel', { hasText: 'Трафик по приложениям' });
  await expect(traffic.locator('tbody tr').first()).toContainText('5.0 MB');

  await expect(page.locator('.panel', { hasText: 'Состояние устройства' })).toContainText('2026-09-01');

  // Перечень: по умолчанию — только не из магазина, флажок показывает всех.
  const inv = page.locator('.panel', { hasText: 'Установленные приложения' });
  await expect(inv).toContainText('Всего 3, системных 1, установлено пользователем 2');
  await expect(inv.locator('tbody tr')).toHaveCount(1);
  await inv.locator('input[type=checkbox]').check();
  await expect(inv.locator('tbody tr')).toHaveCount(2);
  await expect(inv).toContainText('Google Play');

  // Управление (политики/блокировка/роль владельца) — НЕ на «Устройстве», а в «Конфиге».
  await expect(page.locator('.panel', { hasText: 'Политика управления' })).toHaveCount(0);
  await expect(page.locator('.panel', { hasText: 'Блокировка устройства' })).toHaveCount(0);

  // Предыдущий день — пусто, но страница не ломается.
  await page.click('text=← предыдущий');
  await expect(page.locator('.panel', { hasText: 'Приложения на экране' })).toContainText('За этот день приложения на экран не выходили.');
});

test('Android: «агент ослеп» — в «Хостах», фильтрах и на Обзоре', async ({ page }) => {
  await login(page);
  await page.click('.topnav >> text=Хосты');
  const row = page.locator('tbody tr', { hasText: 'phone-02' });
  // Рядом может стоять и HTTP (e2e-стенд без TLS) — ищем именно «агент ослеп».
  await expect(row.locator('.badge.danger', { hasText: 'агент ослеп' })).toHaveCount(1);
  await expect(page.locator('tbody tr', { hasText: 'phone-01' }).locator('.badge.danger', { hasText: 'агент ослеп' })).toHaveCount(0);

  await page.locator('.filters select').nth(2).selectOption({ label: 'Android' });
  await expect(page.locator('a.hostlink')).toHaveText(F.phones);
  await page.locator('.filters select').nth(1).selectOption({ label: 'с проблемами' });
  // На e2e-стенде все агенты по HTTP — это тоже «проблема», поэтому здесь проверяем
  // только, что ослепший телефон в списке.
  await expect(page.locator('a.hostlink', { hasText: 'phone-02' })).toHaveCount(1);

  await page.click('.topnav >> text=Обзор');
  await expect(page.locator('.panel', { hasText: 'Android-агент ослеп' })).toContainText('phone-02');

  await page.goto('/#/hosts/phone-02/info');
  await expect(page.locator('.panel', { hasText: 'Доступ к статистике' })).toContainText('нет — агент ослеп');
});

test('Android: operator видит телефон без вкладки «Устройство»', async ({ page }) => {
  await login(page, F.operator.username);
  await page.goto('/#/hosts/phone-01');
  await expect(page.locator('.tabs a')).toHaveText(['Конфиг', 'Инфо']);
});

test('Android: роль владельца — статус и снятие командой (вкладка «Конфиг»)', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/phone-01/config');
  const panel = page.locator('.panel', { hasText: 'Роль владельца устройства' });
  await expect(panel.locator('.badge.ok')).toHaveText('владелец');
  // Не-владелец — на phone-02.
  await page.goto('/#/hosts/phone-02/config');
  await expect(page.locator('.panel', { hasText: 'Роль владельца устройства' }).locator('.badge.warn')).toHaveText('не владелец');

  // Снятие роли командой: после отправки статус «снять — отправлено», кнопка недоступна.
  await page.goto('/#/hosts/phone-01/config');
  await panel.locator('button', { hasText: 'Снять роль владельца' }).click();
  await expect(panel.locator('.badge.warn', { hasText: 'снять роль — отправлена' })).toHaveCount(1);
  await expect(panel.locator('button', { hasText: 'Снять роль владельца' })).toBeDisabled();
});

test('Android: сетевой журнал и журнал безопасности с фильтром', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/phone-01');
  const net = page.locator('.panel', { hasText: 'Сетевой журнал' });
  await expect(net.locator('tbody tr')).toHaveCount(3);
  await expect(net).toContainText('ads.example.com');
  await expect(net).toContainText('203.0.113.9:443');
  // Фильтр по домену.
  await net.locator('input').last().fill('mail');
  await expect(net.locator('tbody tr')).toHaveCount(1);
  await expect(net.locator('tbody tr')).toContainText('mail.example.com');
  await net.locator('input').last().fill('');
  // Фильтр по приложению (пакет игры).
  await net.locator('input').first().fill(F.phoneApps.game);
  await expect(net.locator('tbody tr')).toHaveCount(2);

  const sec = page.locator('.panel', { hasText: 'Журнал безопасности' });
  await expect(sec.locator('tbody tr')).toHaveCount(1);
  await expect(sec).toContainText('adb_shell_command');
});

test('Android: редактор политик в «Конфиге» — выбор приложений из списка', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/phone-01/config');
  const policy = page.locator('.panel', { hasText: 'Политика управления' });
  await expect(policy).toBeVisible();
  // Блок-лист — пикер из установленных приложений (по имени), не ручной ввод пакетов.
  const picker = policy.locator('.field', { hasText: 'Блокировать приложения' }).locator('.app-picker select');
  await picker.selectOption(A.game);                   // «Игра Пример»
  await policy.getByRole('checkbox').first().check();  // no_install_unknown_sources
  await policy.getByRole('button', { name: 'Сохранить политику' }).click();
  await expect(policy).toContainText('сохранено');
  // Перезаходим — выбранное приложение показано чипом с именем.
  await page.goto('/#/hosts/phone-02/config');
  await page.goto('/#/hosts/phone-01/config');
  const chip = page.locator('.panel', { hasText: 'Политика управления' }).locator('.app-picker .chip').first();
  await expect(chip).toContainText('Игра Пример');
  await expect(chip).toContainText(A.game);
});

test('Android: право «управление телефонами» в списке пользователей', async ({ page }) => {
  await login(page);
  await page.goto('/#/settings/users');
  await expect(page.locator('thead')).toContainText('Телефоны');
  // admin — «всегда».
  const adminRow = page.locator('tbody tr', { hasText: 'admin' }).first();
  await expect(adminRow).toContainText('всегда');
});

test('Android: блокировка устройства — команда и статус (вкладка «Конфиг»)', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/phone-01/config');
  const lock = page.locator('.panel', { hasText: 'Блокировка устройства' });
  await expect(lock).toBeVisible();
  await expect(lock.locator('.badge')).toHaveText('не заблокировано');
  await lock.getByRole('button', { name: 'Заблокировать устройство' }).click();
  // onChanged перезагружает хост — статус становится «команда … отправлена».
  await expect(lock.locator('.badge.warn')).toContainText('команда блокировки');
  await expect(lock.getByRole('button', { name: 'Разблокировать' })).toBeVisible();
});
