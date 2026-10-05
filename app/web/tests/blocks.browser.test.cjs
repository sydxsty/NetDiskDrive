'use strict';
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const {chromium}=require(process.argv[3]||'playwright');
const output=path.resolve(process.argv[2]),web=path.resolve(__dirname,'..');fs.mkdirSync(output,{recursive:true});
(async()=>{
 const server=http.createServer((req,res)=>{const name=new URL(req.url,'http://localhost').pathname.slice(1)||'index.html',file=path.resolve(web,name);if(!file.startsWith(web+path.sep)){res.writeHead(403);res.end();return;}fs.readFile(file,(err,data)=>{res.writeHead(err?404:200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'application/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml'}[path.extname(file)]||'application/octet-stream'});res.end(err?'missing':data);});});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({headless:true,executablePath:process.argv[4]||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const context=await browser.newContext({viewport:{width:1320,height:900},locale:'zh-CN'}),page=await context.newPage(),errors=[],checks=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  let listener;window.__calls=[];window.__revision=1;window.__disk='a';
  const state={connected:true,driverAvailable:true,account:null,settings:{},tasks:[],disks:['a','b'].map(id=>({id,name:'测试盘 '+id,driveLetter:id==='a'?'Z':'Y',capacityBytes:1099511627776,unlocked:true,mounted:true}))};
  const respond=(requestId,data,error)=>queueMicrotask(()=>listener({data:{requestId,ok:!error,data,error}}));
  window.chrome={webview:{addEventListener:(name,fn)=>{if(name==='message')listener=fn;},postMessage:({requestId,method,args})=>{
   window.__calls.push({method,args});let data;
   if(method==='app.state')data=state;
   else if(method==='blocks.summary'){
    data={revision:window.__revision,total_blocks:1000000,pending_objects:200000,pending_bytes:838860800000,changed_pages:17,counts:{pending:200000,synced:200000,uploading:2,uploaded:3,failed:1},types:[{id:'ntfs_mft',label:'NTFS MFT',objects:10,pending_objects:2,pending_bytes:8388608},{id:'data',label:'普通数据 / 未分类',objects:999990,pending_objects:199998,pending_bytes:838852411392}],current_job:{pending_objects:4,pending_bytes:16777216},new_changes:{changed_pages:3,pending_bytes:4194304}};
    if(window.__failSummary){window.__failSummary=false;respond(requestId,null,'模拟刷新失败');return;}
    if(window.__deferSummary){window.__deferSummary=false;window.__releaseSummary=()=>respond(requestId,data);return;}
   }else if(method==='blocks.query'){
    if(window.__failQuery){window.__failQuery=false;window.__failedStart=args.start;respond(requestId,null,'模拟分页失败');return;}
    const filtered=!!args.sync_state,total=filtered?200000:args.content_type?10:1000000;
    data={revision:window.__revision,total_count:total,items:Array.from({length:Math.max(0,Math.min(args.limit,total-args.start))},(_,i)=>{const index=filtered?(args.start+i)*5+1:args.start+i;return{index,object_id:'object-'+args.id+'-'+index,kind:'data',content_type:args.content_type||'data',sync_state:args.sync_state||['synced','pending','local','uploaded','free'][index%5],snapshot_pinned:index%5===0,upload_pinned:true,length:4194304,used_pages:17};})};
   }else if(method==='blocks.changes')data={revision:window.__revision,reset_required:false};
   else if(method==='sync.diagnostics')data={phase:'uploading',message:'等待网盘响应',physical_read_bytes:4194304,api_requests:3};
   else data={items:[]};
   respond(requestId,data);
  }}};
 });
 try{
  await page.goto('http://127.0.0.1:'+server.address().port);await page.locator('nav [data-page="blocks"]').click();
  await page.waitForFunction(()=>document.querySelector('.blocks-feedback')?.textContent.startsWith('已刷新'));
  assert(await page.locator('.block-cell').count()<2000);assert.equal(await page.locator('.pagination').count(),0);
  assert.equal(await page.locator('[data-physical-block="0"]').getAttribute('class'),'block-cell synced');
  checks.push('one million blocks use bounded virtual DOM; uploaded status is not replaced by snapshot/upload pin');
  await page.evaluate(()=>{blockStatus.summary.pending_objects=200005;blockStatus.summary.pending_missing_objects=5;blockStatus.summary.pending_resident_objects=200000;blockStatus.drawStats();});
  assert.equal(await page.locator('[data-status-filter="pending"] strong').innerText(),'200005');
  assert.match(await page.locator('[data-status-filter="pending"]').innerText(),/未缓存在本地 5 个/);
  assert.match(await page.locator('.blocks-filter').innerText(),/筛选显示本地物理块/);
  checks.push('Logical pending uploads include missing objects, while the grid and filters explicitly show physical local blocks.');
  await page.evaluate(()=>{blockStatus.summary.object_size=16777216;blockStatus.summary.pending_objects=3;delete blockStatus.summary.pending_bytes;blockStatus.drawStats();});
  assert.match(await page.locator('.blocks-heading').innerText(),/16.0 MiB/);
  assert.match(await page.locator('[data-status-filter="pending"]').innerText(),/48.0 MiB/);
  checks.push('Block labels and missing-byte estimates follow the authenticated per-volume 16 MiB geometry.');
  await page.evaluate(()=>blockStatus.refresh());


  await page.screenshot({path:path.join(output,'blocks-overview.png')});
  await page.locator('[data-legend="snapshot"]').dragTo(page.locator('[data-legend="synced"]'));
  await page.waitForFunction(()=>document.querySelector('[data-physical-block="0"]')?.classList.contains('snapshot'));
  assert.equal(await page.locator('[data-physical-block]').first().getAttribute('data-physical-block'),'0');
  assert((await page.evaluate(()=>JSON.parse(localStorage.getItem('blockLegendOrder')))).indexOf('snapshot')<(await page.evaluate(()=>JSON.parse(localStorage.getItem('blockLegendOrder')))).indexOf('synced'));
  checks.push('dragging changes color priority and persists order while preserving physical block order');
  await page.locator('[data-status-filter="pending"]').click();
  await page.waitForFunction(()=>document.querySelector('[data-physical-block]')?.dataset.physicalBlock==='1');
  assert(await page.locator('[data-physical-block]').evaluateAll(nodes=>nodes.every(n=>Number(n.dataset.physicalBlock)%5===1)));
  await page.locator('[data-block-op="clear"]').click();await page.waitForFunction(()=>document.querySelector('[data-physical-block]')?.dataset.physicalBlock==='0');
  await page.locator('.blocks-jump').fill('900000');await page.locator('[data-block-op="jump"]').click();
  await page.waitForFunction(()=>[...document.querySelectorAll('[data-physical-block]')].some(n=>Number(n.dataset.physicalBlock)>=900000));
  assert(await page.locator('.block-cell').count()<2000);assert(await page.evaluate(()=>blockStatus.cache.size<=16));
  assert(await page.evaluate(()=>window.__calls.filter(c=>c.method==='blocks.query').every(c=>c.args.limit<=256)));
  checks.push('interactive server filters, large block jump, bounded page cache and request sizes');
  await page.evaluate(()=>{document.querySelector('main').scrollTop=0;window.__deferSummary=true;blockStatus.refresh();});
  await page.waitForFunction(()=>document.querySelector('[data-block-op="refresh"]')?.disabled);
  assert.match(await page.locator('.blocks-feedback').innerText(),/正在刷新/);
  const calls=await page.evaluate(()=>window.__calls.filter(c=>c.method==='blocks.summary').length);
  await page.evaluate(()=>{blockStatus.refresh();blockStatus.refresh();});assert.equal(await page.evaluate(()=>window.__calls.filter(c=>c.method==='blocks.summary').length),calls);
  await page.evaluate(()=>window.__releaseSummary());await page.waitForFunction(()=>!document.querySelector('[data-block-op="refresh"]').disabled);
  await page.evaluate(()=>{window.__failSummary=true;return blockStatus.refresh();});assert.match(await page.locator('.blocks-feedback').innerText(),/刷新失败/);assert(await page.locator('[data-physical-block]').count()>0);
  checks.push('refresh immediately shows busy, coalesces duplicate calls, completes explicitly and retains prior data on errors');
  await page.evaluate(()=>{window.__failQuery=true;return blockStatus.refresh();});const failedStart=await page.evaluate(()=>window.__failedStart);
  const countFailed=()=>page.evaluate(start=>window.__calls.filter(c=>c.method==='blocks.query'&&c.args.start===start).length,failedStart);
  const before=await countFailed();await page.waitForTimeout(200);assert.equal(await countFailed(),before);
  await page.evaluate(()=>blockStatus.refresh());
  await page.evaluate(()=>{window.__deferSummary=true;blockStatus.refresh();});
  await page.locator('#diskSelector').selectOption('b');await page.waitForFunction(()=>document.querySelector('[data-physical-block]')?.title&&blockStatus.disk.id==='b'&&!blockStatus.refreshing);
  await page.evaluate(()=>window.__releaseSummary());assert.equal(await page.evaluate(()=>blockStatus.disk.id),'b');
  assert(await page.evaluate(()=>[...blockStatus.cache.values()].flat().every(b=>b.object_id.startsWith('object-b-'))));
  checks.push('failed page loads do not retry in a loop; stale disk responses cannot replace the selected disk');
  await page.locator('[data-block-op="auto-refresh"]').uncheck();
  const polling=()=>page.evaluate(()=>window.__calls.filter(c=>c.method==='blocks.changes').length);
  const stoppedAt=await polling();await page.waitForTimeout(2200);assert.equal(await polling(),stoppedAt);
  await page.evaluate(()=>{window.__revision++;});
  await page.locator('[data-block-op="refresh"]').click();
  await page.waitForFunction(()=>blockStatus.revision===window.__revision&&!blockStatus.refreshing);
  await page.reload();await page.locator('nav [data-page="blocks"]').click();
  await page.waitForFunction(()=>document.querySelector('.blocks-feedback')?.textContent.startsWith('已刷新'));
  assert.equal(await page.locator('[data-block-op="auto-refresh"]').isChecked(),false);
  const reloadedAt=await polling();await page.waitForTimeout(2200);assert.equal(await polling(),reloadedAt);
  await page.locator('[data-block-op="auto-refresh"]').check();
  await page.waitForFunction(n=>window.__calls.filter(c=>c.method==='blocks.changes').length>n,reloadedAt);
  checks.push('Auto-refresh can be disabled persistently: no polling requests after stopping or reload, manual refresh still works, and enabling resumes polling.');
  await page.locator('.blocks-diagnostics summary').click();await page.evaluate(()=>blockStatus.tick());assert.match(await page.locator('.blocks-diagnostics').innerText(),/等待网盘响应/);
  await page.screenshot({path:path.join(output,'blocks.png')});assert.deepEqual(errors,[]);
  fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({passed:true,checks},null,2));console.log('BLOCKS_UI_OK '+checks.length);
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(error=>{console.error(error);process.exitCode=1;});
