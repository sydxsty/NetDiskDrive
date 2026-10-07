'use strict';

// Local diagnostics share the application's existing state updates. No cloud
// listing, payload fetch or additional global state timer is needed here.
class TaskMonitorView {
 constructor({request,formatBytes,formatPhase}) {
  this.request=request;this.bytes=formatBytes;this.phase=formatPhase;
  this.epoch=0;this.diskId='';this.expanded=false;this.lastQuery=0;this.inFlight=null;
 }
 esc(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
 mount(host){
  this.unmount();this.host=host;this.optionsHtml=null;
  host.innerHTML=`<div class="task-network"><strong>网盘请求</strong><span class="task-network-current" role="status"></span><span class="task-network-limits muted"></span></div><details class="task-diagnostics" ${this.expanded?'open':''}><summary>同步诊断</summary><div class="task-diagnostics-controls"><label>磁盘<select class="task-diagnostics-disk" aria-label="选择诊断磁盘"></select></label><button type="button" class="small" data-monitor-op="refresh">刷新诊断</button><span class="muted">本地计数从本次解锁开始；网盘请求为当前账户会话累计。</span></div><div class="task-diagnostics-feedback muted" role="status"></div><div class="task-diagnostics-values"></div></details>`;
  this.details=host.querySelector('.task-diagnostics');
  this.details.addEventListener('toggle',this.onToggle=()=>{
   this.expanded=this.details.open;
   if(this.expanded)this.refresh();else this.invalidate();
  });
  host.querySelector('.task-diagnostics-disk').addEventListener('change',this.onDisk=event=>{
   this.diskId=event.target.value;this.updateDisk();this.refresh();
  });
  host.querySelector('[data-monitor-op="refresh"]').addEventListener('click',this.onRefresh=()=>this.refresh());
 }
 unmount(){
  this.invalidate();
  if(this.host){
   this.expanded=this.details.open;
   this.details.removeEventListener('toggle',this.onToggle);
   this.host.querySelector('.task-diagnostics-disk').removeEventListener('change',this.onDisk);
   this.host.querySelector('[data-monitor-op="refresh"]').removeEventListener('click',this.onRefresh);
  }
  this.host=null;this.details=null;this.disk=null;
 }
 invalidate(){this.epoch++;this.inFlight=null;this.lastQuery=0;}
 update(state,preferredDisk){
  if(!this.host)return;
  this.disks=state.disks||[];this.connected=!!state.connected;
  const network=state.network,limits=network?.limits||{};
  this.host.querySelector('.task-network-current').textContent=network?`当前 ${Number(network.activeRequests||0)} 个请求进行中，${Number(network.queuedRequests||0)} 个排队`:'请求状态暂不可用';
  this.host.querySelector('.task-network-limits').textContent=`所有磁盘共用 · 每秒 ${limits.requestsPerSecond??state.settings?.baiduRequestsPerSecond??3} 次 · 同时 ${limits.maximumConcurrentRequests??state.settings?.baiduMaximumConcurrentRequests??4} 个`;
  if(!this.disks.some(d=>d.id===this.diskId))this.diskId=this.disks.find(d=>d.id===preferredDisk)?.id||this.disks.find(d=>d.unlocked)?.id||this.disks[0]?.id||'';
  const selector=this.host.querySelector('.task-diagnostics-disk');
  const options=this.disks.map(d=>`<option value="${this.esc(d.id)}">${this.esc(d.name)}${d.unlocked?'':' · 未解锁'}</option>`).join('')||'<option value="">没有磁盘</option>';
  if(this.optionsHtml!==options){selector.innerHTML=options;this.optionsHtml=options;}
  selector.value=this.diskId;selector.disabled=!this.disks.length;
  this.updateDisk();this.tick();
 }
 updateDisk(){
  if(!this.host)return;
  const next=this.disks?.find(d=>d.id===this.diskId),ready=!!(this.connected&&next?.unlocked);
  if(this.disk?.id!==next?.id||this.ready!==ready){
   this.invalidate();this.host.querySelector('.task-diagnostics-values').replaceChildren();
   this.feedback(!next?'添加磁盘后可查看诊断。':!this.connected?'磁盘服务连接后可查看诊断。':!next.unlocked?'解锁这块磁盘后可查看诊断。':'展开后读取当前磁盘的诊断。');
  }
  this.disk=next;this.ready=ready;
  this.host.querySelector('[data-monitor-op="refresh"]').disabled=!ready||!!this.inFlight;
 }
 feedback(message,error=false){
  if(!this.host)return;
  const field=this.host.querySelector('.task-diagnostics-feedback');field.textContent=message;field.classList.toggle('error-text',error);
 }
 valid(epoch,id){return !!this.host&&this.details.open&&epoch===this.epoch&&id===this.diskId&&this.ready;}
 tick(){if(this.host&&this.details.open&&!document.hidden&&Date.now()-this.lastQuery>=2000)this.refresh();}
 async refresh(){
  if(!this.host||!this.details.open||!this.ready)return;
  if(this.inFlight)return this.inFlight.promise;
  const epoch=this.epoch,id=this.diskId,operation={};this.inFlight=operation;this.lastQuery=Date.now();
  this.feedback('正在读取同步诊断…');this.host.querySelector('[data-monitor-op="refresh"]').disabled=true;
  operation.promise=(async()=>{
   try{
    const stats=await this.request('sync.diagnostics',{id});
    if(!this.valid(epoch,id))return;
    this.draw(stats);this.feedback('已刷新 · '+new Date().toLocaleTimeString('zh-CN',{hour12:false}));
   }catch(error){if(this.valid(epoch,id))this.feedback('诊断读取失败，保留已有显示：'+error.message,true);}
   finally{if(this.inFlight===operation){this.inFlight=null;if(this.host)this.host.querySelector('[data-monitor-op="refresh"]').disabled=!this.ready;}}
  })();
  return operation.promise;
 }
 draw(stats){
  const labels={phase:'当前阶段',message:'正在处理',phase_elapsed_ms:'当前阶段（ms）',queue_wait_ms:'请求排队（ms）',filesystem_flush_ms:'文件系统刷新（ms）',physical_read_bytes:'本地读取',physical_write_bytes:'本地写入',upload_read_bytes:'上传读取',foreground_read_bytes:'前台读取',foreground_write_bytes:'前台写入',changed_pages:'实际变化页',deduplicated_pages:'相同内容写入',api_requests:'网盘请求',api_queued:'网盘排队请求',api_active:'网盘进行中请求',api_requests_per_second:'全局请求上限（次/秒）',sent_bytes:'实际传输',reused_bytes:'复用内容',seal_fresh_pages:'封口复用新页',seal_disk_pages:'封口读取旧页',seal_validated_pages:'旧页认证校验',seal_padding_write_bytes:'封口补零写入',crypto_pages:'批量页编码',crypto_parallel_pages:'并行编码页',crypto_worker_limit:'编码工作线程上限',metadata_write_pages:'索引写入页',metadata_write_batches:'合并索引写入次数',cloud_index_lookup_batches:'增量索引查询批次'};
  const fields=Object.entries(stats||{}).filter(([key,value])=>labels[key]&&typeof value!=='object');
  this.host.querySelector('.task-diagnostics-values').innerHTML=fields.map(([key,value])=>`<div><span>${labels[key]}</span><b>${this.esc(key.endsWith('_bytes')?this.bytes(value):key==='phase'?this.phase(value):value)}</b></div>`).join('')||'<span class="muted">暂无诊断数据</span>';
 }
}
