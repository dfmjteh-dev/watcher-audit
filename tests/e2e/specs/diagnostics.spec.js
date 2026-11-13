const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { test, expect, login, F } = require('./helpers');

async function agent(page, request, host) {
  const tokenResponse = await page.request.post('/api/agents/enroll-tokens', {data:{name:'diagnostics-e2e',ttlHours:1},headers:{'X-Requested-With':'XMLHttpRequest'}});
  expect(tokenResponse.ok()).toBeTruthy();
  const {token}=await tokenResponse.json();
  const enrolled=await request.post('/api/enroll',{data:{token,host,kind:'video'}});
  expect(enrolled.ok()).toBeTruthy();
  const {secret}=await enrolled.json();
  return {Authorization:'Bearer '+secret};
}

test('Диагностика: безопасные коды, пакет по запросу, выдача подписанного обслуживания', async ({page,request})=>{
  await login(page);
  const headers=await agent(page,request,'buh-01');
  const key=crypto.createPublicKey(fs.readFileSync(path.join(__dirname,'../.maintenance-key.pem')));
  const keyId=crypto.createHash('sha256').update(key.export({type:'spki',format:'der'})).digest('hex');
  const event={id:crypto.randomUUID().replaceAll('-',''),at:new Date().toISOString(),level:'error',component:'uploader',code:'upload-failed'};
  const sent=await request.post('/api/agent-diagnostics',{headers,data:{events:[event],maintenanceKeyId:keyId,message:'PRIVATE-SECRET-MUST-NOT-APPEAR'}});
  expect(sent.ok()).toBeTruthy();
  await page.goto('/#/hosts/buh-01/info');
  const diagnostics=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Диагностика агента',exact:true})});
  await expect(diagnostics).toContainText('Ошибка выгрузки');
  await expect(diagnostics).not.toContainText('PRIVATE-SECRET-MUST-NOT-APPEAR');
  await diagnostics.getByRole('button',{name:'Запросить диагностический пакет'}).click();
  const states=await (await page.request.get('/api/hosts/buh-01/diagnostics/state')).json();
  const state=states.find(s=>s.maintenanceKeyId===keyId);
  expect(state.requestId).toMatch(/^[a-f0-9]{32}$/);
  // Сервер намеренно ограничивает приём одной учётки раз в пять секунд.
  await new Promise(resolve=>setTimeout(resolve,5100));
  const snapshot={requestId:state.requestId,at:new Date().toISOString(),version:F.agentVersion,platform:'windows',queueBytes:4096,queueFiles:2,memoryBytes:1000000,processes:3,freeBytes:100000000};
  const reported=await request.post('/api/agent-diagnostics',{headers,data:{events:[],snapshot,maintenanceKeyId:keyId}});
  expect(reported.ok()).toBeTruthy();
  await diagnostics.getByRole('button',{name:'Обновить',exact:true}).click();
  await expect(diagnostics.getByText(/Диагностический пакет от/)).toBeVisible();
  await diagnostics.locator('summary').click();
  await expect(diagnostics).toContainText(F.agentVersion);
  // 0.16, п.7: действие — кнопкой с пояснением, учётка выбирается сама (выбор — в «Дополнительно»).
  const maintenance=page.locator('.panel').filter({has:page.getByRole('heading',{name:'Обслуживание защищённого агента',exact:true})});
  await maintenance.getByText('Дополнительно',{exact:true}).click();
  await maintenance.getByLabel('Учётка агента',{exact:true}).selectOption(String(state.credentialId));
  await expect(maintenance).toContainText('Проверит файлы, конфиги и службу');
  const response=page.waitForResponse(r=>r.url().endsWith('/maintenance')&&r.request().method()==='POST');
  await maintenance.getByRole('button',{name:'Восстановить',exact:true}).click();
  const signed=await (await response).json();
  expect(signed.grant.action).toBe('repair');expect(signed.keyId).toBe(keyId);
  const message=Buffer.from('Watcher.AgentMaintenance.v1\n'+JSON.stringify(signed.grant));
  expect(crypto.verify('sha256',message,key,Buffer.from(signed.signature,'base64'))).toBeTruthy();
  // Онлайн-разрешение не скачивается; ход — лентой шагов.
  await expect(maintenance.getByRole('button',{name:'Скачать файл разрешения'})).toBeHidden();
  await expect(maintenance).toContainText('Команда «Восстановление агента» отправлена');
  await expect(maintenance).toContainText('Ждёт, когда агент выйдет на связь');
  // Удаление — только после подтверждения.
  await maintenance.getByRole('button',{name:'Удалить…'}).click();
  await expect(maintenance.getByRole('button',{name:'Да, удалить'})).toBeVisible();
  await maintenance.getByRole('button',{name:'Отмена'}).click();
  // Станция без связи: файл разрешения и готовая команда GuardRepair submit.
  await maintenance.getByText('Станция без связи',{exact:true}).click();
  const downloaded=page.waitForEvent('download');
  await maintenance.getByRole('button',{name:'Скачать файл разрешения'}).click();
  const file=await downloaded;
  expect(file.suggestedFilename()).toMatch(/^watcher-maintenance-[a-f0-9]{32}\.json$/);
  await expect(maintenance.locator('.cmd pre')).toContainText('GuardRepair');
  await expect(maintenance.locator('.cmd pre')).toContainText('submit');
  await expect(maintenance.locator('.cmd pre')).toContainText(file.suggestedFilename());
  await expect(maintenance).toContainText('Файл разрешения выдан');
});

test('Оператор видит диагностику, но не выдаёт разрешения и не запрашивает пакет',async({page})=>{
  await login(page,F.operator.username);await page.goto('/#/hosts/kassa-01/info');
  await expect(page.getByRole('heading',{name:'Диагностика агента',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Запросить диагностический пакет'})).toHaveCount(0);
  await expect(page.getByRole('heading',{name:'Обслуживание защищённого агента',exact:true})).toHaveCount(0);
  const denied=await page.request.post('/api/hosts/kassa-01/diagnostics/request',{headers:{'X-Requested-With':'XMLHttpRequest'}});expect(denied.status()).toBe(403);
  const maintenance=await page.request.get('/api/hosts/kassa-01/maintenance');expect(maintenance.status()).toBe(403);
});

test('Viewer: диагностика ограничена назначенными группами',async({page})=>{
  await login(page,F.viewer.username);
  expect((await page.request.get('/api/hosts/kassa-01/diagnostics')).ok()).toBeTruthy();
  expect((await page.request.get('/api/hosts/buh-01/diagnostics')).status()).toBe(403);
  expect((await page.request.get('/api/hosts/buh-01/diagnostics/state')).status()).toBe(403);
});
