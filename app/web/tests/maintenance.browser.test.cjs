/* Isolated headless UI checks. Every host RPC is fake; external requests are blocked.
 * Usage: node maintenance.browser.test.cjs OUTPUT [PLAYWRIGHT_MODULE] [EDGE_EXECUTABLE] */
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const output=path.resolve(process.argv[2]),webRoot=path.resolve(__dirname,'..');
fs.mkdirSync(output,{recursive:true});
const mutating=/^(?:compact\.(?:start|pause|resume|cancel)|reclaim\.(?:start|pause|resume)|sync\.(?:cleanup|cleanupPause))$/;

(async()=>{
 const checks=[],errors=[],externalRequests=[];
 const server=http.createServer((request,response)=>{
  let file=path.resolve(webRoot,'.'+decodeURIComponent(new URL(request.url,'http://localhost').pathname));
  if(file===webRoot)file=path.join(webRoot,'index.html');
  if(!file.startsWith(webRoot+path.sep)){response.writeHead(403);response.end();return;}
  fs.readFile(file,(error,bytes)=>{
   response.writeHead(error?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml'}[path.extname(file)]||'application/octet-stream'});
   response.end(error?'missing':bytes);
  });
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1360,height:940},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
 await context.route('**/*',route=>{if(new URL(route.request().url()).origin===origin)return route.continue();externalRequests.push(route.request().url());return route.abort();});
 const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>{
  const listeners=[],send=message=>queueMicrotask(()=>listeners.forEach(listener=>listener({data:message})));
  const task=kind=>({id:kind+'-task',diskId:'manual-disk',kind,title:{compact:'整理空间',cleanup:'手动清理云端旧块',reclaim:'手动回收空闲空间'}[kind],state:'paused',message:'已暂停，等待手动继续',canPause:false,canResume:true});
  const state={connected:true,driverAvailable:true,account:{displayName:'隔离测试账户'},settings:{syncIntervalSeconds:60,maxParallelTransfers:2},tasks:['compact','cleanup','reclaim'].map(task),disks:[{id:'manual-disk',name:'手动维护测试盘',containerPath:'C:\\IsolatedFixture\\manual.odv4',driveLetter:'Z',capacityBytes:1073741824,encrypted:true,unlocked:true,mounted:true,sync:{enabled:true,state:'synced',message:'已同步，旧对象等待手动清理',pendingBytes:0}}]};
  window.__calls=[];window.__state=state;
  window.__emit=()=>send({type:'state',data:structuredClone(state)});
  window.chrome={webview:{addEventListener:(name,listener)=>{if(name==='message')listeners.push(listener);},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args:structuredClone(args)});
   const reply=(data,error)=>send({requestId,ok:!error,data,error});
   if(method===window.__failMethod){window.__failMethod=null;reply(null,'请先关闭此盘内的文件、程序和资源管理器窗口，再手动回收；尚未修改任何块。');return;}
   let data={};
   if(method==='app.state')data=structuredClone(state);
   else if(method==='blocks.summary')data={revision:1,total_blocks:0,pending_objects:0,pending_bytes:0,deep_compaction_estimated_bytes:16777216,counts:{},types:[]};
   else if(method==='blocks.query')data={revision:1,total_count:0,items:[]};
   else if(method==='blocks.changes')data={revision:1,reset_required:false};
   else if(method==='snapshots.list')data={items:[]};
   else if(method==='cloud.list')data=[];
   else if(method==='tasks.logs')data={items:[],hasMore:false,nextCursor:null,retentionCount:65536};
   else if(method==='sync.cleanupStatus')data=window.__unknownEstimate?{known:false,objects:0,bytes:0,commits:0}:{known:true,objects:7,bytes:29360128,commits:2};
   else if(/^(compact\.(start|pause|resume|cancel)|reclaim\.(start|pause|resume)|sync\.(cleanup|cleanupPause))$/.test(method)){
    const kind=method.startsWith('compact.')?'compact':method.startsWith('reclaim.')?'reclaim':'cleanup';
    const entry=state.tasks.find(value=>value.kind===kind),paused=method.endsWith('.pause')||method==='sync.cleanupPause';
    Object.assign(entry,{state:method==='compact.cancel'?'cancelled':paused?'paused':'running',canPause:!paused&&method!=='compact.cancel',canResume:paused,message:paused?'已暂停，等待手动继续':'正在处理用户确认的维护请求'});
    data={ok:true,taskId:entry.id};
   }else{reply(null,'Unexpected fixture RPC: '+method);return;}
   if(method===window.__deferMethod){window.__deferMethod=null;window.__releaseReply=()=>{window.__releaseReply=null;reply(data);};return;}
   reply(data);
  }}};
 });
 const calls=()=>page.evaluate(()=>window.__calls);
 const mutations=async()=>(await calls()).filter(call=>mutating.test(call.method));
 const visit=async name=>{await page.locator('nav [data-page="'+name+'"]').click();await page.waitForFunction(name=>document.querySelector('#content').dataset.page===name,name);};
 const closeDialog=async()=>{await page.locator('#dialog [data-action="closeDialog"]').first().click();await page.locator('#dialog').waitFor({state:'hidden'});};
 const openCompact=async()=>{await visit('disks');await page.locator('[data-disk="manual-disk"] [data-action="compact"]').click();await page.locator('#compactMode').waitFor();};
 try{
  await page.goto(origin+'/index.html');await page.locator('[data-disk="manual-disk"]').waitFor();
  for(const name of ['tasks','settings','snapshots','cloud','blocks','disks'])await visit(name);
  await page.evaluate(async()=>{window.__emit();await refresh();});
  await page.waitForTimeout(2200);
  assert.deepEqual(await mutations(),[]);
  assert.equal((await calls()).filter(call=>call.method==='sync.cleanupStatus').length,0);
  checks.push('Initial state, paused tasks, periodic state refresh and all page switches do not start or resume maintenance.');

  await openCompact();assert.equal(await page.locator('#compactMode').inputValue(),'normal');
  assert.equal(await page.locator('#compactLayoutNotice').isVisible(),false);
  assert.match(await page.locator('#compactExplanation').innerText(),/不增加待上传内容/);
  await page.locator('#compactMode').selectOption('deep');
  assert.match(await page.locator('#dialogBody').innerText(),/不会生成新的待上传内容/);
  await page.locator('#compactMode').selectOption('trim');
  assert.match(await page.locator('#dialogBody').innerText(),/先关闭盘内文件和窗口/);
  await page.locator('#compactMode').selectOption('normal');
  assert.deepEqual(await mutations(),[]);await closeDialog();
  assert.deepEqual(await mutations(),[]);
  checks.push('Opening and cancelling maintenance causes no mutation; ordinary reclaim is the default and deep layout compaction preserves cloud identity.');

  for(const mode of ['normal','deep','trim']){
   await openCompact();await page.locator('#compactMode').selectOption(mode);
   if(mode==='deep')await page.screenshot({path:path.join(output,'maintenance-options.png')});
   const before=(await mutations()).length;
   assert.equal((await mutations()).length,before);
   await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
   const after=await mutations();assert.equal(after.length,before+1);
   assert.deepEqual(after.at(-1),{method:mode==='trim'?'reclaim.start':'compact.start',args:{id:'manual-disk',mode}});
   assert.equal(await page.locator('#content').getAttribute('data-page'),'tasks');
  }
  checks.push('Explicit normal/deep confirmation sends compact.start with the selected mode; TRIM sends only reclaim.start.');
  await page.evaluate(async()=>{window.__state.disks[0].compact={state:'paused',mode:'deep',restart_required:true};await refresh();});
  await openCompact();assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);const beforeCancel=(await mutations()).length;
  await page.locator('[data-action="compactCancel"]').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.equal((await mutations()).length,beforeCancel+1);assert.equal((await mutations()).at(-1).method,'compact.cancel');
  await page.evaluate(async()=>{delete window.__state.disks[0].compact;await refresh();});
  await openCompact();await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  checks.push('A stopped deep task can be explicitly cancelled without silently starting another kind of maintenance.');


  await page.evaluate(()=>{window.__failMethod='reclaim.start';});
  await openCompact();await page.locator('#compactMode').selectOption('trim');
  await page.locator('#dialogSubmit').click();await page.locator('#dialogBody .error-text').waitFor();
  assert.match(await page.locator('#dialogBody .error-text').innerText(),/尚未修改任何块/);
  assert.equal(await page.locator('#dialogSubmit').isEnabled(),true);
  await page.screenshot({path:path.join(output,'reclaim-busy.png')});await closeDialog();
  checks.push('A busy-volume rejection stays visible in the dialog, with no success transition or automatic retry.');

  await visit('cloud');
  await page.evaluate(()=>{window.__deferMethod='sync.cleanupStatus';});
  const beforeCleanup=(await mutations()).length;
  await page.locator('[data-action="cloudCleanup"]').click();
  await page.waitForFunction(()=>typeof window.__releaseReply==='function');
  assert.equal((await mutations()).length,beforeCleanup);
  assert.equal(await page.locator('#dialog').isVisible(),false);
  assert.deepEqual((await calls()).at(-1),{method:'sync.cleanupStatus',args:{id:'manual-disk'}});
  await page.evaluate(()=>window.__releaseReply());await page.locator('#dialog').waitFor({state:'visible'});
  assert.match(await page.locator('#dialogBody').innerText(),/7 个旧对象/);
  assert.match(await page.locator('#dialogBody').innerText(),/28\.0 MiB/);
  assert.match(await page.locator('#dialogBody').innerText(),/2 个旧版本描述/);
  assert.equal((await mutations()).length,beforeCleanup);
  await page.screenshot({path:path.join(output,'cloud-cleanup-confirm.png')});await closeDialog();
  assert.equal((await mutations()).length,beforeCleanup);
  checks.push('Cloud cleanup first waits for sync.cleanupStatus, shows the local estimate and does nothing when cancelled.');

  await page.locator('[data-action="cloudCleanup"]').click();await page.locator('#dialog').waitFor({state:'visible'});
  await page.evaluate(()=>{window.__deferMethod='sync.cleanup';});
  await page.locator('#dialogSubmit').click();await page.waitForFunction(()=>typeof window.__releaseReply==='function');
  assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
  await page.locator('#dialogForm').evaluate(form=>{form.requestSubmit();form.requestSubmit();});
  assert.equal((await mutations()).length,beforeCleanup+1);
  assert.deepEqual((await mutations()).at(-1),{method:'sync.cleanup',args:{id:'manual-disk'}});
  await page.evaluate(()=>window.__releaseReply());await page.locator('#dialog').waitFor({state:'hidden'});
  checks.push('Only confirmation sends sync.cleanup; duplicate submissions during its pending response cannot start another request.');

  await visit('cloud');await page.evaluate(()=>{window.__unknownEstimate=true;});
  const beforeUnknown=(await mutations()).length;
  await page.locator('[data-action="cloudCleanup"]').click();await page.locator('#dialog').waitFor({state:'visible'});
  assert.match(await page.locator('#dialogBody').innerText(),/未知对象会保留/);
  assert.equal((await mutations()).length,beforeUnknown);await closeDialog();
  await page.evaluate(()=>{window.__failMethod='sync.cleanupStatus';});
  await page.locator('[data-action="cloudCleanup"]').click();await page.locator('#toast.error').waitFor();
  assert.equal(await page.locator('#dialog').isVisible(),false);
  assert.equal((await mutations()).length,beforeUnknown);
  checks.push('Unknown inventory requires confirmation, and failed estimate queries never fall through to cleanup.');

  await visit('tasks');
  for(const [kind,pause,resume] of [['cleanup','sync.cleanupPause','sync.cleanup'],['reclaim','reclaim.pause','reclaim.resume'],['compact','compact.pause','compact.resume']]){
   const card=page.locator('[data-task="'+kind+'-task"]');
   await card.locator('[data-action="taskPause"]').click();await card.locator('[data-action="taskResume"]').waitFor();
   assert.deepEqual((await mutations()).at(-1),{method:pause,args:kind==='compact'?{id:'manual-disk',taskId:kind+'-task'}:{id:'manual-disk'}});
   await card.locator('[data-action="taskResume"]').click();await card.locator('[data-action="taskPause"]').waitFor();
   assert.deepEqual((await mutations()).at(-1),{method:resume,args:kind==='compact'?{id:'manual-disk',taskId:kind+'-task'}:{id:'manual-disk'}});
  }
  await page.screenshot({path:path.join(output,'maintenance-tasks.png')});
  checks.push('Cleanup, reclaim and compact task controls use their own pause/resume RPCs and the correct disk identifier.');

  const afterManual=(await mutations()).length;
  for(const name of ['settings','disks','cloud','tasks'])await visit(name);
  await page.evaluate(async()=>{window.__emit();await refresh();});await page.waitForTimeout(2200);
  assert.equal((await mutations()).length,afterManual);
  assert.deepEqual(errors,[]);assert.deepEqual(externalRequests,[]);
  checks.push('Later polling and navigation add no maintenance mutations; no external network or real host operation was used.');
  fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks,mutations:await mutations(),pageErrors:errors,externalRequests},null,2));
  console.log('MAINTENANCE_UI_OK '+checks.length);
 }catch(error){fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:false,error:error.stack,checks,calls:await calls(),pageErrors:errors,externalRequests},null,2));await page.screenshot({path:path.join(output,'failure.png')}).catch(()=>{});throw error;}
 finally{await context.close();await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
