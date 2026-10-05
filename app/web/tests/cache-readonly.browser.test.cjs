/* Local browser contract tests; all host calls are mocked and external requests are blocked. */
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const output=path.resolve(process.argv[2]),web=path.resolve(__dirname,'..');fs.mkdirSync(output,{recursive:true});
(async()=>{
 const checks=[],errors=[];
 const server=http.createServer((req,res)=>{const file=path.resolve(web,'.'+new URL(req.url,'http://localhost').pathname);if(!file.startsWith(web+path.sep)){res.writeHead(403);res.end();return;}fs.readFile(file,(e,b)=>{res.writeHead(e?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(e?'missing':b);});});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1380,height:1080},locale:'zh-CN'});
 await context.route('**/*',r=>new URL(r.request().url()).origin===origin?r.continue():r.abort());
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  const listeners=[],reply=m=>queueMicrotask(()=>listeners.forEach(f=>f({data:m})));
  const state={connected:true,driverAvailable:true,account:{accountId:'fixture',displayName:'隔离测试'},settings:{},tasks:[],disks:[{id:'cache-disk',name:'视频库',driveLetter:'Z',containerPath:'C:\\IsolatedFixture\\disk.odv4',capacityBytes:4*1024**4,encrypted:true,unlocked:false,mounted:false,readOnly:false,localCache:{limitBytes:0,policy:'lru'},sync:{enabled:false},cache:{source_ready:false,allocated_bytes:2*1024**3,missing_objects:0}}]};
  window.__state=state;window.__calls=[];
  window.chrome={webview:{addEventListener:(name,f)=>{if(name==='message')listeners.push(f);},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args:structuredClone(args)});const disk=state.disks[0];let data={ok:true};
   if(method==='app.state')data=structuredClone(state);
   else if(method==='cloud.list')data=[];
   else if(method==='tasks.logs')data={items:[],hasMore:false};
   else if(method==='disks.mount'){disk.readOnly=args.readOnly;disk.mounted=true;disk.unlocked=true;}
   else if(method==='disks.unmount'){disk.mounted=false;disk.unlocked=false;}
   else if(method==='disks.unlock')disk.unlocked=true;
   else if(method==='disks.settings')Object.assign(disk,{name:args.name,driveLetter:args.driveLetter,readOnly:args.readOnly});
   else if(method==='cache.settings')disk.localCache={limitBytes:args.limitBytes,policy:args.policy};
   else if(method==='sync.enable'){disk.sync={enabled:true,state:'pending'};disk.localCache=args.localCache;}
   else if(method==='disks.create'){}
   else if(method==='blocks.summary')data={deep_compaction_estimated_bytes:16777216};
   else if(method==='compact.start'||method==='reclaim.start'){}
   else {reply({requestId,ok:false,error:'Unexpected RPC '+method});return;}
   reply({requestId,ok:true,data});
  }}};
 });
 const visit=async name=>{await page.locator('nav [data-page="'+name+'"]').click();await page.waitForFunction(n=>document.querySelector('#content').dataset.page===n,name);};
 const last=method=>page.evaluate(m=>window.__calls.filter(c=>c.method===m).at(-1),method);
 const close=async()=>{await page.locator('#dialog [data-action="closeDialog"]').first().click();};
 try{
  await page.goto(origin+'/index.html');await page.locator('[data-action="mount"]').waitFor();
  await page.locator('[data-action="mount"]').click();assert.equal(await page.locator('#mountReadOnly').inputValue(),'readwrite');
  await page.locator('#mountReadOnly').selectOption('readonly');await page.locator('#password').fill('isolated-password');
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.deepEqual((await last('disks.mount')).args,{id:'cache-disk',readOnly:true,password:'isolated-password'});
  assert.match(await page.locator('[data-disk="cache-disk"] .muted').innerText(),/只读/);
  checks.push('Every mount exposes read-only/read-write, sends the chosen mode with unlock, and displays the active mode.');

  await page.locator('[data-action="diskSettings"]').click();assert.equal(await page.locator('#diskReadOnly').isDisabled(),true);assert.equal(await page.locator('#diskName').isDisabled(),true);
  await page.locator('#cacheLimited').check();await page.locator('#cacheLimit').fill('1');await page.locator('#cachePolicy').selectOption('lfu');
  await page.screenshot({path:path.join(output,'mounted-cache-settings.png'),fullPage:true});
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.deepEqual((await last('cache.settings')).args,{id:'cache-disk',limitBytes:1073741824,policy:'lfu'});
  assert.equal(await page.evaluate(()=>window.__calls.filter(c=>c.method==='disks.settings').length),0);
  checks.push('Mounted disks allow cache-limit/strategy changes without changing mounted name, letter or write mode.');

  await page.locator('[data-action="diskSettings"]').click();assert.equal(await page.locator('#cacheLimit').inputValue(),'1');assert.equal(await page.locator('#cachePolicy').inputValue(),'lfu');
  await page.locator('#cacheLimit').fill('0');const prior=(await last('cache.settings')).args;await page.locator('#dialogSubmit').click();
  assert.deepEqual((await last('cache.settings')).args,prior);await page.locator('#cacheLimited').uncheck();
  assert.equal(await page.locator('#cacheLimit').isDisabled(),true);await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.equal((await last('cache.settings')).args.limitBytes,0);
  checks.push('Settings reopen with saved values; an invalid enabled limit sends no RPC, while disabling the limit explicitly sends zero.');

  await page.locator('[data-action="unmount"]').click();await page.locator('[data-action="mount"]').waitFor();await page.locator('[data-action="mount"]').click();
  assert.equal(await page.locator('#mountReadOnly').inputValue(),'readonly');await close();
  await page.locator('[data-action="diskSettings"]').click();await page.locator('#diskReadOnly').selectOption('readwrite');
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});assert.equal((await last('disks.settings')).args.readOnly,false);
  checks.push('The selected mode persists into the next mount and can be changed after safely unmounting.');

  await page.evaluate(async()=>{window.__state.disks[0].unlocked=true;await refresh();});await visit('cloud');await page.locator('[data-action="syncEnable"]').click();
  await page.locator('#cacheLimited').check();await page.locator('#cacheLimit').fill('256');await page.locator('#cachePolicy').selectOption('sequential');
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.deepEqual((await last('sync.enable')).args,{id:'cache-disk',localCache:{limitBytes:256*1024**3,policy:'sequential'}});
  checks.push('Enabling cloud sync includes a per-disk capacity budget and the selected workload policy.');

  await page.evaluate(async()=>{Object.assign(window.__state.disks[0],{localCache:{limitBytes:1024**3,policy:'lru'},cache:{source_ready:true,allocated_bytes:2*1024**3,over_limit_bytes:1024**3,missing_objects:11},lazy:{enabled:true,cached_objects:4,missing_objects:11}});await refresh();});await visit('disks');
  assert.match(await page.locator('.lazy-cache-status').innerText(),/暂超上限/);assert.match(await page.locator('.lazy-cache-status').innerText(),/未同步内容、索引/);assert.match(await page.locator('.lazy-cache-status').innerText(),/待按需下载 11 块/);
  checks.push('Physical cache usage, soft-limit excess and remaining demand-loaded blocks are shown separately from virtual capacity.');

  await page.evaluate(()=>createDisk());await page.locator('#capacity').fill('4096');await page.locator('#encrypted').uncheck();
  await page.locator('#createReadOnly').selectOption('readonly');await page.evaluate(()=>document.querySelector('#path').value='C:\\IsolatedFixture\\large.odv4');
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  const created=(await last('disks.create')).args;assert.equal(created.capacityBytes,4*1024**4);assert.equal(created.readOnly,true);
  checks.push('Creation supports a 4 TiB virtual capacity and an initial read-only mount choice.');
  assert.deepEqual(errors,[]);fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks},null,2));console.log('PASS cache/read-only UI: '+checks.length+' groups');
 }finally{await context.close();await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
