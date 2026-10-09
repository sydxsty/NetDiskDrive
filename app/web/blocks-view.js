'use strict';

// The view owns only nearby pages. Physical block numbers never depend on legend order.
class BlockStatusView {
 constructor({request,notify,formatBytes}) {
  this.request=request;this.notify=notify;this.bytes=formatBytes;
  this.labels={failed:'失败',pending:'有更新待上传',uploading:'上传中',uploaded:'已上传待提交',synced:'已同步',local:'未纳入当前云版本',snapshot:'快照引用',metadata:'索引 / 控制',unresolved:'未展开',free:'空闲'};
  const defaults=['failed','pending','uploading','uploaded','synced','snapshot','local','metadata','unresolved','free'];
  try{const saved=JSON.parse(localStorage.getItem('blockLegendOrder')||'null');this.order=Array.isArray(saved)?[...new Set([...saved.filter(x=>defaults.includes(x)),...defaults])]:defaults;}catch{this.order=defaults;}
  this.autoRefresh=localStorage.getItem('blockAutoRefresh')!=='false';
  this.epoch=0;this.cache=new Map();this.loads=new Map();this.failedPages=new Set();this.limit=256;this.maxPages=16;this.filter={};this.summary={};this.total=0;this.revision=0;
  this.onScroll=()=>this.schedule();this.onResize=()=>this.schedule();
 }
 esc(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
 mount(host){
  this.unmount();this.host=host;this.scroll=host.closest('main')||host;this.epoch++;
  host.innerHTML=`<div class="blocks-toolbar"><div class="blocks-heading"><strong>块状态</strong><span class="muted">本地物理块 · 按位置排列</span></div><div class="toolbar"><input class="blocks-jump" type="number" min="0" placeholder="块号" aria-label="跳转块号"><button class="small" data-block-op="jump">跳转</button><button class="small" data-block-op="refresh">刷新</button></div></div><div class="blocks-feedback" role="status"></div><div class="blocks-summary"></div><div class="blocks-types"></div><div class="blocks-legend" aria-label="拖动图例调整主色优先级"></div><div class="blocks-filter"></div><div class="blocks-grid-space"><div class="blocks-spacer-top"></div><div class="blocks-dense-grid"></div><div class="blocks-spacer-bottom"></div></div><div class="blocks-detail hidden"></div>`;
  host.addEventListener('click',this.clickHandler=e=>this.click(e));
  host.addEventListener('dragstart',this.dragStart=e=>{const legend=e.target.closest('[data-legend]');if(legend){this.dragging=legend.dataset.legend;e.dataTransfer.setData('text/plain',this.dragging);e.dataTransfer.effectAllowed='move';}});
  host.addEventListener('dragover',this.dragOver=e=>{if(e.target.closest('[data-legend]')){e.preventDefault();e.dataTransfer.dropEffect='move';}});
  host.addEventListener('drop',this.drop=e=>{const target=e.target.closest('[data-legend]')?.dataset.legend;if(target&&this.dragging&&target!==this.dragging){e.preventDefault();this.moveLegend(this.dragging,target);}this.dragging=null;});
  this.scroll.addEventListener('scroll',this.onScroll,{passive:true});this.resize=new ResizeObserver(this.onResize);this.resize.observe(host);
  const toolbar=host.querySelector('.blocks-toolbar>.toolbar');
  toolbar.insertAdjacentHTML('afterbegin',`<label class="checkbox blocks-auto-refresh"><input type="checkbox" data-block-op="auto-refresh" ${this.autoRefresh?'checked':''}>自动刷新</label>`);
  this.updateTimer();this.drawLegend();
 }
 unmount(){
  this.epoch++;clearInterval(this.timer);cancelAnimationFrame(this.frame);this.resize?.disconnect();
  this.scroll?.removeEventListener('scroll',this.onScroll);
  if(this.host){this.host.removeEventListener('click',this.clickHandler);this.host.removeEventListener('dragstart',this.dragStart);this.host.removeEventListener('dragover',this.dragOver);this.host.removeEventListener('drop',this.drop);}
  this.host=null;this.refreshing=null;this.cache.clear();this.loads.clear();
 }
 updateTimer(){clearInterval(this.timer);this.timer=null;if(this.host&&this.autoRefresh)this.timer=setInterval(()=>this.tick(),2000);}
 setAutoRefresh(enabled){this.autoRefresh=enabled;localStorage.setItem('blockAutoRefresh',String(enabled));this.updateTimer();if(enabled)this.tick();}
 setDisk(disk){
  if(this.disk?.id===disk?.id&&this.disk?.unlocked===disk?.unlocked){this.disk=disk;return;}
  this.disk=disk;this.epoch++;this.refreshing=null;this.cache.clear();this.loads.clear();this.summary={};this.total=0;this.filter={};this.revision=0;this.selected=null;
  if(!this.host)return;this.host.querySelector('.blocks-detail').classList.add('hidden');this.drawStats();this.schedule();
  if(disk?.unlocked)this.refresh();else this.feedback(disk?'打开磁盘后查看块状态。':'请选择磁盘。');
 }
 feedback(text,error=false){if(!this.host)return;const el=this.host.querySelector('.blocks-feedback');el.textContent=text;el.classList.toggle('error-text',error);}
 async refresh(){
  if(!this.host||!this.disk?.unlocked)return;if(this.refreshing)return this.refreshing;
  const old={summary:this.summary,revision:this.revision,cache:new Map(this.cache),total:this.total};
  const ticket=++this.epoch,id=this.disk.id;this.loads.clear();this.failedPages.clear();this.feedback('正在刷新块状态…');const button=this.host.querySelector('[data-block-op="refresh"]');button.disabled=true;button.textContent='刷新中…';
  const work=(async()=>{
   try{
    const summary=await this.request('blocks.summary',{id});if(!this.valid(ticket,id))return;
    this.summary=summary;this.revision=summary.revision??0;this.cache.clear();this.loads.clear();this.total=Number(summary.total_blocks??summary.total_count??0);this.drawStats();
    const position=this.position();await this.loadPage(Math.floor(position.first/this.limit),ticket);if(!this.valid(ticket,id))return;
    this.schedule();this.feedback('已刷新 · '+new Date().toLocaleTimeString('zh-CN',{hour12:false}));
   }catch(error){if(this.valid(ticket,id)){this.summary=old.summary;this.revision=old.revision;this.cache=old.cache;this.total=old.total;this.drawStats();this.schedule();this.feedback('刷新失败，保留已有显示：'+error.message,true);}}
   finally{if(this.valid(ticket,id)){button.disabled=false;button.textContent='刷新';this.refreshing=null;}}
  })();this.refreshing=work;return work;
 }
 valid(ticket,id){return !!this.host&&ticket===this.epoch&&this.disk?.id===id;}
 position(){
  if(!this.host)return{first:0,last:0,columns:1};
  const area=this.host.querySelector('.blocks-grid-space'),width=area.clientWidth||600,columns=Math.max(1,Math.floor((width+3)/43));
  const viewport=this.scroll.getBoundingClientRect(),bounds=area.getBoundingClientRect(),offset=Math.max(0,viewport.top-bounds.top);
  const firstRow=Math.max(0,Math.floor(offset/27)-6),lastRow=Math.ceil((offset+this.scroll.clientHeight)/27)+6;
  return{first:Math.min(firstRow*columns,this.total),last:Math.min(lastRow*columns,this.total),columns};
 }
 schedule(){if(this.frame)return;this.frame=requestAnimationFrame(()=>{this.frame=null;this.drawGrid();});}
 async loadPage(page,ticket=this.epoch){
  if(!this.disk?.unlocked||!this.host)return;const key=String(page);if(this.failedPages.has(key))return;if(this.cache.has(key)){const value=this.cache.get(key);this.cache.delete(key);this.cache.set(key,value);return value;}
  if(this.loads.has(key))return this.loads.get(key);
  const id=this.disk.id,filter={...this.filter};
  const work=(async()=>{
   try{
    const result=await this.request('blocks.query',{id,start:page*this.limit,limit:this.limit,...filter});
    if(!this.valid(ticket,id))return;
    this.total=Number(result.total_count??result.total??0);this.cache.set(key,result.items||[]);this.drawStats();
    while(this.cache.size>this.maxPages)this.cache.delete(this.cache.keys().next().value);
    return result.items||[];
   }catch(error){if(this.valid(ticket,id)){this.failedPages.add(key);this.feedback('块列表加载失败：'+error.message,true);}throw error;}
   finally{if(this.valid(ticket,id)){this.loads.delete(key);this.schedule();}}
  })();this.loads.set(key,work);return work;
 }
 state(block){return block.unresolved===true||block.resolved===false?'unresolved':block.sync_state||'local';}
 color(block){
  if(this.state(block)==='unresolved')return 'unresolved';
  const tags=new Set([this.state(block)]);if(block.snapshot_pinned)tags.add('snapshot');if(block.kind==='metadata'||block.kind==='control'||block.content_type==='container_index')tags.add('metadata');
  return this.order.find(x=>tags.has(x))||'local';
 }
 drawGrid(){
  if(!this.host)return;const {first,last,columns}=this.position(),grid=this.host.querySelector('.blocks-dense-grid');grid.style.gridTemplateColumns=`repeat(${columns},minmax(0,1fr))`;
  this.host.querySelector('.blocks-spacer-top').style.height=Math.floor(first/columns)*27+'px';
  this.host.querySelector('.blocks-spacer-bottom').style.height=Math.max(0,Math.ceil(this.total/columns)-Math.ceil(last/columns))*27+'px';
  const html=[];const missing=new Set();
  for(let n=first;n<last;n++){
   const p=Math.floor(n/this.limit),block=this.cache.get(String(p))?.[n%this.limit];
   if(!block){missing.add(p);html.push('<span class="block-cell skeleton" aria-label="加载中"></span>');continue;}
   const number=block.index??block.number??n,label=this.labels[this.state(block)]||this.state(block);
   html.push(`<button class="block-cell ${this.esc(this.color(block))}${this.selected===number?' selected':''}" data-physical-block="${number}" data-position="${n}" title="块 ${number} · ${this.esc(label)} · ${this.esc(block.content_type||block.kind||'未分类')}${block.snapshot_pinned?' · 快照引用':''}">${number}</button>`);
  }
  LiveDom.html(grid,html.join(''));
  // At most two local metadata requests are active; scrolling never queues the whole disk.
  for(const p of missing){if(this.loads.size>=2)break;this.loadPage(p).catch(()=>{});}
 }
 drawLegend(){if(!this.host)return;LiveDom.html(this.host.querySelector('.blocks-legend'),`<span class="muted">主色优先级（可拖动）</span>${this.order.map(key=>`<button draggable="true" data-legend="${key}" class="legend-chip" title="拖动调整；也可用左右方向键"><i class="${key}"></i>${this.labels[key]}</button>`).join('')}`);
  this.host.querySelectorAll('[data-legend]').forEach(el=>el.onkeydown=e=>{if(!['ArrowLeft','ArrowRight'].includes(e.key))return;const from=this.order.indexOf(el.dataset.legend),to=from+(e.key==='ArrowLeft'?-1:1);if(to<0||to>=this.order.length)return;e.preventDefault();[this.order[from],this.order[to]]=[this.order[to],this.order[from]];this.saveOrder();this.host.querySelector(`[data-legend="${el.dataset.legend}"]`)?.focus();});
 }
 moveLegend(from,to){this.order.splice(this.order.indexOf(from),1);this.order.splice(this.order.indexOf(to),0,from);this.saveOrder();}
 saveOrder(){localStorage.setItem('blockLegendOrder',JSON.stringify(this.order));this.drawLegend();this.schedule();}
 drawStats(){
  if(!this.host)return;const s=this.summary,counts=s.counts||{},pending=s.pending_objects??counts.pending??0;
  this.host.querySelector('.blocks-heading .muted').textContent='本地物理块 · 每格 '+this.bytes(s.object_size??this.disk?.objectSizeBytes??4194304)+' · 按位置排列';
  LiveDom.html(this.host.querySelector('.blocks-summary'),`<button class="block-stat${this.filter.sync_state==='pending'?' active':''}" data-status-filter="pending"><span>待同步对象</span><strong>${pending}</strong><small>压缩前 ${s.pending_bytes_estimated?"约 ":""}${this.bytes(s.pending_bytes??pending*Number(s.object_size??this.disk?.objectSizeBytes??4194304))}</small>${s.pending_missing_objects>0?`<small>其中未缓存在本地 ${s.pending_missing_objects} 个</small>`:''}</button><div class="block-stat"><span>已落盘变化页 · 4 KiB</span><strong>${s.changed_pages??0}</strong><small>缓存待保存 ${s.ram_pending_pages??0} 个页版本</small></div>${['uploading','uploaded','synced','failed'].map(key=>`<button class="block-stat${this.filter.sync_state===key?' active':''}" data-status-filter="${key}"><span>${this.labels[key]}</span><strong>${counts[key]??0}</strong></button>`).join('')}<div class="block-stat block-stat-work"><span>本次版本剩余 ${s.current_job?.pending_objects??0} 个</span><small>${this.bytes(s.current_job?.pending_bytes??0)}</small><span>后续新修改 ${s.new_changes?.changed_pages??0} 页</span><small>逻辑变化 ${this.bytes(s.new_changes?.logical_changed_bytes??0)}</small></div>`);
  const names={ntfs_mft:'NTFS MFT',ntfs_log:'NTFS 日志',ntfs_usn:'NTFS USN',ntfs_metadata:'NTFS 其他元数据',data:'普通数据 / 未分类',unknown:'普通数据 / 未分类',container_index:'容器索引',control:'本地控制',free:'空闲'};
  LiveDom.html(this.host.querySelector('.blocks-types'),(s.types||[]).map(type=>`<button class="type-chip${this.filter.content_type===type.id?' active':''}" data-type-filter="${this.esc(type.id)}"><span>${this.esc(type.label||names[type.id]||type.id)}</span><b>${type.objects??0}</b><small>待传 ${type.pending_objects??0} · 压缩前 ${this.bytes(type.pending_bytes??0)}</small></button>`).join('')+'<span class="type-chip"><span>本地控制数据</span><small>不计入上传量</small></span>');
  LiveDom.html(this.host.querySelector('.blocks-filter'),`<span>${Number(s.total_blocks??0)} 个物理块${Object.keys(this.filter).length?' · 当前筛选 '+this.total+' 个':''}</span>${Object.keys(this.filter).length?'<button class="quiet small" data-block-op="clear">清除筛选</button>':''}<span class="muted">上传按对象身份计数；筛选显示本地物理块，快照引用不代表正在上传</span>`+(s.index_complete===false||this.disk?.replica?.indexComplete===false?'<span class="blocks-unresolved-note">云端索引尚未全部展开；统计仅包含已知对象，未展开区域不计为空闲。查看本页不会下载未访问内容。</span>':''));
 }
 async applyFilter(key,value){
  if(key){if(this.filter[key]===value)delete this.filter[key];else this.filter[key]=value;}else this.filter={};
  this.epoch++;this.refreshing=null;this.loads.clear();this.cache.clear();this.selected=null;this.scroll.scrollTop=0;await this.refresh();
 }
 async tick(){
  if(!this.autoRefresh||!this.host||!this.disk?.unlocked||document.hidden||this.refreshing||this.ticking)return;this.ticking=true;
  const ticket=this.epoch,id=this.disk.id;
  try{
   const changes=await this.request('blocks.changes',{id,since_revision:this.revision});if(!this.autoRefresh||!this.valid(ticket,id))return;
   if(changes.revision!==this.revision||changes.reset_required){await this.refresh();return;}
  }catch(error){if(this.autoRefresh&&this.valid(ticket,id))this.feedback('自动更新暂不可用：'+error.message,true);}finally{this.ticking=false;}
 }
 async click(event){
  const status=event.target.closest('[data-status-filter]'),type=event.target.closest('[data-type-filter]'),button=event.target.closest('[data-physical-block]'),op=event.target.closest('[data-block-op]')?.dataset.blockOp;
  try{
   if(op==='auto-refresh'){this.setAutoRefresh(event.target.checked);return;}
   if(status)return this.applyFilter('sync_state',status.dataset.statusFilter);if(type)return this.applyFilter('content_type',type.dataset.typeFilter);
   if(op==='refresh')return this.refresh();if(op==='clear')return this.applyFilter();
   if(op==='jump'){
    const value=Math.max(0,Math.floor(Number(this.host.querySelector('.blocks-jump').value)||0));if(Object.keys(this.filter).length)await this.applyFilter();
    const area=this.host.querySelector('.blocks-grid-space'),columns=this.position().columns;
    this.scroll.scrollTop+=area.getBoundingClientRect().top-this.scroll.getBoundingClientRect().top+Math.floor(Math.min(value,Math.max(0,this.total-1))/columns)*27;this.schedule();return;
   }
   if(button){const n=Number(button.dataset.position),block=this.cache.get(String(Math.floor(n/this.limit)))?.[n%this.limit];if(!block||(block.index??block.number??n)!==Number(button.dataset.physicalBlock))return;this.selected=block.index??n;const details=this.host.querySelector('.blocks-detail');details.classList.remove('hidden');details.innerHTML=`<div class="blocks-detail-head"><strong>块 ${this.selected} · ${this.esc(this.labels[this.state(block)]||this.state(block))}</strong><button class="quiet small" data-block-op="close-detail">关闭详情</button></div><pre>${this.esc(JSON.stringify(block,null,2))}</pre>`;this.schedule();return;}
   if(op==='close-detail')this.host.querySelector('.blocks-detail').classList.add('hidden');
  }catch(error){this.notify(error.message,true);}
 }
}
