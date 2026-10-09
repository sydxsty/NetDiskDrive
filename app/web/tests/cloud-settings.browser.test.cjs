/* Offline UI contract: no account, disk, or external network is used. */
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const out=path.resolve(process.argv[2]),web=path.resolve(__dirname,'..');fs.mkdirSync(out,{recursive:true});
(async()=>{
 const checks=[],errors=[];
 const server=http.createServer((req,res)=>{
  const file=path.resolve(web,'.'+new URL(req.url,'http://localhost').pathname);
  if(!file.startsWith(web+path.sep)){res.writeHead(403);res.end();return;}
  fs.readFile(file,(e,b)=>{res.writeHead(e?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(e?'missing':b);});
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1320,height:1050},locale:'zh-CN'});
 await context.route('**/*',r=>new URL(r.request().url()).origin===origin?r.continue():r.abort());
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  const listeners=[],reply=m=>queueMicrotask(()=>listeners.forEach(f=>f({data:m})));
  const state={connected:true,driverAvailable:true,account:{accountId:'fake',displayName:'功能测试'},settings:{},network:{activeRequests:7,queuedRequests:9},disks:[],tasks:[]};
  window.__calls=[];window.__state=state;
  window.chrome={webview:{addEventListener:(name,f)=>{if(name==='message')listeners.push(f);},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args});let data={};
   if(method==='app.state')data=structuredClone(state);
   else if(method==='settings.save'){Object.assign(state.settings,args);data={ok:true};}
   else if(method==='cloud.list')data=[{id:'12345678-abcd-4321-abcd-123456789012',name:'视频库',encrypted:false,capacityBytes:1073741824,objectSizeBytes:16777216,updatedUtc:'2026-10-05T00:00:00Z'}];
   else if(method==='cloud.import')data={taskId:'fake-import'};
   else if(method==='disks.create'){state.settings.defaultObjectSizeBytes=args.objectSizeBytes;data={ok:true};}
   else if(method==='tasks.logs')data={items:[],hasMore:false};
   else {reply({requestId,ok:false,error:'Unexpected RPC '+method});return;}
   reply({requestId,ok:true,data});
  }}};
 });
 const visit=async name=>{await page.locator('nav [data-page="'+name+'"]').click();await page.waitForFunction(n=>document.querySelector('#content').dataset.page===n,name);};
 try{
  await page.goto(origin+'/index.html');await visit('settings');
  assert.equal(await page.locator('#syncInterval').inputValue(),'3600');assert.equal(await page.locator('#concurrency').inputValue(),'4');
  assert.equal(await page.locator('#baiduRequestRate').inputValue(),'3');assert.equal(await page.locator('#baiduConcurrency').inputValue(),'4');
  assert.equal(await page.locator('#syncPreparationCacheMiB').inputValue(),'64');
  assert.equal(await page.locator('#syncOnExit').isChecked(),false);assert.doesNotMatch(await page.locator('#content').innerText(),/请求进行中|个排队/);
  await page.evaluate(async()=>{Object.assign(window.__state.settings,{syncIntervalSeconds:75,syncPreparationCacheMiB:128,maxParallelTransfers:2,baiduRequestsPerSecond:2,baiduMaximumConcurrentRequests:2,syncOnExit:true});await refresh();});
  assert.equal(await page.locator('#syncInterval').inputValue(),'75');assert.equal(await page.locator('#concurrency').inputValue(),'2');
  assert.equal(await page.locator('#baiduRequestRate').inputValue(),'2');assert.equal(await page.locator('#baiduConcurrency').inputValue(),'2');assert.equal(await page.locator('#syncOnExit').isChecked(),true);
  assert.equal(await page.locator('#syncPreparationCacheMiB').inputValue(),'128');
  checks.push('Missing preferences use 3600-second/4-object/3-rps/4-request/exit-sync-off defaults; saved custom choices remain visible and settings contain no live request monitor.');
  assert.match(await page.locator('#syncPreparationCacheMiB').locator('..').innerText(),/每个正在整理的磁盘分别占用/);
  assert.match(await page.locator('#syncPreparationCacheMiB').locator('..').innerText(),/与本地磁盘容量上限无关/);
  for(const value of [16,1024]){
   await page.locator('#syncPreparationCacheMiB').fill(String(value));await page.locator('[data-action="saveSettings"]').click();
   await page.waitForFunction(value=>window.__state.settings.syncPreparationCacheMiB===value,value);
   const saved=await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').at(-1).args);
   assert.equal(saved.syncPreparationCacheMiB,value);assert.equal(saved.maxParallelTransfers,2);assert.equal(saved.baiduMaximumConcurrentRequests,2);
   await visit('cloud');await visit('settings');assert.equal(await page.locator('#syncPreparationCacheMiB').inputValue(),String(value));
  }
  checks.push('Preparation cache defaults to 64 MiB per disk, honors a stored custom value, and persists the valid 16/1024 MiB boundaries without changing transfer concurrency.');
  const cacheSaves=await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length);
  for(const value of ['', '15','1025','64.5']){
   await page.locator('#syncPreparationCacheMiB').fill(value);await page.locator('[data-action="saveSettings"]').click();
   assert.equal(await page.locator('#syncPreparationCacheMiB').evaluate(node=>node.validity.valid),false);
   assert.equal(await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length),cacheSaves);
   assert.equal(await page.evaluate(()=>window.__state.settings.syncPreparationCacheMiB),1024);
  }
  await page.locator('#syncPreparationCacheMiB').fill('64');
  checks.push('Empty, below-range, above-range and fractional preparation cache values are rejected before settings.save; the saved limit remains unchanged.');
  await page.locator('#baiduRequestRate').fill('0.5');await page.locator('#baiduConcurrency').selectOption('1');
  await page.locator('[data-action="saveSettings"]').click();await page.waitForFunction(()=>window.__state.settings.baiduRequestsPerSecond===0.5);
  await visit('cloud');await visit('settings');
  assert.equal(await page.locator('#baiduRequestRate').inputValue(),'0.5');assert.equal(await page.locator('#baiduConcurrency').inputValue(),'1');
  checks.push('Global rate and concurrency are sent to settings.save and survive page refresh.');
  assert.equal(await page.locator('#syncOnExit').isChecked(),true);
  assert.doesNotMatch(await page.locator('#content').innerText(),/NTFS 访问时间|后台整理/);
  await page.locator('#syncOnExit').uncheck();await page.locator('[data-action="saveSettings"]').click();
  await page.waitForFunction(()=>window.__state.settings.syncOnExit===false);
  await visit('cloud');await visit('settings');
  assert.equal(await page.locator('#syncOnExit').isChecked(),false);
  checks.push('Exit sync is optional, sends false explicitly and persists on refresh; obsolete access-time and background-maintenance settings are removed.');
  await page.locator('#prefetchStrategy').selectOption('adaptive');await page.locator('#prefetchCount').fill('8');
  await page.locator('[data-action="saveSettings"]').click();await page.waitForFunction(()=>window.__state.settings.prefetch?.objectCount===8);
  await visit('cloud');await visit('settings');
  assert.equal(await page.locator('#prefetchStrategy').inputValue(),'adaptive');assert.equal(await page.locator('#prefetchCount').inputValue(),'8');
  await page.locator('#prefetchStrategy').selectOption('disabled');await page.locator('[data-action="saveSettings"]').click();
  await page.waitForFunction(()=>window.__state.settings.prefetch?.strategy==='disabled');
  checks.push('Prefetch strategy, count and disable choice persist through settings.save and a fresh settings page.');
  const countSaves=await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length);
  await page.locator('#prefetchCount').fill('17');await page.locator('[data-action="saveSettings"]').click();
  assert.equal(await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length),countSaves);await page.locator('#prefetchCount').fill('8');
  checks.push('Prefetch count above 16 is rejected before sending settings.');
  for(const size of [4,8,16]){
   await page.locator('header [data-action="create"]').click();
   assert.deepEqual(await page.locator('#objectSize option').evaluateAll(nodes=>nodes.map(n=>Number(n.value))),[4194304,8388608,16777216]);
   await page.locator('#objectSize').selectOption(String(size*1048576));assert.equal(await page.locator('#encrypted,#password').count(),0);
   await page.evaluate(()=>{document.querySelector('#path').value='C:\\IsolatedFixture\\new-size.odv4';});
   await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
   const created=await page.evaluate(()=>window.__calls.filter(c=>c.method==='disks.create').at(-1));assert.equal(created.args.objectSizeBytes,size*1048576);
  }
  await page.locator('header [data-action="create"]').click();assert.equal(await page.locator('#objectSize').inputValue(),String(16*1048576));
  await page.locator('#dialog [data-action="closeDialog"]').first().click();
  checks.push('New disks expose 4/8/16 MiB, send the chosen size, and remember the last creation choice.');
  await page.evaluate(async()=>{
   window.__state.disks=[{id:'compression-view',name:'压缩视图',objectSizeBytes:16777216,driveLetter:'Z',capacityBytes:1073741824,unlocked:true,mounted:false,sync:{enabled:true,state:'uploading',pendingBytes:16777216,estimated:true,completedBytes:16777216,totalBytes:33554432,uploadedBytes:4096,logicalUploadedBytes:16777216,reusedBytes:8388608}}];
   window.__state.tasks=[{id:'compressed-task',diskId:'compression-view',kind:'sync',title:'压缩同步',state:'uploading',completedBytes:16777216,totalBytes:33554432,uploadedBytes:4096,logicalUploadedBytes:16777216,reusedBytes:8388608,canPause:true}];await refresh();
  });
  await visit('cloud');assert.match(await page.locator('.panel .facts').innerText(),/压缩后上传 4.0 KiB/);
  assert.match(await page.locator('.panel .facts').innerText(),/原对象 16.0 MiB/);
  assert.match(await page.locator('.panel .facts').innerText(),/待处理（压缩前）/);
  assert.match(await page.locator('tbody').innerText(),/16.0 MiB/);
  await visit('tasks');assert.match(await page.locator('.task-work-count').innerText(),/压缩后上传 4.0 KiB/);
  assert.equal(await page.locator('[data-task="compressed-task"] .progress').getAttribute('aria-label'),'进度 50%');
  checks.push('Compressed wire bytes are separate from canonical object progress and raw pending estimates in cloud and task views.');
  await page.evaluate(async()=>{window.__state.disks=[];window.__state.tasks=[];await refresh();});await visit('settings');


  const saves=await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length);
  await page.locator('#baiduRequestRate').fill('0');await page.locator('[data-action="saveSettings"]').click();
  assert.equal(await page.evaluate(()=>window.__calls.filter(c=>c.method==='settings.save').length),saves);
  checks.push('Invalid zero rate is rejected before RPC.');
  await page.locator('#baiduRequestRate').fill('0.5');await page.locator('#pageTitle').click();
  await page.screenshot({path:path.join(out,'global-limits.png'),fullPage:true});
  await visit('cloud');await page.locator('[data-action="cloudImport"]').click();
  assert.equal(await page.locator('#lazyImport').count(),0);
  assert.match(await page.locator('#dialogBody').innerText(),/索引和文件内容均按需加载/);
  assert.match(await page.locator('#importModeDescription').innerText(),/默认关闭云同步/);
  await page.evaluate(()=>{document.querySelector('#restorePath').value='C:\\IsolatedFixture\\copy.odv4';});
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  const imported=await page.evaluate(()=>window.__calls.filter(c=>c.method==='cloud.import').at(-1));
  assert.equal(imported.args.lazy,true);assert.equal(imported.args.mode,'copy');assert.equal(imported.args.originalConfirmed,false);
  checks.push('Cloud copy import always loads index and content lazily and explicitly describes its disabled-by-default cloud sync.');
  await visit('cloud');await page.locator('[data-action="cloudImport"]').click();
  assert.equal(await page.locator('#importMode').inputValue(),'copy');
  await page.locator('#importMode').selectOption('original');
  assert.equal(await page.locator('#restoreName').inputValue(),'视频库');
  assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
  await page.locator('#originalConfirmed').check();assert.equal(await page.locator('#dialogSubmit').isEnabled(),true);
  await page.evaluate(()=>{document.querySelector('#restorePath').value='C:\\IsolatedFixture\\original.odv4';});
  await page.screenshot({path:path.join(out,'original-import.png'),fullPage:true});
  await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  const original=await page.evaluate(()=>window.__calls.filter(c=>c.method==='cloud.import').at(-1));
  assert.equal(original.args.mode,'original');assert.equal(original.args.originalConfirmed,true);assert.equal(original.args.lazy,true);
  checks.push('Original restore is explicit, requires absence-of-other-writers confirmation, preserves the suggested name and sends mode separately from lazy loading.');

  await page.evaluate(async()=>{window.__state.disks=[{id:'12345678-abcd-4321-abcd-123456789012',name:'已在本机',driveLetter:'Z',containerPath:'C:\\IsolatedFixture\\old.odv4',capacityBytes:1073741824,mounted:false,unlocked:false}];await refresh();});
  await visit('cloud');await page.locator('[data-action="cloudImport"]').click();
  await page.locator('#importMode').selectOption('original');await page.locator('#originalConfirmed').check();
  assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
  assert.match(await page.locator('#importModeDescription').innerText(),/移除旧记录/);
  await page.locator('#importMode').selectOption('copy');assert.equal(await page.locator('#dialogSubmit').isEnabled(),true);
  await page.locator('#dialog [data-action="closeDialog"]').first().click();
  checks.push('Known local identity blocks original recovery even when closed, while independent-copy import remains available.');

  await page.evaluate(async()=>{window.__state.tasks=[{id:'index-job',diskId:'12345678-abcd-4321-abcd-123456789012',kind:'restore',title:'恢复原硬盘',state:'indexing',message:'正在接入云端索引',completedBytes:4194304,totalBytes:4194304,completedPages:64,totalPages:128,completedIndexNodes:6,canPause:true}];await refresh();});
  await visit('tasks');assert.match(await page.locator('.task-work-count').innerText(),/64 \/ 128 个页映射/);
  assert.equal(await page.locator('[data-task="index-job"] .progress').getAttribute('aria-label'),'进度 50%');
  assert.match(await page.locator('.task-work-count').innerText(),/6 个索引节点/);
  await page.screenshot({path:path.join(out,'index-progress.png'),fullPage:true});
  checks.push('Index validation has its own page/node progress and does not display completed download bytes as completed index work.');
  await page.evaluate(async()=>{Object.assign(window.__state.tasks[0],{state:'verifying',message:'下载已完成，正在完成本地内容校验',completedPages:128});await refresh();});
  assert.match(await page.locator('[data-task="index-job"]').innerText(),/校验本地内容/);
  assert.equal(await page.locator('[data-task="index-job"] .progress').count(),0);
  checks.push('Local content verification after full download remains visibly active without a misleading completed progress bar.');

  await page.evaluate(async()=>{
   window.__state.disks=[{id:'lazy-fixture',name:'按需测试盘',driveLetter:'Z',containerPath:'C:\\IsolatedFixture\\cached.odv4',capacityBytes:1073741824,mounted:true,unlocked:true,lazySource:true,lazy:{enabled:true,cached_objects:3,missing_objects:7},hydration:{activeRequests:1,pendingRequests:2,prefetching:true,lastError:'<missing> 重试后可继续'},sync:{enabled:false}}];
   await refresh();
  });
  await visit('disks');
  assert.match(await page.locator('.lazy-cache-status').innerText(),/已缓存 3 块 · 待按需下载 7 块/);
  assert.match(await page.locator('.lazy-cache-status').innerText(),/1 个读取中 \/ 2 个等待中/);
  assert.match(await page.locator('.lazy-cache-status').innerText(),/<missing> 重试后可继续/);
  assert.equal(await page.locator('.lazy-cache-status missing').count(),0);
  checks.push('Disk card shows native cache and queue counts and escapes download errors.');
  await page.evaluate(async()=>{
   Object.assign(window.__state.disks[0].hydration,{metadataDownloadedBytes:8388608,dataDownloadedBytes:16777216});await refresh();
  });
  assert.match(await page.locator('.lazy-cache-status').innerText(),/本次打开已下载（解压后）：容器索引 8\.0 MiB · 数据 16\.0 MiB/);
  checks.push('Lazy cache displays separate index and data download totals and explicitly labels canonical decompressed bytes.');
  await page.screenshot({path:path.join(out,'lazy-cache.png'),fullPage:true});
  assert.deepEqual(errors,[]);
  fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks},null,2));console.log('PASS cloud settings UI: '+checks.length+' groups');
 }catch(e){fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:false,error:e.stack,checks,errors},null,2));throw e;}
 finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
