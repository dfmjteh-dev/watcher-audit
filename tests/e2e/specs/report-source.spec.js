const {test,expect,login,F}=require('./helpers');
const from=new Date(Date.now()-86400000).toISOString().slice(0,10),to=new Date().toISOString().slice(0,10);

test('Отчёты: пользователь, программа, источники и возврат сохраняют период и фильтры',async({page})=>{
  await login(page);
  await page.goto('/#/reports?t=users&from='+from+'&to='+to+'&workHours=false');
  const users=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Самые активные пользователи',exact:true})});
  await users.getByRole('link',{name:'ivan',exact:true}).first().click();
  await expect(page).toHaveURL(/host=/);await expect(page).toHaveURL(/user=ivan/);await expect(page).toHaveURL(new RegExp('from='+from));
  const apps=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Самые используемые приложения',exact:true})});
  await apps.getByRole('link',{name:'Показать детали',exact:true}).first().click();
  await expect(page).toHaveURL(/t=source/);await expect(page).toHaveURL(/process=/);
  const sources=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Исходные интервалы',exact:true})});
  await expect(sources.locator('tbody tr').first()).toBeVisible();
  await expect(sources.getByRole('link',{name:'Открыть запись',exact:true}).first()).toHaveAttribute('href',/#\/hosts\/.*\/video\?t=/);
  await expect(sources).toContainText('Полнота не измерена');
  await page.reload();await expect(sources.locator('tbody tr').first()).toBeVisible();
  await page.goBack();await expect(page).toHaveURL(/t=apps/);await expect(page).toHaveURL(/user=ivan/);
});

test('Отчёты: прямые источники соблюдают группы viewer и роль operator',async({page})=>{
  await login(page,F.viewer.username);
  const period='?from='+from+'&to='+to+'&kind=records';
  expect((await page.request.get('/api/reports/source'+period+'&host=buh-01')).status()).toBe(403);
  const allowed=await page.request.get('/api/reports/source'+period);expect(allowed.ok()).toBeTruthy();
  const {items}=await allowed.json();expect(items.every(r=>r.host!=='buh-01')).toBeTruthy();
  await login(page,F.operator.username);expect((await page.request.get('/api/reports/source'+period)).status()).toBe(403);
});

test('Отчёт открывает заданный сегмент, а отсутствующий не заменяет соседним',async({page})=>{
  await login(page);
  const rows=await(await page.request.get('/api/segments?host=buh-01')).json();
  const ordered=rows.sort((a,b)=>a.startedAt.localeCompare(b.startedAt));
  await page.goto('/#/hosts/buh-01/video?t='+encodeURIComponent(ordered[0].startedAt)+'&segment='+ordered[2].id+'&offset=0.5');
  await expect(page.locator('.player video:not(.preload)')).toHaveAttribute('src',new RegExp('^/api/segments/'+ordered[2].id+'/video'));
  await page.goto('/#/hosts/buh-01/video?t='+encodeURIComponent(ordered[0].startedAt)+'&segment=999999999&offset=0');
  await expect(page.getByRole('status')).toContainText('Исходная запись недоступна или удалена');
});
