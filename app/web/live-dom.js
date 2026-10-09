'use strict';

// Refresh existing nodes instead of replacing controls under the pointer. Keep
// the latest deferred update only; a removed page must never be rendered again.
window.LiveDom=(()=>{
 const edits=new WeakSet(),waiting=new Map();
 let pointer=null,flushTimer=null;
 const keys=['id','data-task','data-disk','data-snapshot','data-cloud','data-physical-block','data-log-sequence','data-legend','data-status-filter','data-type-filter','data-metric','data-preparation-stage','data-action','data-block-op','data-log-action','value'];
 function key(node){
  if(node.nodeType!==1)return null;
  for(const name of keys)if(node.hasAttribute(name))return node.tagName+':'+name+':'+node.getAttribute(name);
  return node.classList.length?node.tagName+':class:'+node.classList[0]:null;
 }
 function held(host){
  if(pointer&&host.contains(pointer))return true;
  if(document.activeElement?.tagName==='SELECT'&&host.contains(document.activeElement))return true;
  const selection=document.getSelection();
  return !!selection&&!selection.isCollapsed&&(host.contains(selection.anchorNode)||host.contains(selection.focusNode));
 }
 function flush(){
  flushTimer=null;
  for(const [host,work] of waiting){
   if(!host.isConnected){waiting.delete(host);continue;}
   if(held(host))continue;
   waiting.delete(host);work();
  }
 }
 function schedule(){if(flushTimer===null)flushTimer=setTimeout(flush,0);}
 function update(host,work){
  if(!host?.isConnected)return;
  if(held(host)){waiting.set(host,work);return;}
  waiting.delete(host);work();
 }
 function attributes(old,next){
  // Expanded details belong to the reader; controls retain unsaved edits even
  // after focus moves to another setting or the Save button.
  const protect=edits.has(old)||old===document.activeElement;
  for(const attr of [...old.attributes]){
   if((protect&&['value','checked','selected'].includes(attr.name))||(old.tagName==='DETAILS'&&attr.name==='open'))continue;
   if(!next.hasAttribute(attr.name))old.removeAttribute(attr.name);
  }
  for(const attr of next.attributes){
   if((protect&&['value','checked','selected'].includes(attr.name))||(old.tagName==='DETAILS'&&attr.name==='open'))continue;
   if(old.getAttribute(attr.name)!==attr.value)old.setAttribute(attr.name,attr.value);
  }
  return protect;
 }
 function morph(old,next){
  if(old.nodeType!==next.nodeType||old.nodeName!==next.nodeName){old.replaceWith(next.cloneNode(true));return;}
  if(old.nodeType!==1){if(old.nodeValue!==next.nodeValue)old.nodeValue=next.nodeValue;return;}
  const protect=attributes(old,next),value=old.value;
  if(old.tagName!=='INPUT')children(old,next);
  if(old.matches('input,select,textarea')){
   if(protect){if(old.tagName==='SELECT')old.value=value;}
   else{old.value=next.value;if(old.tagName==='INPUT')old.checked=next.checked;}
  }
 }
 function children(host,next){
  const keyed=new Map();
  for(const node of host.childNodes){const id=key(node);if(id!==null){if(!keyed.has(id))keyed.set(id,[]);keyed.get(id).push(node);}}
  let cursor=host.firstChild;
  for(const desired of [...next.childNodes]){
   const id=key(desired);
   let old=id!==null?keyed.get(id)?.shift():cursor&&key(cursor)===null&&cursor.nodeName===desired.nodeName?cursor:null;
   if(!old){old=desired.cloneNode(true);host.insertBefore(old,cursor);}
   else{if(old!==cursor)host.insertBefore(old,cursor);morph(old,desired);}
   cursor=old.nextSibling;
  }
  while(cursor){const nextNode=cursor.nextSibling;cursor.remove();cursor=nextNode;}
 }
 function html(host,markup){
  update(host,()=>{const template=document.createElement('template');template.innerHTML=markup;children(host,template.content);});
 }
 document.addEventListener('input',e=>{if(e.target.matches('input,select,textarea'))edits.add(e.target);},true);
 document.addEventListener('change',e=>{if(e.target.matches('input,select,textarea'))edits.add(e.target);},true);
 document.addEventListener('pointerdown',e=>{pointer=e.target;},true);
 // Once click dispatch starts its target is fixed. Allow the action itself to
 // show immediate feedback; postponed background updates flush afterward.
 document.addEventListener('click',()=>{pointer=null;schedule();},true);
 const release=()=>{const released=pointer;setTimeout(()=>{if(pointer===released)pointer=null;schedule();},0);};
 document.addEventListener('pointerup',release,true);
 document.addEventListener('pointercancel',release,true);
 document.addEventListener('dragend',release,true);
 document.addEventListener('selectionchange',schedule);
 document.addEventListener('focusout',schedule);
 document.addEventListener('keydown',e=>{if(['Enter',' '].includes(e.key)&&e.target.matches('button,summary'))pointer=e.target;},true);
 document.addEventListener('keyup',e=>{if(['Enter',' '].includes(e.key))release();},true);
 window.addEventListener('blur',()=>{pointer=null;schedule();});
 return {html,update,forget:host=>{for(const target of waiting.keys())if(target===host||host.contains(target))waiting.delete(target);}};
})();
