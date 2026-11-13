const { test, expect, login } = require('./helpers');

// Задача E3: плеер суток. Сегменты buh-01 (fixtures.js): A [0–3 с], B [3–6 с]
// встык, C [123–126 с] — после паузы 2 мин.
async function segmentsOf(page, host) {
  return page.evaluate(async h => (await (await fetch('/api/segments?host=' + h)).json())
    .sort((a, b) => a.startedAt.localeCompare(b.startedAt)), host);
}
// URL видео — с версией файла (?v=, сервер пережимает записи и подменяет файл).
const src = id => new RegExp(`^/api/segments/${id}/video(\\?v=[0-9a-f]+)?$`);

test('таймлайн суток: блоки сегментов, клик — переход к моменту', async ({ page }) => {
  await login(page);
  const segs = await segmentsOf(page, 'buh-01');
  await page.goto('/#/hosts/buh-01/video');
  await expect(page.locator('.timeline-seg')).toHaveCount(3);
  // Яркость = motion: у C motion 9, у B — 0.
  const opacity = await page.locator('.timeline-seg').evaluateAll(els => els.map(e => Number(e.style.opacity)));
  expect(opacity[2]).toBeGreaterThan(opacity[1]);

  // Клик в точку таймлайна, где лежит C.
  const track = page.locator('.timeline-track');
  const box = await track.boundingBox();
  const dayStart = new Date(segs[2].startedAt); dayStart.setUTCHours(0, 0, 0, 0);
  const frac = (new Date(segs[2].startedAt).getTime() + 1500 - dayStart.getTime()) / 86_400_000;
  await track.click({ position: { x: frac * box.width, y: box.height / 2 } });
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', src(segs[2].id));
});

test('непрерывное воспроизведение: следующий сегмент сам, пауза между ними видна', async ({ page }) => {
  await login(page);
  const segs = await segmentsOf(page, 'buh-01');
  await page.goto('/#/hosts/buh-01/video');
  await page.locator('tbody tr').nth(1).click();
  const video = page.locator('.player video:not(.preload)');
  await expect(video).toHaveAttribute('src', src(segs[1].id));
  // 0.14.4: следующий сегмент заранее грузится в скрытом элементе — на переходе
  // плеер не перезагружается и не схлопывается.
  await expect(page.locator('.player video.preload')).toHaveAttribute('src', src(segs[2].id));
  await expect.poll(() => page.locator('.player video.preload').evaluate(v => v.readyState)).toBeGreaterThan(0);
  // Все показанные надписи плеера: на 8× сегмент C (3 с) кончается за ~0,4 с,
  // и «пауза 2 мин» тут же сменяется на «это последняя запись суток» — проверка
  // текущей надписи после перехода гонялась с этим (падала с Playwright 1.63).
  await page.evaluate(() => {
    window.__notices = [];
    new MutationObserver(() => {
      const n = document.querySelector('.player-notice');
      if (n && window.__notices.at(-1) !== n.textContent) window.__notices.push(n.textContent);
    }).observe(document.body, { subtree: true, childList: true, characterData: true });
  });
  await page.locator('.speeds button', { hasText: '8×' }).click();
  // B (3 с) кончается — сам переходит на C и сообщает о паузе 2 мин.
  await expect(video).toHaveAttribute('src', src(segs[2].id), { timeout: 15_000 });
  await expect.poll(() => page.evaluate(() => window.__notices)).toContain('пауза 2 мин — без записи');
  // Скорость сохраняется при переходе на следующий сегмент.
  await expect.poll(() => video.evaluate(v => v.playbackRate)).toBe(8);
});

test('горячие клавиши [ ] и пробел', async ({ page }) => {
  await login(page);
  const segs = await segmentsOf(page, 'buh-01');
  await page.goto('/#/hosts/buh-01/video');
  await page.locator('tbody tr').first().click();
  const video = page.locator('.player video:not(.preload)');
  await expect(video).toHaveAttribute('src', src(segs[0].id));
  await page.locator('body').press(']');
  await expect(video).toHaveAttribute('src', src(segs[1].id));
  await page.locator('body').press('[');
  await expect(video).toHaveAttribute('src', src(segs[0].id));
  await expect.poll(() => video.evaluate(v => v.readyState)).toBeGreaterThan(0);
  await page.locator('body').press(' ');
  await expect.poll(() => video.evaluate(v => v.paused)).toBe(true);
});

test('ссылка на момент ведёт обратно в тот же сегмент', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await login(page);
  const segs = await segmentsOf(page, 'buh-01');
  await page.goto('/#/hosts/buh-01/video');
  await page.locator('tbody tr').nth(2).click();
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', src(segs[2].id));
  await page.click('text=Ссылка на момент');
  await expect(page.locator('.player-notice')).toHaveText('ссылка на момент скопирована');
  const link = await page.evaluate(() => navigator.clipboard.readText());
  expect(link).toMatch(/\/#\/hosts\/buh-01\/video\?t=/);

  await page.goto('/#/hosts');
  await page.goto(link);
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', src(segs[2].id));
});

// H9: активное окно рядом с видео. Сегмент C buh-01 (fixtures.js): WINWORD с
// «Договор поставки.docx», через 1,5 с — chrome.
test('окна: поиск находит момент, плеер подписывает активное окно', async ({ page }) => {
  await login(page);
  const segs = await segmentsOf(page, 'buh-01');
  await page.goto('/#/hosts/buh-01/video');
  await page.fill('.activity-search input', 'ДОГОВОР');
  await page.click('.activity-search button');
  const hit = page.locator('.activity-results li');
  await expect(hit).toHaveCount(1);
  await expect(hit).toContainText('WINWORD — Договор поставки.docx');
  await hit.locator('button').click();
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', src(segs[2].id));
  await expect(page.locator('.activity-list summary')).toHaveText('смен окна в сегменте: 2');
  await page.locator('.activity-list summary').click();
  await page.locator('.activity-list li').nth(1).locator('button').click();
  await expect(page.locator('.activity-now')).toContainText('chrome — Почта — Входящие');
});

// 0.13 (п.2): кадр с двумя мониторами — показать один, кнопки видео на месте.
test('несколько мониторов: выбор монитора в плеере', async ({ page }) => {
  await login(page);
  await page.goto('/#/hosts/kassa-01/video');
  await page.locator('tbody tr').first().click();
  const mons = page.locator('.monitors button');
  await expect(mons).toHaveText(['все', '1 (основной)', '2']);
  await mons.nth(2).click();
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('style', /object-view-box: inset\(0px 0px 0px 80px\)/);
  await mons.nth(0).click();
  await expect(page.locator('.player video:not(.preload)')).not.toHaveAttribute('style', /object-view-box/);
});
