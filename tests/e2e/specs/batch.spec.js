const { test, expect, login, F } = require('./helpers');

test('fullscreen survives preloaded segment switch and manual selection', async ({ page }) => {
  await login(page);
  const segments = await page.evaluate(async () => (await (await fetch('/api/segments?host=buh-01')).json()).sort((a,b) => a.startedAt.localeCompare(b.startedAt)));
  await page.goto('/#/hosts/buh-01/video');
  await page.locator('tbody tr').first().click();
  await page.getByRole('button', { name: 'Полный экран', exact: true }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement?.classList.contains('player'))).toBe(true);
  await expect.poll(() => page.evaluate(() => {
    const v = document.querySelector('.player video:not(.preload)');
    const bar = document.querySelector('.player-bar');
    return v.getBoundingClientRect().bottom <= bar.getBoundingClientRect().top + 2 &&
      v.getBoundingClientRect().height > innerHeight * .7;
  })).toBe(true);
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', new RegExp('/' + segments[1].id + '/video'), { timeout: 12000 });
  await expect.poll(() => page.evaluate(() => {
    const v = document.querySelector('.player video:not(.preload)');
    return document.fullscreenElement?.contains(v) && v.videoWidth > 0 && v.getBoundingClientRect().height > 0;
  })).toBe(true);
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', new RegExp('/' + segments[2].id + '/video'), { timeout: 12000 });
  await page.getByTitle('предыдущий сегмент — [').click();
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', new RegExp('/' + segments[1].id + '/video'));
  await expect.poll(() => page.evaluate(() => document.fullscreenElement?.classList.contains('player'))).toBe(true);
  await page.getByRole('button', { name: 'Выйти из полного экрана', exact: true }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement === null)).toBe(true);
});

test('native fullscreen video remains visible when segment advances', async ({ page }) => {
  await login(page);
  const segments = await page.evaluate(async () => (await (await fetch('/api/segments?host=buh-01')).json()).sort((a,b) => a.startedAt.localeCompare(b.startedAt)));
  await page.goto('/#/hosts/buh-01/video');
  await page.locator('tbody tr').first().click();
  await expect(page.locator('.player video:not(.preload)')).not.toHaveAttribute('controlslist', /nofullscreen/);
  await page.locator('.player video:not(.preload)').evaluate(async v => { await v.requestFullscreen(); });
  await expect.poll(() => page.locator('video:fullscreen').evaluate(v => v.getBoundingClientRect().height / innerHeight)).toBeGreaterThan(.95);
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src', new RegExp('/' + segments[1].id + '/video'), { timeout: 12000 });
  await expect.poll(() => page.evaluate(() => document.fullscreenElement?.tagName === 'VIDEO' && !document.fullscreenElement.classList.contains('preload'))).toBe(true);
  await page.evaluate(() => document.exitFullscreen());
});

test('console cursor appends pages and resets on filter change', async ({ page }) => {
  await login(page);
  let pages = 0;
  await page.route('**/api/terminal-sessions?*', async route => {
    const u = new URL(route.request().url());
    expect(u.searchParams.get('page')).toBe('true');
    const base = u.searchParams.has('cursor') ? 50 : 0;
    pages++;
    const items = Array.from({length: 50}, (_, i) => ({ row: { id: base+i+100000, host: 'paged-host', sessionUser: 'user', kind:'terminal-input', input:'command '+(base+i), startedAt:new Date().toISOString(), endedAt:new Date().toISOString(), durationSec:1 }, outputs:[], live:false }));
    await route.fulfill({json:{items,nextCursor: base ? null : 'next'}});
  });
  await page.goto('/#/console');
  await expect(page.locator('tbody tr')).toHaveCount(50);
  await page.getByRole('button', {name:'Показать ещё',exact:true}).click();
  await expect(page.locator('tbody tr')).toHaveCount(100);
  await page.fill('input[placeholder="например, rm -rf"]', 'different');
  await expect(page.locator('tbody tr')).toHaveCount(50);
  expect(pages).toBe(3);
});

test('app classification edits survive catalog pagination and search', async ({ page }) => {
  await login(page);
  await page.route('**/api/settings/app-kinds/seen?*', async route => {
    const u = new URL(route.request().url());
    const second = u.searchParams.has('cursor');
    await route.fulfill({json:{items:[{process:second?'batch-second':'batch-first',count:5,hasSite:false}],nextCursor:second?null:'next',seenDays:30,asOf:new Date().toISOString()}});
  });
  await page.goto('/#/settings/apps');
  const row = page.locator('tbody tr').filter({has:page.locator('td', {hasText:'batch-first'})});
  await row.locator('select').selectOption('ignore');
  await page.getByRole('button',{name:'Показать ещё',exact:true}).click();
  await expect(page.locator('tbody')).toContainText('batch-second');
  await expect(row.locator('select')).toHaveValue('ignore');
  await page.fill('input[placeholder="фильтр по процессу"]', 'batch');
  await expect(row.locator('select')).toHaveValue('ignore');
  await expect(page.getByRole('button',{name:'Сохранить',exact:true})).toBeEnabled();
});

test('negotiated upload rejects invalid mp4 with typed response', async ({ request }) => {
  const response = await request.post('/api/upload', { headers:{Authorization:'Bearer e2e-agent-token','X-Watcher-Video-Validation':'1'},multipart:{video:{name:'negotiated-bad.mp4',mimeType:'video/mp4',buffer:Buffer.from('broken')},meta:{name:'meta.json',mimeType:'application/json',buffer:Buffer.from('{}')}}});
  expect(response.status()).toBe(415);
  expect((await response.json()).code).toBe('invalid-video');
});

test('viewer paging and host suggestions preserve group boundaries', async ({ page }) => {
  await login(page, F.viewer.username);
  const names = await page.evaluate(async () => (await fetch('/api/host-names?limit=100')).json());
  expect(names.sort()).toEqual([...F.groupHosts].sort());
  const sessions = await page.evaluate(async () => (await fetch('/api/terminal-sessions?page=true&sessions=true&host=buh-01')).json());
  expect(sessions.items).toEqual([]);
  for (const path of ['/api/settings/app-kinds/seen', '/api/transcode/issues', '/api/transcode/quarantine/expired']) {
    page.expectHttpError(403, path);
    expect(await page.evaluate(async p => (await fetch(p)).status, path)).toBe(403);
  }
});

test('expired app catalog can refresh without discarding edits', async ({ page }) => {
  await login(page);
  let expired = true;
  await page.route('**/api/settings/app-kinds/seen?*', async route => {
    const u = new URL(route.request().url());
    if (u.searchParams.has('cursor') && expired) {
      expired = false;
      await route.fulfill({status:409,json:{error:'list expired, refresh the page'}});
      return;
    }
    await route.fulfill({json:{items:[{process:'batch-expiring',count:1,hasSite:false}],nextCursor:'next',seenDays:30,asOf:new Date().toISOString()}});
  });
  await page.goto('/#/settings/apps');
  const row = page.locator('tbody tr', {hasText:'batch-expiring'});
  await row.locator('select').selectOption('ignore');
  page.expectHttpError(409, '/api/settings/app-kinds/seen');
  await page.getByRole('button',{name:'Показать ещё',exact:true}).click();
  await expect(page.locator('.error')).toContainText('список устарел');
  await page.getByRole('button',{name:'Обновить',exact:true}).click();
  await expect(page.locator('.error')).toHaveCount(0);
  await expect(row.locator('select')).toHaveValue('ignore');
  await expect(page.getByRole('button',{name:'Сохранить',exact:true})).toBeEnabled();
});

test('editing an override outside loaded catalog preserves explicit app kind', async ({ page }) => {
  await login(page);
  const previous = await page.evaluate(async () => (await fetch('/api/settings/app-kinds?seen=false')).json());
  const put = overrides => page.evaluate(async overrides => (await fetch('/api/settings/app-kinds', {method:'PUT',headers:{'Content-Type':'application/json','X-Requested-With':'XMLHttpRequest'},body:JSON.stringify({overrides})})).status, overrides);
  try {
    expect(await put({...previous.overrides,'batch-unknown-browser':'ignore'})).toBe(200);
    await page.route('**/api/settings/app-kinds/seen?*', route => route.fulfill({json:{items:[],nextCursor:null,seenDays:30,asOf:new Date().toISOString()}}));
    await page.goto('/#/settings/apps');
    await page.locator('tbody tr',{hasText:'batch-unknown-browser'}).locator('select').selectOption('app');
    await page.getByRole('button',{name:'Сохранить',exact:true}).click();
    await expect.poll(() => page.evaluate(async () => (await (await fetch('/api/settings/app-kinds?seen=false')).json()).overrides['batch-unknown-browser'])).toBe('app');
  } finally { await put(previous.overrides); }
});
