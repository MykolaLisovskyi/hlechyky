/*
  Озвуч рекламу — конкурс, а не гра-кімната.

  Правила, записи й голоси живуть на сервері (Games/Impl/AdContest.cs) і ходять власним HTTP
  (/api/ads). Тут — рендер і наміри: показати сценарій, дати мікрофон, програти чужі записи,
  віддати голос.

  Малюємо в двох місцях одним і тим самим кодом:
   - панель «🎙 Реклама» поруч із «Профілем» (HGames.registerPanel);
   - картка гри 'ad-contest' — вона ж плитка «Реклама глека» в «Компанії». Картка потрібна ще й
     тому, що завантажувач модулів у core.js бере файли рівно з каталогу ігор: модуль, який реєструє
     саму лише панель, ніхто б не завантажив (див. коментар в Impl/AdContestTile.cs).

  Запис — це скопійовані з app.js функції MediaRecorder: імпортувати звідти нема як, а ліміт тут
  свій (сервер каже його в maxSeconds, типово 30 с).
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="6" y="1.6" width="4" height="7.5" rx="2" fill="var(--accent)"/>'
    + '<path d="M3.6 7.2a4.4 4.4 0 0 0 8.8 0" stroke="var(--accent2)" stroke-width="1.4" fill="none" stroke-linecap="round"/>'
    + '<path d="M8 11.6v2.8" stroke="var(--accent2)" stroke-width="1.4" stroke-linecap="round"/></svg>';

  // =============================================================================================
  // Дрібне
  // =============================================================================================

  const escOf = (ctx) => (ctx && ctx.esc) || ((s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])));
  const toastOf = (ctx) => (ctx && ctx.toast) || ((t) => console.log('[ads]', t));
  const nickOf = (ctx) => (ctx && ctx.me && ctx.me.nick) || '';
  const isAdmin = (ctx) => !!(ctx && ctx.me && ctx.me.role === 'admin');

  /// Той самий api(), що в app.js: нік їде заголовком, помилка приходить полем message.
  async function api(ctx, method, path, body) {
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(nickOf(ctx)) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((data && data.message) || ('HTTP ' + r.status));
    return data;
  }

  /// Спінер у кнопці, поки сервер думає (копія busy() з app.js).
  async function busy(btn, label, fn) {
    if (!btn || btn.disabled) return;
    const html = btn.innerHTML;
    btn.disabled = true;
    btn.classList.add('busy');
    btn.innerHTML = '<span class="spin"></span> ' + label;
    try { return await fn(); }
    finally { if (btn.isConnected) { btn.disabled = false; btn.classList.remove('busy'); btn.innerHTML = html; } }
  }

  const fmt = (sec) => Math.floor(sec / 60) + ':' + String(Math.floor(sec % 60)).padStart(2, '0');

  /// «2 дні 4 год» / «3 год 12 хв» / «за хвилину»: точні секунди тут нікому не потрібні.
  function left(iso) {
    const ms = Date.parse(iso) - Date.now();
    if (!(ms > 0)) return 'ось-ось закриється';
    const m = Math.floor(ms / 60000), h = Math.floor(m / 60), d = Math.floor(h / 24);
    if (d > 0) return 'ще ' + d + ' ' + plural(d, 'день', 'дні', 'днів') + ' ' + (h % 24) + ' год';
    if (h > 0) return 'ще ' + h + ' год ' + (m % 60) + ' хв';
    return 'ще ' + Math.max(1, m) + ' ' + plural(m, 'хвилина', 'хвилини', 'хвилин');
  }
  function plural(n, one, few, many) {
    const a = Math.abs(n);
    if (a % 100 >= 11 && a % 100 <= 14) return many;
    return (a % 10) === 1 ? one : (a % 10 >= 2 && a % 10 <= 4) ? few : many;
  }
  const votes = (n) => n + ' ' + plural(n, 'голос', 'голоси', 'голосів');

  // =============================================================================================
  // Стан: дані одні на всі місця, де ми малюємо
  // =============================================================================================

  // air — ефір реклами, його бачить лише господар; null — не господар або сервер ще не вміє /api/ads/air.
  // draft — що господар набрав у полях частоти й ще не зберіг: опитування не має витирати це з-під пальців.
  const state = { data: null, error: '', loading: false, air: null, draft: { every: null, mins: null } };
  const hosts = [];            // { el, ctx } — панель і/або картка кімнати
  let poll = 0;
  let tickTimer = 0;

  function alive() {
    for (let i = hosts.length - 1; i >= 0; i--) if (!hosts[i].el.isConnected) hosts.splice(i, 1);
    return hosts.length > 0;
  }

  function attach(el, ctx) {
    alive();
    const found = hosts.find((h) => h.el === el);
    if (found) { found.ctx = ctx; return; }
    hosts.push({ el, ctx });
    if (!poll) poll = setInterval(() => {
      if (!alive()) { stop(); return; }
      // Панель лишається в дереві, коли людина пішла на «Ефір» (core.js DOM не чіпає, app.js лише
      // перемикає класи на body) — тож питаємо сервер тільки поки нас справді видно.
      if (visible()) load();
    }, 15000);
    if (!tickTimer) tickTimer = setInterval(paintClocks, 30000);
  }

  /// Чи видно нас на екрані: сховану вкладку браузера й закриту вкладку «Ігри» опитувати ні до чого.
  const visible = () => !document.hidden && hosts.some((h) => h.el.offsetParent);

  // Повернулись до вкладки — показуємо свіже, а не те, що було чверть години тому.
  document.addEventListener('visibilitychange', () => { if (!document.hidden && alive() && visible()) load(); });

  function stop() {
    clearInterval(poll); poll = 0;
    clearInterval(tickTimer); tickTimer = 0;
  }

  async function load() {
    if (!alive() || state.loading) return;
    state.loading = true;
    try {
      const ctx = hosts[0].ctx;
      const [data, air] = await Promise.all([
        api(ctx, 'GET', '/api/ads'),
        isAdmin(ctx) ? api(ctx, 'GET', '/api/ads/air').catch(() => null) : Promise.resolve(null),
      ]);
      state.data = data;
      state.air = air;
      state.error = '';
    } catch (e) {
      state.error = e.message;
    } finally {
      state.loading = false;
      paintAll();
    }
  }

  const paintAll = () => { if (alive()) for (const h of hosts) paint(h.el, h.ctx); };
  const paintClocks = () => {
    if (!alive()) { stop(); return; }
    for (const h of hosts) h.el.querySelectorAll('[data-till]').forEach((e) => { e.textContent = left(e.dataset.till); });
  };

  // =============================================================================================
  // Запис у браузері (скопійовано з app.js; свій ліміт і своя адреса)
  // =============================================================================================

  const MIMES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
  const canRecord = () => !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
  // target: 'contest' — запис у конкурс, 'air' — своя реклама господаря просто в ефір.
  const rec = { host: null, target: 'contest', stage: 'idle', recorder: null, stream: null, chunks: [], startedAt: 0, timer: 0,
    tossed: false, blob: null, url: null, sec: 0, max: 30 };
  const AIR_MAX = 60;

  async function startRec(host, ctx, max, target) {
    if (rec.recorder) return;
    if (!canRecord()) { toastOf(ctx)('Цей браузер не вміє писати звук (треба https і свіжий Chrome, Firefox або Safari)', 'err'); return; }
    if (!nickOf(ctx)) { toastOf(ctx)('Спершу скажи, як тебе кликати', 'err'); return; }
    let stream;
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
    catch (e) {
      toastOf(ctx)(e.name === 'NotAllowedError' ? 'Мікрофон не дозволено — дозволь у браузері й спробуй ще' : 'Мікрофон не відкрився: ' + e.message, 'err');
      return;
    }
    dropBlob();
    rec.host = host;
    rec.target = target || 'contest';
    rec.max = max || 30;
    rec.stream = stream;
    rec.chunks = [];
    rec.tossed = false;
    const type = MIMES.find((m) => MediaRecorder.isTypeSupported(m));
    try { rec.recorder = new MediaRecorder(stream, type ? { mimeType: type, audioBitsPerSecond: 96000 } : undefined); }
    catch { rec.recorder = new MediaRecorder(stream); }
    rec.recorder.ondataavailable = (e) => { if (e.data && e.data.size) rec.chunks.push(e.data); };
    rec.recorder.onstop = finishRec;
    rec.recorder.start();
    rec.startedAt = Date.now();
    rec.stage = 'live';
    paintAll();
    rec.timer = setInterval(() => {
      // Хост міг зникнути з дерева: панель у core.js перемальовується без unmount (перемкнув вкладку —
      // і в тілі вже новий div). Перевішуємо запис на живе місце, а як живого нема — гасимо мікрофон,
      // інакше червона крапка горіла б до кінця ліміту, а записане лягло б у відірваний вузол.
      if (rec.host && !rec.host.isConnected) {
        const live = hosts.find((h) => h.el.isConnected);
        if (!live) { rec.tossed = true; stopRec(); return; }
        rec.host = live.el;
        paintAll();
      }
      const sec = (Date.now() - rec.startedAt) / 1000;
      const el = rec.host && rec.host.querySelector('.rkrec-time');
      if (el) el.textContent = fmt(sec);
      if (sec >= rec.max) stopRec();          // довше сервер усе одно не візьме
    }, 200);
    meterOn(stream);
  }

  function stopRec() {
    clearInterval(rec.timer);
    rec.timer = 0;
    if (rec.recorder && rec.recorder.state !== 'inactive') { try { rec.recorder.stop(); } catch { /* уже стало */ } }
  }

  function finishRec() {
    const type = (rec.recorder && rec.recorder.mimeType) || 'audio/webm';
    const sec = Math.round((Date.now() - rec.startedAt) / 1000);
    const blob = new Blob(rec.chunks, { type });
    rec.recorder = null;
    rec.chunks = [];
    meterOff();
    releaseMic();
    if (rec.tossed) { closeRec(); return; }
    if (blob.size < 1024) { closeRec(); toastOf(hosts[0] && hosts[0].ctx)('Нічого не записалось, спробуй ще раз', 'err'); return; }
    rec.blob = blob;
    rec.url = URL.createObjectURL(blob);
    rec.sec = sec;
    rec.stage = 'prev';
    paintAll();
  }

  function closeRec() {
    clearInterval(rec.timer);
    rec.timer = 0;
    meterOff();
    releaseMic();
    dropBlob();
    rec.recorder = null;
    rec.stage = 'idle';
    rec.host = null;
    paintAll();
  }
  function dropBlob() {
    if (rec.url) URL.revokeObjectURL(rec.url);
    rec.url = null;
    rec.blob = null;
  }
  function releaseMic() {
    if (rec.stream) rec.stream.getTracks().forEach((t) => t.stop());   // гасне й червона крапка у вкладці
    rec.stream = null;
  }

  // Смужки рівня: видно, що мікрофон таки чує, а не пише тишу.
  let actx = null, analyser = null, raf = 0;
  function meterOn(stream) {
    try {
      actx = new (window.AudioContext || window.webkitAudioContext)();
      analyser = actx.createAnalyser();
      analyser.fftSize = 256;
      actx.createMediaStreamSource(stream).connect(analyser);
      const data = new Uint8Array(analyser.frequencyBinCount);
      const step = () => {
        if (!analyser) return;
        analyser.getByteFrequencyData(data);
        if (rec.host) rec.host.querySelectorAll('.rkrec-bars i').forEach((b, i) => {
          b.style.transform = 'scaleY(' + Math.max(0.14, Math.min(1, (data[2 + i * 3] / 255) * 1.7)) + ')';
        });
        raf = requestAnimationFrame(step);
      };
      step();
    } catch { /* без смужок теж пишеться */ }
  }
  function meterOff() {
    cancelAnimationFrame(raf);
    raf = 0;
    analyser = null;
    try { if (actx) actx.close(); } catch { /* уже закритий */ }
    actx = null;
  }

  async function sendRec(id, ctx) {
    if (!rec.blob) return;
    const r = await fetch(rec.target === 'air' ? '/api/ads/air/record' : '/api/ads/' + id + '/entry', {
      method: 'POST',
      headers: { 'Content-Type': rec.blob.type || 'application/octet-stream', 'X-Nick': encodeURIComponent(nickOf(ctx)) },
      body: rec.blob,
    });
    let data = null;
    try { data = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((data && data.message) || ('HTTP ' + r.status));
    toastOf(ctx)((data && data.message) || 'Запис прийнято', 'ok');
    closeRec();
    await load();
  }

  window.addEventListener('pagehide', closeRec);

  // =============================================================================================
  // Програвач: одне <audio> на весь модуль, щоб два записи не грали разом
  // =============================================================================================

  let audio = null;
  function play(trackId) {
    if (!audio) { audio = new Audio(); audio.addEventListener('ended', () => { audio.dataset.id = ''; paintAll(); }); }
    if (audio.dataset.id === trackId && !audio.paused) { audio.pause(); audio.dataset.id = ''; paintAll(); return; }
    audio.dataset.id = trackId;
    audio.src = '/api/voice/' + encodeURIComponent(trackId) + '.mp3';
    audio.play().then(paintAll).catch(() => { audio.dataset.id = ''; paintAll(); });
  }
  const playing = (trackId) => !!(audio && audio.dataset.id === trackId && !audio.paused);

  // =============================================================================================
  // Рендер
  // =============================================================================================

  function paint(host, ctx) {
    const esc = escOf(ctx);
    if (!state.data) {
      host.innerHTML = state.error
        ? '<div class="gempty">Конкурс не прочитався: ' + esc(state.error) + '</div>'
        : '<div class="gwait"><span class="spin"></span> дивлюсь, що там із рекламою…</div>';
      return;
    }
    const a = state.data.active;
    const html = '<div class="rkbox">'
      + (a ? active(a, ctx, esc, host) : '<div class="gempty">Зараз конкурсу нема. Новий відкривається щопонеділка опівдні.</div>')
      + past(state.data.past || [], esc)
      + airBox(ctx, esc, host)
      + admin(a, ctx)
      + '</div>';
    if (host.dataset.sig !== html) {
      // Господар набирає частоту — не перемальовуємо поле під курсором, решта почекає наступного разу.
      const f = document.activeElement;
      if (f && host.contains(f) && f.matches('[data-every], [data-mins]')) return;
      host.dataset.sig = html;
      host.innerHTML = html;
    }
    wire(host, ctx, a);
  }

  function active(a, ctx, esc, host) {
    const mine = a.entries.find((e) => e.mine);
    return '<div class="rkposter"><span class="rkposter-h">Сценарій</span>' + esc(a.script) + '</div>'
      + '<div class="rkbar">'
      + '<span class="chip" data-till="' + esc(a.closesAt) + '">' + esc(left(a.closesAt)) + '</span>'
      + '<span class="chip">' + a.entries.length + ' ' + plural(a.entries.length, 'запис', 'записи', 'записів') + '</span>'
      + '<span class="muted small">до ' + a.maxSeconds + ' с</span>'
      + '</div>'
      + recBox(a, ctx, esc, mine, host)
      + entries(a, esc, ctx)
      ;
  }

  /// Мікрофон один на вкладку: там, де його взяли, — жива смужка, у другому місці — просто напис.
  /// Живий запис чи прослуховування малюємо лише в тому блоці, для якого писали (конкурс чи ефір).
  function recNow(host, target, esc) {
    if (rec.stage === 'idle' || rec.target !== target) return '';
    if (rec.host !== host)
      return '<div class="rkrec"><span class="muted small">Запис іде в іншій картці — доспівай там.</span></div>';
    if (rec.stage === 'live') {
      return '<div class="rkrec live"><span class="rkrec-dot"></span><span class="rkrec-time">0:00</span>'
        + '<div class="rkrec-bars">' + '<i></i>'.repeat(16) + '</div>'
        + '<span class="muted small">ліміт ' + fmt(rec.max) + '</span>'
        + '<button class="primary" data-rec="stop">Готово</button>'
        + '<button class="ghost icon" data-rec="cancel" title="Викинути">✕</button></div>';
    }
    if (rec.stage === 'prev') {
      return '<div class="rkrec prev"><span class="rkrec-mic">🎙</span>'
        + '<audio controls src="' + esc(rec.url) + '"></audio><span class="chip">' + fmt(rec.sec) + '</span>'
        + '<button class="primary" data-rec="send">' + (target === 'air' ? 'Пустити в ефір' : 'Подати на конкурс') + '</button>'
        + '<button class="ghost" data-rec="again">Ще раз</button>'
        + '<button class="ghost icon danger" data-rec="drop" title="Викинути">✕</button></div>';
    }
    return '';
  }

  function recBox(a, ctx, esc, mine, host) {
    const now = recNow(host, 'contest', esc);
    if (now) return now;
    return '<div class="rkrec"><button class="primary" data-rec="start">🎙 '
      + (mine ? 'Перезаписати' : 'Записати рекламу') + '</button>'
      + (mine ? '<button class="ghost danger" data-drop="' + a.id + '">Забрати свій запис</button>' : '')
      + '<span class="muted small">' + (mine
        ? 'Твій запис уже в конкурсі — новий замінить старий разом із голосами за нього.'
        : 'Прочитай сценарій уголос. Можна своїми словами.') + '</span>'
      + '</div>';
  }

  function entries(a, esc, ctx) {
    if (!a.entries.length) return '<div class="gempty">Записів ще нема. Будь першим — і всі голосуватимуть за тебе.</div>';
    const rows = a.entries.slice().sort((x, y) => y.votes - x.votes || x.id - y.id);
    const onAir = state.air && state.air.on && state.air.on.trackId;
    return '<div class="rklist">' + rows.map((e) => {
      const voted = a.myVote === e.id;
      // Господар може пустити будь-який запис в ефір, не чекаючи кінця конкурсу.
      const air = !state.air ? ''
        : e.trackId === onAir ? '<span class="chip warn">📻 в ефірі</span>'
        : '<button class="ghost" data-air-entry="' + e.id + '" title="Крутити цей запис в ефірі">📻 В ефір</button>';
      return '<div class="rkrow' + (e.mine ? ' mine' : '') + (voted ? ' voted' : '') + '">'
        + '<button class="ghost icon rkplay" data-play="' + esc(e.trackId) + '" title="Послухати">'
        + (playing(e.trackId) ? '⏸' : '▶') + '</button>'
        + '<span class="rknick">' + esc(e.nick) + (e.mine ? ' <span class="muted small">(це ти)</span>' : '') + '</span>'
        + '<span class="muted small">' + fmt(e.seconds || 0) + '</span>'
        + '<span class="rkvotes">' + esc(votes(e.votes)) + '</span>'
        + (e.mine
          ? '<span class="muted small">за себе не можна</span>'
          : '<button class="' + (voted ? 'active' : 'ghost') + '" data-vote="' + e.id + '">'
            + (voted ? '✓ Мій голос' : 'Голосую') + '</button>')
        + air
        + '</div>';
    }).join('') + '</div>';
  }

  function past(list, esc) {
    if (!list.length) return '';
    return '<h4>Минулі переможці</h4><div class="rkpast">' + list.map((p) => '<div class="rkrow">'
      + (p.trackId ? '<button class="ghost icon rkplay" data-play="' + esc(p.trackId) + '" title="Послухати">'
        + (playing(p.trackId) ? '⏸' : '▶') + '</button>' : '<span class="rkplay muted">—</span>')
      + '<span class="rknick">' + esc(p.winner || 'без переможця') + '</span>'
      + '<span class="rkvotes">' + esc(votes(p.votes || 0)) + '</span>'
      + '<span class="muted small">' + esc(String(p.closedAt || '').slice(0, 10)) + '</span>'
      + '</div>').join('') + '</div>';
  }

  /// Ефір реклами — тільки господареві: що крутиться, як часто, «в ефір зараз» і своя реклама.
  function airBox(ctx, esc, host) {
    const air = state.air;
    if (!air || !isAdmin(ctx)) return '';
    const on = air.on;
    const every = state.draft.every != null ? state.draft.every : air.everyTracks;
    const mins = state.draft.mins != null ? state.draft.mins : air.minMinutes;
    const now = recNow(host, 'air', esc);
    return '<h4>Реклама в ефірі</h4><div class="rkair">'
      + (on
        ? '<div class="rkrow"><button class="ghost icon rkplay" data-play="' + esc(on.trackId) + '" title="Послухати">'
          + (playing(on.trackId) ? '⏸' : '▶') + '</button>'
          + '<span class="rknick">' + esc(on.nick) + '</span>'
          + '<span class="muted small">' + fmt(on.seconds || 0) + '</span>'
          + '<span class="chip">' + (on.house ? 'поставив господар' : 'переможець конкурсу') + '</span></div>'
        : '<div class="gempty">Реклами в ефірі нема: ні своєї, ні переможця. Пусти запис із конкурсу або запиши свою.</div>')
      + (air.houseMissing ? '<div class="muted small">Файл твоєї реклами зник із кешу — поки крутиться переможець.</div>' : '')
      + (air.jingle ? '' : '<div class="muted small">⚠ Джингл вимкнено в налаштуваннях (Ad:Jingle / Ad:Enabled).</div>')
      + '<div class="rkairrow">'
      + '<button class="primary" data-air-now' + (on ? '' : ' disabled') + '>📻 В ефір зараз</button>'
      + (on && on.house ? '<button class="ghost danger" data-air-clear>Прибрати свою</button>' : '')
      + (now ? '' : '<button class="ghost" data-rec="air-start">🎙 Записати свою</button>')
      + '</div>'
      + now
      + '<div class="rkairrow"><span>Раз на</span>'
      + '<input type="number" min="1" max="100" step="1" data-every value="' + esc(every) + '">'
      + '<span>треків, не частіше ніж раз на</span>'
      + '<input type="number" min="0" max="600" step="1" data-mins value="' + esc(mins) + '">'
      + '<span>хв</span><button class="ghost" data-air-save>Зберегти</button></div>'
      + '<span class="muted small">Від останньої реклами: ' + air.since + ' ' + plural(air.since, 'трек', 'треки', 'треків')
      + '. Реклама йде, лише коли на сайті хтось є.</span>'
      + '</div>';
  }

  function admin(a, ctx) {
    if (!isAdmin(ctx)) return '';
    return '<div class="rkadmin">'
      + (a ? '<button class="ghost" data-close="' + a.id + '">Закрити конкурс</button>'
           : '<button class="primary" data-new>Новий конкурс</button>')
      + '<span class="muted small">видно тільки господареві</span></div>';
  }

  /// Слухачі вішаємо після кожного перемальовування: розмітка щоразу нова.
  function wire(host, ctx, a) {
    const toast = toastOf(ctx);
    const act = async (btn, label, fn) => busy(btn, label, async () => {
      try { const r = await fn(); if (r && r.message) toast(r.message, 'ok'); await load(); }
      catch (e) { toast(e.message, 'err'); }
    });

    host.querySelectorAll('[data-play]').forEach((b) => b.onclick = () => play(b.dataset.play));
    host.querySelectorAll('[data-vote]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'голосую…', () => api(ctx, 'POST', '/api/ads/' + a.id + '/vote', { entryId: +b.dataset.vote })));
    host.querySelectorAll('[data-drop]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'забираю…', () => api(ctx, 'DELETE', '/api/ads/' + b.dataset.drop + '/entry')));
    host.querySelectorAll('[data-new]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'відкриваю…', () => api(ctx, 'POST', '/api/ads/new')));
    host.querySelectorAll('[data-close]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'закриваю…', () => api(ctx, 'POST', '/api/ads/' + b.dataset.close + '/close')));

    host.querySelectorAll('[data-air-entry]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'ставлю…', () => api(ctx, 'POST', '/api/ads/air/entry', { entryId: +b.dataset.airEntry })));
    host.querySelectorAll('[data-air-now]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'ставлю…', () => api(ctx, 'POST', '/api/ads/air/now')));
    host.querySelectorAll('[data-air-clear]').forEach((b) => b.onclick = (e) =>
      act(e.currentTarget, 'прибираю…', () => api(ctx, 'DELETE', '/api/ads/air')));
    host.querySelectorAll('[data-every], [data-mins]').forEach((inp) => {
      inp.oninput = () => { state.draft[inp.matches('[data-every]') ? 'every' : 'mins'] = inp.value; };
      inp.onkeydown = (e) => { if (e.key === 'Enter') { const s = host.querySelector('[data-air-save]'); if (s) s.click(); } };
    });
    host.querySelectorAll('[data-air-save]').forEach((b) => b.onclick = (e) => {
      const every = +(host.querySelector('[data-every]') || {}).value;
      const mins = +(host.querySelector('[data-mins]') || {}).value;
      act(e.currentTarget, 'зберігаю…', async () => {
        const r = await api(ctx, 'POST', '/api/ads/air/every', { everyTracks: every, minMinutes: mins });
        state.draft = { every: null, mins: null };
        document.activeElement && document.activeElement.blur && document.activeElement.blur();
        return r;
      });
    });

    host.querySelectorAll('[data-rec]').forEach((b) => b.onclick = (e) => {
      const what = b.dataset.rec;
      const max = rec.target === 'air' ? AIR_MAX : a && a.maxSeconds;
      if (what === 'start') startRec(host, ctx, a && a.maxSeconds, 'contest');
      else if (what === 'air-start') startRec(host, ctx, AIR_MAX, 'air');
      else if (what === 'stop') stopRec();
      else if (what === 'cancel') { rec.tossed = true; stopRec(); }
      else if (what === 'again') { const t = rec.target; closeRec(); startRec(host, ctx, max, t); }
      else if (what === 'drop') closeRec();
      else if (what === 'send') act(e.currentTarget, 'несу…', async () => { await sendRec(a && a.id, ctx); return null; });
    });
  }

  // =============================================================================================
  // Дві точки входу: панель у навігації і картка в лобі
  // =============================================================================================

  function mount(host, ctx) {
    host.classList.add('rkpanel');
    // Той самий випадок, що й у тіку запису, але вже без чекання: панель перемалювалась, поки людина
    // говорила в мікрофон — перевішуємо запис сюди, щоб було де натиснути «Готово» і «Подати».
    if (rec.stage !== 'idle' && rec.host && !rec.host.isConnected) rec.host = host;
    attach(host, ctx);
    paint(host, ctx);
    load();
  }

  HGames.registerPanel({
    id: 'ads',
    title: 'Реклама',
    icon: '🎙',
    mount,
    update: (host, ctx) => { attach(host, ctx); paint(host, ctx); },
  });

  /*
    Штовхаємо каркас, коли запам'ятана вкладка — саме наша.

    core.js у show() малює тіло панелі ДО того, як завантажаться модулі: renderExtra('ads') нашої
    панелі ще не знає, пише «Панель зникла.» і запам'ятовує chromeFor = 'x:ads'. Далі модуль таки
    приходить, registerPanel чесно кличе renderPanel(), але той бачить chromeFor === panel і вважає,
    що тіло вже намальоване, — і напис лишається, поки людина сама не перемкне панель туди-сюди.
    Робимо це перемикання за неї (сусідня вкладка лобі малюється без жодного запиту). Вкладки —
    кнопки .gnav [data-go] (лобі — data-go=""); так само зроблено в svoya.js.
    Прибрати, щойно registerPanel скидатиме chromeFor сам — див. «Потрібно від каркаса» у spec.
  */
  setTimeout(() => {
    const mine = () => document.querySelector('.gnav [data-go="x:ads"]');
    const lobby = document.querySelector('.gnav [data-go=""]');
    if (!mine() || !mine().classList.contains('on') || !lobby || document.querySelector('.rkpanel')) return;
    if (!document.querySelector('.gview .gempty')) return;
    lobby.click();
    setTimeout(() => { const b = mine(); if (b) b.click(); }, 50);
  }, 0);

  HGames.register({
    id: 'ad-contest',
    icon: ICON,
    mount,
    update: (host, ctx) => { attach(host, ctx); paint(host, ctx); },
    unmount(host) {
      const i = hosts.findIndex((h) => h.el === host);
      if (i >= 0) hosts.splice(i, 1);
      if (rec.host === host) closeRec();
      if (!hosts.length) stop();
    },
    // status() тут свідомо нема: картку перемальовує наше опитування, а рядок статусу core.js чіпає
    // лише на подіях кімнати — конкурс не реалтайм, подій нема, і рядок застигав би на першій
    // відповіді назавжди. Відлік до закриття і так стоїть чипом у самому тілі.
  });
})();
