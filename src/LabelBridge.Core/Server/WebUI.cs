using System.Text;

namespace LabelBridge.Core.Server;

public static class WebUI
{
    public const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Online Printer Server</title>
<style>
:root{--bg:#0b1020;--card:#141b33;--muted:#8fa0c2;--text:#e9efff;--accent:#4da3ff;--ok:#2ecc71;--warn:#f4b400;--bad:#ff5c7a;--line:#263252}
*{box-sizing:border-box} body{margin:0;font-family:Segoe UI,system-ui,Arial,sans-serif;background:linear-gradient(180deg,#0b1020,#0e1630);color:var(--text)}
header{padding:16px 20px;border-bottom:1px solid var(--line);display:flex;gap:12px;align-items:center;justify-content:space-between;position:sticky;top:0;background:rgba(11,16,32,.9);backdrop-filter:blur(6px)}
h1{font-size:18px;margin:0} .muted{color:var(--muted)} .wrap{max-width:1200px;margin:0 auto;padding:16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px}
.card h2{font-size:15px;margin:0 0 10px 0}
input,select,button,textarea{font:inherit} input,select,textarea{width:100%;padding:9px 10px;border-radius:8px;border:1px solid var(--line);background:#0f162b;color:var(--text)}
label{font-size:12px;color:var(--muted);display:block;margin:8px 0 4px}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
button{padding:9px 12px;border-radius:8px;border:1px solid var(--line);background:#1b2746;color:var(--text);cursor:pointer}
button.primary{background:var(--accent);border-color:#2f7fd6;color:#041021;font-weight:600}
button.good{background:#173526;border-color:#24553b;color:#bff3d3}
button.bad{background:#3a1820;border-color:#5a2431;color:#ffd7de}
table{width:100%;border-collapse:collapse;font-size:13px} th,td{padding:8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
.pill{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;border:1px solid var(--line)}
.ok{color:#bff3d3;background:#173526} .pending{color:#ffe7a8;background:#3a3014} .badpill{color:#ffd7de;background:#3a1820}
.hidden{display:none} .small{font-size:12px}
pre{white-space:pre-wrap;word-break:break-word;background:#0f162b;padding:10px;border-radius:8px;border:1px solid var(--line)}
nav{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:12px}
nav button.active{outline:2px solid #2f7fd6}
#msg{min-height:20px;margin:8px 0;color:var(--muted)}
.two{display:grid;grid-template-columns:1fr 1fr;gap:10px}
@media (max-width:800px){.two{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
  <div><h1>Online Printer Server</h1><div class="muted small" id="hdrSub">Web UI & print queue</div></div>
  <div class="row"><span id="userInfo" class="muted small"></span><button id="logoutBtn" class="hidden" onclick="logout()">Logout</button></div>
</header>
<div class="wrap">
  <div id="msg"></div>

  <div id="setupView" class="card hidden" style="max-width:460px;margin:40px auto">
    <h2>Initial setup</h2>
    <div class="muted small">First run: create the admin account and password for this server.</div>
    <label>Username</label><input id="setupUser" value="admin">
    <label>Password</label><input id="setupPass" type="password" placeholder="At least 6 characters">
    <label>Confirm password</label><input id="setupPass2" type="password">
    <div class="row" style="margin-top:12px"><button class="primary" onclick="doSetup()">Create admin account</button></div>
  </div>

  <div id="loginView" class="card hidden" style="max-width:420px;margin:40px auto">
    <h2>Admin login</h2>
    <label>Username</label><input id="loginUser" value="admin">
    <label>Password</label><input id="loginPass" type="password">
    <div class="row" style="margin-top:12px"><button class="primary" onclick="doLogin()">Login</button></div>
  </div>

  <div id="appView" class="hidden">
    <nav id="tabs"></nav>
    <div id="tab-dashboard" class="tab"></div>
    <div id="tab-printer" class="tab hidden"></div>
    <div id="tab-clients" class="tab hidden"></div>
    <div id="tab-jobs" class="tab hidden"></div>
    <div id="tab-drivers" class="tab hidden"></div>
    <div id="tab-settings" class="tab hidden"></div>
  </div>
</div>
<script>
const S={token:localStorage.getItem('lb_token')||'', me:null, tab:'dashboard'};
function msg(t,err){const e=document.getElementById('msg'); e.textContent=t||''; e.style.color=err?'#ff9fb0':'#8fa0c2';}
async function api(path, opts={}){
  opts.headers=opts.headers||{}; if(S.token) opts.headers['Authorization']='Bearer '+S.token;
  if(opts.body && !(opts.body instanceof FormData) && typeof opts.body!=='string'){opts.headers['Content-Type']='application/json'; opts.body=JSON.stringify(opts.body);} 
  const r=await fetch(path, opts); const ct=r.headers.get('content-type')||''; let data=null;
  if(ct.includes('application/json')){ try{data=await r.json();}catch{data=null;} } else { try{data=await r.text();}catch{data=null;} }
  if(r.status===401){S.token=''; localStorage.removeItem('lb_token'); showLogin(); throw new Error((data&&data.error)||'Unauthorized');}
  if(!r.ok){ const e=new Error((data&&data.error)||(typeof data==='string'?data:null)||('HTTP '+r.status)); e.data=data; e.status=r.status; throw e; }
  return data;
}
function esc(s){return (s??'').toString().replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]))}
function showSetup(){document.getElementById('setupView').classList.remove('hidden'); document.getElementById('loginView').classList.add('hidden'); document.getElementById('appView').classList.add('hidden'); document.getElementById('logoutBtn').classList.add('hidden'); document.getElementById('userInfo').textContent='';}
function showLogin(){document.getElementById('setupView').classList.add('hidden'); document.getElementById('loginView').classList.remove('hidden'); document.getElementById('appView').classList.add('hidden'); document.getElementById('logoutBtn').classList.add('hidden'); document.getElementById('userInfo').textContent='';}
function showApp(){document.getElementById('setupView').classList.add('hidden'); document.getElementById('loginView').classList.add('hidden'); document.getElementById('appView').classList.remove('hidden'); document.getElementById('logoutBtn').classList.remove('hidden'); document.getElementById('userInfo').textContent=(S.me&&S.me.username)||'admin'; renderTabs(); showTab(S.tab);}
async function init(){
  try{ const st=await api('/api/status').catch(()=>null); if(st&&st.needsSetup){ showSetup(); return; } }catch{}
  try{ if(S.token){ S.me=await api('/api/auth/me'); if(S.me&&S.me.authenticated){ showApp(); return; } } }catch{}
  try{ const st2=await api('/api/status').catch(()=>null); if(st2&&st2.needsSetup){ showSetup(); return; } }catch{}
  showLogin();
}
async function doSetup(){
  try{
    const u=document.getElementById('setupUser').value||'admin';
    const p=document.getElementById('setupPass').value||'';
    const p2=document.getElementById('setupPass2').value||'';
    if(p.length<6){ msg('Password must be at least 6 characters.', true); return; }
    if(p!==p2){ msg('Passwords do not match.', true); return; }
    msg('Creating admin account...');
    const d=await api('/api/auth/setup',{method:'POST',body:{username:u,password:p}});
    S.token=d.token; localStorage.setItem('lb_token',S.token);
    try{ S.me=await api('/api/auth/me'); }catch{ S.me={username:u, authenticated:true}; }
    msg('Admin account created.');
    showApp();
  }catch(e){ msg(e.message,true); }
}
async function doLogin(){
  try{
    msg('Logging in...');
    const d=await api('/api/auth/login',{method:'POST',body:{username:document.getElementById('loginUser').value,password:document.getElementById('loginPass').value}});
    S.token=d.token; localStorage.setItem('lb_token',S.token);
    S.me=await api('/api/auth/me');
    msg(d.mustChange?'Logged in. Please change the initial password in Settings.':'');
    showApp();
    if(d.mustChange) showTab('settings');
  }catch(e){ msg(e.message,true); }
}
function logout(){ S.token=''; localStorage.removeItem('lb_token'); api('/api/auth/logout',{method:'POST'}).catch(()=>{}); init(); }
const TABDEFS=[['dashboard','Dashboard'],['printer','Printer'],['clients','Clients'],['jobs','Jobs'],['drivers','Drivers'],['settings','Settings']];
function renderTabs(){ const n=document.getElementById('tabs'); n.innerHTML=TABDEFS.map(([id,name])=>`<button class="${S.tab===id?'active':''}" onclick="showTab('${id}')">${name}</button>`).join(''); }
async function showTab(id){ S.tab=id; renderTabs(); document.querySelectorAll('.tab').forEach(t=>t.classList.add('hidden')); document.getElementById('tab-'+id).classList.remove('hidden'); try{ if(id==='dashboard') await loadDash(); if(id==='printer') await loadPrinter(); if(id==='clients') await loadClients(); if(id==='jobs') await loadJobs(); if(id==='drivers') await loadDrivers(); if(id==='settings') await loadSettings(); }catch(e){msg(e.message,true)} }
function pill(status){const s=(status||'').toLowerCase(); const cls=s==='approved'?'ok':(s==='pending'?'pending':'badpill'); return `<span class="pill ${cls}">${esc(status)}</span>`}

async function loadDash(){ const [p,clients,jobs,upd] = await Promise.all([api('/api/admin/printer'), api('/api/admin/clients'), api('/api/admin/jobs'), api('/api/admin/update-info').catch(()=>({}))]);
  const pending=clients.filter(c=>c.Status==='Pending').length; const approved=clients.filter(c=>c.Status==='Approved').length;
  const prof=[p.Profile?.Make,p.Profile?.Model,p.Profile?.Width,p.Profile?.Height,p.Profile?.Dpi&& (p.Profile.Dpi+'dpi'),p.Profile?.Orientation,p.Profile?.PrintLanguage].filter(Boolean).join(' · ');
  const updMsg = upd.hasUpdate ? `Update available: ${upd.latest||''}` : (upd.ok===false ? 'Update check unavailable' : 'Up to date');
  const updPill = upd.hasUpdate ? 'pending' : (upd.ok===false ? 'pending' : 'ok');
  const updActions = upd.hasUpdate
    ? `<div class="row" style="margin-top:10px"><button onclick="showTab('settings')">Open update</button>${upd.url?`<a href="${esc(upd.url)}" target="_blank" rel="noopener"><button>Release page</button></a>`:''}</div>`
    : (upd.url?`<div class="small muted" style="margin-top:8px"><a href="${esc(upd.url)}" target="_blank" rel="noopener">Release page</a></div>`:'');
  document.getElementById('tab-dashboard').innerHTML=`
  <div class="grid">
    <div class="card"><h2>Server</h2><div class="muted small">Current: ${esc(upd.current||'')} ${upd.latest?`· Latest: ${esc(upd.latest)}`:''}</div><div style="margin-top:8px"><span class="pill ${updPill}">${esc(updMsg)}</span></div>${upd.message?`<div class="small muted" style="margin-top:8px">${esc(upd.message)}</div>`:''}${updActions}</div>
    <div class="card"><h2>Printer</h2><div><b>${esc(p.Name||'Printer')}</b></div><div class="muted">${esc(p.Host||'(not set)')}:${esc(p.Port||9100)} ${p.Enabled?'(enabled)':'(disabled)'}</div><div class="small muted">Profile: ${esc(prof||'(not set)')}</div></div>
    <div class="card"><h2>Clients</h2><div>Approved: <b>${approved}</b></div><div>Pending: <b>${pending}</b></div></div>
    <div class="card"><h2>Jobs</h2><div>Queued/Retrying: <b>${jobs.length}</b></div><div class="small muted">Server forwards jobs directly to the configured network printer.</div></div>
  </div>`; }

async function loadPrinter(){ const p=await api('/api/admin/printer'); const pr=p.Profile||{};
  document.getElementById('tab-printer').innerHTML=`
  <div class="grid">
    <div class="card"><h2>Printer configuration</h2>
      <div class="small muted">Configure any network label printer (any model/vendor). Set host/IP and port (commonly 9100 for RAW), and define the label/profile settings to push to approved Windows clients.</div>
      <label>Printer name</label><input id="pName" value="${esc(p.Name||'')}">
      <div class="two"><div><label>Host / IP</label><input id="pHost" value="${esc(p.Host||'')}"></div><div><label>Port</label><input id="pPort" type="number" value="${esc(p.Port||9100)}"></div></div>
      <label><input type="checkbox" id="pEnabled" ${p.Enabled!==false?'checked':''}> Enabled</label>
      <h2 style="margin-top:14px">Label/profile (pushed to clients)</h2>
      <div class="small muted" style="margin-bottom:8px">Make/model and print language are detected from the printer automatically when you save or test the connection. Clients use them to pick the matching Windows driver.</div>
      <div class="two"><div><label>Make (detected)</label><input id="pfMake" value="${esc(pr.Make||'')}" placeholder="e.g. Zebra, TSC"></div><div><label>Model (detected)</label><input id="pfModel" value="${esc(pr.Model||'')}" placeholder="e.g. ZD421"></div></div>
      ${pr.DetectedUtc?`<div class="small muted">Last detected: ${esc(pr.DetectedUtc)}</div>`:''}
      <label>Profile name</label><input id="pfName" value="${esc(pr.Name||'Default')}">
      <div class="two"><div><label>Width (e.g. 4in, 100mm)</label><input id="pW" value="${esc(pr.Width||'')}"></div><div><label>Height (e.g. 6in, 150mm)</label><input id="pH" value="${esc(pr.Height||'')}"></div></div>
      <div class="two"><div><label>DPI (e.g. 203, 300)</label><input id="pDpi" value="${esc(pr.Dpi||'')}"></div><div><label>Orientation</label><input id="pOri" value="${esc(pr.Orientation||'')}" placeholder="portrait / landscape"></div></div>
      <div class="two"><div><label>Media type</label><input id="pMedia" value="${esc(pr.MediaType||'')}" placeholder="gap / mark / continuous"></div><div><label>Print method</label><input id="pMethod" value="${esc(pr.PrintMethod||'')}" placeholder="direct-thermal / thermal-transfer"></div></div>
      <div class="two"><div><label>Darkness</label><input id="pDark" value="${esc(pr.Darkness||'')}"></div><div><label>Speed</label><input id="pSpeed" value="${esc(pr.Speed||'')}"></div></div>
      <label>Print language</label><input id="pLang" value="${esc(pr.PrintLanguage||'')}" placeholder="ZPL, EPL, CPCL, TSPL, ESC/POS, RAW, etc.">
      <label>Driver hint (exact Windows driver/model name for virtual printer)</label><input id="pDrv" value="${esc(pr.DriverHint||'')}" placeholder="e.g. ZDesigner ZD420-300dpi ZPL, Generic / Text Only, etc.">
      <div class="row" style="margin-top:12px"><button class="primary" onclick="savePrinter()">Save</button><button onclick="detectPrinter()">Detect printer</button><button onclick="probePrinter()">Test connection</button><button onclick="sendTest()">Send test label</button></div>
      <div id="pMsg" class="small muted" style="margin-top:8px"></div>
    </div>
    <div class="card"><h2>Auto-scan (TCP port)</h2>
      <label>Subnet CIDR (optional, e.g. 192.168.50.0/24)</label><input id="scanSubnet" placeholder="leave empty to scan local /24s">
      <label>Port</label><input id="scanPort" type="number" value="${esc(p.Port||9100)}">
      <div class="row" style="margin-top:10px"><button onclick="scanNet()">Scan</button></div>
      <div id="scanOut" class="small muted" style="margin-top:10px">Scan may take several seconds.</div>
    </div>
  </div>`;
}

async function savePrinter(){ try{ const body={name:v('pName'),host:v('pHost'),port:parseInt(v('pPort')||'9100'),enabled:document.getElementById('pEnabled').checked,
  profile:{name:v('pfName'),make:v('pfMake'),model:v('pfModel'),width:v('pW'),height:v('pH'),dpi:v('pDpi'),orientation:v('pOri'),mediaType:v('pMedia'),printMethod:v('pMethod'),darkness:v('pDark'),speed:v('pSpeed'),printLanguage:v('pLang'),driverHint:v('pDrv')}}; 
  await api('/api/admin/printer',{method:'PUT',body}); msg('Printer/profile saved. Approved clients will receive the updated profile.'); document.getElementById('pMsg').textContent='Saved.';}catch(e){msg(e.message,true)} }
async function detectPrinter(){ try{ const r=await api('/api/admin/printer/detect',{method:'POST',body:{host:v('pHost'),port:parseInt(v('pPort')||'9100')}}); document.getElementById('pMsg').textContent=r.message; if(r.profile){ if(document.getElementById('pfMake'))document.getElementById('pfMake').value=r.profile.Make||''; if(document.getElementById('pfModel'))document.getElementById('pfModel').value=r.profile.Model||''; if(document.getElementById('pLang'))document.getElementById('pLang').value=r.profile.PrintLanguage||''; if(document.getElementById('pDrv'))document.getElementById('pDrv').value=r.profile.DriverHint||''; } msg(r.message); await loadPrinter(); }catch(e){document.getElementById('pMsg').textContent=e.message; msg(e.message,true)} }
async function probePrinter(){ try{ const r=await api('/api/admin/printer/probe',{method:'POST',body:{host:v('pHost'),port:parseInt(v('pPort')||'9100')}}); document.getElementById('pMsg').textContent=r.message; msg(r.message);}catch(e){document.getElementById('pMsg').textContent=e.message; msg(e.message,true)} }
async function sendTest(){ try{ const r=await api('/api/admin/printer/test',{method:'POST'}); document.getElementById('pMsg').textContent=r.message; msg(r.message);}catch(e){document.getElementById('pMsg').textContent=e.message; msg(e.message,true)} }
async function scanNet(){ try{ document.getElementById('scanOut').textContent='Scanning...'; const s=encodeURIComponent(v('scanSubnet')||''); const port=parseInt(v('scanPort')||v('pPort')||'9100'); const r=await api('/api/admin/scan?subnet='+s+'&port='+port); if(!r.results||!r.results.length){document.getElementById('scanOut').innerHTML='No open printers found on port '+esc(port)+'.'; return;} document.getElementById('scanOut').innerHTML='<table><tr><th>Host</th><th>Port</th><th></th></tr>'+r.results.map(x=>`<tr><td>${esc(x.host)}</td><td>${esc(x.port)}</td><td><button onclick="useHost('${esc(x.host)}',${x.port})">Use</button></td></tr>`).join('')+'</table>'; }catch(e){document.getElementById('scanOut').textContent=e.message;} }
function useHost(h,p){ if(document.getElementById('pHost')){document.getElementById('pHost').value=h; document.getElementById('pPort').value=p; msg('Selected '+h+':'+p+' (click Save)');} }
function v(id){const e=document.getElementById(id); return e?e.value:''}

async function loadClients(){ const list=await api('/api/admin/clients');
  const rows=list.map(c=>`<tr><td><b>${esc(c.Name||'')}</b><div class="small muted">${esc(c.Machine||'')}</div><div class="small muted">${esc(c.Id)}</div></td><td>${pill(c.Status)}</td><td class="small muted">Created ${esc(c.CreatedUtc||'')}<br>Seen ${esc(c.LastSeenUtc||'-')}</td><td class="row">
    ${c.Status!=='Approved'?`<button class="good" onclick="clientAction('${c.Id}','approve')">Approve</button>`:''}
    ${c.Status==='Approved'?`<button class="bad" onclick="clientAction('${c.Id}','revoke')">Revoke</button>`:''}
    ${c.Status==='Pending'?`<button onclick="clientAction('${c.Id}','decline')">Decline</button>`:''}
    <button onclick="clientDel('${c.Id}')">Delete</button></td></tr>`).join('');
  document.getElementById('tab-clients').innerHTML=`<div class="card"><h2>Client requests & access</h2>
    <div class="small muted" style="margin-bottom:8px">Windows clients register via the server URL and appear here as Pending. Approve to issue an access token; revoke to block printing. The client must fetch config/token after approval.</div>
    <table><tr><th>Client</th><th>Status</th><th>Activity</th><th>Actions</th></tr>${rows||'<tr><td colspan="4" class="muted">No clients yet.</td></tr>'}</table></div>`;
}
async function clientAction(id,act){ try{ const r=await api(`/api/admin/clients/${id}/${act}`,{method:'POST'}); if(r.token){ alert('Client approved. Token (shown once, client can fetch via status/config if implemented):\n\n'+r.token+'\n\nNormally the Windows client polls/fetches its token after approval.'); } msg('Updated.'); await loadClients(); }catch(e){msg(e.message,true)} }
async function clientDel(id){ if(!confirm('Delete this client record?')) return; try{ await api(`/api/admin/clients/${id}`,{method:'DELETE'}); await loadClients(); }catch(e){msg(e.message,true)} }

async function loadJobs(){ const jobs=await api('/api/admin/jobs');
  const rows=jobs.map(j=>`<tr><td>${esc(j.Name||j.Id)}</td><td class="small muted">${esc(j.Id)}</td><td>${esc(j.Source||'')}</td><td>${esc(j.Size)}</td><td>${esc(j.Attempts)}</td><td>${pill(j.State)}</td><td class="small muted">${esc(j.LastError||'')}</td></tr>`).join('');
  document.getElementById('tab-jobs').innerHTML=`<div class="card"><h2>Print queue</h2><div class="row" style="margin-bottom:8px"><button onclick="loadJobs()">Refresh</button></div><table><tr><th>Name</th><th>ID</th><th>Source</th><th>Bytes</th><th>Attempts</th><th>State</th><th>Last error</th></tr>${rows||'<tr><td colspan="7" class="muted">Queue is empty.</td></tr>'}</table></div>`; }

async function loadDrivers(){ const [drivers,state]=await Promise.all([api('/api/admin/drivers'), api('/api/admin/printer')]);
  const sel=(await api('/api/admin/client-bundle-info')).ok; // no-op to keep warm
  const rows=drivers.map(d=>`<tr><td><b>${esc(d.DisplayName)}</b><div class="small muted">${esc(d.FileName)} v${esc(d.Version||'?')}</div></td><td>${esc(d.Size)}</td><td class="small muted">${esc(d.Sha256?.slice(0,16)||'')}</td><td class="row"><button onclick="selectDriver('${d.Id}')">Select</button><button class="bad" onclick="delDriver('${d.Id}')">Delete</button></td></tr>`).join('');
  document.getElementById('tab-drivers').innerHTML=`<div class="grid"><div class="card"><h2>Driver/profile packages</h2>
    <div class="small muted">Upload the printer driver/installer package that Windows clients should download and install (admin/UAC on client). This server provides the selected package and printer/profile settings to approved clients.</div>
    <label>Driver file (.exe/.msi/.zip)</label><input id="drvFile" type="file">
    <div class="two"><div><label>Display name</label><input id="drvName"></div><div><label>Version</label><input id="drvVer"></div></div>
    <label>Silent install args (optional, e.g. /S or /qn)</label><input id="drvArgs">
    <div class="row" style="margin-top:10px"><button class="primary" onclick="uploadDriver()">Upload</button></div>
  </div>
  <div class="card"><h2>Available packages</h2><table><tr><th>Package</th><th>Size</th><th>SHA256</th><th></th></tr>${rows||'<tr><td colspan="4" class="muted">No driver uploaded yet. You can still use clients if the driver is preinstalled.</td></tr>'}</table></div></div>`;
}
async function uploadDriver(){ try{ const f=document.getElementById('drvFile').files[0]; if(!f){msg('Choose a file',true); return;} msg('Uploading...'); const buf=await f.arrayBuffer(); const r=await fetch('/api/admin/drivers/upload',{method:'POST', headers:{'Authorization':'Bearer '+S.token,'Content-Type':'application/octet-stream','X-File-Name':f.name,'X-Display-Name':v('drvName')||f.name,'X-Version':v('drvVer')||'','X-Silent-Args':v('drvArgs')||''}, body:buf}); const d=await r.json().catch(()=>null); if(!r.ok) throw new Error((d&&d.error)||('HTTP '+r.status)); msg('Uploaded.'); await loadDrivers(); }catch(e){msg(e.message,true)} }
async function selectDriver(id){ try{ await api('/api/admin/drivers/select',{method:'POST',body:{id}}); msg('Selected driver.'); await loadDrivers(); }catch(e){msg(e.message,true)} }
async function delDriver(id){ if(!confirm('Delete driver package?')) return; try{ await api('/api/admin/drivers/'+id,{method:'DELETE'}); await loadDrivers(); }catch(e){msg(e.message,true)} }

async function loadSettings(){ const me=await api('/api/auth/me'); const info=await api('/api/admin/client-bundle-info'); const upd=await api('/api/admin/update-info').catch(()=>({ok:false,error:'unavailable'}));
  const autoApply = upd && upd.autoApplySupported;
  const updMsg = upd && upd.ok
    ? `<div class="small"><b>Current:</b> ${esc(upd.current||'')} &nbsp; <b>Latest:</b> ${esc(upd.latest||'?')} ${upd.hasUpdate?'<span class="pill pending">Update available</span>':'<span class="pill ok">Up to date</span>'}</div>
       ${upd.message?`<div class="small muted" style="margin-top:6px">${esc(upd.message)}</div>`:''}
       ${upd.url?`<div class="small" style="margin-top:6px"><a href="${esc(upd.url)}" target="_blank" rel="noopener">Release page</a></div>`:''}
       <div class="row" style="margin-top:10px">
         <button onclick="checkUpd()">Refresh</button>
         ${upd.hasUpdate && autoApply ? `<button class="primary" onclick="applyUpd()">Download & apply server update</button>` : ''}
         ${upd.hasUpdate && !autoApply && upd.url ? `<a href="${esc(upd.url)}" target="_blank" rel="noopener"><button class="primary">Download update</button></a>` : ''}
       </div>
       ${upd.hasUpdate && !autoApply ? `<div class="small muted" style="margin-top:8px">Automatic apply is only supported on Linux/systemd installs. Download the release or run the command below on the server.</div>` : ''}
       ${upd.manual ? `<div class="small muted" style="margin-top:8px">Manual upgrade command:</div><pre id="updManual" class="small" style="margin-top:4px;white-space:pre-wrap">${esc(upd.manual)}</pre>` : ''}
       <pre id="updOut" class="small" style="margin-top:8px;white-space:pre-wrap">${esc(upd.error||'')}</pre>`
    : `<div class="small muted">Update info unavailable: ${esc(upd&&upd.error||'unknown')}</div><div class="row" style="margin-top:10px"><button onclick="loadSettings()">Retry</button></div>`;
  const updHtml = updMsg;
  document.getElementById('tab-settings').innerHTML=`<div class="grid">
    <div class="card"><h2>Admin password</h2>
      ${me.mustChange?'<div class="pill pending">Password change recommended</div>':''}
      <label>Current password</label><input id="curPw" type="password"><label>New password</label><input id="newPw" type="password">
      <div class="row" style="margin-top:10px"><button class="primary" onclick="changePw()">Change password</button></div>
    </div>
    <div class="card"><h2>Client connection info</h2>
      <div class="small muted">Windows clients connect to this server over the internet/LAN using this base URL (put HTTPS reverse proxy in front for internet use).</div>
      <label>Server base URL (as seen by this browser)</label><input readonly value="${esc(info.serverBaseUrl||'')}">
      <pre class="small">Client flow:\n1. Install the Online Printer Client setup and enter this server URL\n2. Submit connection request and wait for admin approval\n3. Admin approves client in Clients tab\n4. Client receives printer/profile and optional driver package from server\n5. Client downloads/installs driver and installs/repairs the local "Online Printer" virtual printer (admin/UAC may be required)\n6. Print from any app to "Online Printer" - jobs upload to server and server forwards them directly to the configured network printer (TCP/9100)</pre>
    </div>
    <div class="card"><h2>Server update</h2>${updHtml}</div>
  </div>`;
}
async function checkUpd(){ try{ await loadSettings(); msg('Update info refreshed.'); }catch(e){msg(e.message,true)} }
async function applyUpd(){ try{ const out=document.getElementById('updOut'); if(out) out.textContent='Applying update...'; msg('Applying server update...'); const r=await api('/api/admin/update-apply',{method:'POST',body:{}}); if(out) out.textContent=(r.message||'') + (r.details?'\n\n'+JSON.stringify(r.details,null,2):''); msg(r.message||'Update requested.'); setTimeout(()=>loadSettings().catch(()=>{}), 4000); }catch(e){ const out=document.getElementById('updOut'); const d=e.data||{}; const extra = d.details ? ('\n\n'+JSON.stringify(d.details,null,2)) : (d.manual?('\n\nManual:\n'+d.manual):''); if(out) out.textContent=(e.message||String(e))+extra; msg(e.message,true); await loadSettings().catch(()=>{}); } }
async function changePw(){ try{ await api('/api/admin/change-password',{method:'POST',body:{current:v('curPw'),next:v('newPw')}}); msg('Password changed.'); S.me=await api('/api/auth/me'); await loadSettings(); }catch(e){msg(e.message,true)} }

init();
</script>
</body></html>
""";
}
