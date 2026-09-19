/*
  Конструктор пакетів «Своєї гри» — панель «🎯 Своя гра» поруч із «Профілем». Вантажиться лише тоді, коли панель
  відкрили (заглушка в svoya.js), щоб модуль самої гри лишався легким.

  API (Impl/SvoyaSetup.cs, усе під /api/games/svoya):
    GET packs → { builtin, public, mine, canCreate }        рядки без запитань
    GET packs/{id} → { data: { pack, canEdit, ready, problems } }
    POST packs (порожнє тіло — класичний шаблон) · PUT packs/{id} (пакет цілком) · DELETE packs/{id}
    POST packs/{id}/copy · POST packs/{id}/hide {hidden} · POST packs/{id}/media (multipart file)
    GET|POST packs/{id}/tts → { ready, total } · POST check { text, answers[] } → { ok }
  Автозбереження: через 1,5 с після останньої зміни — PUT усього пакета. Сервер не зберігає лише зламане
  (задовге, криве), а недороблене зберігає як чернетку й каже, чого бракує.
*/
(() => {
  const ROOT = '/api/games/svoya/packs';
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const TYPES = [['normal', 'Звичайне'], ['cat', '🐱 Кіт у мішку'], ['auction', '🔨 Аукціон']];
  const MAX_Q = 8, MAX_THEMES = 10, MAX_ROUNDS = 10;

  const S = {
    host: null, ctx: null,
    list: null, loading: false, query: '',
    mode: 'list',
    pack: null, ready: false, problems: [], showProblems: false,
    round: 0, cell: null,
    saveTimer: 0, saving: false, dirty: false, status: '', statusErr: false,
    tts: null, ttsTimer: 0, uploading: '',
  };

  function nickOf() { return (S.ctx && S.ctx.me && S.ctx.me.nick) || ''; }
  const toast = (t, k) => (S.ctx && S.ctx.toast ? S.ctx.toast(t, k) : console.log('[svoya]', t));

  async function api(method, path, body) {
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(nickOf()) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) {
      const e = new Error((data && data.message) || ('HTTP ' + r.status));
      e.errors = data && data.errors;
      throw e;
    }
    return data;
  }

  // =============================================================================================
  // Список
  // =============================================================================================

  async function loadList() {
    S.loading = true;
    try { S.list = await api('GET', ROOT); }
    catch (e) { toast('Пакети не завантажились: ' + e.message, 'err'); }
    finally { S.loading = false; }
    if (S.mode === 'list') render();
  }

  function rowHtml(p, admin) {
    const themes = (p.rounds || []).filter((r) => !r.final).map((r) => r.themes.filter(Boolean).join(', ')).filter(Boolean).join(' · ');
    const badges = [];
    if (p.source === 'builtin') badges.push('<span class="chip">від Глечиків</span>');
    if (p.ready === false) badges.push('<span class="chip warn">чернетка</span>');
    else if (p.source !== 'builtin') badges.push(p.public ? '<span class="chip">публічний</span>' : '<span class="chip">лише мені</span>');
    if (p.hidden) badges.push('<span class="chip warn">сховано</span>');
    const btn = (act, label, cls) => '<button type="button" class="' + (cls || 'ghost small') + '" data-a="' + act + '" data-id="' + esc(p.id) + '">' + label + '</button>';
    const acts = [];
    if (p.ready !== false) { acts.push(btn('play', '▶ Грати', 'primary small')); acts.push(btn('host', '🎙 Вести самому')); }
    if (p.canEdit) acts.push(btn('edit', '✏️ Редагувати'));
    if (S.list && S.list.canCreate) acts.push(btn('copy', '⧉ Копія'));
    if (p.canEdit) acts.push(btn('delete', '🗑'));
    if (admin && p.source !== 'builtin' && p.public) acts.push(btn(p.hidden ? 'unhide' : 'hide', p.hidden ? 'Показати' : 'Сховати'));
    return '<div class="spk-row"><div class="spk-main"><b>' + esc(p.title || 'без назви') + '</b> ' + badges.join(' ')
      + '<div class="muted small">' + esc(p.author) + ' · ' + p.questions + ' запитань' + (p.mediaMb ? ' · ' + p.mediaMb + ' МБ медіа' : '')
      + (p.plays ? ' · зіграно ' + p.plays : '') + '</div>'
      + (themes ? '<div class="spk-themes small">' + esc(themes) + '</div>' : '') + '</div>'
      + '<div class="spk-acts">' + acts.join('') + '</div></div>';
  }

  function listHtml() {
    if (!S.list) return '<div class="svwait"><span class="spin"></span> завантажую пакети…</div>';
    const admin = S.ctx && S.ctx.me && S.ctx.me.role === 'admin';
    const q = S.query.trim().toLowerCase();
    const fits = (p) => !q || (p.title + ' ' + p.author + ' ' + (p.rounds || []).map((r) => r.themes.join(' ')).join(' ')).toLowerCase().includes(q);
    const group = (title, items, empty) => {
      const list = (items || []).filter(fits);
      return '<div class="spk-group"><h4>' + title + '</h4>'
        + (list.length ? list.map((p) => rowHtml(p, admin)).join('') : '<div class="muted small">' + (q ? 'нічого не знайшлось' : empty) + '</div>') + '</div>';
    };
    return '<div class="spk-head"><h3>🎯 Своя гра — пакети запитань</h3>'
      + (S.list.canCreate
        ? '<button type="button" class="primary" data-a="new">＋ Новий пакет</button>'
        : '<span class="muted small">Увійди під своїм ніком (не гостем), щоб робити пакети</span>')
      + '</div>'
      + '<input type="search" class="spk-search" placeholder="знайти пакет чи тему…" value="' + esc(S.query) + '">'
      + group('Мої', S.list.mine, 'ще нема — натисни «Новий пакет» або скопіюй готовий')
      + group('Від Глечиків', S.list.builtin, 'поки порожньо')
      + group('Публічні', S.list.public, 'ніхто ще не поділився');
  }

  async function play(id, live) {
    try {
      const r = await HGames.call('CreateRoom', 'svoya', live ? { host: 'live' } : {});
      if (!r || !r.ok) { toast((r && r.message) || 'Стіл не поставився', 'err'); return; }
      const p = await HGames.call('Act', r.roomId, 'pack', { id });
      if (p && !p.ok) toast(p.message, 'err');
      location.hash = '#games/room/' + encodeURIComponent(r.roomId);
    } catch (e) { toast(e.message, 'err'); }
  }

  async function listAction(a, id) {
    try {
      if (a === 'new') { const r = await api('POST', ROOT); openEditor(r.data); return; }
      if (a === 'play') return play(id, false);
      if (a === 'host') return play(id, true);
      if (a === 'edit') { const r = await api('GET', ROOT + '/' + encodeURIComponent(id)); openEditor(r.data); return; }
      if (a === 'copy') { const r = await api('POST', ROOT + '/' + encodeURIComponent(id) + '/copy'); toast(r.message, 'ok'); await loadList(); return; }
      if (a === 'delete') {
        if (!confirm('Видалити пакет разом із медіа? Повернути не вийде.')) return;
        await api('DELETE', ROOT + '/' + encodeURIComponent(id));
        toast('Пакет видалено', 'ok');
        await loadList();
        return;
      }
      if (a === 'hide' || a === 'unhide') { await api('POST', ROOT + '/' + encodeURIComponent(id) + '/hide', { hidden: a === 'hide' }); await loadList(); }
    } catch (e) { toast(e.message, 'err'); }
  }

  // =============================================================================================
  // Редактор
  // =============================================================================================

  function openEditor(full) {
    S.mode = 'edit';
    S.pack = full.pack;
    S.ready = !!full.ready;
    S.problems = full.problems || [];
    S.round = 0;
    S.cell = null;
    S.status = '';
    S.tts = null;
    render();
  }

  function closeEditor() {
    flush().then(() => {
      S.mode = 'list';
      S.pack = null;
      clearInterval(S.ttsTimer);
      loadList();
      render();
    });
  }

  const R = () => S.pack.rounds[S.round];
  const Q = () => (S.cell ? R().themes[S.cell.t].questions[S.cell.q] : null);

  /// Ціни для раунду n (від 1): крок 100 × n × (1..k) — як у SvoyaPack.Prices.
  const prices = (n, k) => Array.from({ length: k }, (_, i) => 100 * n * (i + 1));

  function newQuestion(price) { return { price, type: 'normal', text: '', answer: '', accept: [] }; }

  // ---------- збереження ----------

  function touch(structural) {
    S.dirty = true;
    S.status = 'є зміни…';
    S.statusErr = false;
    clearTimeout(S.saveTimer);
    S.saveTimer = setTimeout(save, 1500);
    if (structural) render(); else paintStatus();
  }

  async function save() {
    clearTimeout(S.saveTimer);
    if (!S.pack || !S.dirty) return;
    if (S.saving) { S.saveTimer = setTimeout(save, 500); return; }
    S.saving = true;
    S.dirty = false;
    S.status = 'зберігаю…';
    paintStatus();
    try {
      const r = await api('PUT', ROOT + '/' + encodeURIComponent(S.pack.id), S.pack);
      S.ready = !!r.data.ready;
      S.problems = r.data.problems || [];
      S.status = 'збережено ' + new Date().toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
    } catch (e) {
      S.dirty = true;
      S.statusErr = true;
      S.status = 'не зберіглось: ' + e.message + (e.errors && e.errors.length ? ' — ' + e.errors.slice(0, 3).join('; ') : '');
    } finally {
      S.saving = false;
      paintStatus();
    }
  }

  async function flush() { if (S.dirty) await save(); }

  function paintStatus() {
    const el = S.host && S.host.querySelector('.spk-status');
    if (!el) return;
    el.innerHTML = '<span class="' + (S.statusErr ? 'err' : 'muted') + ' small">' + esc(S.status) + '</span> '
      + (S.ready ? '<span class="chip ok">✅ готовий до гри</span>'
        : '<button type="button" class="chip warn" data-a="problems">⚠ ' + S.problems.length + ' зауваж.</button>');
    const pr = S.host.querySelector('.spk-problems');
    if (pr) {
      pr.hidden = !S.showProblems || S.ready;
      pr.innerHTML = S.problems.slice(0, 60).map((p) => '<div>' + esc(p) + '</div>').join('') + (S.problems.length > 60 ? '<div>…</div>' : '');
    }
  }

  /// Підпис вибраної клітинки (✓/•, 🐱, 🖼) — оновлюємо лише його, щоб не збивати фокус з інших полів.
  function paintCell() {
    const q = Q();
    const b = S.host && S.host.querySelector('.spk-cell.on');
    if (q && b) b.innerHTML = cellLabel(q, R().type === 'final');
  }

  // ---------- розмітка ----------

  function editorHtml() {
    const p = S.pack;
    const tabs = p.rounds.map((r, i) => '<button type="button" class="spk-tab' + (i === S.round ? ' on' : '') + '" data-a="round" data-i="' + i + '">'
      + esc(r.name || 'Раунд ' + (i + 1)) + (r.type === 'final' ? ' 🏁' : '') + '</button>').join('')
      + (p.rounds.length < MAX_ROUNDS ? '<button type="button" class="spk-tab add" data-a="addRound">＋ раунд</button>' : '');
    const r = R();
    return '<div class="spk-head"><button type="button" class="ghost" data-a="back">← До списку</button>'
      + '<div class="spk-status"></div>'
      + '<div class="spk-acts"><button type="button" class="ghost small" data-a="tts">🗣 Озвучити' + (S.tts ? ' ' + S.tts.ready + '/' + S.tts.total : '') + '</button>'
      + '<button type="button" class="primary small" data-a="playThis">▶ Грати</button></div></div>'
      + '<div class="spk-problems small" hidden></div>'
      + '<div class="spk-meta">'
      + '<input class="spk-title" data-f="title" maxlength="60" placeholder="Назва пакета" value="' + esc(p.title) + '">'
      + '<textarea data-f="description" maxlength="300" rows="2" placeholder="Опис (необов\'язково)">' + esc(p.description || '') + '</textarea>'
      + '<label class="small"><input type="checkbox" data-f="public"' + (p.public ? ' checked' : '') + '> публічний — у нього зможуть грати всі</label></div>'
      + '<div class="spk-tabs">' + tabs + '</div>'
      + (r ? '<div class="spk-round">'
        + '<input data-f="roundName" maxlength="60" placeholder="Назва раунду" value="' + esc(r.name) + '">'
        + '<select data-f="roundType"><option value="normal"' + (r.type !== 'final' ? ' selected' : '') + '>звичайний</option>'
        + '<option value="final"' + (r.type === 'final' ? ' selected' : '') + '>фінал (по одному запитанню на тему)</option></select>'
        + (r.type !== 'final' ? '<button type="button" class="ghost small" data-a="reprice">Перерахувати ціни</button>' : '')
        + (p.rounds.length > 1 ? '<button type="button" class="ghost small" data-a="delRound">✕ раунд</button>' : '')
        + '</div><div class="spk-grid">' + gridHtml() + '</div>'
        + '<div class="spk-drawer">' + drawerHtml() + '</div>' : '');
  }

  function cellLabel(q, final) {
    const filled = (q.text || q.media) && q.answer;
    const marks = (q.type === 'cat' ? '🐱' : q.type === 'auction' ? '🔨' : '')
      + (q.media ? (q.media.kind === 'image' ? '🖼' : q.media.kind === 'audio' ? '🔊' : '🎬') : '');
    return '<b>' + (final ? '🏁' : q.price) + '</b><i>' + marks + (filled ? '✓' : '•') + '</i>';
  }

  function gridHtml() {
    const r = R();
    if (!r) return '';
    const final = r.type === 'final';
    return r.themes.map((t, ti) => '<div class="spk-trow">'
      + '<div class="spk-tname"><input data-f="theme" data-t="' + ti + '" maxlength="60" placeholder="Тема ' + (ti + 1) + '" value="' + esc(t.name) + '">'
      + '<span class="spk-tbtns"><button type="button" class="ghost small" data-a="up" data-t="' + ti + '" title="Вище">⬆</button>'
      + '<button type="button" class="ghost small" data-a="down" data-t="' + ti + '" title="Нижче">⬇</button>'
      + '<button type="button" class="ghost small" data-a="delTheme" data-t="' + ti + '" title="Прибрати тему">✕</button></span></div>'
      + '<div class="spk-cells">' + t.questions.map((q, qi) => '<button type="button" class="spk-cell'
        + (S.cell && S.cell.t === ti && S.cell.q === qi ? ' on' : '') + '" data-a="cell" data-t="' + ti + '" data-q="' + qi + '">' + cellLabel(q, final) + '</button>').join('')
      + (!final && t.questions.length < MAX_Q ? '<button type="button" class="spk-cell add" data-a="addQ" data-t="' + ti + '" title="Ще запитання">＋</button>' : '')
      + '</div></div>').join('')
      + (r.themes.length < MAX_THEMES ? '<button type="button" class="ghost small" data-a="addTheme">＋ тема</button>' : '');
  }

  function mediaBox(m, which) {
    const url = m ? '/api/games/svoya/media/' + encodeURIComponent(S.pack.id) + '/' + encodeURIComponent(m.file) : '';
    const preview = !m ? '' : m.kind === 'image' ? '<img src="' + esc(url) + '" alt="">'
      : m.kind === 'audio' ? '<audio src="' + esc(url) + '" controls preload="none"></audio>'
        : '<video src="' + esc(url) + '" controls playsinline preload="none"></video>';
    return '<div class="spk-media">' + preview
      + (m ? '<span class="muted small">' + (m.seconds ? m.seconds + ' с' : '') + '</span><button type="button" class="ghost small" data-a="unmedia" data-w="' + which + '">✕ прибрати</button>' : '')
      + '<label class="ghost small spk-upload">' + (S.uploading === which ? '⏳ завантажую…' : m ? '↻ замінити' : '＋ картинка / звук / відео')
      + '<input type="file" accept="image/*,audio/*,video/*" data-up="' + which + '" hidden></label></div>';
  }

  function drawerHtml() {
    const q = Q();
    if (!q) return '<div class="muted small">Натисни клітинку, щоб написати запитання.</div>';
    const final = R().type === 'final';
    return '<div class="spk-qhead"><b>' + esc(R().themes[S.cell.t].name || 'Тема') + (final ? '' : ' · ' + q.price) + '</b>'
      + (!final ? '<button type="button" class="ghost small" data-a="delQ">✕ прибрати запитання</button>' : '') + '</div>'
      + (!final ? '<div class="spk-line"><label>Тип <select data-f="type">' + TYPES.map(([v, l]) => '<option value="' + v + '"' + (q.type === v ? ' selected' : '') + '>' + l + '</option>').join('') + '</select></label>'
        + '<label>Ціна <input type="number" data-f="price" min="1" step="100" value="' + q.price + '"></label>'
        + (q.type === 'cat' ? '<label>Ціна кота <input type="number" data-f="catPrice" min="1" step="100" placeholder="на вибір" value="' + (q.catPrice || '') + '"></label>' : '')
        + '</div>' : '')
      + '<label class="spk-block">Запитання<textarea data-f="text" maxlength="600" rows="3" placeholder="Що читає ведучий">' + esc(q.text) + '</textarea></label>'
      + mediaBox(q.media, 'media')
      + '<label class="spk-block">Відповідь<input data-f="answer" maxlength="120" placeholder="Правильна відповідь" value="' + esc(q.answer) + '"></label>'
      + '<label class="spk-block">Також приймати <span class="muted small">(через ;)</span><input data-f="accept" placeholder="прізвище без імені; латиницею…" value="' + esc((q.accept || []).join('; ')) + '"></label>'
      + '<div class="spk-check"><input class="spk-try" placeholder="а якщо напишуть…"><button type="button" class="ghost small" data-a="check">Перевірити</button><span class="spk-checked small"></span></div>'
      + '<label class="spk-block">Коментар <span class="muted small">(ведучий скаже після відповіді)</span><textarea data-f="comment" maxlength="600" rows="2">' + esc(q.comment || '') + '</textarea></label>'
      + '<div class="muted small">Медіа до відповіді (покажемо при розкритті):</div>' + mediaBox(q.answerMedia, 'answerMedia');
  }

  // ---------- зміни ----------

  function onInput(e) {
    const el = e.target;
    const f = el.dataset && el.dataset.f;
    if (!f || !S.pack) return;
    const p = S.pack;
    const q = Q();
    let structural = false;
    switch (f) {
      case 'title': p.title = el.value; break;
      case 'description': p.description = el.value; break;
      case 'public': p.public = el.checked; break;
      case 'roundName': R().name = el.value; structural = e.type === 'change'; break;
      case 'roundType': {
        R().type = el.value;
        if (el.value === 'final') R().themes.forEach((t) => { t.questions = [t.questions[0] || newQuestion(0)]; t.questions[0].type = 'normal'; });
        structural = true;
        break;
      }
      case 'theme': R().themes[+el.dataset.t].name = el.value; break;
      case 'type': q.type = el.value; if (q.type !== 'cat') delete q.catPrice; structural = true; break;
      case 'price': q.price = parseInt(el.value, 10) || 0; break;
      case 'catPrice': q.catPrice = parseInt(el.value, 10) || null; break;
      case 'text': q.text = el.value; break;
      case 'answer': q.answer = el.value; break;
      case 'accept': q.accept = el.value.split(';').map((x) => x.trim()).filter(Boolean); break;
      case 'comment': q.comment = el.value; break;
      default: return;
    }
    touch(structural);
    if (!structural) paintCell();
  }

  function reprice() {
    const n = S.pack.rounds.filter((r, i) => i <= S.round && r.type !== 'final').length;
    R().themes.forEach((t) => { const ps = prices(n, t.questions.length); t.questions.forEach((q, i) => { q.price = ps[i]; }); });
  }

  async function upload(which, file) {
    if (!file) return;
    S.uploading = which;
    render();
    try {
      const fd = new FormData();
      fd.append('file', file);
      const r = await fetch(ROOT + '/' + encodeURIComponent(S.pack.id) + '/media', { method: 'POST', body: fd, headers: { 'X-Nick': encodeURIComponent(nickOf()) } });
      const data = await r.json().catch(() => null);
      if (!r.ok) throw new Error((data && data.message) || 'HTTP ' + r.status);
      const d = data.data;
      Q()[which] = { kind: d.kind, file: d.file, seconds: d.seconds };
      if (d.warning) toast(d.warning, 'wait');
      S.uploading = '';
      touch(true);
      await save();                      // одразу: інакше файл, на який ще ніхто не посилається, могло б прибрати
    } catch (e) {
      S.uploading = '';
      toast('Не завантажилось: ' + e.message, 'err');
      render();
    }
  }

  async function tts() {
    await flush();
    try {
      const r = await api('POST', ROOT + '/' + encodeURIComponent(S.pack.id) + '/tts');
      S.tts = r.data;
      render();
      clearInterval(S.ttsTimer);
      S.ttsTimer = setInterval(async () => {
        if (!S.pack || S.mode !== 'edit') { clearInterval(S.ttsTimer); return; }
        try {
          const p = await api('GET', ROOT + '/' + encodeURIComponent(S.pack.id) + '/tts');
          S.tts = p.data;
          const b = S.host && S.host.querySelector('[data-a="tts"]');
          if (b) b.textContent = '🗣 Озвучити ' + S.tts.ready + '/' + S.tts.total;
          if (S.tts.ready >= S.tts.total) clearInterval(S.ttsTimer);
        } catch { clearInterval(S.ttsTimer); }
      }, 3000);
    } catch (e) { toast(e.message, 'err'); }
  }

  async function check() {
    const q = Q();
    const input = S.host.querySelector('.spk-try');
    const out = S.host.querySelector('.spk-checked');
    if (!q || !input || !input.value.trim()) return;
    try {
      const r = await api('POST', '/api/games/svoya/check', { text: input.value, answers: [q.answer, ...(q.accept || [])] });
      out.textContent = r.ok ? '✅ зарахується' : '❌ не зарахується — додай у «також приймати»';
      out.className = 'spk-checked small ' + (r.ok ? 'ok' : 'err');
    } catch (e) { out.textContent = e.message; }
  }

  function editAction(a, d) {
    const p = S.pack;
    const r = R();
    switch (a) {
      case 'back': return closeEditor();
      case 'problems': S.showProblems = !S.showProblems; return paintStatus();
      case 'round': S.round = +d.i; S.cell = null; return render();
      case 'addRound': {
        const normals = p.rounds.filter((x) => x.type !== 'final').length;
        const ps = prices(normals + 1, 5);
        const nr = { name: 'Раунд ' + (normals + 1), type: 'normal', themes: Array.from({ length: 5 }, () => ({ name: '', questions: ps.map(newQuestion) })) };
        const fi = p.rounds.findIndex((x) => x.type === 'final');
        if (fi >= 0) p.rounds.splice(fi, 0, nr); else p.rounds.push(nr);
        S.round = fi >= 0 ? fi : p.rounds.length - 1;
        S.cell = null;
        return touch(true);
      }
      case 'delRound':
        if (!confirm('Прибрати раунд «' + (r.name || '') + '» з усіма запитаннями?')) return;
        p.rounds.splice(S.round, 1);
        S.round = Math.max(0, S.round - 1);
        S.cell = null;
        return touch(true);
      case 'reprice': reprice(); return touch(true);
      case 'addTheme': {
        const n = p.rounds.filter((x, i) => i <= S.round && x.type !== 'final').length;
        r.themes.push({ name: '', questions: r.type === 'final' ? [newQuestion(0)] : prices(n, 5).map(newQuestion) });
        return touch(true);
      }
      case 'delTheme':
        if (!confirm('Прибрати тему з усіма її запитаннями?')) return;
        r.themes.splice(+d.t, 1);
        S.cell = null;
        return touch(true);
      case 'up': case 'down': {
        const i = +d.t;
        const j = a === 'up' ? i - 1 : i + 1;
        if (j < 0 || j >= r.themes.length) return;
        [r.themes[i], r.themes[j]] = [r.themes[j], r.themes[i]];
        if (S.cell && S.cell.t === i) S.cell.t = j; else if (S.cell && S.cell.t === j) S.cell.t = i;
        return touch(true);
      }
      case 'addQ': {
        const t = r.themes[+d.t];
        const last = t.questions.length ? t.questions[t.questions.length - 1].price : 0;
        t.questions.push(newQuestion(last + (t.questions.length > 1 ? last - t.questions[t.questions.length - 2].price : 100)));
        S.cell = { t: +d.t, q: t.questions.length - 1 };
        return touch(true);
      }
      case 'delQ': {
        const t = r.themes[S.cell.t];
        t.questions.splice(S.cell.q, 1);
        S.cell = null;
        return touch(true);
      }
      case 'cell': S.cell = { t: +d.t, q: +d.q }; return render();
      case 'unmedia': delete Q()[d.w]; return touch(true);
      case 'check': return check();
      case 'tts': return tts();
      case 'playThis':
        return flush().then(() => (S.ready ? play(p.id, false) : toast('Пакет ще не готовий — подивись зауваження', 'err')));
    }
  }

  // =============================================================================================
  // Каркас панелі
  // =============================================================================================

  function render() {
    if (!S.host) return;
    const focus = document.activeElement && S.host.contains(document.activeElement) ? document.activeElement.dataset.f : null;
    S.host.innerHTML = '<div class="spk">' + (S.mode === 'edit' && S.pack ? editorHtml() : listHtml()) + '</div>';
    if (S.mode === 'edit') paintStatus();
    if (focus) { const el = S.host.querySelector('[data-f="' + focus + '"]'); if (el && el.focus) el.focus(); }
  }

  function bind(host) {
    if (host._spk) return;
    host._spk = true;
    host.addEventListener('click', (e) => {
      const b = e.target.closest('[data-a]');
      if (!b) return;
      if (S.mode === 'list') listAction(b.dataset.a, b.dataset.id);
      else editAction(b.dataset.a, b.dataset);
    });
    host.addEventListener('input', (e) => {
      if (e.target.classList.contains('spk-search')) {
        S.query = e.target.value;
        const pos = e.target.selectionStart;
        render();
        const s = host.querySelector('.spk-search');
        if (s) { s.focus(); s.setSelectionRange(pos, pos); }
        return;
      }
      if (e.target.tagName === 'SELECT' || e.target.type === 'checkbox') return;   // їм вистачить change
      onInput(e);
    });
    host.addEventListener('change', (e) => {
      if (e.target.dataset && e.target.dataset.up) { upload(e.target.dataset.up, e.target.files && e.target.files[0]); return; }
      if (e.target.tagName === 'SELECT' || e.target.type === 'checkbox') onInput(e);
    });
  }

  window.SvoyaPacks = {
    mount(host, ctx) {
      S.host = host;
      S.ctx = ctx;
      bind(host);
      if (!S.list && !S.loading) loadList();
      render();
    },
    update(host, ctx) {
      S.ctx = ctx;
      if (S.host !== host) { S.host = host; bind(host); render(); }
    },
  };
  window.addEventListener('beforeunload', () => { if (S.dirty) save(); });
})();
