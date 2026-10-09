/* Real browser, isolated host fixtures, no cloud credentials or external network. */
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const output=path.resolve(process.argv[2]),web=path.resolve(__dirname,'..');fs.mkdirSync(output,{recursive:true});
(async()=>{
 const checks=[],errors=[];
 const server=http.createServer((req,res)=>{const file=path.resolve(web,'.'+new URL(req.url,'http://localhost').pathname);if(!file.startsWith(web+path.sep)){res.writeHead(403);res.end();return;}fs.readFile(file,(error,body)=>{res.writeHead(error?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(error?'missing':body);});});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1380,height:1080},locale:'zh-CN'});
 await context.route('**/*',route=>new URL(route.request().url()).origin===origin?route.continue():route.abort());
 const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>{
  const listeners=[],reply=message=>queueMicrotask(()=>listeners.forEach(callback=>callback({data:message})));
  const state={connected:true,account:{displayName:'隔离测试'},settings:{},network:{activeRequests:1,queuedRequests:2},disks:[{id:'disk-a',name:'测试磁盘',driveLetter:'Z',containerPath:'C:\\IsolatedFixture\\plain.odv4',capacityBytes:1024**3,unlocked:true,mounted:false,cloudEncrypted:false,sync:{enabled:false,message:'已保存'}}],tasks:[{id:'job-a',diskId:'disk-a',kind:'sync',title:'同步测试',state:'uploading',message:'上传中',canPause:true,completedBytes:0,totalBytes:4194304}]};
  window.__state=state;window.__calls=[];window.__delayed=[];window.__holdState=false;window.__emitState=data=>reply({type:'state',data:structuredClone(data)});window.__blockRevision=1;
  window.chrome={webview:{addEventListener:(name,callback)=>{if(name==='message')listeners.push(callback);},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args:structuredClone(args)});let data={ok:true};
   if(method==='app.state'){data=structuredClone(state);if(window.__holdState){window.__delayed.push(()=>reply({requestId,ok:true,data}));return;}}
   else if(method==='settings.save')Object.assign(state.settings,args);
   else if(method==='cloud.list')data=[{id:'cloud-a',name:'加密来源',encrypted:true,capacityBytes:1024**3}];
   else if(method==='snapshots.list')data={items:[{id:'snap-a',name:'本地快照'}]};
   else if(method==='tasks.logs')data={items:[],hasMore:false};
   else if(method==='blocks.summary')data={revision:window.__blockRevision,total_blocks:256,counts:{pending:window.__blockRevision,synced:16},types:[{id:'data',objects:256,pending_objects:1}]};
   else if(method==='blocks.query')data={total_count:256,items:Array.from({length:Math.min(args.limit,256-args.start)},(_,n)=>({index:args.start+n,sync_state:'pending',content_type:'data'}))};
   else if(method==='blocks.changes')data={revision:window.__blockRevision};
   else if(method==='cloud.unlock')state.disks[0].cloudKeyRequired=false;
   else if(method==='sync.enable'){state.disks[0].sync.enabled=true;state.disks[0].cloudEncrypted=args.encrypted;}
   else if(!['sync.pause','disks.create','snapshots.restore','cloud.import'].includes(method)){reply({requestId,ok:false,error:'Unexpected RPC '+method});return;}
   reply({requestId,ok:true,data});
  }}};
 });
 const visit=async name=>{await page.locator('nav [data-page="'+name+'"]').click();await page.waitForFunction(value=>document.querySelector('#content').dataset.page===value,name);};
 const count=method=>page.evaluate(value=>window.__calls.filter(c=>c.method===value).length,method);
 const last=method=>page.evaluate(value=>window.__calls.filter(c=>c.method===value).at(-1),method);
 const close=async()=>{await page.locator('#dialog .dialog-actions [data-action="closeDialog"]').click();await page.locator('#dialog').waitFor({state:'hidden'});};
 const press=async locator=>{await locator.scrollIntoViewIfNeeded();const rect=await locator.boundingBox();await page.mouse.move(rect.x+rect.width/2,rect.y+rect.height/2);await page.mouse.down();};
 try{
  await page.goto(origin+'/index.html');await page.locator('[data-action="mount"]').waitFor();
  const mount=page.locator('[data-action="mount"]');await mount.evaluate(node=>window.__mount=node);await press(mount);
  await page.evaluate(async()=>{window.__state.disks[0].sync.message='刷新中的状态';await refresh();});
  assert.equal(await mount.evaluate(node=>node===window.__mount),true);await page.mouse.up();await page.locator('#mountReadOnly').waitFor();
  assert.equal(await page.locator('#dialog input[type="password"]').count(),0);await close();
  checks.push('A state refresh between real pointerdown and pointerup preserves the mount target and the click; local mounting has no password field.');

  await mount.focus();await mount.evaluate(node=>window.__focused=node);await page.evaluate(async()=>{window.__state.disks[0].sync.message='第二次状态';await refresh();});
  assert.equal(await mount.evaluate(node=>node===window.__focused&&node===document.activeElement),true);
  await page.keyboard.press('Enter');await page.locator('#mountReadOnly').waitFor();await close();
  const before=await page.locator('.card-path').innerText();
  await page.locator('.card-path').evaluate(node=>{const selection=getSelection(),range=document.createRange();range.selectNodeContents(node);selection.removeAllRanges();selection.addRange(range);});
  await page.evaluate(async()=>{window.__state.disks[0].containerPath='C:\\IsolatedFixture\\renamed.odv4';await refresh();});
  assert.equal(await page.evaluate(()=>getSelection().toString()),before);
  await page.evaluate(()=>getSelection().removeAllRanges());await page.waitForFunction(()=>document.querySelector('.card-path').textContent.includes('renamed'));
  checks.push('Unchanged action nodes retain keyboard focus, Enter still works, and selection survives refresh until released before the latest text is applied.');

  await visit('settings');await page.locator('#syncInterval').fill('123');await page.locator('#baiduRequestRate').fill('2.5');await page.locator('#syncOnExit').check();
  await page.locator('#pageTitle').click();await page.evaluate(async()=>{await refresh();await refresh();});
  assert.equal(await page.locator('#syncInterval').inputValue(),'123');assert.equal(await page.locator('#baiduRequestRate').inputValue(),'2.5');assert.equal(await page.locator('#syncOnExit').isChecked(),true);
  await press(page.locator('[data-action="saveSettings"]'));await page.evaluate(async()=>refresh());await page.mouse.up();
  await page.waitForFunction(()=>window.__calls.some(c=>c.method==='settings.save'));
  assert.equal((await last('settings.save')).args.syncIntervalSeconds,123);assert.equal((await last('settings.save')).args.syncOnExit,true);
  checks.push('Multiple unsaved settings survive blur and repeated refresh; a refresh during the Save click preserves and submits every edit.');

  await visit('tasks');const pause=page.locator('[data-task="job-a"] [data-action="taskPause"]');await pause.evaluate(node=>window.__pause=node);await press(pause);
  await page.evaluate(async()=>{window.__state.tasks[0].completedBytes=2097152;window.__state.tasks[0].message='进度更新';await refresh();});
  assert.equal(await pause.evaluate(node=>node===window.__pause),true);await page.mouse.up();await page.waitForFunction(()=>window.__calls.some(c=>c.method==='sync.pause'));
  assert.equal(await count('sync.pause'),1);assert.equal((await last('sync.pause')).args.id,'disk-a');
  await page.waitForFunction(()=>document.querySelector('[data-task="job-a"] .progress').getAttribute('aria-label')==='进度 50%');
  checks.push('Task progress refresh retains the Pause button through a held pointer and invokes exactly one pause on the intended disk.');

  await visit('blocks');await page.locator('[data-physical-block="0"]').waitFor();const stat=page.locator('[data-status-filter="pending"]');await stat.evaluate(node=>window.__stat=node);await press(stat);
  await page.evaluate(async()=>{window.__blockRevision++;await blockStatus.refresh();});assert.equal(await stat.evaluate(node=>node===window.__stat),true);await page.mouse.up();
  await page.waitForFunction(()=>window.__calls.some(c=>c.method==='blocks.query'&&c.args.sync_state==='pending'));
  const cell=page.locator('[data-physical-block="0"]');await cell.waitFor();await cell.evaluate(node=>window.__cell=node);await press(cell);
  await page.evaluate(async()=>{window.__blockRevision++;await blockStatus.refresh();});assert.equal(await cell.evaluate(node=>node===window.__cell),true);await page.mouse.up();
  await page.waitForFunction(()=>!document.querySelector('.blocks-detail').classList.contains('hidden'));
  assert.match(await page.locator('.blocks-detail-head').innerText(),/块 0/);
  checks.push('Block statistic filters and physical-block cells remain clickable through independent block refreshes without changing the selected block.');

  await visit('disks');await page.evaluate(()=>{window.__holdState=true;window.__state.disks[0].name='过期状态';window.__first=refresh();});
  await page.evaluate(()=>{window.__state.disks[0].name='新状态';window.__second=refresh();window.__holdState=false;window.__delayed.pop()();});
  await page.evaluate(async()=>{await window.__second;window.__delayed.pop()();await window.__first;});
  assert.match(await page.locator('.card h2').innerText(),/新状态/);
  await page.evaluate(()=>{window.__holdState=true;window.__state.disks[0].name='推送前';window.__third=refresh();window.__state.disks[0].name='推送最新';window.__emitState(window.__state);});
  await page.waitForFunction(()=>document.querySelector('.card h2').textContent==='推送最新');
  await page.evaluate(async()=>{window.__holdState=false;window.__delayed.pop()();await window.__third;});assert.equal(await page.locator('.card h2').innerText(),'推送最新');
  checks.push('Out-of-order app.state responses and requests older than a pushed state cannot overwrite the latest UI state.');

  await page.locator('header [data-action="create"]').click();assert.equal(await page.locator('#encrypted,#dialog input[type="password"]').count(),0);
  await page.evaluate(()=>document.querySelector('#path').value='C:\\IsolatedFixture\\new.odv4');await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  const created=(await last('disks.create')).args;assert.equal('password' in created,false);assert.equal('encrypted' in created,false);
  await visit('snapshots');await page.locator('[data-action="snapshotRestore"]').click();assert.equal(await page.locator('#dialog input[type="password"]').count(),0);await close();
  await visit('cloud');await page.locator('[data-action="cloudImport"]').click();assert.match(await page.locator('#password').locator('..').innerText(),/云端密码/);await close();
  await page.locator('[data-action="syncEnable"]').click();assert.equal(await page.locator('#cloudEncrypted').isChecked(),true);
  await page.locator('#password').fill('correct horse battery staple');await page.locator('#confirmPassword').fill('different');await page.locator('#dialogSubmit').click();
  assert.equal(await count('sync.enable'),0);assert.match(await page.locator('#dialogBody .error-text').innerText(),/两次云端密码/);
  await page.locator('#confirmPassword').fill('correct horse battery staple');await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  const enabled=(await last('sync.enable')).args;assert.equal(enabled.encrypted,true);assert.equal(enabled.password,'correct horse battery staple');
  await visit('disks');assert.match(await page.locator('.card-head .muted').innerText(),/本地明文 · 云端加密/);
  checks.push('Creation and local snapshot restore expose no local encryption; cloud import asks for the cloud password, sync validates matching passwords, and disk status separates plaintext local storage from cloud encryption.');

  await page.evaluate(async()=>{window.__state.disks[0].cloudKeyRequired=true;await refresh();});
  assert.equal(await page.locator('[data-action="mount"]').isEnabled(),true);
  await page.locator('[data-action="cloudUnlock"]').click();await page.locator('#dialogSubmit').click();assert.equal(await count('cloud.unlock'),0);
  await page.locator('#password').fill('recovered-cloud-secret');await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.deepEqual((await last('cloud.unlock')).args,{id:'disk-a',password:'recovered-cloud-secret'});
  await page.waitForFunction(()=>!document.querySelector('[data-action="cloudUnlock"]'));
  await page.evaluate(async()=>{window.__state.disks[0].cloudKeyRequired=true;await refresh();});await visit('cloud');
  await page.locator('[data-action="cloudUnlock"]').click();await page.locator('#password').fill('another-source-secret');await page.locator('#dialogSubmit').click();await page.locator('#dialog').waitFor({state:'hidden'});
  assert.equal(await count('cloud.unlock'),2);assert.equal((await last('cloud.unlock')).args.password,'another-source-secret');
  checks.push('Missing saved cloud keys expose cloud-only unlock on both disk and cloud pages, reject an empty password, send the recovery RPC, allow another source to be unlocked, and never disable local mounting.');

  assert.deepEqual(errors,[]);fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks},null,2));console.log('PASS live refresh UI: '+checks.length+' groups');
 }catch(error){fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:false,error:error.stack,checks,errors},null,2));throw error;}
 finally{await context.close();await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
