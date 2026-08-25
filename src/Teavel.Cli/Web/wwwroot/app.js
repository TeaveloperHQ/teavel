/*
  관리 화면.

  틀이 없다(리액트도, 번들러도 없다). 이 화면은 exe 안에 묻혀 교사 PC 로 가는데,
  그 PC 는 인터넷을 안 쓴다는 것이 Teavel 의 약속이라 CDN 을 부를 수 없고,
  묶어서 넣으면 그만큼 exe 가 커진다. 여기서 필요한 것은 표 몇 개와 단추 몇 개다.

  판단은 여기 없다. 무엇이 정리 후보이고 어느 반이 어느 팀인지는 전부 서버(C#)가 정해
  내려 주고, 이 파일은 그것을 그리고 누른 것을 돌려보낸다.
*/

'use strict';

// ── 열쇠 ────────────────────────────────────────────────────────────────
//
// 첫 주소(?t=…)로 한 번 받고 주소창에서 지운다. 주소창에 남겨 두면 관리자가
// 그대로 복사해 어딘가에 붙일 수 있다. 새로 고쳐도 살아 있게 세션에 둔다.

const KEY = (() => {
  const fromUrl = new URLSearchParams(location.search).get('t');
  if (fromUrl) {
    sessionStorage.setItem('teavel-key', fromUrl);
    history.replaceState(null, '', location.pathname + location.hash);
    return fromUrl;
  }
  return sessionStorage.getItem('teavel-key') || '';
})();

async function api(path, body, raw) {
  const opt = { method: body === undefined ? 'GET' : 'POST', headers: { 'X-Teavel-Token': KEY } };

  if (body !== undefined) {
    if (raw) { opt.body = body; opt.headers['X-Teavel-Filename'] = encodeURIComponent(raw); }
    else { opt.body = JSON.stringify(body); opt.headers['Content-Type'] = 'application/json'; }
  }

  const res = await fetch(path, opt);
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

// ── 잔손 ────────────────────────────────────────────────────────────────

const $ = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));

function esc(s) {
  return String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

let toastTimer = 0;
function toast(text, bad) {
  const el = $('#toast');
  el.textContent = text;
  el.classList.toggle('bad', !!bad);
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, bad ? 7000 : 3500);
}

/** 물어보는 창. 확인이면 적은 값(또는 true), 아니면 null. */
function ask({ title, body, confirm, danger, field, placeholder }) {
  return new Promise(resolve => {
    const veil = document.createElement('div');
    veil.id = 'veil';
    veil.innerHTML = `
      <div class="box" role="dialog" aria-modal="true">
        <h3>${esc(title)}</h3>
        <div>${body || ''}</div>
        ${field ? `<input type="text" id="ask-field" placeholder="${esc(placeholder || '')}" autocomplete="off">` : ''}
        <div class="acts">
          <button id="ask-no">그만두기</button>
          <button id="ask-yes" class="${danger ? 'bad' : 'go'}">${esc(confirm || '확인')}</button>
        </div>
      </div>`;

    document.body.appendChild(veil);
    const input = $('#ask-field', veil);
    (input || $('#ask-yes', veil)).focus();

    const done = v => { veil.remove(); document.removeEventListener('keydown', onKey); resolve(v); };
    const onKey = e => {
      if (e.key === 'Escape') done(null);
      if (e.key === 'Enter' && input) done(input.value);
    };

    document.addEventListener('keydown', onKey);
    $('#ask-no', veil).onclick = () => done(null);
    $('#ask-yes', veil).onclick = () => done(input ? input.value : true);
    veil.onclick = e => { if (e.target === veil) done(null); };
  });
}

// ── 오래 걸리는 일 ──────────────────────────────────────────────────────
//
// 팀 열일곱 개를 만들면 몇 분이 걸린다. 그동안 아무것도 안 보이면 관리자는 끊긴 줄 알고
// 창을 닫고, 그러면 절반만 만들어진 채로 끝난다. 그래서 서버가 흘려보내는 줄을 받아
// 아래에서 계속 보여 준다.

let watching = null;

async function run(started, onDone) {
  if (!started || !started.ok) { toast((started && started.message) || '시작하지 못했습니다.', true); return; }

  const drawer = $('#drawer');
  $('#drawer-title').textContent = started.title || '진행 중';
  $('#drawer-state').textContent = '돌고 있습니다…';
  $('#drawer-lines').innerHTML = '';
  $('#drawer-close').hidden = true;
  drawer.hidden = false;

  clearInterval(watching);
  let from = 0;

  watching = setInterval(async () => {
    let view;
    try { view = await api('/api/job?id=' + encodeURIComponent(started.jobId) + '&from=' + from); }
    catch (e) { return; }

    if (!view.ok) return;

    for (const line of view.lines) {
      const div = document.createElement('div');
      div.className = line.kind;
      div.textContent = line.text;
      $('#drawer-lines').appendChild(div);
    }

    if (view.lines.length) $('#drawer-lines').scrollTop = $('#drawer-lines').scrollHeight;
    from = view.next;

    if (view.done) {
      clearInterval(watching);
      watching = null;
      $('#drawer-state').textContent = view.summary || '끝났습니다.';
      $('#drawer-close').hidden = false;
      if (onDone) onDone();
    }
  }, 500);
}

$('#drawer-close').onclick = () => { $('#drawer').hidden = true; };

// ── 낱장 ────────────────────────────────────────────────────────────────

const PAGES = {};
let state = {};

async function draw() {
  // 브라우저는 한글 해시를 퍼센트 인코딩해서 돌려준다 — '#/그룹' 을 눌러도
  // location.hash 는 '#/%EA%B7%B8%EB%A3%B9' 다. 풀지 않으면 어느 낱장도 못 찾아
  // 늘 첫 장만 뜬다.
  let hash = location.hash || '#/한눈에';
  try { hash = decodeURIComponent(hash); } catch (e) { /* 온 그대로 쓴다 */ }

  const name = hash.replace('#/', '');
  const page = PAGES[name] || PAGES['한눈에'];

  $$('a.nav').forEach(a => a.classList.toggle('on', a.getAttribute('href') === '#/' + name));
  $('#page').innerHTML = '<div class="loading">읽는 중…</div>';

  try { await page(); }
  catch (e) { $('#page').innerHTML = `<div class="card bad"><b>읽지 못했습니다.</b><p>${esc(e.message)}</p></div>`; }

  await badges();
}

/** 왼쪽 메뉴의 빨간 숫자 — 손볼 것이 남은 자리를 짚어 준다. */
async function badges() {
  try {
    const o = await api('/api/overview');
    const hello = await api('/api/hello');

    $('#school').textContent = hello.school || '';
    $('#tag-groups').textContent = o.candidates ? o.candidates : '';
    $('#tag-plan').textContent = o.toCreate ? o.toCreate : '';
    $('#tag-roster').textContent = hello.rosterRows ? '' : '없음';
    $('#tag-roster').style.background = hello.rosterRows ? '' : 'var(--faint)';
  } catch (e) { /* 메뉴 숫자는 없어도 화면은 돈다 */ }
}

// ── ① 한눈에 ────────────────────────────────────────────────────────────

PAGES['한눈에'] = async () => {
  const o = await api('/api/overview');
  const hello = await api('/api/hello');

  const todo = [];
  if (!hello.rosterRows) todo.push(['명단을 올리시면 반 팀과 학생 넣기가 열립니다.', '#/명단', '명단 올리기']);
  if (o.candidates) todo.push([`정리해 볼 만한 그룹이 ${o.candidates}개 있습니다.`, '#/그룹', '보러 가기']);
  if (o.toCreate) todo.push([`아직 없는 반 팀이 ${o.toCreate}개 있습니다.`, '#/만들기', '만들러 가기']);
  if (o.conflicts) todo.push([`이름이 비슷해 사람이 봐야 할 것이 ${o.conflicts}개 있습니다.`, '#/만들기', '확인하기']);
  if (o.nameless) todo.push([`표시 이름이 비어 있는 계정이 ${o.nameless}개 있습니다.`, '#/사람', '이름 붙이기']);
  if (o.security) todo.push([`보안 그룹 ${o.security}개는 정식 관리 센터에서 손으로 만드셔야 합니다.`, '', '']);

  $('#page').innerHTML = `
    <h1>한눈에</h1>
    <p class="lede">지금 학교 M365 가 어떤 상태인지, 그리고 무엇부터 하시면 되는지입니다.</p>

    <div class="tiles">
      <div class="tile"><div class="n">${o.groups}</div><div class="k">그룹</div></div>
      <div class="tile"><div class="n">${o.teams}</div><div class="k">그중 팀</div></div>
      <div class="tile"><div class="n">${o.people}</div><div class="k">사람</div></div>
      <div class="tile ${o.unlicensed ? 'hot' : ''}"><div class="n">${o.unlicensed}</div><div class="k">라이선스 없는 계정</div></div>
      <div class="tile ${o.candidates ? 'hot' : ''}"><div class="n">${o.candidates}</div><div class="k">정리 후보</div></div>
      <div class="tile ${o.toCreate ? 'hot' : ''}"><div class="n">${o.toCreate}</div><div class="k">만들 반 팀</div></div>
    </div>

    <h2>무엇부터 하면 되나</h2>
    ${todo.length === 0
      ? `<div class="card"><b>손볼 것이 없습니다.</b><p class="lede" style="margin:6px 0 0">지금은 선언한 대로 다 갖춰져 있습니다.</p></div>`
      : todo.map(([text, href, label]) => `
        <div class="card" style="display:flex;align-items:center;gap:14px">
          <div class="grow">${esc(text)}</div>
          ${href ? `<a href="${href}"><button>${esc(label)}</button></a>` : ''}
        </div>`).join('')}

    <h2>라이선스 묶음</h2>
    <p class="lede">같은 라이선스를 받은 사람끼리 묶은 것입니다. 대개 가장 큰 묶음이 학생, 그다음이 교사입니다.</p>
    <div class="wrap"><table>
      <thead><tr><th>사람 수</th><th>부서</th><th>보기</th></tr></thead>
      <tbody>${o.licenses.map(c => `
        <tr class="${c.unlicensed ? 'dim' : ''}">
          <td class="num">${c.count}명 ${c.unlicensed ? '<span class="pill stop">라이선스 없음</span>' : ''}</td>
          <td>${esc((c.departments || []).join(' · ')) || '<span class="sub">비어 있음</span>'}</td>
          <td class="sub">${esc(c.sample)}</td>
        </tr>`).join('')}</tbody>
    </table></div>`;
};

// ── ② 그룹 · 팀 ─────────────────────────────────────────────────────────

PAGES['그룹'] = async () => {
  const g = await api('/api/groups');
  state.groups = g.rows;

  $('#page').innerHTML = `
    <h1>그룹 · 팀</h1>
    <p class="lede">
      학교에 지금 있는 것 전부입니다. <b>지우면 그 안의 파일과 대화가 함께 사라집니다.</b>
      이름만 바꾸면 내용은 그대로 남으니, 잘 모르겠으면 그냥 두시거나 이름만 바꾸세요.
    </p>

    <div class="row">
      <input type="search" id="q" placeholder="이름으로 찾기" style="width:260px">
      <label style="display:flex;align-items:center;gap:6px">
        <input type="checkbox" id="only"> 정리 후보만 보기
      </label>
    </div>

    <div class="wrap"><table>
      <thead><tr>
        <th>이름</th><th>무엇</th><th class="num">구성원</th><th>만든 날</th><th>어떻게 볼지</th><th>할 수 있는 것</th>
      </tr></thead>
      <tbody id="rows"></tbody>
    </table></div>`;

  const paint = () => {
    const q = $('#q').value.trim();
    const only = $('#only').checked;

    const rows = state.groups.filter(r =>
      (!only || r.candidate) && (!q || r.name.includes(q) || r.alias.includes(q)));

    $('#rows').innerHTML = rows.length === 0
      ? `<tr><td colspan="6" class="sub">해당하는 것이 없습니다.</td></tr>`
      : rows.map(r => `
        <tr>
          <td>
            <div class="name">${esc(r.name)}</div>
            <div class="sub">${esc(r.alias)}</div>
          </td>
          <td class="tight">${r.isTeam ? '팀' : '그룹'}</td>
          <td class="num">${r.members >= 0 ? r.members + '명' : '모름'}</td>
          <td class="tight sub">${esc(r.created) || '모름'}</td>
          <td>
            <span class="pill ${r.candidate ? 'cand' : r.locked ? 'sys' : 'use'}">${esc(r.bucket)}</span>
            ${r.note ? `<div class="sub">${esc(r.note)}</div>` : ''}
          </td>
          <td class="tight">
            ${r.locked
              ? '<span class="sub">건드리지 않습니다</span>'
              : `<button class="tiny" data-do="rename" data-alias="${esc(r.alias)}">이름 바꾸기</button>
                 ${r.archiveName ? `<button class="tiny" data-do="archive" data-alias="${esc(r.alias)}">보관</button>` : ''}
                 <button class="tiny bad" data-do="delete" data-alias="${esc(r.alias)}">지우기</button>`}
          </td>
        </tr>`).join('');

    $$('#rows button[data-do]').forEach(b => b.onclick = () => act(b.dataset.do, b.dataset.alias));
  };

  $('#q').oninput = paint;
  $('#only').onchange = paint;
  paint();
};

async function act(what, alias) {
  const row = state.groups.find(r => r.alias === alias);
  if (!row) return;

  if (what === 'rename') {
    const name = await ask({
      title: '이름 바꾸기',
      body: `<p>안의 파일과 대화는 그대로 남습니다. 메일 주소(별칭)는 바뀌지 않습니다.</p>`,
      field: true, placeholder: row.name, confirm: '바꾸기',
    });
    if (name === null) return;

    const res = await api('/api/groups/rename', { alias, newName: (name || '').trim() || row.name });
    if (!res.ok) { toast(res.message, true); return; }

    toast(res.message);
    await draw();
    return;
  }

  if (what === 'archive') {
    const yes = await ask({
      title: '지난 학년도로 보관',
      body: `<p><b>'${esc(row.archiveName)}'</b> 로 이름을 바꾸고 <b>학생만 내보냅니다.</b>
             팀과 그 안의 파일·대화는 그대로 남아 담당 선생님은 계속 보실 수 있습니다.</p>`,
      confirm: '보관하기',
    });
    if (!yes) return;

    run(await api('/api/groups/archive', { alias }), draw);
    return;
  }

  if (what === 'delete') {
    // 콘솔에서 쓰던 문을 그대로 옮겼다 — 단추 하나로 지워지면 잘못 눌러 사라진다.
    const typed = await ask({
      title: '지우기 — 되돌릴 수 없습니다',
      danger: true, confirm: '지웁니다',
      body: `<p><b>${esc(row.name)}</b> 안의 파일과 대화가 함께 사라집니다. 되살릴 방법이 없습니다.<br>
             정말 지우시려면 아래에 <b>이름을 그대로</b> 적어 주세요.</p>`,
      field: true, placeholder: row.name,
    });
    if (typed === null) return;

    const res = await api('/api/groups/delete', { alias, typed: (typed || '').trim() });
    if (!res.ok) { toast(res.message, true); return; }
    run(res, draw);
  }
}

// ── ③ 명단 ──────────────────────────────────────────────────────────────

PAGES['명단'] = async () => {
  const hello = await api('/api/hello');

  $('#page').innerHTML = `
    <h1>명단</h1>
    <p class="lede">
      학생 명단이 있으면 훨씬 많은 것을 대신 해 드릴 수 있습니다.
      <b>몇 학년 몇 반까지 있는지도 명단을 보면 알 수 있어</b> 따로 여쭙지 않아도 됩니다.
      양식은 맞추지 않으셔도 됩니다.
    </p>

    ${hello.roster ? `<div class="card"><b>지금 올려 두신 명단</b>
      <div>${esc(hello.roster)} — 쓸 수 있는 줄 ${hello.rosterRows}개</div></div>` : ''}

    <div class="drop" id="drop">
      <p>여기에 파일을 끌어다 놓으시거나</p>
      <button class="go" id="pick">파일 고르기</button>
      <input type="file" id="file" hidden accept=".csv,.txt,.tsv,.xlsx,.xlsm,.hwpx">
      <p class="sub" style="margin-top:14px">csv · xlsx · hwpx 를 읽습니다.<br>
      한셀은 [다른 이름으로 저장] 에서 xlsx 로, 한글은 HWPX 로 한 번 저장해 주세요.</p>
    </div>

    <div id="result"></div>`;

  const drop = $('#drop');
  const file = $('#file');

  $('#pick').onclick = () => file.click();
  file.onchange = () => file.files[0] && send(file.files[0]);

  drop.ondragover = e => { e.preventDefault(); drop.classList.add('over'); };
  drop.ondragleave = () => drop.classList.remove('over');
  drop.ondrop = e => {
    e.preventDefault();
    drop.classList.remove('over');
    if (e.dataTransfer.files[0]) send(e.dataTransfer.files[0]);
  };

  async function send(f) {
    $('#result').innerHTML = '<div class="loading">읽는 중…</div>';

    let res;
    try { res = await api('/api/roster', await f.arrayBuffer(), f.name); }
    catch (e) { $('#result').innerHTML = `<div class="card bad">${esc(e.message)}</div>`; return; }

    if (!res.ok) {
      $('#result').innerHTML = `<div class="card bad"><b>${esc(res.message)}</b>
        ${(res.details || []).map(d => `<div class="sub">${esc(d)}</div>`).join('')}</div>`;
      return;
    }

    const byGrade = {};
    for (const c of res.shape) (byGrade[c.grade] = byGrade[c.grade] || []).push(c);

    $('#result').innerHTML = `
      <div class="card">
        <b>${esc(res.message)}</b>
        <div class="sub">${(res.how || []).map(esc).join(' · ')}</div>
      </div>

      <h2>명단을 보니 이 학교는 이렇습니다</h2>
      <div class="card">
        <b>${esc(res.describe)}</b>
        ${Object.keys(byGrade).sort((a, b) => a - b).map(g => `
          <div style="margin-top:8px">${esc(g)}학년 &nbsp;
            ${byGrade[g].map(c => `<span class="pill new">${c.classNo}반 ${c.count}명</span>`).join(' ')}
          </div>`).join('')}
        <div class="row" style="margin:16px 0 0">
          <a href="#/만들기"><button class="go">이 구조로 반 팀 맞추기</button></a>
        </div>
      </div>

      ${res.bad.length ? `<div class="card warn">
        <b>쓸 수 없는 줄이 ${res.bad.length}개 있습니다.</b> 그 줄은 빼고 넣습니다.
        ${res.bad.map(b => `<div class="sub">${b.line}번째 줄 — ${esc(b.problems.join(' · '))}</div>`).join('')}
      </div>` : ''}`;

    await badges();
  }
};

// ── ④ 반 팀 만들기 ──────────────────────────────────────────────────────

PAGES['만들기'] = async () => {
  const p = await api('/api/plan');

  const make = p.rows.filter(r => r.action === '만들 것');
  const hand = p.rows.filter(r => r.action === '손으로');
  const stuck = p.rows.filter(r => r.action === '확인 필요');
  const have = p.rows.filter(r => r.action === '이미 있음');

  $('#page').innerHTML = `
    <h1>반 팀 만들기</h1>
    <p class="lede">
      ${p.fromRoster
        ? '올려 주신 명단에서 읽은 학교 모양으로 맞춥니다.'
        : '명단이 없어 적어 둔 학교 구조를 그대로 씁니다. <a href="#/명단">명단을 올리시면</a> 이 학교 모양에 맞춰 드립니다.'}
      <b>이미 있는 것은 건드리지 않습니다.</b> 여러 번 하셔도 안전합니다.
    </p>

    <div class="card" style="display:flex;align-items:center;gap:14px">
      <div class="grow"><b>${esc(p.summary)}</b></div>
      <button class="go" id="go" ${make.length === 0 ? 'disabled' : ''}>
        ${make.length ? `${make.length}개 만들기` : '만들 것이 없습니다'}
      </button>
    </div>

    ${stuck.length ? `<div class="card warn">
      <b>사람이 봐야 할 것이 ${stuck.length}개 있습니다.</b> 이것들은 만들지 않고 건너뜁니다.
      ${stuck.map(r => `<div style="margin-top:6px">${esc(r.name)} <span class="sub">— ${esc(r.reason)}</span></div>`).join('')}
      <div class="sub" style="margin-top:8px">그대로 만들면 거의 같은 것이 둘 생기고, 아이들이 어디로 들어갈지 모르게 됩니다.
      <a href="#/그룹">그룹 · 팀</a> 에서 이름을 맞추시거나 지우신 뒤 다시 오세요.</div>
    </div>` : ''}

    ${hand.length ? `<div class="card warn">
      <b>보안 그룹 ${hand.length}개는 Teavel 이 아직 만들지 못합니다.</b>
      정식 관리 센터에서 손으로 만들어 주세요.
      ${hand.map(r => `<div class="sub">${esc(r.name)}</div>`).join('')}
    </div>` : ''}

    <h2>만들 것 ${make.length}개</h2>
    <div class="wrap"><table>
      <thead><tr><th>이름</th><th>별칭</th><th>무엇</th><th>채널</th></tr></thead>
      <tbody>${make.length === 0
        ? `<tr><td colspan="4" class="sub">없습니다.</td></tr>`
        : make.map(r => `<tr>
            <td class="name">${esc(r.name)}</td>
            <td class="sub">${esc(r.alias)}</td>
            <td class="tight">${r.kind === 'Team' ? '팀' : r.kind === 'Security' ? '보안 그룹' : 'M365 그룹'}</td>
            <td class="sub">${esc((r.channels || []).join(' · ')) || '—'}</td>
          </tr>`).join('')}</tbody>
    </table></div>

    <h2>이미 있는 것 ${have.length}개</h2>
    <p class="lede">건드리지 않습니다. 채널만 모자라면 채워 넣습니다.</p>
    <div class="wrap"><table>
      <thead><tr><th>이름</th><th>테넌트에 있는 것</th></tr></thead>
      <tbody>${have.length === 0
        ? `<tr><td colspan="2" class="sub">없습니다.</td></tr>`
        : have.map(r => `<tr class="dim"><td>${esc(r.name)}</td><td>${esc(r.existing)}</td></tr>`).join('')}</tbody>
    </table></div>`;

  const go = $('#go');
  if (!go || go.disabled) return;

  go.onclick = async () => {
    const yes = await ask({
      title: `${make.length}개를 만듭니다`,
      confirm: '만들기',
      body: `<p>없는 것만 만듭니다. 이미 있는 것은 건드리지 않습니다.<br>
             팀을 만들려면 <b>로그인이 한 번 더</b> 필요합니다 — 창이 따로 뜹니다.</p>`,
    });
    if (!yes) return;

    run(await api('/api/plan/create', {}), draw);
  };
};

// ── ⑤ 구성원 넣기 ───────────────────────────────────────────────────────

PAGES['구성원'] = async () => {
  const c = await api('/api/classes');

  if (!c.hasRoster) {
    $('#page').innerHTML = `
      <h1>구성원 넣기</h1>
      <div class="card"><b>명단이 먼저입니다.</b>
        <p class="lede" style="margin:6px 0 14px">누구를 어느 반에 넣을지는 명단에 들어 있습니다.</p>
        <a href="#/명단"><button class="go">명단 올리러 가기</button></a>
      </div>`;
    return;
  }

  state.classes = c.rows;

  $('#page').innerHTML = `
    <h1>구성원 넣기</h1>
    <p class="lede">
      명단의 학생을 반별 팀에 넣습니다. <b>이미 들어 있는 사람은 빼고 셌습니다</b> —
      학기 중에 전학생이 한 명 오면 그 한 명만 들어갑니다.
    </p>

    <div class="card" style="display:flex;align-items:center;gap:14px">
      <div class="grow">
        <b>${esc(c.summary)}</b>
        ${c.scanned ? '' : '<div class="sub">아직 각 팀에 누가 들어 있는지 읽지 않았습니다. 읽어야 정확한 인원이 나옵니다.</div>'}
      </div>
      <button id="scan">지금 팀 현황 읽기</button>
    </div>

    <div class="wrap"><table>
      <thead><tr><th>반</th><th>팀</th><th class="num">넣을 사람</th><th class="num">이미</th><th>담임</th><th></th></tr></thead>
      <tbody>${c.rows.map((r, i) => `
        <tr class="${r.problem ? 'dim' : ''}">
          <td class="name">${esc(r.classKey)}</td>
          <td>${esc(r.team) || '<span class="pill stop">팀 없음</span>'}
              ${r.problem ? `<div class="sub">${esc(r.problem)}</div>` : ''}</td>
          <td class="num">${r.toAdd ? r.toAdd + '명' : '—'}</td>
          <td class="num sub">${r.already ? r.already + '명' : '—'}</td>
          <td class="sub">${esc(r.owner) || '<span class="pill stop">없음</span>'}</td>
          <td class="tight">
            ${r.toAdd ? `<button class="tiny" data-see="${i}">누구인지 보기</button>
                         <button class="tiny go" data-add="${i}">${r.toAdd}명 넣기</button>` : ''}
          </td>
        </tr>
        <tr id="see-${i}" hidden><td colspan="6" style="background:var(--band)"></td></tr>`).join('')}
      </tbody>
    </table></div>`;

  $('#scan').onclick = async () => run(await api('/api/classes/scan', {}), draw);

  $$('button[data-see]').forEach(b => b.onclick = () => {
    const i = +b.dataset.see;
    const tr = $('#see-' + i);
    tr.hidden = !tr.hidden;
    b.textContent = tr.hidden ? '누구인지 보기' : '접기';

    if (!tr.hidden) tr.firstElementChild.innerHTML =
      state.classes[i].people.map(p =>
        `<span class="pill new" style="margin:2px">${esc(p.number)}번 ${esc(p.name)}</span>`).join(' ');
  });

  $$('button[data-add]').forEach(b => b.onclick = async () => {
    const r = state.classes[+b.dataset.add];

    const yes = await ask({
      title: `${r.classKey} — ${r.people.length}명 넣기`,
      confirm: '넣기',
      body: `<p><b>${esc(r.team)}</b> 에 넣습니다.<br>
             학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.</p>`,
    });
    if (!yes) return;

    run(await api('/api/members/add', {
      groupId: r.groupId,
      upns: r.people.map(p => p.upn),
      role: 'Member',
      label: `${r.classKey} 학생 넣기`,
    }), draw);
  });
};

// ── ⑥ 담임 ──────────────────────────────────────────────────────────────

PAGES['담임'] = async () => {
  const c = await api('/api/classes');
  const t = await api('/api/teachers');

  if (!c.hasRoster) {
    $('#page').innerHTML = `
      <h1>담임</h1>
      <div class="card"><b>명단이 먼저입니다.</b>
        <p class="lede" style="margin:6px 0 14px">어느 반이 있는지를 알아야 담임을 정할 수 있습니다.</p>
        <a href="#/명단"><button class="go">명단 올리러 가기</button></a>
      </div>`;
    return;
  }

  const rows = c.rows.filter(r => r.groupId);
  const options = t.rows.map(p =>
    `<option value="${esc(p.upn)}">${esc(p.name)} — ${esc(p.upn)}</option>`).join('');

  $('#page').innerHTML = `
    <h1>담임</h1>
    <p class="lede">
      담임을 정해 두면 그 선생님이 <b>팀 설정을 바꾸고 과제를 낼 수 있습니다.</b>
      정해 두지 않으면 그 일을 관리자가 반마다 대신 하게 됩니다.
      모르는 반은 비워 두세요 — 나중에 다시 오시면 됩니다.
    </p>

    ${c.scanned ? '' : `<div class="card warn">
      <b>지금 담임이 누구인지 아직 읽지 않았습니다.</b>
      읽지 않고 지정하면 이미 담임이 있는 반에 한 명을 더 얹게 될 수 있습니다.
      <div class="row" style="margin:12px 0 0"><button id="scan">지금 읽기</button></div>
    </div>`}

    <div class="wrap"><table>
      <thead><tr><th>반</th><th>팀</th><th>지금 담임</th><th>정할 담임</th></tr></thead>
      <tbody>${rows.map((r, i) => `
        <tr class="${r.owner ? 'dim' : ''}">
          <td class="name">${esc(r.classKey)}</td>
          <td class="sub">${esc(r.team)}</td>
          <td>${r.owner ? esc(r.owner) : '<span class="pill stop">없음</span>'}</td>
          <td>${r.owner
            ? '<span class="sub">이미 있어 건드리지 않습니다</span>'
            : `<select data-pick="${i}"><option value="">— 비워 둠 —</option>${options}</select>`}</td>
        </tr>`).join('')}</tbody>
    </table></div>

    <div class="row end" style="margin-top:16px">
      <span class="sub" id="count">아직 아무도 정하지 않으셨습니다.</span>
      <button class="go" id="go" disabled>담임 지정하기</button>
    </div>`;

  const scan = $('#scan');
  if (scan) scan.onclick = async () => run(await api('/api/classes/scan', {}), draw);

  const picked = () => $$('select[data-pick]')
    .map(s => ({ sel: s, row: rows[+s.dataset.pick] }))
    .filter(x => x.sel.value)
    .map(x => ({ classKey: x.row.classKey, groupId: x.row.groupId, upn: x.sel.value }));

  const retally = () => {
    const n = picked().length;
    $('#count').textContent = n ? `${n}개 반의 담임을 정하셨습니다.` : '아직 아무도 정하지 않으셨습니다.';
    $('#go').disabled = n === 0;
  };

  $$('select[data-pick]').forEach(s => s.onchange = retally);

  $('#go').onclick = async () => {
    const picks = picked();

    // 한 분이 두 반의 담임이 되는 것은 대개 잘못 고른 것이다. 막지는 않는다 —
    // 작은 학교에서는 정말 그럴 수 있다. 다만 그냥 지나가게 두지도 않는다.
    const seen = {};
    const twice = [];
    for (const p of picks) {
      if (seen[p.upn]) twice.push(p.upn); else seen[p.upn] = 1;
    }

    const yes = await ask({
      title: `담임 ${picks.length}명 지정`,
      confirm: '지정하기',
      body: `${twice.length ? `<p class="pill stop" style="padding:6px 10px">같은 선생님이 두 반 이상에 지정돼 있습니다. 확인해 주세요.</p>` : ''}
             <p>지정하려면 <b>로그인이 한 번 더</b> 필요합니다 — 창이 따로 뜹니다.</p>
             ${picks.map(p => `<div class="sub">${esc(p.classKey)} — ${esc(p.upn)}</div>`).join('')}`,
    });
    if (!yes) return;

    run(await api('/api/owners/assign', { picks }), draw);
  };
};

// ── ⑦ 사람 · 이름 ───────────────────────────────────────────────────────

PAGES['사람'] = async () => {
  const p = await api('/api/people');
  state.people = p.rows;

  $('#page').innerHTML = `
    <h1>사람 · 이름</h1>
    <p class="lede">
      학교 테넌트에 있는 계정입니다. <b>표시 이름이 비어 있거나 성·이름이 나뉘어 있으면</b>
      Teams 에서 사람을 찾기 어려워집니다. 여기서 바로 고치실 수 있습니다.
    </p>

    <div class="card"><b>${esc(p.summary)}</b></div>

    <div class="row">
      <input type="search" id="q" placeholder="이름 · 아이디로 찾기" style="width:280px">
      <label style="display:flex;align-items:center;gap:6px"><input type="checkbox" id="onlyBad"> 이름이 비어 있는 것만</label>
    </div>

    <div class="wrap"><table>
      <thead><tr><th>이름</th><th>아이디</th><th>부서</th><th>어떻게 볼지</th><th></th></tr></thead>
      <tbody id="rows"></tbody>
    </table></div>`;

  const paint = () => {
    const q = $('#q').value.trim();
    const bad = $('#onlyBad').checked;

    const rows = state.people
      .filter(r => (!bad || !r.name.trim()) && (!q || r.name.includes(q) || r.upn.includes(q)))
      .slice(0, 500);

    $('#rows').innerHTML = rows.length === 0
      ? `<tr><td colspan="5" class="sub">해당하는 것이 없습니다.</td></tr>`
      : rows.map(r => `
        <tr class="${r.licensed ? '' : 'dim'}">
          <td class="name">${esc(r.name) || '<span class="pill stop">비어 있음</span>'}</td>
          <td class="sub">${esc(r.upn)}</td>
          <td class="sub">${esc(r.department) || '—'}</td>
          <td class="tight">
            ${r.faculty ? '<span class="pill use">교사 라이선스</span>' : ''}
            ${r.licensed ? '' : '<span class="pill stop">라이선스 없음</span>'}
            ${r.outsider ? '<span class="pill sys">학교 밖</span>' : ''}
          </td>
          <td class="tight"><button class="tiny" data-upn="${esc(r.upn)}">이름 바꾸기</button></td>
        </tr>`).join('');

    $$('#rows button[data-upn]').forEach(b => b.onclick = async () => {
      const who = state.people.find(x => x.upn === b.dataset.upn);

      const name = await ask({
        title: '표시 이름 바꾸기',
        body: `<p>${esc(who.upn)}<br>Teams·아웃룩에 이 이름으로 보입니다.</p>`,
        field: true, placeholder: who.name || '홍길동', confirm: '바꾸기',
      });
      if (name === null || !name.trim()) return;

      const res = await api('/api/people/rename', { upn: who.upn, displayName: name.trim() });
      toast(res.message, !res.ok);
      if (res.ok) await draw();
    });
  };

  $('#q').oninput = paint;
  $('#onlyBad').onchange = paint;
  paint();
};

// ── 나가기 ──────────────────────────────────────────────────────────────

$('#refresh').onclick = async () => run(await api('/api/refresh', {}), draw);

$('#quit').onclick = async () => {
  const yes = await ask({
    title: '끝내기',
    confirm: '끝내기',
    body: '<p>관리 화면을 닫습니다. 지금까지 하신 것은 이미 학교 M365 에 반영돼 있습니다.</p>',
  });
  if (!yes) return;

  await api('/api/quit', {});
  document.body.innerHTML = '<div style="padding:80px;text-align:center">' +
    '<h1>끝났습니다</h1><p>이 창은 닫으셔도 됩니다.</p></div>';
};

window.addEventListener('hashchange', draw);
draw();
