/* Manual replica UI contract. All disk/cloud responses are isolated fixtures; external requests are blocked. */
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const output=path.resolve(process.argv[2]),web=path.resolve(__dirname,'..');fs.mkdirSync(output,{recursive:true});
(async()=>{
 const checks=[],errors=[];
 const server=http.createServer((req,res)=>{const file=path.resolve(web,'.'+new URL(req.url,'http://localhost').pathname);if(!file.startsWith(web+path.sep)){res.writeHead(403);res.end();return;}fs.readFile(file,(error,body)=>{res.writeHead(error?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(error?'missing':body);});});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1360,height:1050},locale:'zh-CN'});
 await context.route('**/*',route=>new URL(route.request().url()).origin===origin?route.continue():route.abort());
 const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>{
  const listeners=[],reply=message=>queueMicrotask(()=>listeners.forEach(callback=>callback({data:message})));
  const state={connected:true,driverAvailable:true,account:{accountId:'isolated',displayName:'隔离测试'},settings:{},tasks:[],disks:[
   {id:'replica',name:'资料副本',driveLetter:'Z',containerPath:'C:\\IsolatedFixture\\copy.odv4',capacityBytes:4*1024**4,objectSizeBytes:8388608,encrypted:true,mounted:true,unlocked:true,readOnly:true,sync:{enabled:false},lazy:{enabled:true,cached_objects:4,missing_objects:2,index_complete:false},replica:{available:true,sourceGeneration:12,hasLocalChanges:false,indexComplete:false,state:'idle',verifiedScope:'可使用；已加载对象已验证，未访问部分按需验证'}},
   {id:'local',name:'本地盘',driveLetter:'Y',containerPath:'C:\\IsolatedFixture\\local.odv4',capacityBytes:1024**3,mounted:false,unlocked:true,sync:{enabled:false}}
  ]};
  window.__state=state;window.__calls=[];window.__logs=[];window.__nextCandidate={generation:13,expectedRevision:'9007199254740993',localChanges:false,unchanged:false};window.__delayPrepare=false;window.__delayApply=false;window.__applyError=null;
  window.chrome={webview:{addEventListener:(name,callback)=>{if(name==='message')listeners.push(callback);},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args:structuredClone(args)});const disk=state.disks.find(d=>d.id===args.id)||state.disks[0];let data={ok:true};
   if(method==='app.state'){if(window.__failState){window.__failState=false;reply({requestId,ok:false,error:'模拟界面状态读取失败'});return;}data=structuredClone(state);}
   else if(method==='cloud.list')data=[];
   else if(method==='tasks.logs')data={items:window.__logs,hasMore:false};
   else if(method==='snapshots.list')data={items:[]};
   else if(method==='disks.unmount'){disk.mounted=false;disk.unlocked=false;}
   else if(method==='disks.unlock')disk.unlocked=true;
   else if(method==='replica.prepare'){
    if(disk.mounted){reply({requestId,ok:false,error:'请先卸载'});return;}
    if(window.__prepareError){disk.replica.state='error';disk.replica.message=window.__prepareError;reply({requestId,ok:false,error:window.__prepareError});return;}
    disk.replica.state='preparing';
    const finish=()=>{const candidate={token:'candidate-'+window.__calls.filter(c=>c.method==='replica.prepare').length,...structuredClone(window.__nextCandidate)};disk.replica.state=candidate.unchanged?'idle':'ready';reply({requestId,ok:true,data:candidate});};
    if(window.__delayPrepare){window.__finishPrepare=finish;return;}finish();return;
   }
   else if(method==='replica.cancel'){if(window.__cancelError){reply({requestId,ok:false,error:window.__cancelError});return;}disk.replica.state='idle';}
   else if(method==='replica.apply'){
    disk.replica.state='applying';
    const finish=()=>{if(window.__applyError){disk.replica.state='ready';reply({requestId,ok:false,error:window.__applyError});return;}disk.replica.state='idle';disk.replica.hasLocalChanges=false;disk.replica.sourceGeneration=window.__nextCandidate.generation;disk.replica.identityReady=!window.__identityPending;if(window.__failRefreshAfterApply){window.__failRefreshAfterApply=false;window.__failState=true;}reply({requestId,ok:true,data:{generation:disk.replica.sourceGeneration,identityReady:disk.replica.identityReady}});};
    if(window.__delayApply){window.__finishApply=finish;return;}finish();return;
   }
   else if(method==='blocks.summary')data={revision:1,total_blocks:256,object_size:8388608,index_complete:false,counts:{},types:[]};
   else if(method==='blocks.query')data={total_count:256,items:Array.from({length:Math.min(args.limit,256-args.start)},(_,index)=>({index:args.start+index,sync_state:index===0?'free':'synced',unresolved:index===0,content_type:index===0?'unknown':'data'}))};
   else if(method==='blocks.changes')data={revision:1};
   else {reply({requestId,ok:false,error:'Unexpected RPC '+method});return;}
   reply({requestId,ok:true,data});
  }}};
 });
 const card=()=>page.locator('[data-disk="replica"]');
 const button=()=>card().locator('[data-action="replicaLatest"]');
 const visit=async name=>{await page.locator('nav [data-page="'+name+'"]').click();await page.waitForFunction(value=>document.querySelector('#content').dataset.page===value,name);};
 const count=method=>page.evaluate(value=>window.__calls.filter(c=>c.method===value).length,method);
 const last=method=>page.evaluate(value=>window.__calls.filter(c=>c.method===value).at(-1),method);
 const cancel=async()=>{await page.locator('#dialog .dialog-actions [data-action="closeDialog"]').click();await page.locator('#dialog').waitFor({state:'hidden'});await page.waitForFunction(()=>!replicaRequests.size);};
 const setFixture=async patch=>{await page.evaluate(async value=>{Object.assign(window.__nextCandidate,value.candidate||{});Object.assign(window.__state.disks[0],value.disk||{});Object.assign(window.__state.disks[0].replica,value.replica||{});await refresh();},patch);};
 try{
  await page.goto(origin+'/index.html');await button().waitFor();
  assert.equal(await button().isDisabled(),true);assert.match(await card().locator('.replica-hint').innerText(),/先安全卸载/);
  assert.equal(await page.locator('[data-disk="local"] [data-action="replicaLatest"]').count(),0);
  assert.match(await card().locator('.lazy-cache-status').innerText(),/未展开部分按需加载/);
  await page.evaluate(async()=>{await refresh();await refresh();});await visit('settings');await visit('disks');
  assert.equal(await count('replica.prepare'),0);assert.equal(await count('replica.status'),0);
  const guard=await page.evaluate(async()=>{try{await loadLatestReplica(window.__state.disks[0]);return false;}catch(error){return error.message;}});
  assert.match(guard,/先安全卸载/);assert.equal(await count('replica.prepare'),0);
  checks.push('Only imported disks expose manual latest loading; mounted UI and handler reject it, and ordinary state/page refresh never checks remote generations.');

  await card().locator('[data-action="unmount"]').click();await button().waitFor({state:'visible'});
  await button().click();assert.match(await page.locator('#dialogTitle').innerText(),/解锁/);
  await page.locator('#password').fill('isolated-password');await page.locator('#dialogSubmit').click();
  await page.waitForFunction(()=>document.querySelector('#dialogTitle').textContent==='加载云端最新快照');
  assert.deepEqual((await last('disks.unlock')).args,{id:'replica',password:'isolated-password'});
  assert.equal(await count('disks.mount'),0);assert.equal(await count('replica.apply'),0);
  await page.waitForFunction(()=>document.activeElement?.textContent==='取消');
  assert.match(await page.locator('#dialogBody').innerText(),/目标版本 13/);
  const prepared=(await last('replica.prepare')).args;assert.deepEqual(prepared,{id:'replica'});
  await cancel();assert.equal((await last('replica.cancel')).args.token,'candidate-1');
  checks.push('A locked encrypted replica unlocks without mounting; clean updates still await target-version confirmation with Cancel focused, and cancelling releases the reservation.');

  await button().click();await page.evaluate(()=>window.__failRefreshAfterApply=true);await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.match(await page.locator('#toast').innerText(),/快照已加载，但界面状态尚未刷新/);assert.equal(await count('replica.apply'),1);
  await page.evaluate(async()=>refresh());
  const applied=(await last('replica.apply')).args;assert.deepEqual(applied,{id:'replica',token:'candidate-2',expectedRevision:'9007199254740993',discardLocalChanges:false});
  assert.match(await card().locator('.replica-status').innerText(),/来源版本 13/);
  assert.equal(await page.evaluate(()=>window.__state.disks[0].mounted),false);assert.equal(await page.evaluate(()=>window.__state.disks[0].sync.enabled),false);
  checks.push('Applying a clean update submits the exact candidate/revision, leaves the disk unmounted with upload disabled, and distinguishes post-commit display refresh failure from failed apply.');

  await setFixture({candidate:{generation:13,localChanges:true,unchanged:false},disk:{readOnly:true},replica:{hasLocalChanges:true}});
  const beforeDirty=await count('replica.apply');await button().click();await page.locator('#replicaDiscard').waitFor();
  assert.match(await page.locator('#dialogBody').innerText(),/会丢弃本地修改/);assert.match(await page.locator('#dialogBody').innerText(),/现在已改为只读/);
  assert.equal(await page.locator('#replicaDiscard').isChecked(),false);assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
  await page.screenshot({path:path.join(output,'discard-local-confirmation.png'),fullPage:true});
  await page.waitForFunction(()=>document.activeElement?.textContent==='取消');await page.keyboard.press('Enter');await page.locator('#dialog').waitFor({state:'hidden'});
  await page.waitForFunction(()=>!replicaRequests.size);assert.equal(await count('replica.apply'),beforeDirty);
  assert.equal(await page.evaluate(()=>window.__state.disks[0].replica.hasLocalChanges),true);
  checks.push('Dirty state survives switching to read-only and even a same-generation reload warns; Enter defaults to cancellation and keeps local edits.');

  await button().click();await page.locator('#replicaDiscard').check();
  await page.evaluate(()=>{window.__delayApply=true;window.__identityPending=true;});await page.locator('#dialogSubmit').click();
  await page.waitForFunction(()=>typeof window.__finishApply==='function');
  const beforeApplyingCancel=await count('replica.cancel');await page.keyboard.press('Escape');
  await page.locator('#dialog .dialog-actions [data-action="closeDialog"]').click();
  assert.equal(await page.locator('#dialog').isVisible(),true);assert.equal(await count('replica.cancel'),beforeApplyingCancel);
  assert.equal((await last('replica.apply')).args.discardLocalChanges,true);
  await page.evaluate(()=>{window.__delayApply=false;window.__finishApply();});await page.locator('#dialog').waitFor({state:'hidden'});
  assert.equal(await page.evaluate(()=>window.__state.disks[0].replica.hasLocalChanges),false);
  assert.match(await page.locator('#toast').innerText(),/快照已加载；磁盘身份准备尚未完成/);assert.match(await card().locator('.replica-status').innerText(),/未准备好前不会挂载/);
  await page.evaluate(()=>window.__identityPending=false);
  checks.push('Discard requires explicit acknowledgment; once atomic apply is submitted, Escape/cancel cannot issue a conflicting cancellation; committed snapshots with pending identity preparation report the remaining work.');

  await setFixture({candidate:{generation:14,localChanges:false,unchanged:false}});
  await page.evaluate(()=>window.__delayPrepare=true);await button().click();
  await page.waitForFunction(()=>typeof window.__finishPrepare==='function');assert.match(await button().innerText(),/正在检查/);
  assert.equal(await card().locator('[data-action="mount"]').isDisabled(),true);
  await card().locator('[data-action="replicaCancel"]').click();await page.waitForFunction(()=>!replicaRequests.size);
  const beforeLate=await count('replica.apply');await page.evaluate(()=>{window.__delayPrepare=false;window.__finishPrepare();});
  await page.waitForFunction(()=>window.__state.disks[0].replica.state==='idle');
  assert.equal(await page.locator('#dialog').isVisible(),false);assert.equal(await count('replica.apply'),beforeLate);
  checks.push('Preparation gives immediate feedback, prevents mounting, supports cancellation, and ignores a late candidate response.');

  await setFixture({candidate:{generation:13,localChanges:false,unchanged:true}});
  const beforeUnchanged=await count('replica.apply');await button().click();await page.waitForFunction(()=>!replicaRequests.size);
  assert.equal(await count('replica.apply'),beforeUnchanged);assert.equal(await page.locator('#dialog').isVisible(),false);
  assert.match(await page.locator('#toast').innerText(),/无需下载/);
  checks.push('An unchanged clean source completes without apply or a replacement confirmation.');

  await setFixture({candidate:{generation:14,unchanged:false,localChanges:false}});await button().click();await page.locator('#dialogSubmit').waitFor();
  await page.evaluate(()=>window.__applyError='本地修订已变化，请重新检查');await page.locator('#dialogSubmit').click();
  await page.locator('#dialogBody .error-text').waitFor();assert.match(await page.locator('#dialogBody .error-text').innerText(),/修订已变化/);
  assert.equal(await page.evaluate(()=>window.__state.disks[0].replica.sourceGeneration),13);await cancel();
  await page.evaluate(()=>window.__applyError=null);
  const beforeFailedPrepareCancel=await count('replica.cancel');await page.evaluate(()=>window.__prepareError='模拟云端版本读取失败');
  await button().click();await page.waitForFunction(()=>!replicaRequests.size);await page.evaluate(async()=>refresh());
  assert.match(await card().locator('.replica-status .error-text').innerText(),/云端版本读取失败/);assert.equal(await button().isDisabled(),false);
  assert.equal(await count('replica.cancel'),beforeFailedPrepareCancel);await page.evaluate(()=>{window.__prepareError=null;window.__delayPrepare=true;window.__cancelError='模拟取消请求失败';});
  await button().click();await card().locator('[data-action="replicaCancel"]').click();
  await page.waitForFunction(()=>replicaRequests.get('replica')?.cancelWork===null);assert.match(await page.locator('#toast').innerText(),/取消请求失败/);
  await page.evaluate(()=>{window.__cancelError=null;window.__delayPrepare=false;window.__finishPrepare();});
  await page.locator('#dialogSubmit').waitFor();await cancel();assert.match(await page.locator('#toast').innerText(),/已取消加载/);
  checks.push('Revision and preparation failures stay visible without claiming a switch or masking failure as cancellation; failed cancellation permits a late valid candidate and a successful retry.');

  await setFixture({replica:{message:'<script>fixture</script>',state:'preparing'}});
  assert.equal(await card().locator('.replica-status script').count(),0);assert.match(await card().locator('.replica-status').innerText(),/<script>fixture<\/script>/);
  await setFixture({disk:{unlocked:false,lazySource:{},lazy:{enabled:true,index_complete:false}},replica:{state:'idle',message:''}});
  assert.match(await card().locator('.lazy-cache-status').innerText(),/解锁后查看缓存数量/);assert.doesNotMatch(await card().locator('.lazy-cache-status').innerText(),/已缓存 0/);
  await setFixture({disk:{unlocked:true,lazy:{enabled:true,cached_objects:4,missing_objects:2,index_complete:false}}});
  await setFixture({replica:{state:'idle',message:''}});await visit('blocks');
  await page.locator('[data-physical-block="0"]').waitFor();assert.match(await page.locator('[data-physical-block="0"]').getAttribute('class'),/unresolved/);
  assert.doesNotMatch(await page.locator('[data-physical-block="0"]').getAttribute('class'),/\bfree\b/);
  assert.match(await page.locator('[data-physical-block="0"]').getAttribute('title'),/未展开/);
  assert.match(await page.locator('.blocks-unresolved-note').innerText(),/未展开区域不计为空闲/);
  assert.ok(await page.locator('.block-cell').count()<400);
  checks.push('Unknown lazy regions are labeled unexpanded rather than free; block rendering stays bounded and status text is escaped.');

  await page.evaluate(async()=>{window.__state.tasks=[{id:'pull',diskId:'replica',kind:'replica',title:'加载云端快照',state:'downloading',message:'读取必要索引',downloadedBytes:2048,reusedBytes:8388608,readyToMount:true,verifiedScope:'未访问部分按需验证'}];window.__logs=[{sequence:2,timestampUtc:'2026-10-07T01:00:00Z',diskId:'replica',kind:'replica',action:'applied',message:'切换完成',generation:13},{sequence:1,timestampUtc:'2026-10-07T00:59:00Z',diskId:'replica',kind:'replica',action:'prepared',message:'等待确认',generation:13}];await refresh();});
  await visit('tasks');assert.match(await page.locator('.task-summary-card').innerText(),/压缩后下载 2.0 KiB · 缓存复用 8.0 MiB/);
  assert.match(await page.locator('.task-summary-card').innerText(),/可使用 · 未访问部分按需验证/);
  await page.locator('#taskJournal tbody tr').first().waitFor();assert.match(await page.locator('#taskJournal tbody').innerText(),/快照加载完成/);assert.match(await page.locator('#taskJournal tbody').innerText(),/等待加载确认/);
  assert.equal(await count('sync.enable'),0);assert.equal(await count('sync.now'),0);assert.equal(await count('disks.mount'),0);
  checks.push('Task summaries separate actual compressed download and cache reuse, show the partial verification boundary, and no replica path enables or starts cloud upload.');
  assert.deepEqual(errors,[]);
  fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks},null,2));console.log('PASS replica UI: '+checks.length+' groups');
 }catch(error){fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:false,error:error.stack,checks,errors},null,2));throw error;}
 finally{await context.close();await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
