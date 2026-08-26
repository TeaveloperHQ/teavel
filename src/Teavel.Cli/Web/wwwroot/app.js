/*
  관리 화면.

  틀이 없다(리액트도, 번들러도 없다). 이 화면은 exe 안에 묻혀 교사 PC 로 가는데,
  그 PC 는 인터넷을 안 쓴다는 것이 Teavel 의 약속이라 CDN 을 부를 수 없고,
  묶어서 넣으면 그만큼 exe 가 커진다. 여기서 필요한 것은 표 몇 개와 단추 몇 개다.

  판단은 여기 없다. 무엇이 정리 후보이고 어느 반이 어느 팀인지는 전부 서버(C#)가 정해
  내려 주고, 이 파일은 그것을 그리고 누른 것을 돌려보낸다.

  낱장은 셋뿐이다 — 구성원 · 팀 · 그룹. 그보다 잘게 나누면 관리자가 '어느 낱장으로
  가야 하나' 부터 정해야 하고, 그것부터가 막히는 자리다.
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
    // 파일 이름은 퍼센트로 감싼다. HTTP 머리글은 라틴 문자만 실려서
    // '명단.xlsx' 를 그대로 붙이면 fetch 가 그 자리에서 거부한다.
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

/** 창 하나를 띄우고 안을 채운다. 닫으면 정리된다. */
function veil(html, onReady) {
  const el = document.createElement('div');
  el.id = 'veil';
  el.innerHTML = `<div class="box" role="dialog" aria-modal="true">${html}</div>`;
  document.body.appendChild(el);

  const close = () => { el.remove(); document.removeEventListener('keydown', onKey); };
  const onKey = e => { if (e.key === 'Escape') close(); };

  document.addEventListener('keydown', onKey);
  el.onclick = e => { if (e.target === el) close(); };

  if (onReady) onReady(el, close);
  return close;
}

/** 물어보는 창. 확인이면 적은 값(또는 true), 아니면 null. */
function ask({ title, body, confirm, danger, field, placeholder }) {
  return new Promise(resolve => {
    const close = veil(`
      <h3>${esc(title)}</h3>
      <div>${body || ''}</div>
      ${field ? `<input type="text" id="ask-field" placeholder="${esc(placeholder || '')}" autocomplete="off">` : ''}
      <div class="acts">
        <button id="ask-no">그만두기</button>
        <button id="ask-yes" class="${danger ? 'bad' : 'go'}">${esc(confirm || '확인')}</button>
      </div>`, (el, shut) => {

      const input = $('#ask-field', el);
      (input || $('#ask-yes', el)).focus();

      const done = v => { shut(); resolve(v); };
      el.addEventListener('keydown', e => { if (e.key === 'Enter' && input) done(input.value); });
      $('#ask-no', el).onclick = () => done(null);
      $('#ask-yes', el).onclick = () => done(input ? input.value : true);

      // 창을 밖에서 닫아도(Esc·바깥 누르기) 약속은 풀려야 한다.
      const watch = new MutationObserver(() => {
        if (!document.body.contains(el)) { watch.disconnect(); resolve(null); }
      });
      watch.observe(document.body, { childList: true });
    });
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

  $('#drawer-title').textContent = started.title || '진행 중';
  $('#drawer-state').textContent = '돌고 있습니다…';
  $('#drawer-lines').innerHTML = '';
  $('#drawer-close').hidden = true;
  $('#drawer').hidden = false;

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
  // location.hash 는 '#/%EA%B7%B8%EB%A3%B9' 다. 풀지 않으면 늘 첫 장만 뜬다.
  let hash = location.hash || '#/한눈에';
  try { hash = decodeURIComponent(hash); } catch (e) { /* 온 그대로 쓴다 */ }

  const name = hash.replace('#/', '');
  const page = PAGES[name] || PAGES['한눈에'];

  $$('a.nav').forEach(a => a.classList.toggle('on', a.getAttribute('href') === '#/' + name));
  $('#page').innerHTML = '<div class="loading">읽는 중…</div>';

  try { await page(); }
  catch (e) { $('#page').innerHTML = `<div class="card bad"><b>읽지 못했습니다.</b><p>${esc(e.message)}</p></div>`; }

  await chrome();
}

/** 왼쪽 메뉴의 숫자와 위쪽 명단 띠 — 어느 낱장에서나 같아야 한다. */
async function chrome() {
  try {
    const o = await api('/api/overview');
    const hello = await api('/api/hello');

    if (hello.school) $('#school').textContent = hello.school;

    const set = (id, n) => { const el = $(id); el.textContent = n ? n : ''; };
    set('#tag-team', o.toCreateTeams + o.candidateTeams);
    set('#tag-group', o.toCreateGroups + o.candidateGroups + o.security);
    set('#tag-member', o.nameless);

    const bar = $('#roster-bar');
    bar.classList.toggle('empty', !hello.rosterRows);
    $('#roster-what').innerHTML = hello.rosterRows
      ? `명단 <b>${esc(hello.roster)}</b> · 쓸 수 있는 줄 ${hello.rosterRows}개`
      : '<b>명단을 아직 안 올리셨습니다.</b> 명단이 있어야 반 팀·학생 넣기·담임을 할 수 있습니다.';
    $('#roster-open').textContent = hello.rosterRows ? '다른 명단으로 바꾸기' : '명단 올리기';
  } catch (e) { /* 메뉴 숫자는 없어도 화면은 돈다 */ }
}

// ── 명단 (띠 + 창) ──────────────────────────────────────────────────────

$('#roster-open').onclick = () => openRoster();

function openRoster() {
  veil(`
    <h3>명단</h3>
    <p>학생 명단이 있으면 훨씬 많은 것을 대신 해 드릴 수 있습니다.
    <b>몇 학년 몇 반까지 있는지도 명단을 보면 알 수 있어</b> 따로 여쭙지 않아도 됩니다.
    양식은 맞추지 않으셔도 됩니다.</p>

    <div class="drop" id="drop">
      <p>여기에 파일을 끌어다 놓으시거나</p>
      <button class="go" id="pick">파일 고르기</button>
      <input type="file" id="file" hidden accept=".csv,.txt,.tsv,.xlsx,.xlsm,.hwpx">
      <p class="sub" style="margin-top:14px">csv · xlsx · hwpx 를 읽습니다.<br>
      한셀은 [다른 이름으로 저장] 에서 xlsx 로, 한글은 HWPX 로 한 번 저장해 주세요.</p>
    </div>

    <!--
      양식은 '맞춰야 하는 것' 이 아니라 '없을 때 쓰는 보기' 다.
      그 순서를 뒤집으면 이미 명단을 들고 계신 분이 굳이 옮겨 적게 된다.
    -->
    <details class="tell">
      <summary>명단이 아예 없으시면 — 양식 내려받기</summary>
      <div class="body">
        <p><b>이미 명단이 있으시면 양식은 필요 없습니다.</b> 그대로 올리세요 —
        열 이름이 '학급' 이든 '성명' 이든 '계정' 이든 알아서 찾습니다.</p>

        <p>처음부터 만드셔야 한다면 이 다섯 칸만 채우시면 됩니다.</p>

        <div class="wrap" style="margin-bottom:12px"><table>
          <thead><tr><th>학년</th><th>반</th><th>번호</th><th>이름</th><th>아이디</th></tr></thead>
          <tbody>
            <tr><td>1</td><td>1</td><td>1</td><td>김민준</td><td class="sub">10101@school.example.kr</td></tr>
            <tr><td>1</td><td>1</td><td>2</td><td>이서연</td><td class="sub">10102@school.example.kr</td></tr>
          </tbody>
        </table></div>

        <p class="sub">
          <b>아이디</b>는 학생이 Teams 에 로그인할 때 쓰는 주소입니다. 이것이 있어야 반에 넣을 수 있습니다.<br>
          <b>학번</b>과 <b>표시 이름</b>은 적지 않으셔도 됩니다 — 위 다섯 칸으로 Teavel 이 만들어 채웁니다.
        </p>

        <a href="/roster-template.csv" download="명단-양식.csv"><button>양식 내려받기 (csv)</button></a>
        <span class="sub">엑셀에서 바로 열립니다.</span>
      </div>
    </details>

    <div id="roster-result"></div>
    <div class="acts"><button id="roster-done">닫기</button></div>`,

  (el, shut) => {
    const drop = $('#drop', el);
    const file = $('#file', el);

    $('#roster-done', el).onclick = shut;
    $('#pick', el).onclick = () => file.click();
    file.onchange = () => file.files[0] && send(file.files[0]);

    drop.ondragover = e => { e.preventDefault(); drop.classList.add('over'); };
    drop.ondragleave = () => drop.classList.remove('over');
    drop.ondrop = e => {
      e.preventDefault();
      drop.classList.remove('over');
      if (e.dataTransfer.files[0]) send(e.dataTransfer.files[0]);
    };

    async function send(f) {
      const out = $('#roster-result', el);
      out.innerHTML = '<div class="loading">읽는 중…</div>';

      let res;
      try { res = await api('/api/roster', await f.arrayBuffer(), f.name); }
      catch (e) { out.innerHTML = `<div class="card bad">${esc(e.message)}</div>`; return; }

      if (!res.ok) {
        out.innerHTML = `<div class="card bad"><b>${esc(res.message)}</b>
          ${(res.details || []).map(d => `<div class="sub">${esc(d)}</div>`).join('')}</div>`;
        return;
      }

      const byGrade = {};
      for (const c of res.shape) (byGrade[c.grade] = byGrade[c.grade] || []).push(c);

      out.innerHTML = `
        <div class="card">
          <b>${esc(res.message)}</b>
          <div class="sub">${(res.how || []).map(esc).join(' · ')}</div>
        </div>
        <div class="card">
          <b>${esc(res.describe)}</b>
          ${Object.keys(byGrade).sort((a, b) => a - b).map(g => `
            <div style="margin-top:8px">${esc(g)}학년 &nbsp;
              ${byGrade[g].map(c => `<span class="pill new">${c.classNo}반 ${c.count}명</span>`).join(' ')}
            </div>`).join('')}
        </div>
        ${res.bad.length ? `<div class="card warn">
          <b>쓸 수 없는 줄이 ${res.bad.length}개 있습니다.</b> 그 줄은 빼고 넣습니다.
          ${res.bad.map(b => `<div class="sub">${b.line}번째 줄 — ${esc(b.problems.join(' · '))}</div>`).join('')}
        </div>` : ''}`;

      await draw();
    }
  });
}

// ── 한눈에 ──────────────────────────────────────────────────────────────

PAGES['한눈에'] = async () => {
  const o = await api('/api/overview');
  const hello = await api('/api/hello');

  const todo = [];
  if (!hello.rosterRows) todo.push(['명단을 올리시면 반 팀과 학생 넣기가 열립니다.', '', '명단 올리기']);
  if (o.toCreateTeams) todo.push([`아직 없는 반 팀이 ${o.toCreateTeams}개 있습니다.`, '#/팀', '만들러 가기']);
  if (o.conflicts) todo.push([`이름이 비슷해 사람이 봐야 할 것이 ${o.conflicts}개 있습니다.`, '#/팀', '확인하기']);
  if (o.candidates) todo.push([`정리해 볼 만한 것이 ${o.candidates}개 있습니다.`, '#/그룹', '보러 가기']);
  if (o.security) todo.push([`보안 그룹 ${o.security}개는 Teavel 이 만들지 않습니다. 무엇인지 적어 두었습니다.`, '#/그룹', '읽어 보기']);
  if (o.nameless) todo.push([`표시 이름이 비어 있는 계정이 ${o.nameless}개 있습니다.`, '#/구성원', '이름 붙이기']);

  $('#page').innerHTML = `
    <h1>한눈에</h1>
    <p class="lede">지금 학교 M365 가 어떤 상태인지, 그리고 무엇부터 하시면 되는지입니다.</p>

    <div class="tiles">
      <div class="tile"><div class="n">${o.people}</div><div class="k">사람</div></div>
      <div class="tile"><div class="n">${o.teams}</div><div class="k">팀</div></div>
      <div class="tile"><div class="n">${o.groups - o.teams}</div><div class="k">그 밖의 그룹</div></div>
      <div class="tile ${o.unlicensed ? 'hot' : ''}"><div class="n">${o.unlicensed}</div><div class="k">라이선스 없는 계정</div></div>
      <div class="tile ${o.toCreate ? 'hot' : ''}"><div class="n">${o.toCreate}</div><div class="k">아직 없는 것</div></div>
      <div class="tile ${o.candidates ? 'hot' : ''}"><div class="n">${o.candidates}</div><div class="k">정리 후보</div></div>
    </div>

    <h2>무엇부터 하면 되나</h2>
    ${todo.length === 0
      ? `<div class="card"><b>손볼 것이 없습니다.</b><p class="lede" style="margin:6px 0 0">지금은 선언한 대로 다 갖춰져 있습니다.</p></div>`
      : todo.map(([text, href, label], i) => `
        <div class="card" style="display:flex;align-items:center;gap:14px">
          <div class="grow">${esc(text)}</div>
          ${href ? `<a href="${href}"><button>${esc(label)}</button></a>`
                 : `<button data-todo="${i}">${esc(label)}</button>`}
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

  $$('button[data-todo]').forEach(b => b.onclick = () => openRoster());
};

// ── 구성원 ──────────────────────────────────────────────────────────────

/**
 * 왼쪽 나무를 짓는다 — 교사 / 학생(학년 · 반) / 그 밖.
 *
 * 칸마다 걸개를 두는 것보다 이쪽이 낫다. 값을 미리 알아야 칠 수 있는 상자와 달리,
 * 쓸 수 있는데, 나무는 열어 보면 그 학교에 무엇이 있는지가 그대로 나온다.
 * 관리자가 아는 말('3학년 2반')이 그대로 가지 이름이 된다.
 */
function peopleTree(rows) {
  const kids = [];

  const put = (id, label, list, sub) => {
    if (list.length) kids.push({ id, label, count: list.length, kids: sub || [] });
  };

  put('role:교사', '교사', rows.filter(r => r.role === '교사'));

  const students = rows.filter(r => r.role === '학생');
  if (students.length) {
    const grades = [...new Set(students.map(r => r.grade).filter(Boolean))]
      .sort((a, b) => a - b)
      .map(g => {
        const inGrade = students.filter(r => r.grade === g);
        const classes = [...new Set(inGrade.map(r => r.classNo).filter(Boolean))]
          .sort((a, b) => a - b)
          .map(c => ({
            id: `class:${g}-${c}`, label: `${c}반`,
            count: inGrade.filter(r => r.classNo === c).length, kids: [],
          }));
        return { id: `grade:${g}`, label: `${g}학년`, count: inGrade.length, kids: classes };
      });

    const stray = students.filter(r => !r.grade || !r.classNo);
    if (stray.length) grades.push({ id: 'stray', label: '학년·반 모름', count: stray.length, kids: [] });

    kids.push({ id: 'role:학생', label: '학생', count: students.length, kids: grades });
  }

  put('role:그 밖', '그 밖', rows.filter(r => r.role === '그 밖'));
  put('role:라이선스 없음', '라이선스 없음', rows.filter(r => r.role === '라이선스 없음'));
  put('role:학교 밖', '학교 밖', rows.filter(r => r.role === '학교 밖'));

  return [{ id: 'all', label: '전체', count: rows.length, kids }];
}

/** 고른 가지에 드는 사람인지. */
function inBranch(r, pick) {
  if (!pick || pick === 'all') return true;
  if (pick === 'stray') return r.role === '학생' && (!r.grade || !r.classNo);
  if (pick.startsWith('role:')) return r.role === pick.slice(5);
  if (pick.startsWith('grade:')) return r.role === '학생' && r.grade === pick.slice(6);

  if (pick.startsWith('class:')) {
    const [g, c] = pick.slice(6).split('-');
    return r.role === '학생' && r.grade === g && r.classNo === c;
  }
  return true;
}

function treeHtml(nodes, depth, pick, open) {
  return nodes.map(n => {
    const has = n.kids.length > 0;
    const shown = open.has(n.id);

    return `<div class="node ${n.id === pick ? 'on' : ''}" data-node="${esc(n.id)}"
                 style="padding-left:${8 + depth * 16}px">
        <span class="tw i" data-twist="${esc(n.id)}">${has ? (shown ? '' : '') : ''}</span>
        <span class="grow">${esc(n.label)}</span>
        <span class="n">${n.count}</span>
      </div>
      ${has && shown ? treeHtml(n.kids, depth + 1, pick, open) : ''}`;
  }).join('');
}

/**
 * 학교 사람 명부.
 *
 * 교사인지 학생인지 Teavel 이 단정하지 않는다 — 라이선스와 아이디 생김새를 나란히 두면
 * 관리자가 한눈에 가른다. 학생 아이디는 학번 꼴이고 교사는 아니다.
 */
PAGES['구성원'] = async () => {
  const p = await api('/api/people');

  state.people = p.rows.map(r => ({ ...r, groupText: r.groups.join(' · ') }));
  state.sort = state.sort || { key: 'name', dir: 1 };
  state.pick = state.pick || 'all';
  state.open = state.open || new Set(['all', 'role:학생']);

  const tree = peopleTree(state.people);

  $('#page').innerHTML = `
    <h1>구성원</h1>
    <p class="lede">
      학교 테넌트에 있는 사람 전부입니다. 왼쪽에서 <b>교사 · 학년 · 반</b>을 열어 보시면 됩니다.
      ${p.hasRoster ? '' : '학년·반은 표시 이름의 학번에서 알아냈습니다 — 명단을 올리시면 더 정확해집니다.'}
    </p>

    ${p.scanned ? '' : `<div class="card warn" style="display:flex;align-items:center;gap:14px">
      <div class="grow">
        <b>누가 어느 팀에 있는지 아직 안 읽었습니다.</b>
        <div class="sub">읽어야 '속해 있는 그룹' 칸이 채워집니다. 팀 수만큼 물어보므로 조금 걸리고,
        <b>로그인이 한 번 더</b> 필요합니다.</div>
      </div>
      <button id="scan">지금 읽기</button>
    </div>`}

    <div class="commands">
      <button class="cmd" id="bulk"><span class="i">&#xE710;</span> 명단으로 학생 넣기</button>
      <button class="cmd" id="pw"><span class="i">&#xE192;</span> 비밀번호 재설정</button>
      <span class="grow"></span>
      <input type="search" id="q" placeholder="이름 · 아이디로 찾기" style="width:230px">
      <span class="sub" id="count"></span>
    </div>

    <div class="split">
      <div class="tree" id="tree"></div>
      <div class="wrap"><table>
        <thead><tr>
          <th class="sortable" data-sort="name">표시 이름 <span class="arrow" data-arrow="name"></span></th>
          <th class="sortable" data-sort="upn">ID <span class="arrow" data-arrow="upn"></span></th>
          <th class="sortable" data-sort="license">라이선스 <span class="arrow" data-arrow="license"></span></th>
          <th class="sortable" data-sort="groupText">속해 있는 그룹 <span class="arrow" data-arrow="groupText"></span></th>
          <th></th>
        </tr></thead>
        <tbody id="rows"></tbody>
      </table></div>
    </div>`;

  $('#bulk').onclick = () => openClasses('add');
  $('#q').oninput = paint;

  // 지금 왼쪽 나무에서 고른 가지가 그대로 대상이 된다 —
  // '3학년 2반' 을 열어 두고 누르면 그 반이고, '전체' 면 전체다.
  $('#pw').onclick = () => openPassword(
    state.people.filter(r => inBranch(r, state.pick)),
    treeLabel(tree, state.pick));

  const scan = $('#scan');
  if (scan) scan.onclick = async () => run(await api('/api/classes/scan', {}), draw);

  $$('th.sortable').forEach(th => th.onclick = () => {
    const key = th.dataset.sort;
    state.sort = { key, dir: state.sort.key === key ? -state.sort.dir : 1 };
    paint();
  });

  paintTree();
  paint();

  function paintTree() {
    $('#tree').innerHTML = treeHtml(tree, 0, state.pick, state.open);

    $$('#tree [data-twist]').forEach(el => el.onclick = e => {
      e.stopPropagation();
      const id = el.dataset.twist;
      if (state.open.has(id)) state.open.delete(id); else state.open.add(id);
      paintTree();
    });

    $$('#tree [data-node]').forEach(el => el.onclick = () => {
      state.pick = el.dataset.node;
      state.open.add(state.pick);
      paintTree();
      paint();
    });
  }

  function paint() {
    const q = $('#q').value.trim();
    const { key, dir } = state.sort;

    const branch = state.people.filter(r => inBranch(r, state.pick));
    const rows = branch.filter(r => !q || r.name.includes(q) || r.upn.includes(q));

    // 그룹 칸은 글자가 아니라 개수로 줄을 세운다 — '많이 속한 사람' 을 보려는 칸이다.
    rows.sort((a, b) => dir * (key === 'groupText'
      ? (a.groups.length - b.groups.length) || a.name.localeCompare(b.name, 'ko')
      : String(a[key] || '').localeCompare(String(b[key] || ''), 'ko')));

    $$('[data-arrow]').forEach(s => {
      s.textContent = s.dataset.arrow === key ? (dir > 0 ? '▲' : '▼') : '';
    });

    $('#count').textContent = `${rows.length}명`
      + (rows.length !== state.people.length ? ` (전체 ${state.people.length}명)` : '');

    $('#rows').innerHTML = rows.length === 0
      ? `<tr><td colspan="5" class="sub">여기에 든 사람이 없습니다.</td></tr>`
      : rows.slice(0, 500).map(r => `
        <tr class="${r.licensed ? '' : 'dim'}">
          <td class="name">${esc(r.name) || '<span class="pill stop">비어 있음</span>'}
              ${r.outsider ? '<span class="pill sys">학교 밖</span>' : ''}</td>
          <td class="sub">${esc(r.upn)}</td>
          <td class="tight">
            <span class="pill ${r.license === '교사' ? 'use' : r.license === '학생' ? 'new'
                              : r.license === '없음' ? 'stop' : 'sys'}">${esc(r.license)}</span>
            ${r.licenseCount ? `<span class="sub"> 묶음 ${r.licenseCount}명</span>` : ''}
          </td>
          <td>${r.groups.length
              ? r.groups.map(g => `<span class="pill sys" style="margin:1px">${esc(g)}</span>`).join(' ')
              : `<span class="sub">${p.scanned ? '없음' : '아직 안 읽음'}</span>`}</td>
          <td class="tight"><button class="tiny" data-upn="${esc(r.upn)}"><span class="i"></span> 이름 바꾸기</button></td>
        </tr>`).join('')
        + (rows.length > 500 ? `<tr><td colspan="5" class="sub">앞의 500명만 보여 드립니다. 왼쪽에서 반을 골라 주세요.</td></tr>` : '');

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
  }
};

/** 고른 가지의 이름. '3학년 2반' 처럼 관리자가 아는 말로 되돌린다. */
function treeLabel(nodes, pick) {
  for (const n of nodes) {
    if (n.id === pick) return n.label;
    const deep = treeLabel(n.kids, pick);
    if (deep) return n.id === 'all' ? deep : `${n.label} ${deep}`;
  }
  return '';
}

/**
 * 비밀번호 재설정.
 *
 * <b>이것 하나만 Microsoft Graph 가 필요하다.</b> Exchange 에도 Teams 에도 비밀번호
 * cmdlet 이 없고 MSOnline·AzureAD 는 은퇴했다. 그래서 처음 보는 동의 화면이 한 번 뜨는데,
 * 거기서 겁을 먹고 [취소] 를 누르면 이 기능이 통째로 막힌다. 무엇에 동의하는지 미리 적는다.
 */
async function openPassword(people, where) {
  const targets = people.filter(r => r.licensed);

  veil(`
    <h3>비밀번호 재설정</h3>
    <p><b>${esc(where || '고른 사람')}</b> — ${targets.length}명</p>

    ${targets.length === 0 ? `<div class="card warn">
      <b>바꿀 수 있는 사람이 없습니다.</b>
      <div class="sub">라이선스가 없는 계정은 비밀번호를 바꿔도 로그인하지 못합니다.</div>
    </div>` : ''}

    <div id="pw-ready"><div class="loading">준비 상태를 보는 중…</div></div>

    <div class="card">
      <label style="display:flex;align-items:center;gap:8px">
        <input type="checkbox" id="pw-must" checked>
        <span>학생이 <b>처음 로그인할 때 새 비밀번호를 정하게</b> 합니다</span>
      </label>
      <div class="sub" style="margin-top:6px">
        꺼 두면 나눠 준 임시 비밀번호를 계속 쓰게 됩니다. 대개는 켜 두시는 편이 낫습니다.
      </div>
    </div>

    <details class="tell">
      <summary>어떤 권한을 허용하게 되나요?</summary>
      <div class="body">
        <p>비밀번호는 Teams·Exchange 로는 바꿀 수 없습니다. Microsoft Graph 라는 창구가
        따로 있고, 그 창구를 열려면 <b>한 번 허용</b>이 필요합니다.</p>

        <p>요청하는 것은 <b>하나뿐</b>입니다 — <code>User-PasswordProfile.ReadWrite.All</code>.
        말 그대로 <b>비밀번호를 바꾸는 것</b> 말고는 아무것도 못 합니다.
        메일도, 파일도, 그룹도 못 봅니다.</p>

        <p><b>허용해도 다른 선생님이 관리자가 되지는 않습니다.</b> 실제로 할 수 있는 일은
        <i>허용한 범위 ∩ 그 사람의 역할</i> 이라서, 허용은 권한을 넓히는 것이 아니라
        창구를 여는 것입니다.</p>

        <p>자기 자신이나 상급 관리자의 비밀번호는 바꿀 수 없습니다 — 그런 계정은
        건너뛰고 나머지를 마저 합니다.</p>
      </div>
    </details>

    <div class="card warn">
      <b>바뀐 비밀번호는 한 번만 보여 드립니다.</b>
      <div class="sub">끝나면 종이로 옮길 파일을 내려받으실 수 있습니다.
      Teavel 은 그것을 어디에도 저장하지 않습니다 — 받아 가시면 그것으로 끝입니다.</div>
    </div>

    <div class="acts">
      <button id="pw-no">그만두기</button>
      <button id="pw-go" class="go" ${targets.length ? '' : 'disabled'}>${targets.length}명 재설정</button>
    </div>`,

  (el, shut) => {
    $('#pw-no', el).onclick = shut;

    // 모듈이 갖춰졌는지부터 본다. 없으면 여기서 갖출 수 있게 한다 —
    // '무엇을 설치하세요' 라고 적어 두고 끝내면 그 자리에서 막힌다.
    api('/api/password/ready').then(r => {
      $('#pw-ready', el).innerHTML = r.ready
        ? ''
        : `<div class="card warn">
             <b>${esc(r.message)}</b>
             ${(r.details || []).map(d => `<div class="sub">${esc(d)}</div>`).join('')}
             <div class="row" style="margin:12px 0 0"><button id="pw-install">지금 갖추기</button></div>
           </div>`;

      const inst = $('#pw-install', el);
      if (inst) inst.onclick = async () => { shut(); run(await api('/api/password/install', {}), draw); };
    }).catch(() => { $('#pw-ready', el).innerHTML = ''; });

    $('#pw-go', el).onclick = async () => {
      const mustChange = $('#pw-must', el).checked;
      shut();

      const started = await api('/api/password/reset', {
        upns: targets.map(r => r.upn),
        mustChange,
        label: `비밀번호 재설정 — ${where || ''} ${targets.length}명`,
      });

      run(started, () => offerSlips(started.jobId, where));
    };
  });
}

/**
 * 바뀐 비밀번호를 종이로 옮길 파일로 내준다.
 *
 * 서버는 이것을 내주고 곧바로 지운다. 그래서 <b>여기서 받지 않으면 다시 알 방법이 없고</b>,
 * 그게 맞다 — 남아 있으면 언젠가 새 나간다.
 */
async function offerSlips(jobId, where) {
  let res;
  try { res = await api('/api/password/result?id=' + encodeURIComponent(jobId)); }
  catch (e) { return; }

  if (!res.ok || !res.rows.length) return;

  const head = '이름,ID,임시 비밀번호';
  const body = res.rows.map(r =>
    [r.name, r.upn, r.password].map(v => `"${String(v).replace(/"/g, '""')}"`).join(',')).join('\r\n');

  // 엑셀이 한글을 제대로 열려면 BOM 이 있어야 한다.
  const blob = new Blob(['﻿' + head + '\r\n' + body + '\r\n'], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);

  veil(`
    <h3>바뀐 비밀번호 ${res.rows.length}개</h3>
    <p><b>이 화면을 닫으면 다시 볼 수 없습니다.</b> 지금 내려받아 종이로 옮겨 주세요.</p>

    <div class="row">
      <a id="pw-save" href="${url}" download="임시비밀번호-${esc((where || '').replace(/[\\/:*?"<>|]/g, ''))}.csv">
        <button class="go"><span class="i">&#xE896;</span> 파일로 내려받기</button>
      </a>
      <span class="sub">엑셀에서 바로 열립니다.</span>
    </div>

    <div class="wrap" style="max-height:40vh;overflow-y:auto"><table>
      <thead><tr><th>이름</th><th>ID</th><th>임시 비밀번호</th></tr></thead>
      <tbody>${res.rows.map(r => `<tr>
        <td class="name">${esc(r.name)}</td>
        <td class="sub">${esc(r.upn)}</td>
        <td><code>${esc(r.password)}</code></td>
      </tr>`).join('')}</tbody>
    </table></div>

    <div class="card warn" style="margin-top:14px">
      <b>내려받은 파일은 종이로 옮기신 뒤 지워 주세요.</b>
      <div class="sub">내려받기 폴더에 그대로 두면 그 PC 를 쓰는 누구나 볼 수 있습니다.</div>
    </div>

    <div class="acts"><button id="pw-done" class="go">다 옮겼습니다</button></div>`,

  (el, shut) => {
    $('#pw-done', el).onclick = () => { URL.revokeObjectURL(url); shut(); };
  });
}

/** 한꺼번에 하는 일 — 반 × 학생, 반 × 담임. 낱장을 늘리지 않고 창으로 연다. */
async function openClasses(mode) {
  const c = await api('/api/classes');

  if (!c.hasRoster) {
    veil(`<h3>명단이 먼저입니다</h3>
      <p>누구를 어느 반에 넣을지는 명단에 들어 있습니다.</p>
      <div class="acts"><button id="go" class="go">명단 올리러 가기</button></div>`,
      (el, shut) => { $('#go', el).onclick = () => { shut(); openRoster(); }; });
    return;
  }

  veil(`<div id="classbody" class="wide"><div class="loading">읽는 중…</div></div>`, (el, shut) => {
    const body = $('#classbody', el);
    el.querySelector('.box').classList.add('wide');

    const done = `<div class="acts"><button id="close">닫기</button></div>`;
    (mode === 'owner' ? fillOwners(body, c, done) : Promise.resolve(fillAdd(body, c, done)))
      .then(() => { const b = $('#close', el); if (b) b.onclick = shut; });
  });
}

/** 학생 넣기 — 반 단위로. 이미 들어 있는 사람은 서버가 이미 빼 두었다. */
function fillAdd(body, c, tail) {
  state.classes = c.rows;

  body.innerHTML = `
    <h3>명단으로 학생 한꺼번에 넣기</h3>
    <p class="sub">이미 들어 있는 사람은 빼고 셌습니다 — 전학생이 한 명 오면 그 한 명만 들어갑니다.</p>

    <div class="card" style="display:flex;align-items:center;gap:14px">
      <div class="grow">
        <b>${esc(c.summary)}</b>
        ${c.scanned ? '' : '<div class="sub">아직 각 팀에 누가 들어 있는지 읽지 않았습니다. 읽어야 정확한 인원이 나옵니다.</div>'}
      </div>
      <button id="scan">지금 팀 현황 읽기</button>
    </div>

    <div class="wrap"><table>
      <thead><tr><th>반</th><th>팀</th><th class="num">넣을 사람</th><th class="num">이미</th><th></th></tr></thead>
      <tbody>${c.rows.map((r, i) => `
        <tr class="${r.problem ? 'dim' : ''}">
          <td class="name">${esc(r.classKey)}</td>
          <td>${esc(r.team) || '<span class="pill stop">팀 없음</span>'}
              ${r.problem ? `<div class="sub">${esc(r.problem)}</div>` : ''}</td>
          <td class="num">${r.toAdd ? r.toAdd + '명' : '—'}</td>
          <td class="num sub">${r.already ? r.already + '명' : '—'}</td>
          <td class="tight">
            ${r.toAdd ? `<button class="tiny" data-see="${i}">누구인지 보기</button>
                         <button class="tiny go" data-add="${i}">${r.toAdd}명 넣기</button>` : ''}
          </td>
        </tr>
        <tr id="see-${i}" hidden><td colspan="5" style="background:var(--band)"></td></tr>`).join('')}
      </tbody>
    </table></div>
    ${tail || ''}`;

  $('#scan').onclick = async () => run(await api('/api/classes/scan', {}), draw);

  $$('button[data-see]', body).forEach(b => b.onclick = () => {
    const tr = $('#see-' + b.dataset.see);
    tr.hidden = !tr.hidden;
    b.textContent = tr.hidden ? '누구인지 보기' : '접기';

    if (!tr.hidden) tr.firstElementChild.innerHTML =
      state.classes[+b.dataset.see].people.map(p =>
        `<span class="pill new" style="margin:2px">${esc(p.number)}번 ${esc(p.name)}</span>`).join(' ');
  });

  $$('button[data-add]', body).forEach(b => b.onclick = async () => {
    const r = state.classes[+b.dataset.add];

    const yes = await ask({
      title: `${r.classKey} — ${r.people.length}명 넣기`,
      confirm: '넣기',
      body: `<p><b>${esc(r.team)}</b> 에 넣습니다.<br>학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.</p>`,
    });
    if (!yes) return;

    run(await api('/api/members/add', {
      groupId: r.groupId,
      upns: r.people.map(p => p.upn),
      role: 'Member',
      label: `${r.classKey} 학생 넣기`,
    }), draw);
  });
}

/** 담임 — 반마다 팀 소유자. 콘솔에서는 반 하나마다 물었다. */
async function fillOwners(body, c, tail) {
  const t = await api('/api/teachers');
  const rows = c.rows.filter(r => r.groupId);
  const options = t.rows.map(p => `<option value="${esc(p.upn)}">${esc(p.name)} — ${esc(p.upn)}</option>`).join('');

  body.innerHTML = `
    <h3>반별 담임 정하기</h3>
    <p class="sub">
      담임을 정해 두면 그 선생님이 <b>팀 설정을 바꾸고 과제를 낼 수 있습니다.</b>
      정해 두지 않으면 그 일을 관리자가 반마다 대신 하게 됩니다. 모르는 반은 비워 두세요.
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
    </div>
    ${tail || ''}`;

  const scan = $('#scan', body);
  if (scan) scan.onclick = async () => run(await api('/api/classes/scan', {}), draw);

  const picked = () => $$('select[data-pick]', body)
    .filter(s => s.value)
    .map(s => ({ classKey: rows[+s.dataset.pick].classKey, groupId: rows[+s.dataset.pick].groupId, upn: s.value }));

  const retally = () => {
    const n = picked().length;
    $('#count', body).textContent = n ? `${n}개 반의 담임을 정하셨습니다.` : '아직 아무도 정하지 않으셨습니다.';
    $('#go', body).disabled = n === 0;
  };

  $$('select[data-pick]', body).forEach(s => s.onchange = retally);

  $('#go', body).onclick = async () => {
    const picks = picked();

    // 한 분이 두 반의 담임이 되는 것은 대개 잘못 고른 것이다. 막지는 않는다 —
    // 작은 학교에서는 정말 그럴 수 있다. 다만 그냥 지나가게 두지도 않는다.
    const seen = {};
    let twice = false;
    for (const p of picks) { if (seen[p.upn]) twice = true; else seen[p.upn] = 1; }

    const yes = await ask({
      title: `담임 ${picks.length}명 지정`,
      confirm: '지정하기',
      body: `${twice ? `<p class="pill stop" style="padding:6px 10px">같은 선생님이 두 반 이상에 지정돼 있습니다. 확인해 주세요.</p>` : ''}
             <p>지정하려면 <b>로그인이 한 번 더</b> 필요합니다 — 창이 따로 뜹니다.</p>
             ${picks.map(p => `<div class="sub">${esc(p.classKey)} — ${esc(p.upn)}</div>`).join('')}`,
    });
    if (!yes) return;

    run(await api('/api/owners/assign', { picks }), draw);
  };
}

// ── 팀 · 그룹 (같은 뼈대, 갈라 보여 줄 뿐) ──────────────────────────────

PAGES['팀'] = () => stock({
  title: '팀 (Teams)',
  lede: '수업 팀을 만들고, 지난 학년도 팀을 정리합니다. <b>이미 있는 것은 건드리지 않습니다.</b>',
  kind: 'team',
});

PAGES['그룹'] = () => stock({
  title: '그룹',
  lede: '팀이 아닌 그룹입니다 — 메일 그룹, 보안 그룹, 예전에 쓰다 남은 것들.',
  kind: 'group',
});

async function stock({ title, lede, kind }) {
  const team = kind === 'team';
  const p = await api('/api/plan');
  const g = await api('/api/groups');

  state.groups = g.rows;

  const mine = r => (r.kind === 'Team') === team;
  const make = p.rows.filter(r => r.action === '만들 것' && mine(r));
  const stuck = p.rows.filter(r => r.action === '확인 필요' && mine(r));
  const hand = team ? [] : p.rows.filter(r => r.action === '손으로');

  $('#page').innerHTML = `
    <h1>${esc(title)}</h1>
    <p class="lede">${lede}</p>

    ${stuck.length ? `<div class="card warn">
      <b>사람이 봐야 할 것이 ${stuck.length}개 있습니다.</b> 이것들은 만들지 않고 건너뜁니다.
      ${stuck.map(r => `<div style="margin-top:6px">${esc(r.name)} <span class="sub">— ${esc(r.reason)}</span></div>`).join('')}
      <div class="sub" style="margin-top:8px">그대로 만들면 거의 같은 것이 둘 생기고, 아이들이 어디로 들어갈지 모르게 됩니다.
      아래에서 이름을 맞추시거나 지우신 뒤 다시 오세요.</div>
    </div>` : ''}

    ${hand.length ? securityCard(hand) : ''}

    <p class="lede">지우면 그 안의 파일과 대화가 함께 사라집니다.
    이름만 바꾸면 내용은 그대로 남으니, 잘 모르겠으면 그냥 두시거나 이름만 바꾸세요.</p>

    <!--
      할 수 있는 일은 목록 위 가로줄에 모은다 — 관리 센터가 어느 목록에서나 그렇게 한다.
      담임은 팀의 소유자이므로 사람 쪽이 아니라 여기에 둔다.
    -->
    <div class="commands">
      <button class="cmd" id="go" ${make.length ? '' : 'disabled'}>
        <span class="i">&#xE710;</span> ${team ? '반 팀 만들기' : '그룹 만들기'}${make.length ? ` (${make.length})` : ''}
      </button>
      ${team ? `<button class="cmd" id="owners"><span class="i">&#xE77B;</span> 반별 담임 정하기</button>` : ''}
      <span class="grow"></span>
      <input type="search" id="q" placeholder="이름으로 찾기" style="width:220px">
      <label style="display:flex;align-items:center;gap:6px;padding:0 10px">
        <input type="checkbox" id="only"> 정리 후보만
      </label>
      <span class="sub" id="count"></span>
    </div>

    <div class="wrap"><table>
      <thead><tr>
        <th>이름</th><th class="num">구성원</th><th>만든 날</th><th>어떻게 볼지</th><th>할 수 있는 것</th>
      </tr></thead>
      <tbody id="rows"></tbody>
    </table></div>

    ${p.own ? `<div class="card" style="margin-top:20px;display:flex;align-items:center;gap:14px">
      <div class="grow">
        <b>만들 목록을 이 학교에 맞게 손보신 것이 있습니다.</b>
        <div class="sub">Teavel 판이 올라가도 그대로 남습니다.</div>
      </div>
      <button id="reset">처음 상태로 되돌리기</button>
    </div>` : ''}`;

  const owners = $('#owners');
  if (owners) owners.onclick = () => openClasses('owner');

  const go = $('#go');
  if (go && make.length) go.onclick = async () => {
    const yes = await ask({
      title: `${make.length}개를 만듭니다`,
      confirm: '만들기',
      body: `<p>없는 것만 만듭니다. 이미 있는 것은 건드리지 않습니다.
             ${team ? '<br>팀을 만들려면 <b>로그인이 한 번 더</b> 필요합니다 — 창이 따로 뜹니다.' : ''}</p>
             <div class="wrap"><table>
               <thead><tr><th>이름</th><th>별칭</th><th>채널</th></tr></thead>
               <tbody>${make.map(r => `<tr>
                 <td class="name">${esc(r.name)}</td>
                 <td class="sub">${esc(r.alias)}</td>
                 <td class="sub">${esc((r.channels || []).join(' · ')) || '—'}</td>
               </tr>`).join('')}</tbody>
             </table></div>`,
    });
    if (!yes) return;
    run(await api('/api/plan/create', { kind }), draw);
  };

  wireDrop();
  wireReset();

  const paint = () => {
    const q = $('#q').value.trim();
    const only = $('#only').checked;

    const all = state.groups.filter(r => r.isTeam === team);
    const rows = all.filter(r => (!only || r.candidate) && (!q || r.name.includes(q) || r.alias.includes(q)));

    $('#count').textContent = `${rows.length}개` + (rows.length !== all.length ? ` (전체 ${all.length}개)` : '');

    $('#rows').innerHTML = rows.length === 0
      ? `<tr><td colspan="5" class="sub">해당하는 것이 없습니다.</td></tr>`
      : rows.map(r => `
        <tr>
          <td>
            <div class="name">${esc(r.name)}</div>
            <div class="sub">${esc(r.alias)}</div>
          </td>
          <td class="num">${r.members >= 0 ? r.members + '명' : '모름'}</td>
          <td class="tight sub">${esc(r.created) || '모름'}</td>
          <td>
            <span class="pill ${r.candidate ? 'cand' : r.locked ? 'sys' : 'use'}">${esc(r.bucket)}</span>
            ${r.note ? `<div class="sub">${esc(r.note)}</div>` : ''}
          </td>
          <td class="tight">
            ${r.locked
              ? '<span class="sub">건드리지 않습니다</span>'
              : `<button class="tiny" data-do="rename" data-alias="${esc(r.alias)}"><span class="i"></span> 이름</button>
                 ${r.archiveName ? `<button class="tiny" data-do="archive" data-alias="${esc(r.alias)}"><span class="i"></span> 보관</button>` : ''}
                 <button class="tiny bad" data-do="delete" data-alias="${esc(r.alias)}"><span class="i"></span> 지우기</button>`}
          </td>
        </tr>`).join('');

    $$('#rows button[data-do]').forEach(b => b.onclick = () => tidy(b.dataset.do, b.dataset.alias));
  };

  $('#q').oninput = paint;
  $('#only').onchange = paint;
  paint();
}

function securityCard(hand) {
  return `<div class="card warn">
    <b>보안 그룹 ${hand.length}개는 Teavel 이 만들지 않습니다.</b>
    ${hand.map(r => `<div style="margin-top:4px">${esc(r.name)}</div>`).join('')}

    <details class="tell plain">
      <summary>보안 그룹이 무엇이고, 어떻게 만드나요?</summary>
      <div class="body">
        <p><b>사람을 묶어 두는 것</b>입니다. 권한이나 정책을 여러 사람에게 한꺼번에 걸 때 씁니다 —
        예를 들어 어떤 앱을 교직원에게만 허용하는 식입니다.</p>

        <p><b>팀이 아닙니다.</b> 대화도 파일도 없고 메일 주소도 없습니다.
        만들어 두어도 학생·교사 화면에는 아무것도 나타나지 않습니다.</p>

        <p><b>왜 Teavel 이 안 만드나</b> — 이것 하나만 Microsoft Graph 권한이 필요합니다.
        그 권한을 받으려면 관리자가 <b>낯선 이름의 앱에 넓은 권한을 승인하는 화면</b>을 봐야 합니다.
        잘 모르는 채로 승인하시게 두는 것보다, 이 하나를 손으로 만드시는 편이 낫다고 봤습니다.
        팀·M365 그룹·구성원은 마이크로소프트 자체 모듈로 되기 때문에 그런 화면이 없습니다.</p>

        <p><b>만드는 순서</b> — 네 번 누르면 됩니다.</p>
        <ol>
          <li>아래 단추로 Microsoft 365 관리 센터를 엽니다</li>
          <li>왼쪽에서 <b>[팀 및 그룹] → [활성 팀 및 그룹]</b></li>
          <li>위쪽 <b>[보안 그룹]</b> 탭 → <b>[보안 그룹 추가]</b></li>
          <li>이름에 <b>${esc(hand[0].name)}</b> 을(를) 그대로 적고 [추가]</li>
        </ol>
        <p class="sub">화면 이름은 테넌트 언어·판에 따라 조금 다를 수 있습니다.
        '그룹' 아래에서 종류를 <b>보안</b>으로 고르는 자리를 찾으시면 됩니다.</p>

        <p><b>지금 안 만드셔도 됩니다.</b> 이 그룹에 걸어 둔 정책이 없다면 있어도 아무 일도 하지 않습니다.
        안 만드셔도 반 팀·학생 넣기·담임은 전부 그대로 됩니다.</p>

        <div class="row" style="margin:16px 0 0">
          <a href="https://admin.microsoft.com/#/groups" target="_blank" rel="noreferrer noopener">
            <button>관리 센터의 그룹 화면 열기 ↗</button>
          </a>
          ${hand.map(r => `<button data-drop="${esc(r.id)}" data-name="${esc(r.name)}">
            '${esc(r.name)}' 은 이 학교엔 필요 없습니다</button>`).join('')}
        </div>
      </div>
    </details>
  </div>`;
}

function wireDrop() {
  $$('button[data-drop]').forEach(b => b.onclick = async () => {
    const yes = await ask({
      title: `'${b.dataset.name}' 을(를) 뺍니다`,
      confirm: '뺍니다',
      body: `<p>이 학교의 만들 목록에서만 뺍니다. <b>테넌트는 건드리지 않습니다</b> —
             이미 만들어져 있다면 그대로 남아 있습니다.</p>
             <p>다음부터 이 안내가 안 뜹니다. 나중에 [처음 상태로 되돌리기] 로 되살릴 수 있습니다.</p>`,
    });
    if (!yes) return;

    const res = await api('/api/tree/drop', { id: b.dataset.drop, name: b.dataset.name });
    toast(res.message, !res.ok);
    if (res.ok) await draw();
  });
}

function wireReset() {
  const reset = $('#reset');
  if (!reset) return;

  reset.onclick = async () => {
    const yes = await ask({
      title: '처음 상태로 되돌리기',
      confirm: '되돌립니다',
      body: '<p>이 학교에 맞게 손보신 것을 버리고 Teavel 이 들고 온 처음 목록으로 돌아갑니다.<br>테넌트는 건드리지 않습니다.</p>',
    });
    if (!yes) return;

    const res = await api('/api/tree/reset', {});
    toast(res.message, !res.ok);
    if (res.ok) await draw();
  };
}

/** 이름 바꾸기 · 보관 · 지우기. 팀이든 그룹이든 하는 일은 같다. */
async function tidy(what, alias) {
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
    toast(res.message, !res.ok);
    if (res.ok) await draw();
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
