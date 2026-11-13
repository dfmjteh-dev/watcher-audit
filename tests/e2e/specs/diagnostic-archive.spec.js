const {test,expect,login,F}=require('./helpers');

test('Очищенный архив: запрос администратора, готовность и скачивание ZIP',async({page,request})=>{
  await login(page);
  try {
  const tokenResponse=await page.request.post('/api/agents/enroll-tokens',{data:{name:'archive-ui',ttlHours:1},headers:{'X-Requested-With':'XMLHttpRequest'}});
  const {token}=await tokenResponse.json();const enrolled=await request.post('/api/enroll',{data:{token,host:'archive-ui',kind:'video'}});
  const {secret}=await enrolled.json();const headers={Authorization:'Bearer '+secret};
  await page.goto('/#/hosts/archive-ui/info');
  const panel=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Диагностика агента',exact:true})});
  await panel.getByRole('button',{name:'Запросить очищенный архив',exact:true}).click();
  await expect(panel).toContainText('Ожидаем архив до');
  const polled=await request.post('/api/agent-diagnostics',{headers,data:{events:[]}});const {archiveRequest}=await polled.json();expect(archiveRequest).toBeTruthy();
  const sent=await request.post('/api/agent-diagnostics/archive',{headers,data:{requestId:archiveRequest.id,collectedAt:new Date().toISOString(),version:'0.15.1',platform:'windows',events:[],updates:[],logs:[{source:'msi',lines:1,warnings:0,errors:0,truncated:false,installerCodes:[1603]}]}});expect(sent.ok()).toBeTruthy();
  await panel.getByRole('button',{name:'Обновить',exact:true}).click();
  await expect(panel).toContainText('Архив получен:');
  const download=page.waitForEvent('download');await panel.getByRole('link',{name:'Скачать очищенный архив',exact:true}).click();
  const archive=await download;expect(archive.suggestedFilename()).toBe(`watcher-diagnostics-${archiveRequest.id}.zip`);
  } finally {
    const response=await page.request.get('/api/inventory/archive-ui');
    if(response.ok()) {
      const {host}=await response.json();
      const removed=await page.request.delete('/api/fleet/'+host.id,{headers:{'X-Requested-With':'XMLHttpRequest'}});
      expect(removed.ok()).toBeTruthy();
    }
  }

});

for(const role of ['operator','viewer'])test(`${role}: архивы недоступны через UI и прямой API`,async({page})=>{
  await login(page,F[role].username);await page.goto('/#/hosts/kassa-01/info');
  await expect(page.getByRole('button',{name:'Запросить очищенный архив',exact:true})).toHaveCount(0);
  const url='/api/hosts/kassa-01/diagnostics/archives';
  expect((await page.request.get(url)).status()).toBe(403);
  expect((await page.request.post(url,{headers:{'X-Requested-With':'XMLHttpRequest'}})).status()).toBe(403);
  expect((await page.request.get(url+'/00000000000000000000000000000000')).status()).toBe(403);
});
