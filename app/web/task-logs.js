(() => {
  'use strict';
  const PAGE_SIZE = 200;
  const escape = value => String(value ?? '').replace(/[&<>"']/g, character => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[character]));
  const actionLabels = {
    'sync.started':'开始同步','sync.completed':'同步完成','sync.confirmed':'同步完成','sync.failed':'同步失败','sync.paused':'同步暂停',
    'upload.started':'开始上传','upload.confirmed':'上传确认','upload.failed':'上传失败','object.reused':'复用已有对象',
    'commit.confirmed':'版本确认','cleanup.started':'开始清理','cleanup.completed':'清理完成','cleanup.confirmed':'清理确认','cleanup.failed':'清理失败',
    'restore.started':'开始恢复','restore.completed':'恢复完成','restore.failed':'恢复失败','restore.paused':'恢复暂停',
    'download.started':'开始下载','download.confirmed':'下载确认','download.failed':'下载失败',
    'demand.started':'按需下载','demand.verified':'下载校验通过','prefetch.started':'提前加载','prefetch.verified':'预取校验通过',
    'disk.removed':'移出列表','disk.deleted':'删除本地容器','disks.delete':'移除或删除磁盘',
    'cache.hit':'使用同步记录','cache.reconcile':'核对云端版本','cache.recovered':'恢复发布记录',
    'preparing.started':'准备同步版本','upload.reused':'复用已有对象','sync.unchanged':'没有新变更',
    'cleanup.deferred':'等待恢复结束','cleanup.deleted':'清理确认','cleanup.pending':'等待继续清理'
  };
  const kindLabels = {sync:'同步',restore:'云端恢复',snapshotRestore:'快照恢复',compact:'整理',download:'按需读取',network:'网盘请求',disk:'磁盘',system:'应用'};
  const objectKinds = {data:'数据块',map:'索引对象',root:'根对象',metadata:'元数据',index:'索引对象',commit:'版本根对象',obsolete:'旧对象'};
  function timestamp(value) {
    const date = new Date(value);
    if (!value || Number.isNaN(date.getTime())) return {date:'—',time:'—',full:'时间不可用'};
    return {
      date:date.toLocaleDateString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit'}),
      time:date.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}),
      full:date.toLocaleString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false})
    };
  }
  function status(entry) {
    if (String(entry.level).toLowerCase() === 'error' || /\.failed$/.test(entry.action)) return ['失败','red'];
    if (String(entry.level).toLowerCase() === 'warning') return ['提醒','amber'];
    if (/\.paused$/.test(entry.action)) return ['已暂停','amber'];
    if (entry.action === 'object.reused' || entry.action === 'upload.reused') return ['已复用','blue'];
    if (/\.(confirmed|completed|deleted|removed|unchanged|verified)$/.test(entry.action)) return ['成功','green'];
    if (/\.started$/.test(entry.action)) return ['开始','blue'];
    return ['记录',''];
  }

  class TaskLogView {
    constructor({request,getDisks,formatBytes,notify}) {
      this.request=request; this.getDisks=getDisks; this.formatBytes=formatBytes; this.notify=notify;
      this.items=[]; this.before=null; this.history=[]; this.nextCursor=null; this.hasMore=false;
      this.diskId=''; this.level=''; this.revision=0; this.requestTicket=0; this.loading=false; this.polling=false;
      this.latestSequence=null; this.hasNew=false; this.error=''; this.loaded=false; this.scrollTop=0;
      this.retainedFrom=null; this.retentionCount=null; this.warning=''; this.host=null; this.timer=null;
    }
    mount(host) {
      if (this.host === host) return;
      this.host=host;
      host.innerHTML=`<div class="journal-heading"><div><h2>任务日志</h2><p>最近保留的日志 · 最新在前 · 时间为本机时区</p></div><button class="small" data-log-action="refresh">刷新日志</button></div>
        <div class="journal-tools"><label>磁盘<select class="journal-disk-filter" aria-label="筛选日志磁盘"></select></label><label>结果<select class="journal-level-filter" aria-label="筛选日志结果"><option value="">全部记录</option><option value="error">仅错误</option></select></label><span class="journal-retention muted"></span></div>
        <div class="journal-warning hidden" role="status"></div><div class="journal-status" aria-live="polite"><span class="journal-status-text"></span><button class="small hidden" data-log-action="latest">有新记录 · 查看最新</button></div>
        <div class="journal-scroll" tabindex="0" aria-label="任务日志记录"><table class="journal-table"><thead><tr><th scope="col">时间（本地）</th><th scope="col">结果</th><th scope="col">磁盘 / 任务</th><th scope="col">记录</th></tr></thead><tbody></tbody></table><div class="journal-empty hidden"></div></div>
        <div class="journal-pagination"><span class="journal-page-info"></span><div><button class="small" data-log-action="newer">较新一页</button><button class="small" data-log-action="older">较早一页</button><button class="quiet small" data-log-action="latest">回到最新</button></div></div>`;
      host.querySelector('.journal-level-filter').value=this.level;
      host.addEventListener('click',event => {
        const button=event.target.closest('[data-log-action]');
        if (!button) return;
        event.preventDefault(); event.stopPropagation();
        const action=button.dataset.logAction;
        if (action==='refresh'||action==='latest') this.refresh();
        if (action==='older') this.older();
        if (action==='newer') this.newer();
        if (action==='copy') this.copyObject(button.dataset.sequence);
      });
      host.addEventListener('change',event => {
        if (!event.target.matches('.journal-disk-filter,.journal-level-filter')) return;
        this.diskId=host.querySelector('.journal-disk-filter').value;
        this.level=host.querySelector('.journal-level-filter').value;
        this.revision++; this.polling=false; this.before=null; this.history=[]; this.items=[]; this.loaded=false; this.latestSequence=null; this.hasNew=false;
        this.load({resetScroll:true});
      });
      this.updateDisks(); this.renderRows(); this.renderStatus();
      host.querySelector('.journal-scroll').scrollTop=this.scrollTop;
      this.timer=setInterval(()=>this.poll(),3000);
      if (!this.loaded) this.load(); else this.poll();
    }
    unmount() {
      if (this.host) this.scrollTop=this.host.querySelector('.journal-scroll').scrollTop;
      clearInterval(this.timer); this.timer=null; this.host=null; this.revision++;
      this.loading=false; this.polling=false;
    }
    args(before=null) {
      return {limit:PAGE_SIZE,...(before!==null?{before}:{}),...(this.diskId?{diskId:this.diskId}:{}),...(this.level?{level:this.level}:{})};
    }
    normalize(result) {
      const items=(Array.isArray(result?.items)?result.items:[])
        .filter(entry=>entry&&Number.isSafeInteger(Number(entry.sequence))&&Number(entry.sequence)>=0)
        .sort((a,b)=>Number(b.sequence)-Number(a.sequence)).slice(0,PAGE_SIZE);
      const next=result?.nextCursor;
      const nextCursor=next!==undefined&&next!==null&&Number.isSafeInteger(Number(next))?Number(next):null;
      return {items,nextCursor,hasMore:result?.hasMore===true&&nextCursor!==null,
        retainedFrom:result?.oldestTimestampUtc??result?.retainedFrom??null,
        retentionCount:Number.isFinite(result?.retentionCount)?result.retentionCount:null,
        warning:typeof result?.warning==='string'?result.warning:''};
    }
    async load({resetScroll=false,before=this.before,history=this.history}={}) {
      if (!this.host) return;
      const revision=this.revision,ticket=++this.requestTicket,args=this.args(before);
      this.loading=true; this.error=''; this.renderStatus();
      try {
        const result=this.normalize(await this.request('tasks.logs',args));
        if (!this.host||revision!==this.revision||ticket!==this.requestTicket) return;
        const scroll=this.host.querySelector('.journal-scroll');
        const position=resetScroll?0:scroll.scrollTop;
        this.before=before; this.history=[...history];
        this.items=result.items; this.nextCursor=result.nextCursor; this.hasMore=result.hasMore;
        this.retainedFrom=result.retainedFrom; this.retentionCount=result.retentionCount; this.warning=result.warning; this.loaded=true;
        if (this.before===null) { this.latestSequence=this.items[0]?.sequence??null; this.hasNew=false; }
        this.updateDisks(); this.renderRows(); scroll.scrollTop=position;
      } catch (error) {
        if (this.host&&revision===this.revision&&ticket===this.requestTicket) {
          this.error=error.message||'日志暂时无法载入。';
          if (this.items.length===0) this.renderRows();
        }
      } finally {
        if (revision===this.revision&&ticket===this.requestTicket) { this.loading=false; this.renderStatus(); }
      }
    }
    async poll() {
      if (!this.host||document.hidden||this.loading||this.polling) return;
      const revision=this.revision;
      this.polling=true;
      try {
        const result=this.normalize(await this.request('tasks.logs',this.args()));
        if (!this.host||revision!==this.revision) return;
        this.error=''; this.retainedFrom=result.retainedFrom; this.retentionCount=result.retentionCount; this.warning=result.warning;
        const newest=result.items[0]?.sequence??null;
        if (!this.loaded||(newest!==null&&String(newest)!==String(this.latestSequence))) {
          const selection=document.getSelection();
          const selecting=selection&&!selection.isCollapsed&&this.host.contains(selection.anchorNode);
          const interacting=this.host.contains(document.activeElement)&&document.activeElement.matches('button,select,input');
          const scroll=this.host.querySelector('.journal-scroll');
          if (this.before===null&&scroll.scrollTop<=8&&!selecting&&!interacting) {
            this.items=result.items; this.nextCursor=result.nextCursor; this.hasMore=result.hasMore; this.loaded=true;
            this.hasNew=false; this.updateDisks(); this.renderRows(); scroll.scrollTop=0;
          } else this.hasNew=true;
          this.latestSequence=newest;
        }
        this.renderStatus();
      } catch (error) {
        if (this.host&&revision===this.revision) { this.error=error.message||'日志暂时无法更新。'; this.renderStatus(); }
      } finally { if (revision===this.revision) this.polling=false; }
    }
    refresh() {
      this.revision++; this.polling=false;
      return this.load({resetScroll:true,before:null,history:[]});
    }
    older() {
      if (this.loading||!this.hasMore||this.nextCursor===null) return;
      const history=[...this.history,this.before],before=this.nextCursor;
      this.revision++; this.polling=false;
      return this.load({resetScroll:true,before,history});
    }
    newer() {
      if (this.loading||this.history.length===0) return;
      const before=this.history[this.history.length-1],history=this.history.slice(0,-1);
      this.revision++; this.polling=false;
      return this.load({resetScroll:true,before,history});
    }
    name(id) { return this.getDisks().find(d=>d.id===id)?.name || (id?'磁盘 '+String(id).slice(0,8):'应用'); }
    updateDisks() {
      if (!this.host) return;
      const names=new Map(this.getDisks().map(d=>[d.id,d.name]));
      for (const entry of this.items) if (entry.diskId&&!names.has(entry.diskId)) names.set(entry.diskId,this.name(entry.diskId));
      if (this.diskId&&!names.has(this.diskId)) names.set(this.diskId,this.name(this.diskId));
      const select=this.host.querySelector('.journal-disk-filter');
      const options='<option value="">全部磁盘</option>'+[...names].map(([id,name])=>`<option value="${escape(id)}">${escape(name)}</option>`).join('');
      if (select.dataset.options!==options&&document.activeElement!==select) { select.innerHTML=options;select.value=this.diskId;select.dataset.options=options; }
      for (const node of this.host.querySelectorAll('.journal-disk-name')) node.textContent=this.name(node.dataset.diskId);
    }
    renderRows() {
      if (!this.host) return;
      const body=this.host.querySelector('tbody'),empty=this.host.querySelector('.journal-empty');
      body.innerHTML=this.items.map(entry=>{
        const time=timestamp(entry.timestampUtc),[label,color]=status(entry);
        const details=[];
        if (entry.objectKind) details.push(objectKinds[entry.objectKind]||entry.objectKind);
        if (entry.bytes!==null&&entry.bytes!==undefined) details.push((entry.wireBytes!=null?'原对象 ':'')+this.formatBytes(entry.bytes));
        if (entry.wireBytes!==null&&entry.wireBytes!==undefined) details.push('压缩后 '+this.formatBytes(entry.wireBytes));
        if (entry.generation!==null&&entry.generation!==undefined) details.push('版本 '+entry.generation);
        const action=actionLabels[entry.action]||kindLabels[entry.kind]||'后台操作';
        return `<tr data-log-sequence="${escape(entry.sequence)}"><td><time datetime="${escape(entry.timestampUtc)}" title="${escape(time.full)}">${escape(time.date)}<br><strong>${escape(time.time)}</strong></time><span class="journal-sequence">#${escape(entry.sequence)}</span></td>
          <td><span class="badge ${color}">${label}</span></td><td><strong class="journal-disk-name" data-disk-id="${escape(entry.diskId||'')}" title="${escape(entry.diskId||'')}">${escape(this.name(entry.diskId))}</strong><span class="journal-kind">${escape(kindLabels[entry.kind]||entry.kind||'')}</span></td>
          <td><div class="journal-message"><strong>${escape(action)}</strong><span>${escape(entry.message||'')}</span></div>${entry.objectId?`<div class="journal-object"><span>对象</span><code>${escape(entry.objectId)}</code><button class="quiet small" data-log-action="copy" data-sequence="${escape(entry.sequence)}" aria-label="复制完整对象 ID">复制 ID</button></div>`:''}${details.length?`<div class="journal-meta">${details.map(escape).join(' · ')}</div>`:''}</td></tr>`;
      }).join('');
      empty.textContent=this.error?'暂时无法载入日志，请重试。':this.loaded?'当前范围内没有记录，可调整筛选或返回最新日志。':'正在载入日志…';
      empty.classList.toggle('hidden',this.items.length!==0);
    }
    renderStatus() {
      if (!this.host) return;
      const status=this.host.querySelector('.journal-status-text');
      status.textContent=this.error||(this.loading?'正在载入…':this.before===null?'查看最新记录；阅读时有新日志会提示。':'正在查看较早记录，自动更新不会改变这一页。');
      status.classList.toggle('error-text',!!this.error);
      this.host.querySelector('.journal-status [data-log-action="latest"]').classList.toggle('hidden',!this.hasNew);
      this.host.querySelector('[data-log-action="older"]').disabled=this.loading||!this.hasMore;
      this.host.querySelector('[data-log-action="newer"]').disabled=this.loading||this.history.length===0;
      this.host.querySelector('[data-log-action="refresh"]').disabled=this.loading;
      this.host.querySelector('.journal-page-info').textContent=`第 ${this.history.length+1} 页 · 本页 ${this.items.length} 条 · 每页最多 ${PAGE_SIZE} 条`;
      this.host.querySelector('.journal-retention').textContent=[this.retentionCount!==null?'最多保留 '+this.retentionCount.toLocaleString('zh-CN')+' 条':'',this.retainedFrom?'起始 '+timestamp(this.retainedFrom).full:''].filter(Boolean).join(' · ');
      const warning=this.host.querySelector('.journal-warning');warning.textContent=this.warning;warning.classList.toggle('hidden',!this.warning);
    }
    async copyObject(sequence) {
      const entry=this.items.find(item=>String(item.sequence)===String(sequence));
      if (!entry?.objectId) return;
      const value=String(entry.objectId);
      try {
        if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
        await navigator.clipboard.writeText(value);
        this.notify('完整对象 ID 已复制');
      } catch {
        const focused=document.activeElement,field=document.createElement('textarea');
        field.value=value;field.className='journal-copy-buffer';field.setAttribute('readonly','');document.body.append(field);field.select();
        const copied=document.execCommand('copy');field.remove();focused?.focus({preventScroll:true});
        this.notify(copied?'完整对象 ID 已复制':'复制未完成，请选中完整对象 ID 手动复制。',!copied);
      }
    }
  }
  window.TaskLogView=TaskLogView;
})();
