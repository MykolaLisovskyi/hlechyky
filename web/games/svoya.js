/*
  «Своя гра». Правила, час і перевірка відповідей — на сервері (Impl/Svoya.cs); модуль малює поле, запитання,
  кнопку й рахунок і шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'lobby'|'intro'|'board'|'reading'|'buzz'|'answering'|'reveal'|'done', mode: 'auto'|'live',
      host: seat|null, options: { answer, buzz, early, voice },
      pack: { id, title, description, author, rounds: [{ name, final, themes[] }] } | null,
      round, rounds, roundName, board: [{ theme, cells: [{ price, open }] }] | null,
      chooser, cell: { theme, q } | null, question: { theme, price, text, media } | null,
      answer: { text, accept[], comment, media } | null,   // усім — після розкриття; живому ведучому — одразу
      answering, correct, until, totalMs, paused, leftMs, waiting,
      scores[], wrong[], tries: [{ seat, text, ok }], presses: [{ seat, ms }], appeals: [{ seat, text }],
      say: { id, text, url } | null, voice: { on, available },
      me: { isHost, canPick, canBuzz, canAnswer, canAppeal, canJudge, canChoosePack } | null,
      left[], error, result }
  Ходи: pack {id} (лобі, господар) · pick {theme, q} · buzz · answer {text} · appeal · judge {seat, accept}
        живий ведучий: open · verdict {ok} · nobody · next · pause · resume · adjust {seat, delta} · voice {on}
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.5" y="2.5" width="13" height="11" rx="1.5" fill="none" stroke="var(--accent)" stroke-width="1.4"/>'
    + '<path d="M1.5 6.2h13M1.5 9.8h13M5.8 2.5v11M10.2 2.5v11" stroke="var(--accent)" stroke-width="1"/>'
    + '<circle cx="12.4" cy="11.7" r="2.4" fill="var(--clay)"/></svg>';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 9);

  function st(root) {
    if (!root._sv) root._sv = { timer: 0, packs: null, packsAt: 0, loadingPacks: false, query: '', sayId: 0, media: '', speaking: false, radioMuted: null };
    return root._sv;
  }

  /// Той самий api(), що в app.js: нік їде заголовком, помилка приходить полем message.
  async function api(ctx, method, path, body) {
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent((ctx.me && ctx.me.nick) || '') },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((data && data.message) || ('HTTP ' + r.status));
    return data;
  }

  const mediaUrl = (v, m) => (m && v.pack ? '/api/games/svoya/media/' + encodeURIComponent(v.pack.id) + '/' + encodeURIComponent(m.file) : '');

  function mediaHtml(v, m, cls) {
    if (!m) return '';
    const src = esc(mediaUrl(v, m));
    if (m.kind === 'image') return '<img class="svmedia ' + cls + '" src="' + src + '" alt="">';
    if (m.kind === 'audio') return '<audio class="svmedia ' + cls + '" src="' + src + '" controls preload="auto"></audio>';
    if (m.kind === 'video') return '<video class="svmedia ' + cls + '" src="' + src + '" controls playsinline preload="auto"></video>';
    return '';
  }

  const nick = (ctx, i) => ctx.nickOf(i) || ('місце ' + (i + 1));

  // ---------- лобі: вибір пакета ----------

  async function loadPacks(root, ctx, force) {
    const s = st(root);
    if (s.loadingPacks || (!force && s.packs && Date.now() - s.packsAt < 30000)) return;
    s.loadingPacks = true;
    try { s.packs = await api(ctx, 'GET', '/api/games/svoya/packs'); s.packsAt = Date.now(); }
    catch (e) { ctx.toast('Пакети не завантажились: ' + e.message, 'err'); }
    finally { s.loadingPacks = false; }
    if (root._ctx) render(root, root._ctx);
  }

  function packRow(p, chosen) {
    const themes = (p.rounds || []).filter((r) => !r.final).map((r) => r.themes.join(', ')).join(' · ');
    return '<button type="button" class="svpack' + (chosen ? ' on' : '') + '" data-do="pack" data-id="' + esc(p.id) + '">'
      + '<b>' + esc(p.title) + '</b>'
      + '<span class="muted small">' + esc(p.author) + ' · ' + p.questions + ' запитань' + (p.plays ? ' · зіграно ' + p.plays : '') + '</span>'
      + (themes ? '<span class="svthemes small">' + esc(themes) + '</span>' : '')
      + '</button>';
  }

  function lobbyHtml(root, ctx, v) {
    const s = st(root);
    const me = v.me || {};
    const head = '<div class="svmode muted small">Ведучий: ' + (v.mode === 'live'
      ? '🎙 жива людина — ' + esc(nick(ctx, v.host)) + ' (не грає, читає й судить)'
      : '🤖 автомат' + (v.options && v.options.voice !== 'none' ? ' з голосом' : '')) + '</div>';
    const chosen = v.pack
      ? '<div class="svchosen"><div class="svptitle">' + esc(v.pack.title) + '</div>'
        + (v.pack.description ? '<div class="muted small">' + esc(v.pack.description) + '</div>' : '')
        + '<div class="muted small">від ' + esc(v.pack.author) + '</div>'
        + v.pack.rounds.map((r) => '<div class="svround"><b>' + esc(r.name) + (r.final ? ' 🏁' : '') + '</b> '
          + r.themes.map((t) => '<span class="chip">' + esc(t) + '</span>').join(' ') + '</div>').join('')
        + '</div>'
      : '<div class="svwait">' + (me.canChoosePack ? 'Обери пакет запитань нижче' : 'Господар обирає пакет…') + '</div>';
    if (!me.canChoosePack) return head + chosen;
    if (!s.packs) { loadPacks(root, ctx); return head + chosen + '<div class="svwait"><span class="spin"></span> завантажую пакети…</div>'; }
    const q = s.query.trim().toLowerCase();
    const fits = (p) => !q || (p.title + ' ' + p.author + ' ' + (p.rounds || []).map((r) => r.themes.join(' ')).join(' ')).toLowerCase().includes(q);
    const group = (title, list) => {
      const items = (list || []).filter((p) => p.ready !== false).filter(fits);
      return items.length ? '<div class="svgroup"><div class="muted small">' + title + '</div>'
        + items.map((p) => packRow(p, v.pack && v.pack.id === p.id)).join('') + '</div>' : '';
    };
    const list = group('Від Глечиків', s.packs.builtin) + group('Мої', s.packs.mine) + group('Публічні', s.packs.public);
    return head + chosen + '<div class="svpicker">'
      + (list || '<div class="svwait">' + (q ? 'Нічого не знайшлось' : 'Пакетів ще нема — зроби свій у «🎯 Своя гра» праворуч') + '</div>')
      + '</div>';
  }

  // ---------- поле ----------

  function boardHtml(ctx, v) {
    const me = v.me || {};
    const cols = Math.max(...v.board.map((t) => t.cells.length));
    return '<div class="svboard" style="--cols:' + cols + '">'
      + v.board.map((t, ti) => '<div class="svtheme">' + esc(t.theme) + '</div>'
        + t.cells.map((c, qi) => c.open
          ? '<button type="button" class="svcell"' + (me.canPick ? ' data-do="pick" data-t="' + ti + '" data-q="' + qi + '"' : ' disabled') + '>' + c.price + '</button>'
          : '<span class="svcell gone"></span>').join('')
        + (t.cells.length < cols ? '<span class="svcell gone"></span>'.repeat(cols - t.cells.length) : '')).join('')
      + '</div>';
  }

  // ---------- «телевізор»: запитання й відповідь ----------

  function tvHtml(ctx, v) {
    const q = v.question;
    if (!q) return '';
    const open = v.phase === 'reveal' || v.phase === 'finale';
    const a = v.answer;
    let html = '<div class="svtv' + (open ? ' open' : '') + '">'
      + '<div class="svq-head"><span>' + esc(q.theme) + '</span><b>' + (q.price > 0 ? q.price : '🏁') + '</b></div>'
      + (q.text ? '<div class="svq-text">' + esc(q.text) + '</div>' : '')
      + mediaHtml(v, q.media, 'q');
    if (a && open) {
      html += '<div class="svanswer">' + esc(a.text) + '</div>'
        + (a.comment ? '<div class="svcomment">' + esc(a.comment) + '</div>' : '')
        + mediaHtml(v, a.media, 'a');
    }
    return html + '</div>';
  }

  /// Хто що відповідав (у auto — написане, у live — лише ✓/✗ на рахунку).
  function triesHtml(ctx, v) {
    const t = v.tries || [];
    if (!t.length) return '';
    return '<div class="svtries">' + t.map((x) => '<span class="svtry ' + (x.ok ? 'ok' : 'no') + '">'
      + esc(nick(ctx, x.seat)) + ': «' + esc(x.text || '') + '» ' + (x.ok ? '✓' : '✗') + '</span>').join('') + '</div>';
  }

  function appealsHtml(ctx, v) {
    const me = v.me || {};
    let html = '';
    if (me.canAppeal) html += '<button type="button" class="ghost" data-do="appeal">⚖️ Оскаржити — я ж правильно написав</button>';
    for (const a of v.appeals || []) {
      html += '<div class="svappeal">⚖️ ' + esc(nick(ctx, a.seat)) + ' просить зарахувати «' + esc(a.text) + '»'
        + (me.canJudge ? ' <button type="button" class="primary small" data-do="judge" data-seat="' + a.seat + '" data-ok="1">Зарахувати</button>'
          + '<button type="button" class="ghost small" data-do="judge" data-seat="' + a.seat + '" data-ok="0">Ні</button>' : ' — вирішує господар')
        + '</div>';
    }
    return html;
  }

  /// Пульт живого ведучого: відповідь (лише йому), черговість натискань, судейські кнопки.
  function hostHtml(ctx, v) {
    const me = v.me || {};
    if (!me.isHost || v.phase === 'lobby' || v.phase === 'done') return '';
    const a = v.answer;
    const btn = (act, label, cls, extra) => '<button type="button" class="' + (cls || 'ghost') + '" data-do="' + act + '"' + (extra || '') + '>' + label + '</button>';
    let html = '<div class="svhost"><div class="muted small">🎙 Пульт ведучого — це бачиш лише ти</div>';
    if (a && v.phase !== 'reveal') {
      html += '<div class="svhans"><b>' + esc(a.text) + '</b>'
        + (a.accept && a.accept.length ? '<span class="muted small"> · також: ' + esc(a.accept.join('; ')) + '</span>' : '')
        + (a.comment ? '<div class="muted small">' + esc(a.comment) + '</div>' : '') + '</div>';
    }
    const row = [];
    if (v.phase === 'reading') row.push(btn('open', '🔔 Кнопка!', 'primary'));
    if (v.phase === 'answering') {
      row.push(btn('verdict', '✓ Правильно', 'primary svok', ' data-ok="1"'));
      row.push(btn('verdict', '✗ Ні', 'ghost svno', ' data-ok="0"'));
    }
    if (v.phase === 'reading' || v.phase === 'buzz' || v.phase === 'answering') row.push(btn('nobody', 'Ніхто — показати відповідь'));
    if (v.phase === 'intro' || v.phase === 'reveal' || v.phase === 'finale') row.push(btn('next', 'Далі ▶', 'primary'));
    row.push(v.paused ? btn('resume', '▶ Далі гра', 'primary') : btn('pause', '⏸ Пауза'));
    if (v.voice && v.voice.available)
      row.push(btn('voice', v.voice.on ? '🗣 Читає голос — вимкнути' : '🗣 Хай читає голос', 'ghost', ' data-on="' + (v.voice.on ? '0' : '1') + '"'));
    html += '<div class="svrow">' + row.join('') + '</div>';
    if ((v.presses || []).length) {
      html += '<div class="svpresses">🔔 ' + v.presses.map((p, i) => '<span' + (i === 0 ? ' class="first"' : '') + '>'
        + esc(nick(ctx, p.seat)) + ' <i>' + (p.ms / 1000).toFixed(2) + ' с</i></span>').join('') + '</div>';
    }
    return html + '</div>';
  }

  // ---------- рахунок ----------

  function scoresHtml(ctx, v) {
    const me = v.me || {};
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const n = ctx.nickOf(i);
      if (!n || i === v.host) continue;
      rows.push({ i, n, score: (v.scores || [])[i] || 0 });
    }
    if (v.phase === 'done') rows.sort((a, b) => b.score - a.score);
    const step = (v.question && v.question.price) || 100;
    const left = v.left || [];
    return rows.map((r) => {
      const cls = ['svsc'];
      if (r.i === ctx.seat) cls.push('me');
      if (left.indexOf(r.i) >= 0) cls.push('off');
      if (r.i === v.answering) cls.push('answering');
      if (r.i === v.correct) cls.push('right');
      if ((v.wrong || []).indexOf(r.i) >= 0) cls.push('wrong');
      const tags = (r.i === v.chooser && v.phase === 'board' ? '<em title="обирає">👉</em>' : '')
        + (r.i === v.answering ? '<em title="відповідає">🎤</em>' : '');
      const adj = me.isHost && v.phase !== 'done'
        ? '<span class="svadj"><button type="button" class="ghost small" data-do="adjust" data-seat="' + r.i + '" data-d="' + (-step) + '">−' + step + '</button>'
          + '<button type="button" class="ghost small" data-do="adjust" data-seat="' + r.i + '" data-d="' + step + '">+' + step + '</button></span>'
        : '';
      return '<div class="' + cls.join(' ') + '"><span class="svn">' + esc(r.n) + '</span>' + tags + adj
        + '<b class="' + (r.score < 0 ? 'neg' : '') + '">' + r.score + '</b></div>';
    }).join('');
  }

  // ---------- таймер ----------

  function timer(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.svtime');
    const span = box.querySelector('span');
    const bar = box.querySelector('i');
    const live = ctx.playing && v.phase !== 'lobby' && v.phase !== 'done';
    box.style.visibility = live && (v.until || v.paused || v.waiting) ? 'visible' : 'hidden';
    if (!live) return;
    if (v.paused) { bar.style.width = '100%'; span.textContent = '⏸'; return; }
    if (v.waiting || !v.until) { bar.style.width = '100%'; span.textContent = '…'; return; }
    const left = Math.max(0, new Date(v.until).getTime() - Date.now());
    bar.style.width = Math.max(0, Math.min(100, left / (v.totalMs || 1) * 100)) + '%';
    bar.classList.toggle('hot', (v.phase === 'buzz' || v.phase === 'answering') && left < 4000);
    const t = String(Math.ceil(left / 1000));
    if (span.textContent !== t) span.textContent = t;
  }

  // ---------- голос ведучого ----------
  // Звучить лише там, де ввімкнено «🔊 Ведучий тут»: типово — у глядача (телевізор) і в господаря столу, щоб
  // не лунало з восьми телефонів разом. Вибір живе в localStorage. Радіо на час репліки глушимо, як у Melody.

  const SPK_KEY = 'svoyaSpeaker';

  function speakerOn(ctx) {
    let saved = null;
    try { saved = localStorage.getItem(SPK_KEY); } catch { /* приватне вікно */ }
    if (saved === '1') return true;
    if (saved === '0') return false;
    const host = ctx.room && ctx.me && String(ctx.room.host || '').toLowerCase() === String(ctx.me.nick || '').toLowerCase();
    return ctx.seat == null || !!host;
  }

  function setSpeaker(on) {
    try { localStorage.setItem(SPK_KEY, on ? '1' : '0'); } catch { /* приватне вікно */ }
  }

  function radio() { return document.getElementById('audio'); }

  function duck(s, on) {
    const r = radio();
    if (!r) return;
    if (on && s.radioMuted == null) { s.radioMuted = r.muted; r.muted = true; }
    if (!on && s.radioMuted != null) { r.muted = s.radioMuted; s.radioMuted = null; }
  }

  function hush(root) {
    const s = st(root);
    const a = root.querySelector('.svvoice');
    if (a && !a.paused) a.pause();
    if (window.speechSynthesis && s.speaking) { try { speechSynthesis.cancel(); } catch { /* нема */ } }
    s.speaking = false;
    duck(s, false);
  }

  function ukVoice() {
    if (!window.speechSynthesis) return null;
    return speechSynthesis.getVoices().find((x) => /^uk/i.test(x.lang)) || null;
  }

  function voice(root, ctx, v) {
    const s = st(root);
    const say = v.say;
    if (v.phase === 'answering' || v.phase === 'done' || v.phase === 'lobby') { if (s.speaking) hush(root); }
    if (!say || say.id === s.sayId) return;
    s.sayId = say.id;
    if (!speakerOn(ctx) || !ctx.playing) return;
    hush(root);
    if (say.url) {
      const a = root.querySelector('.svvoice');
      a.src = say.url;
      duck(s, true);
      s.speaking = true;
      a.play().catch(() => { s.speaking = false; duck(s, false); });
    } else {
      const uk = ukVoice();
      if (!uk) return;                                         // без українського голосу краще тиша, ніж англійський акцент
      const u = new SpeechSynthesisUtterance(say.text);
      u.voice = uk; u.lang = uk.lang; u.rate = 1.05;
      u.onend = u.onerror = () => { s.speaking = false; duck(s, false); };
      duck(s, true);
      s.speaking = true;
      speechSynthesis.speak(u);
    }
  }

  function speakerBtn(root, ctx) {
    const b = root.querySelector('.svspk');
    const on = speakerOn(ctx);
    const text = on ? '🔊 Ведучий тут' : '🔇 Ведучий';
    if (b.textContent !== text) b.textContent = text;
    b.title = on ? 'Голос ведучого звучить на цьому пристрої. Натисни — вимкнути' : 'Голос ведучого тут мовчить. Натисни — хай звучить тут (телевізор, колонка)';
    b.classList.toggle('on', on);
  }

  /// Медіа запитання (звук, відео) грає там само, де й голос.
  function autoplayMedia(root, ctx, v) {
    const s = st(root);
    const el = root.querySelector('.svmedia.q');
    const key = v.phase === 'reading' && el ? v.round + ':' + (v.cell ? v.cell.theme + '/' + v.cell.q : '') : '';
    if (key === s.media) return;
    s.media = key;
    if (!key || !speakerOn(ctx) || !el.play) return;
    el.play().catch(() => { /* браузер не дав — є кнопка ▶ на самому плеєрі */ });
  }

  // ---------- кіт, аукціон, фінал ----------

  const sp = (v) => (v.me && v.me.special) || {};

  function catHtml(ctx, v) {
    const c = v.cat || {};
    const me = sp(v);
    let html = '<div class="svbig">🐱 Кіт у мішку!</div>';
    if (c.to == null) {
      if (me.canGive) {
        html += '<div class="svwho">Кому віддаси запитання?</div><div class="svrow center">';
        for (let i = 0; i < seatsOf(ctx); i++) {
          if (!ctx.nickOf(i) || i === v.host || i === c.from || (v.left || []).indexOf(i) >= 0) continue;
          html += '<button type="button" class="primary" data-do="give" data-seat="' + i + '">' + esc(nick(ctx, i)) + '</button>';
        }
        html += '</div>';
      } else html += '<div class="svwho">' + esc(nick(ctx, c.from)) + ' вирішує, кому віддати кота</div>';
    } else if (c.choosing) {
      html += '<div class="svwho">Кіт дістався: ' + esc(nick(ctx, c.to)) + '</div>';
      html += me.canCatPrice
        ? '<div class="svrow center"><button type="button" class="ghost" data-do="catPrice" data-max="0">За ' + c.min + '</button>'
          + '<button type="button" class="primary" data-do="catPrice" data-max="1">За ' + c.max + '</button></div>'
        : '<div class="svwho">обирає ціну: ' + c.min + ' або ' + c.max + '</div>';
    }
    return html;
  }

  function auctionHtml(ctx, v) {
    const a = v.auction || {};
    const me = sp(v);
    let html = '<div class="svbig">🔨 Аукціон</div>'
      + '<div class="svauc"><span>Номінал <b>' + a.nominal + '</b></span>'
      + '<span>Ставка <b>' + a.current + '</b>' + (a.holder != null ? ' — ' + esc(nick(ctx, a.holder)) : '') + (a.allIn ? ' 💥 ва-банк' : '') + '</span></div>';
    if (a.turn != null) html += '<div class="svwho">' + (a.turn === ctx.seat ? 'Твій хід у торгах' : 'Торгується ' + esc(nick(ctx, a.turn))) + '</div>';
    const row = [];
    if (me.canAllIn) row.push('<button type="button" class="ghost" data-do="allin">💥 Ва-банк (' + ((v.scores || [])[ctx.seat] || 0) + ')</button>');
    if (me.canPass) row.push('<button type="button" class="ghost" data-do="pass">Пас</button>');
    if (me.canPassFor) row.push('<button type="button" class="ghost" data-do="passFor">Пас за ' + esc(nick(ctx, a.turn)) + '</button>');
    if (row.length) html += '<div class="svrow center">' + row.join('') + '</div>';
    if ((a.bids || []).length) {
      html += '<div class="svbids">' + a.bids.map((b) => '<span>' + esc(nick(ctx, b.seat)) + ': '
        + (b.what === 'pass' ? 'пас' : b.what === 'allin' ? 'ва-банк ' + b.amount : b.amount) + '</span>').join('') + '</div>';
    }
    return html;
  }

  function finalHtml(ctx, v) {
    const f = v.final || {};
    const me = sp(v);
    const themes = f.themes || [];
    let html = '';
    if (v.phase === 'strike') {
      html += '<div class="svbig">🏁 Фінал</div><div class="svwho">' + (me.canStrike && f.turn === ctx.seat ? 'Викресли тему, яка тобі не до душі'
        : me.canStrike ? 'Викреслює ' + esc(nick(ctx, f.turn)) + ' — можеш за нього' : 'Викреслює ' + esc(nick(ctx, f.turn))) + '</div>';
      html += '<div class="svfthemes">' + themes.map((t, i) => (f.struck || []).indexOf(i) >= 0
        ? '<span class="svft struck">' + esc(t) + '</span>'
        : '<button type="button" class="svft"' + (me.canStrike ? ' data-do="strike" data-theme="' + i + '"' : ' disabled') + '>' + esc(t) + '</button>').join('') + '</div>';
      return html;
    }
    const theme = f.theme >= 0 ? themes[f.theme] : '';
    if (v.phase === 'bet') {
      html += '<div class="svbig">🏁 ' + esc(theme) + '</div>'
        + '<div class="svwho">' + (me.canBet ? (me.bet ? 'Твоя ставка: ' + me.bet + '. Можна змінити, поки йде час' : 'Скільки ставиш?') : 'Фіналісти роблять ставки') + '</div>';
    }
    if (v.phase === 'final' || v.phase === 'judging' || v.phase === 'finale') html += tvHtml(ctx, v);
    if (v.phase === 'final' && me.answer) html += '<div class="svwho">Твоя відповідь: «' + esc(me.answer) + '» — можна переписати</div>';
    const rows = f.rows || [];
    const done = f.betted || [];
    html += '<div class="svfinal">' + (f.finalists || []).map((s) => {
      const r = rows.find((x) => x.seat === s);
      const flag = v.phase === 'bet' ? (done.indexOf(s) >= 0 ? '✓ ставку зроблено' : '…')
        : v.phase === 'final' ? ((f.answered || []).indexOf(s) >= 0 ? '✓ відповідь є' : '…') : '';
      let cell = '<span class="svn">' + esc(nick(ctx, s)) + '</span>';
      if (r && r.answer != null && v.phase !== 'bet') cell += '<i>«' + esc(r.answer) + '»</i>';
      if (r && r.bet != null) cell += '<em>' + r.bet + '</em>';
      if (r && r.ok != null) cell += r.ok ? '<b class="ok">✓</b>' : '<b class="no">✗</b>';
      if (me.canFinalJudge) cell += '<button type="button" class="ghost small" data-do="finalVerdict" data-seat="' + s + '" data-ok="1">✓</button>'
        + '<button type="button" class="ghost small" data-do="finalVerdict" data-seat="' + s + '" data-ok="0">✗</button>';
      if (flag) cell += '<span class="muted small">' + flag + '</span>';
      return '<div class="svfrow">' + cell + '</div>';
    }).join('') + '</div>';
    if (me.canFinalJudge) html += '<div class="svrow center"><button type="button" class="primary" data-do="next">Розкрити ▶</button></div>';
    return html;
  }

  /// Числове поле для ставки в аукціоні й у фіналі — живе окремо від перемальовки, як і пошук.
  function numForm(root, ctx, v) {
    const form = root.querySelector('.svnum');
    const me = sp(v);
    const a = v.auction || {};
    const mine = (v.scores || [])[ctx.seat] || 0;
    let mode = '';
    if (v.phase === 'auction' && me.canBid) mode = 'bid';
    else if (v.phase === 'bet' && me.canBet) mode = 'bet';
    const was = form.dataset.mode || '';
    form.hidden = !mode;
    form.dataset.mode = mode;
    if (!mode) return;
    const input = form.querySelector('input');
    const min = mode === 'bid' ? a.minBid : 1;
    input.min = min;
    input.max = mine;
    form.querySelector('span').textContent = 'Ставка (' + min + '…' + mine + ')';
    form.querySelector('button').textContent = mode === 'bid' ? 'Підняти' : me.bet ? 'Змінити' : 'Поставити';
    if (was !== mode) { input.value = String(min); setTimeout(() => input.focus(), 0); }
  }

  // ---------- збирання ----------

  function headText(ctx, v) {
    if (!ctx.playing && v.phase !== 'done') return 'Своя гра';
    if (v.phase === 'done') return 'Партію зіграно';
    return (v.roundName || '') + (v.rounds > 1 ? ' · ' + v.round + ' з ' + v.rounds : '');
  }

  function stageHtml(root, ctx, v) {
    if (v.phase === 'lobby' || (ctx.room && ctx.room.status === 'lobby')) return lobbyHtml(root, ctx, v);
    if (v.phase === 'intro') {
      const r = v.pack && v.pack.rounds[v.round - 1];
      return '<div class="svintro"><div class="svptitle">' + esc(v.roundName || '') + '</div>'
        + (r ? r.themes.map((t) => '<div class="svitheme">' + esc(t) + '</div>').join('') : '') + '</div>';
    }
    if (v.phase === 'board') {
      const me = v.me || {};
      const who = v.chooser == null ? '' : me.canPick && ctx.seat === v.chooser ? 'Твій вибір — тисни клітинку'
        : me.isHost ? 'Обирає ' + nick(ctx, v.chooser) + ' (можеш обрати й сам)' : 'Обирає ' + nick(ctx, v.chooser);
      return '<div class="svwho">' + esc(who) + '</div>' + (v.board ? boardHtml(ctx, v) : '');
    }
    if (v.phase === 'done') {
      const res = v.result || {};
      const w = (res.winners || []).map((i) => esc(nick(ctx, i))).join(' і ');
      return '<div class="svintro"><div class="svptitle">' + (v.error ? esc(v.error) : w ? '🏆 ' + w : 'Ніхто не вийшов у плюс') + '</div></div>'
        + (v.final && (v.final.rows || []).length ? '<div class="muted small">Фінал</div>' + finalHtml(ctx, v) : '');
    }
    if (v.phase === 'cat') return catHtml(ctx, v);
    if (v.phase === 'auction') return auctionHtml(ctx, v);
    if (['strike', 'bet', 'final', 'judging', 'finale'].indexOf(v.phase) >= 0) return finalHtml(ctx, v);
    return tvHtml(ctx, v) + triesHtml(ctx, v) + appealsHtml(ctx, v);
  }

  function set(el, html) { if (el._html !== html) { el._html = html; el.innerHTML = html; } }

  function render(root, ctx) {
    root._ctx = ctx;
    const v = ctx.view || {};
    const me = v.me || {};
    const head = root.querySelector('.svhead');
    const text = headText(ctx, v);
    if (head.textContent !== text) head.textContent = text;
    set(root.querySelector('.svstage'), stageHtml(root, ctx, v));
    set(root.querySelector('.svhostbox'), hostHtml(ctx, v));
    set(root.querySelector('.svscores'), ctx.playing || v.phase === 'done' ? scoresHtml(ctx, v) : '');

    // пошук пакетів — живе поле, тому не в stage (інакше перемальовка з'їдала б набране)
    root.querySelector('.svsearch').hidden = !(v.phase === 'lobby' && me.canChoosePack && st(root).packs);

    // велика кнопка: гравцям, коли йде запитання
    const buzz = root.querySelector('.svbuzz');
    const showBuzz = ctx.mine && !me.isHost && ctx.playing && ['reading', 'buzz', 'answering'].indexOf(v.phase) >= 0 && v.solo == null;
    buzz.hidden = !showBuzz;
    buzz.disabled = !me.canBuzz;
    buzz.classList.toggle('live', !!me.canBuzz);
    buzz.textContent = v.answering === ctx.seat ? '🎤 Відповідай!' : (v.wrong || []).indexOf(ctx.seat) >= 0 ? 'Спробу використано'
      : v.answering != null ? '🎤 ' + nick(ctx, v.answering) : me.canBuzz ? '🔔 Я знаю!' : v.phase === 'reading' ? 'Слухаємо…' : '🔔';

    // поле відповіді (лише в auto, лише тому, хто натиснув)
    const form = root.querySelector('.svform');
    const was = !form.hidden;
    const canType = me.canAnswer || sp(v).canFinalAnswer;
    form.hidden = !canType;
    if (canType && !was) {
      const input = form.querySelector('input');
      input.value = '';
      setTimeout(() => input.focus(), 0);
    }
    numForm(root, ctx, v);
    speakerBtn(root, ctx);
    voice(root, ctx, v);
    autoplayMedia(root, ctx, v);
    timer(root, ctx);
  }

  function onClick(root, e) {
    const b = e.target.closest('[data-do]');
    const ctx = root._ctx;
    if (!b || !ctx || b.disabled) return;
    const d = b.dataset;
    const act = (a, p) => ctx.act(a, p);
    switch (d.do) {
      case 'pack': act('pack', { id: d.id }); break;
      case 'pick': act('pick', { theme: +d.t, q: +d.q }); break;
      case 'buzz': act('buzz'); break;
      case 'appeal': act('appeal'); break;
      case 'judge': act('judge', { seat: +d.seat, accept: d.ok === '1' }); break;
      case 'verdict': act('verdict', { ok: d.ok === '1' }); break;
      case 'adjust': act('adjust', { seat: +d.seat, delta: +d.d }); break;
      case 'voice': act('voice', { on: d.on === '1' }); break;
      case 'give': act('give', { seat: +d.seat }); break;
      case 'catPrice': act('catPrice', { max: d.max === '1' }); break;
      case 'strike': act('strike', { theme: +d.theme }); break;
      case 'finalVerdict': act('finalVerdict', { seat: +d.seat, ok: d.ok === '1' }); break;
      default: act(d.do); break;
    }
  }

  // ---------- панель конструктора ----------
  // Сам конструктор — у svoya-packs.js і вантажиться, лише коли панель відкрили: гравцям за столом він ні до чого.

  let packsModule = null;

  function loadConstructor() {
    if (window.SvoyaPacks) return Promise.resolve();
    if (!packsModule) {
      packsModule = new Promise((ok, fail) => {
        const el = document.createElement('script');
        el.src = '/games/svoya-packs.js';
        el.onload = ok;
        el.onerror = () => { packsModule = null; fail(new Error('конструктор не завантажився')); };
        document.head.appendChild(el);
      });
    }
    return packsModule;
  }

  HGames.registerPanel({
    id: 'svoya',
    title: 'Своя гра',
    icon: '🎯',
    mount(host, ctx) {
      if (window.SvoyaPacks) { window.SvoyaPacks.mount(host, ctx); return; }
      host.innerHTML = '<div class="svwait"><span class="spin"></span> відкриваю конструктор…</div>';
      loadConstructor().then(() => window.SvoyaPacks.mount(host, ctx))
        .catch((e) => { host.innerHTML = '<div class="svwait">' + esc(e.message) + '</div>'; });
    },
    update(host, ctx) { if (window.SvoyaPacks) window.SvoyaPacks.update(host, ctx); },
  });

  HGames.register({
    id: 'svoya',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x'],

    mount(root, ctx) {
      root.innerHTML = '<div class="svwrap">'
        + '<div class="svtop"><div class="svhead muted small"></div><button type="button" class="ghost small svspk"></button>'
        + '<div class="svtime"><i></i><span></span></div></div>'
        + '<audio class="svvoice" preload="auto"></audio>'
        + '<input class="svsearch" type="search" placeholder="знайти пакет…" hidden>'
        + '<div class="svstage"></div>'
        + '<div class="svhostbox"></div>'
        + '<button type="button" class="svbuzz" data-do="buzz" hidden>🔔</button>'
        + '<form class="svform" hidden><input type="text" maxlength="120" autocomplete="off" spellcheck="false" enterkeyhint="send" placeholder="твоя відповідь…">'
        + '<button class="primary" type="submit">➤</button></form>'
        + '<form class="svnum" hidden><span class="muted small"></span><input type="number" inputmode="numeric"><button class="primary" type="submit"></button></form>'
        + '<div class="svscores"></div>'
        + '</div>';
      const s = st(root);
      root.addEventListener('click', (e) => onClick(root, e));
      root.querySelector('.svspk').addEventListener('click', () => {
        const c = root._ctx;
        if (!c) return;
        const on = !speakerOn(c);
        setSpeaker(on);
        if (!on) hush(root);
        speakerBtn(root, c);
      });
      const a = root.querySelector('.svvoice');
      const done = () => { s.speaking = false; duck(s, false); };
      a.addEventListener('ended', done);
      // F5 посеред репліки — стару не повторюємо: звучить лише те, що ведучий скаже вже при нас
      s.sayId = (ctx.view && ctx.view.say && ctx.view.say.id) || 0;
      const search = root.querySelector('.svsearch');
      search.addEventListener('input', () => { s.query = search.value; if (root._ctx) render(root, root._ctx); });
      root.querySelector('.svnum').addEventListener('submit', (e) => {
        e.preventDefault();
        const c = root._ctx;
        const f = e.currentTarget;
        const amount = parseInt(f.querySelector('input').value, 10);
        if (!c || !Number.isFinite(amount)) return;
        c.act(f.dataset.mode === 'bid' ? 'bid' : 'bet', { amount });
      });
      root.querySelector('.svform').addEventListener('submit', (e) => {
        e.preventDefault();
        const input = e.currentTarget.querySelector('input');
        const text = input.value.trim();
        if (!text || !root._ctx) return;
        const final = root._ctx.view && root._ctx.view.phase === 'final';
        root._ctx.act('answer', { text }).then((r) => { if (r && r.ok && !final) input.value = ''; });
      });
      s.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 250);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    unmount(root) {
      const s = root._sv;
      if (s) clearInterval(s.timer);
      hush(root);
    },

    onKey(e, ctx) {
      // пробіл — кнопка (коли не друкуєш відповідь)
      if (e.key !== ' ' && e.code !== 'Space') return false;
      if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return false;
      const v = ctx.view || {};
      if (!(v.me && v.me.canBuzz)) return false;
      ctx.act('buzz');
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      const me = v.me || {};
      if (ctx.room && ctx.room.status === 'lobby') return v.pack ? 'Пакет «' + v.pack.title + '» — господар тисне «Почати»' : 'Господар обирає пакет';
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? (v.error || 'Партію зіграно') : '';
      if (v.paused) return '⏸ Пауза';
      if (v.waiting) return 'Ведучий збирається з думками…';
      switch (v.phase) {
        case 'intro': return 'Теми раунду';
        case 'board': return me.canPick && ctx.seat === v.chooser ? 'Обирай запитання' : 'Обирає ' + nick(ctx, v.chooser);
        case 'reading': return me.isHost ? 'Читай уголос і тисни «Кнопка!»' : me.canBuzz ? 'Знаєш — тисни!' : 'Слухаємо запитання';
        case 'buzz': return me.isHost ? 'Чекаємо на кнопку' : me.canBuzz ? 'Кнопка відкрита — тисни!' : 'Кнопка відкрита';
        case 'answering':
          if (v.answering === ctx.seat) return v.mode === 'live' ? 'Кажи відповідь уголос!' : 'Пиши відповідь!';
          return me.isHost ? nick(ctx, v.answering) + ' відповідає — суди' : 'Відповідає ' + nick(ctx, v.answering);
        case 'reveal': return v.correct != null ? 'Правильно: ' + nick(ctx, v.correct) : 'Ніхто не відповів';
        case 'cat': return 'Кіт у мішку';
        case 'auction': return v.auction && v.auction.turn === ctx.seat ? 'Твій хід у торгах' : 'Аукціон';
        case 'strike': return 'Фінал: викреслюємо теми';
        case 'bet': return 'Фінал: ставки';
        case 'final': return sp(v).canFinalAnswer ? 'Пиши відповідь — у всіх 30 секунд' : 'Фіналісти пишуть відповіді';
        case 'judging': return me.isHost ? 'Оціни відповіді й розкривай' : 'Ведучий перевіряє відповіді';
        case 'finale': return 'Розкриваємо фінал';
      }
      return '';
    },
  });
})();
