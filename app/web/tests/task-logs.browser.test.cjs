/* Browser integration checks with an isolated profile and a fake WebView bridge.
 * Usage: node task-logs.browser.test.cjs OUTPUT [PLAYWRIGHT_MODULE] [EDGE_EXECUTABLE]
 * No disk controller, credentials or cloud provider is used. */
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const {chromium}=require(process.argv[3]||'playwright');
const webRoot=path.resolve(__dirname,'..');
const output=path.resolve(process.argv[2]||'task-log-test-output');
fs.mkdirSync(output,{recursive:true});
const diskA={id:'disk-a',name:'日志测试盘',containerPath:'C:\\Mock\\journal-a.odv3',capacityBytes:1073741824,driveLetter:'Z',encrypted:true,mounted:false,unlocked:false};
const diskB={id:'disk-b',name:'已解锁测试盘',containerPath:'C:\\Mock\\journal-b.odv3',capacityBytes:1073741824,driveLetter:'Y',encrypted:true,mounted:false,unlocked:true};
const fixture=Array.from({length:700},(_,i)=>{
  const sequence=700-i;
  return {sequence,timestampUtc:new Date(Date.UTC(2026,9,4,12,0,sequence)).toISOString(),diskId:sequence%2===0?'disk-a':'disk-b',runId:'mock-run',kind:'sync',
    action:sequence%7===0?'upload.failed':sequence%3===0?'object.reused':'upload.confirmed',level:sequence%7===0?'error':'info',
    message:sequence===700?'<img src=x onerror="window.__injected=true">':'已校验远端对象，记录本次传输结果。',
    objectId:'block-'+String(sequence).padStart(8,'0')+'-01234567-89ab-cdef-0123-456789abcdef',objectKind:'data',bytes:4194304,wireBytes:1024,generation:9};
});
const initialState={connected:true,driverAvailable:true,account:null,disks:[diskA,diskB],settings:{syncIntervalSeconds:60,maxParallelTransfers:2},network:{activeRequests:2,queuedRequests:5,limits:{requestsPerSecond:3,maximumConcurrentRequests:4}},
  tasks:[{id:'run-b',diskId:'disk-b',kind:'sync',title:'同步 · 已解锁测试盘',state:'uploading',message:'正在上传增量对象',completedBytes:4194304,totalBytes:16777216,canPause:true}]};

(async()=>{
  const checks=[];
  const server=http.createServer((request,response)=>{
    let target=path.resolve(webRoot,'.'+decodeURIComponent(new URL(request.url,'http://localhost').pathname));
    if(target===webRoot)target=path.join(webRoot,'index.html');
    if(!target.startsWith(webRoot+path.sep)){response.writeHead(403);response.end();return;}
    fs.readFile(target,(error,bytes)=>{
      if(error){response.writeHead(404);response.end();return;}
      const type={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml'}[path.extname(target)]||'application/octet-stream';
      response.writeHead(200,{'Content-Type':type});response.end(bytes);
    });
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const executablePath=process.argv[4]||path.join(process.env['ProgramFiles(x86)']||'C:/Program Files (x86)','Microsoft/Edge/Application/msedge.exe');
  const browser=await chromium.launch({executablePath,headless:true});
  const context=await browser.newContext({viewport:{width:1360,height:940},locale:'zh-CN',timezoneId:'Asia/Shanghai'});
  const origin='http://127.0.0.1:'+server.address().port;
  await context.route('**/*',route=>new URL(route.request().url()).origin===origin?route.continue():route.abort());
  const page=await context.newPage();
  const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  await page.addInitScript(({fixture,initialState,diskA})=>{
    const listeners=[];
    let state=structuredClone(initialState),logs=structuredClone(fixture);
    const send=message=>queueMicrotask(()=>listeners.forEach(listener=>listener({data:message})));
    window.__bridgeRequests=[];window.__deferredLogs=[];window.__deferredDiagnostics=[];window.__state=state;
    localStorage.setItem('blockAutoRefresh','false');
    window.__emitState=()=>send({type:'state',data:structuredClone(state)});
    window.__restoreDiskA=()=>{if(!state.disks.some(d=>d.id===diskA.id))state.disks.unshift(structuredClone(diskA));window.__emitState();};
    window.__addLog=sequence=>logs.unshift({...logs[0],sequence,timestampUtc:new Date(Date.UTC(2026,9,4,12,0,sequence)).toISOString(),level:'info',action:'upload.confirmed',message:'新增的确认记录',objectId:'new-object-'+sequence});
    Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async value=>{window.__copied=value;}}});
    window.chrome=window.chrome||{};
    window.chrome.webview={addEventListener:(name,handler)=>{if(name==='message')listeners.push(handler);},postMessage:request=>{
      const {method,args,requestId}=request;
      window.__bridgeRequests.push({method,args:structuredClone(args)});
      let data=null;
      if(method==='app.state')data=structuredClone(state);
      if(method==='sync.diagnostics'){
        data={phase:'uploading',message:'诊断 '+args.id,physical_read_bytes:4194304,api_requests:window.__bridgeRequests.filter(r=>r.method==='sync.diagnostics').length};
        if(window.__failNextDiagnostic){window.__failNextDiagnostic=false;send({requestId,ok:false,error:'模拟诊断读取失败'});return;}
        if(window.__deferNextDiagnostic){window.__deferNextDiagnostic=false;window.__deferredDiagnostics.push(()=>send({requestId,ok:true,data}));return;}
      }
      if(method==='tasks.logs'){
        if(window.__failNextLogs){window.__failNextLogs=false;send({requestId,ok:false,error:'日志读取暂时失败（模拟）'});return;}
        let matched=logs.filter(entry=>(!args.diskId||entry.diskId===args.diskId)&&(!args.level||entry.level===args.level)&&(args.before===undefined||entry.sequence<args.before));
        matched.sort((a,b)=>b.sequence-a.sequence);
        const items=matched.slice(0,Math.min(200,args.limit||200));
        data={items:structuredClone(items),hasMore:matched.length>items.length,nextCursor:items.at(-1)?.sequence??null,retentionCount:65536,oldestTimestampUtc:logs.at(-1).timestampUtc,warning:window.__journalWarning||null};
        if(window.__deferNextLog){window.__deferNextLog=false;window.__deferredLogs.push(()=>send({requestId,ok:true,data}));return;}
      }
      if(method==='disks.delete'){
        const disk=state.disks.find(d=>d.id===args.id);
        if(!disk||disk.mounted||disk.unlocked||(args.deleteContainer&&args.confirmName!==disk.name)){send({requestId,ok:false,error:'模拟删除保护拒绝操作'});return;}
        state.disks=state.disks.filter(d=>d.id!==args.id);
        data={deletedId:disk.id,containerDeleted:args.deleteContainer,containerPath:disk.containerPath};
      }
      if(method==='disks.unmount'){
        const disk=state.disks.find(d=>d.id===args.id);disk.mounted=false;disk.unlocked=false;data=structuredClone(state);
      }
      if(method==='disks.delete'&&window.__deferDelete){window.__completeDelete=()=>send({requestId,ok:true,data});return;}
      send({requestId,ok:true,data});
    }};
  },{fixture,initialState,diskA});
  try{
    await page.goto('http://127.0.0.1:'+server.address().port+'/index.html');
    await page.locator('nav [data-page="tasks"]').click();
    await page.waitForFunction(()=>document.querySelectorAll('.journal-table tbody tr').length===200);
    assert.equal(await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence'),'700');
    assert.match(await page.locator('.journal-table tbody tr').first().innerText(),/20:11:40/);
    assert.match(await page.locator('.journal-table tbody tr').first().innerText(),/4\.0 MiB/);
    assert.match(await page.locator('.journal-table tbody tr').first().innerText(),/压缩后 1.0 KiB/);
    checks.push('Object logs distinguish original object bytes from compressed wire bytes.');
    assert.equal(await page.locator('.journal-table img').count(),0);
    assert.match(await page.locator('.journal-retention').innerText(),/最多保留 65,536 条/);
    await page.evaluate(async()=>{window.__journalWarning='日志写入暂时失败，已有记录仍可查看。';await taskJournal.poll();});
    assert.match(await page.locator('.journal-warning').innerText(),/日志写入暂时失败/);
    await page.evaluate(async()=>{window.__journalWarning=null;await taskJournal.poll();});
    assert.equal(await page.evaluate(()=>window.__injected),undefined);
    await page.locator('.journal-table tbody tr').first().locator('[data-log-action="copy"]').click();
    assert.equal(await page.evaluate(()=>window.__copied),fixture[0].objectId);
    checks.push('latest-first 200-row bound, local timestamps with seconds, object size, escaped content and complete-ID copy');
    await page.screenshot({path:path.join(output,'task-logs.png')});

    await page.evaluate(()=>{document.activeElement.blur();document.querySelector('.journal-scroll').scrollTop=640;});
    const beforeRows=await page.locator('.journal-table tbody tr').evaluateAll(rows=>rows.map(row=>row.dataset.logSequence));
    const beforeScroll=await page.locator('.journal-scroll').evaluate(node=>node.scrollTop);
    await page.evaluate(async()=>{window.__addLog(701);await taskJournal.poll();});
    await page.locator('.journal-status [data-log-action="latest"]').waitFor({state:'visible'});
    assert.deepEqual(await page.locator('.journal-table tbody tr').evaluateAll(rows=>rows.map(row=>row.dataset.logSequence)),beforeRows);
    assert.equal(await page.locator('.journal-scroll').evaluate(node=>node.scrollTop),beforeScroll);
    await page.locator('.journal-status [data-log-action="latest"]').click();
    await page.waitForFunction(()=>document.querySelector('.journal-table tbody tr')?.dataset.logSequence==='701');
    assert.equal(await page.locator('.journal-scroll').evaluate(node=>node.scrollTop),0);
    checks.push('new records preserve rows and scroll while reading, then explicit latest refresh updates them');

    const oldest=Number(await page.locator('.journal-table tbody tr').last().getAttribute('data-log-sequence'));
    await page.locator('[data-log-action="older"]').click();
    await page.waitForFunction(sequence=>Number(document.querySelector('.journal-table tbody tr')?.dataset.logSequence)<sequence,oldest);
    const olderTop=await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence');
    await page.evaluate(async()=>{window.__addLog(702);await taskJournal.poll();});
    assert.equal(await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence'),olderTop);
    assert.equal(await page.locator('.journal-table tbody tr').count(),200);
    assert.equal(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='tasks.logs'&&r.args.before!==undefined).at(-1).args.before),oldest);
    checks.push('older pages use an exclusive cursor and are never overwritten by polling or appended without a bound');

    const diagnosticCalls=()=>page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='sync.diagnostics').length);
    assert.match(await page.locator('.task-network-current').innerText(),/当前 2 个请求进行中，5 个排队/);
    assert.equal(await diagnosticCalls(),0);assert.equal(await page.locator('.task-diagnostics').getAttribute('open'),null);
    await page.locator('.task-diagnostics summary').click();
    assert.match(await page.locator('.task-diagnostics-feedback').innerText(),/解锁这块磁盘/);assert.equal(await diagnosticCalls(),0);
    await page.locator('.task-diagnostics-disk').selectOption('disk-b');
    await page.waitForFunction(()=>document.querySelector('.task-diagnostics-values').textContent.includes('诊断 disk-b'));
    assert.equal(await page.evaluate(()=>localStorage.getItem('blockAutoRefresh')), 'false');
    const diagnosticRows=await page.locator('.journal-table tbody tr').evaluateAll(rows=>rows.map(row=>row.dataset.logSequence));
    await page.locator('.journal-scroll').evaluate(node=>node.scrollTop=140);
    const diagnosticScroll=await page.locator('.journal-scroll').evaluate(node=>node.scrollTop);
    await page.locator('.task-diagnostics-disk').focus();
    await page.evaluate(()=>{window.__state.network.activeRequests=3;window.__state.network.queuedRequests=7;window.__emitState();});
    await page.waitForFunction(()=>document.querySelector('.task-network-current').textContent.includes('3 个请求进行中，7 个排队'));
    assert.equal(await page.evaluate(()=>document.activeElement.className),'task-diagnostics-disk');
    assert.deepEqual(await page.locator('.journal-table tbody tr').evaluateAll(rows=>rows.map(row=>row.dataset.logSequence)),diagnosticRows);
    assert.equal(await page.locator('.journal-scroll').evaluate(node=>node.scrollTop),diagnosticScroll);
    await page.locator('.journal-disk-filter').focus();
    await page.evaluate(()=>{window.__state.network.queuedRequests=8;window.__emitState();});
    await page.waitForFunction(()=>document.querySelector('.task-network-current').textContent.includes('8 个排队'));
    assert.equal(await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence'),olderTop);
    checks.push('Task requests update while diagnostic/log selectors retain focus; closed/locked diagnostics issue no requests, and expanded disk diagnostics work with block auto-refresh disabled without replacing log pages or scroll.');

    await page.evaluate(()=>{window.__state.disks.push({...window.__state.disks[1],id:'disk-c',name:'另一诊断盘'});window.__emitState();});
    const beforeDeferred=await diagnosticCalls();
    await page.evaluate(()=>{window.__deferNextDiagnostic=true;taskMonitor.refresh();taskMonitor.refresh();});
    await page.waitForFunction(()=>window.__deferredDiagnostics.length===1);assert.equal(await diagnosticCalls(),beforeDeferred+1);
    await page.locator('.task-diagnostics-disk').selectOption('disk-c');
    await page.waitForFunction(()=>document.querySelector('.task-diagnostics-values').textContent.includes('诊断 disk-c'));
    await page.evaluate(()=>window.__deferredDiagnostics.shift()());
    assert.match(await page.locator('.task-diagnostics-values').innerText(),/诊断 disk-c/);
    await page.evaluate(()=>{window.__state.disks=window.__state.disks.filter(d=>d.id!=='disk-c');window.__emitState();});
    await page.locator('.task-diagnostics-disk').selectOption('disk-b');
    await page.waitForFunction(()=>document.querySelector('.task-diagnostics-values').textContent.includes('诊断 disk-b'));
    const oldDiagnostic=await page.locator('.task-diagnostics-values').innerText();
    await page.evaluate(()=>window.__failNextDiagnostic=true);await page.locator('[data-monitor-op="refresh"]').click();
    await page.locator('.task-diagnostics-feedback.error-text').waitFor();assert.equal(await page.locator('.task-diagnostics-values').innerText(),oldDiagnostic);
    const beforePoll=await diagnosticCalls();await page.waitForFunction(n=>window.__bridgeRequests.filter(r=>r.method==='sync.diagnostics').length>n,beforePoll);
    await page.waitForFunction(()=>document.querySelector('.task-diagnostics-feedback').textContent.startsWith('已刷新'));
    assert.equal(await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence'),olderTop);
    await page.screenshot({path:path.join(output,'task-monitor-diagnostics.png')});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
    assert.ok(await page.locator('.journal-scroll').evaluate(node=>node.clientHeight)>150);
    await page.locator('.task-diagnostics summary').click();
    const closedAt=await diagnosticCalls();await page.waitForTimeout(2300);assert.equal(await diagnosticCalls(),closedAt);
    checks.push('Diagnostic reads coalesce, ignore stale disk replies, retain data on error, poll only while expanded, and leave room for the paginated log without horizontal overflow.');

    await page.locator('.journal-disk-filter').selectOption('disk-a');
    await page.locator('.journal-level-filter').selectOption('error');
    await page.waitForFunction(()=>document.querySelectorAll('.journal-table tbody tr').length>0&&document.querySelectorAll('.journal-table tbody tr').length<200);
    assert.equal(await page.locator('.journal-disk-name').evaluateAll(nodes=>nodes.every(node=>node.dataset.diskId==='disk-a')),true);
    assert.equal(await page.locator('.journal-table .badge').evaluateAll(nodes=>nodes.every(node=>node.textContent==='失败')),true);
    assert.equal(await page.locator('[data-log-action="older"]').isDisabled(),true);
    await page.evaluate(()=>{window.__deferNextLog=true;});
    await page.locator('.journal-disk-filter').selectOption('disk-b');
    await page.waitForFunction(()=>window.__deferredLogs.length===1);
    await page.locator('.journal-disk-filter').selectOption('disk-a');
    await page.waitForFunction(()=>!taskJournal.loading);
    await page.evaluate(()=>window.__deferredLogs.shift()());
    await page.waitForTimeout(50);
    assert.equal(await page.locator('.journal-disk-name').evaluateAll(nodes=>nodes.every(node=>node.dataset.diskId==='disk-a')),true);
    checks.push('disk/error filters are applied on the server and delayed stale filter responses are ignored');

    await page.locator('.journal-level-filter').selectOption('');
    await page.locator('.journal-disk-filter').selectOption('');
    await page.waitForFunction(()=>!taskJournal.loading&&document.querySelectorAll('.journal-table tbody tr').length===200);
    const currentTop=await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence');
    await page.evaluate(()=>{window.__failNextLogs=true;});
    await page.locator('[data-log-action="older"]').click();
    await page.locator('.journal-status-text.error-text').waitFor();
    assert.equal(await page.locator('.journal-table tbody tr').first().getAttribute('data-log-sequence'),currentTop);
    assert.match(await page.locator('.journal-page-info').innerText(),/第 1 页/);
    checks.push('a failed page request preserves the previous page and its navigation state');

    await page.locator('.task-diagnostics summary').click();
    await page.waitForFunction(()=>!taskMonitor.inFlight);
    await page.evaluate(()=>{window.__deferNextDiagnostic=true;taskMonitor.refresh();});
    await page.waitForFunction(()=>window.__deferredDiagnostics.length===1);
    await page.locator('nav [data-page="settings"]').click();
    await page.evaluate(()=>window.__deferredDiagnostics.shift()());
    const diagnosticsAfterLeave=await diagnosticCalls();
    assert.equal(await page.locator('.task-diagnostics').count(),0);assert.doesNotMatch(await page.locator('#content').innerText(),/请求进行中|个排队/);
    const requests=await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='tasks.logs').length);
    await page.waitForTimeout(3300);
    assert.equal(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='tasks.logs').length),requests);
    assert.equal(await diagnosticCalls(),diagnosticsAfterLeave);
    checks.push('Log and diagnostic polling stop outside the task page; delayed diagnostic replies do not recreate controls in settings.');

    await page.locator('nav [data-page="disks"]').click();
    await page.locator('[data-disk="disk-a"] [data-action="diskSettings"]').click();
    await page.locator('[data-action="deleteDiskSettings"]').click();
    await page.locator('#deleteContainer').waitFor();
    assert.equal(await page.locator('#deleteContainer').isChecked(),false);
    assert.equal(await page.locator('#dialogSubmit').innerText(),'从列表移除');
    assert.match(await page.locator('.delete-container-path').innerText(),/C:\\Mock\\journal-a\.odv3/);
    await page.locator('#dialogSubmit').click();
    await page.waitForFunction(()=>window.__bridgeRequests.some(r=>r.method==='disks.delete'));
    assert.deepEqual(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='disks.delete').at(-1).args),{id:'disk-a',deleteContainer:false,confirmName:''});
    await page.locator('#dialog').waitFor({state:'hidden'});
    await page.locator('[data-disk="disk-a"]').waitFor({state:'detached'});
    await page.evaluate(()=>window.__restoreDiskA());
    await page.locator('[data-disk="disk-a"] [data-action="diskSettings"]').click();
    await page.locator('[data-action="deleteDiskSettings"]').click();
    await page.locator('#deleteContainer').check();
    assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
    assert.match(await page.locator('.delete-warning').innerText(),/容器和本地快照会被删除，未同步内容将丢失；云端已有副本保留/);
    await page.locator('#deleteConfirmName').fill('错误名称');
    assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
    await page.locator('#deleteConfirmName').fill(diskA.name);
    assert.equal(await page.locator('#dialogSubmit').isEnabled(),true);
    await page.screenshot({path:path.join(output,'delete-confirm.png')});
    await page.evaluate(()=>{window.__deferDelete=true;});
    await page.locator('#dialogSubmit').click();
    await page.waitForFunction(()=>window.__bridgeRequests.filter(r=>r.method==='disks.delete').length===2);
    await page.locator('#deleteConfirmName').fill('changed during request');
    await page.locator('#deleteConfirmName').fill(diskA.name);
    assert.equal(await page.locator('#dialogSubmit').isDisabled(),true);
    await page.locator('#dialogForm').evaluate(form=>form.requestSubmit());
    assert.equal(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='disks.delete').length),2);
    await page.evaluate(()=>window.__completeDelete());
    assert.deepEqual(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='disks.delete').at(-1).args),{id:'disk-a',deleteContainer:true,confirmName:diskA.name});
    await page.locator('#dialog').waitFor({state:'hidden'});
    await page.locator('[data-disk="disk-b"] [data-action="diskSettings"]').click();
    assert.equal(await page.locator('[data-action="deleteDiskSettings"]').isDisabled(),true);
    checks.push('remove-only is the default; permanent deletion requires the exact name; open disks cannot enter deletion and a pending delete cannot be submitted twice');
    await page.locator('#dialog [data-action="closeDialog"]').first().click();
    const closeDisk=page.locator('[data-disk="disk-b"] [data-action="unmount"]');
    assert.equal(await closeDisk.innerText(),'关闭磁盘');
    assert.equal(await closeDisk.isVisible(),true);
    assert.equal(await page.locator('[data-disk="disk-b"] [data-action="mount"]').isVisible(),true);
    await closeDisk.click();
    await page.waitForFunction(()=>window.__bridgeRequests.some(r=>r.method==='disks.unmount'&&r.args.id==='disk-b'));
    assert.deepEqual(await page.evaluate(()=>window.__bridgeRequests.filter(r=>r.method==='disks.unmount').at(-1).args),{id:'disk-b'});
    await page.waitForFunction(()=>!document.querySelector('[data-disk="disk-b"] [data-action="unmount"]'));
    await page.locator('[data-disk="disk-b"] [data-action="diskSettings"]').click();
    assert.equal(await page.locator('[data-action="deleteDiskSettings"]').isEnabled(),true);
    await page.locator('[data-action="deleteDiskSettings"]').click();
    await page.locator('#deleteContainer').waitFor();
    assert.equal(await page.locator('#dialogSubmit').isEnabled(),true);
    checks.push('an unlocked unmounted disk can close directly while retaining mount; the exact close RPC enables deletion without mounting');
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks},null,2));
    console.log('TASK_LOG_UI_TESTS_OK '+checks.length);
  }catch(error){fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:false,error:error.stack,checks,pageErrors:errors},null,2));throw error;}
  finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
