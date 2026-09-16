/*
  «Вгадай мелодію». Треки, уривки, час і очки — на сервері (Impl/Melody.cs); модуль грає уривок, приймає здогадки
  й показує, хто що впізнав.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'loading'|'play'|'reveal'|'done', round, rounds, clipSec, until, totalMs,
      clip: '/api/games/melody/<токен>.mp3' | null,
      me: null | { artist, title, points },
      found: [{ seat, artist, title, points }],
      answer: null | { artist, title, thumb },     // лише після раунду
      skip: seat[],                               // хто готовий пропустити цей трек
      scores[], left[], error, result }
  Хід: Act('guess', { text }) — відповідь приходить тостом («🎤 виконавець +70» або «Мимо»);
       Act('skip') — «готовий пропустити» (ще раз — передумав). Усі, хто не вгадав усе, готові — трек пропускається.

  Поки звучить уривок, радіо на сторінці глушимо (muted), а потім повертаємо як було.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M6 12.5V3.2l7-1.4v9.2" fill="none" stroke="var(--accent)" stroke-width="1.5" stroke-linejoin="round"/>'
    + '<circle cx="4.3" cy="12.4" r="1.9" fill="var(--clay)"/><circle cx="11.3" cy="11" r="1.9" fill="var(--clay)"/></svg>';

  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 8);

  function st(root) {
    if (!root._mg) root._mg = { clip: '', timer: 0, radioMuted: null, played: false, disliked: {} };
    return root._mg;
  }

  // ---------- звук ----------

  function radio() { return document.getElementById('audio'); }

  function duck(s, on) {
    const r = radio();
    if (!r) return;
    if (on && s.radioMuted === null) { s.radioMuted = r.muted; r.muted = true; }
    if (!on && s.radioMuted !== null) { r.muted = s.radioMuted; s.radioMuted = null; }
  }

  function player(root) { return root.querySelector('.mgaudio'); }

  // Гучність уривків — своя, окремо від радіо. Повзунок іде по децибелах, як у плеєрі радіо (app.js):
  // 0..100, крок 0.5 дБ, тож тихі рівні мають десятки кроків. У localStorage — сама гучність 0..1.
  const VOL_DB = 50;
  const VOL_KEY = 'melodyVolume';
  const posToVol = (p) => (p <= 0 ? 0 : Math.pow(10, -VOL_DB * (1 - p / 100) / 20));
  const volToPos = (v) => (v <= 0 ? 0 : Math.min(100, Math.max(1, Math.round(100 * (1 + 20 * Math.log10(v) / VOL_DB)))));

  function savedVolume() {
    const read = (k) => { try { return parseFloat(localStorage.getItem(k) ?? ''); } catch { return NaN; } };
    const own = read(VOL_KEY);
    if (Number.isFinite(own)) return own;
    const radioVol = read('volume');                  // вперше — як у радіо, щоб не оглушити
    return Number.isFinite(radioVol) ? radioVol : 0.5;
  }

  function setVolume(root, p, save) {
    p = Math.min(100, Math.max(0, p));
    const v = posToVol(p);
    const a = player(root);
    if (a) a.volume = v;
    const range = root.querySelector('.mgvol input');
    if (range && +range.value !== p) range.value = p;
    const icon = root.querySelector('.mgvol button');
    if (icon) icon.textContent = p === 0 ? '🔇' : p < 50 ? '🔈' : p < 85 ? '🔉' : '🔊';
    if (save) { try { localStorage.setItem(VOL_KEY, String(v)); } catch { /* приватне вікно */ } }
  }

  function bindVolume(root) {
    const box = root.querySelector('.mgvol');
    const range = box.querySelector('input');
    const icon = box.querySelector('button');
    let before = 60;
    setVolume(root, volToPos(savedVolume()), false);
    range.oninput = () => setVolume(root, parseInt(range.value, 10), true);
    icon.onclick = () => {
      const p = parseInt(range.value, 10);
      if (p > 0) { before = p; setVolume(root, 0, true); } else setVolume(root, before || 60, true);
    };
    range.addEventListener('wheel', (e) => {
      e.preventDefault();
      setVolume(root, parseInt(range.value, 10) + (e.deltaY < 0 ? 2 : -2), true);
    }, { passive: false });
  }

  function play(root) {
    const a = player(root);
    const s = st(root);
    if (!a || !a.src) return;
    a.currentTime = 0;
    duck(s, true);
    a.play().then(() => { s.played = true; paintPlay(root); })
      .catch(() => { duck(s, false); paintPlay(root); });   // браузер не дав грати без кліку — покажемо кнопку
  }

  function paintPlay(root) {
    const a = player(root);
    const btn = root.querySelector('.mgplay');
    if (!a || !btn) return;
    const playing = !a.paused && !a.ended;
    btn.textContent = playing ? '⏸ Пауза' : st(root).played ? '↻ Ще раз' : '▶ Слухати';
    const disc = root.querySelector('.mgdisc');
    if (disc) disc.classList.toggle('spin', playing);
  }

  function progress(root) {
    const a = player(root);
    const bar = root.querySelector('.mgwave i');
    if (!a || !bar) return;
    const d = a.duration || 1;
    bar.style.width = Math.min(100, (a.currentTime / d) * 100) + '%';
  }

  // ---------- 👎 ----------
  // Дизлайк живе в базі, а не в партії: тиснеш — трек (і та сама пісня з інших завантажень) більше не трапиться
  // в «Вгадай мелодію» нікому. Стан кнопки браузер тримає сам: у партію й так потрапляють лише треки без дизлайків.

  function dislikeBtn(root, id, small) {
    const on = !!st(root).disliked[id];
    return '<button type="button" class="ghost mgdis' + (on ? ' on' : '') + (small ? ' small' : '') + '" data-track="' + String(id).replace(/"/g, '&quot;') + '"'
      + ' title="' + (on ? 'Зняти дизлайк' : 'Більше не давати цей трек у «Вгадай мелодію»') + '">'
      + (small ? '👎' : on ? '👎 Не трапиться' : '👎 Більше не давати') + '</button>';
  }

  async function dislike(root, id) {
    if (!id || !window.HGames) return;
    const r = await HGames.call('MelodyDislike', id);
    if (!r || !r.ok) return;
    st(root).disliked[id] = !!r.disliked;
    root.querySelectorAll('.mgdis').forEach((b) => {
      if (b.dataset.track !== id) return;
      const small = b.classList.contains('small');
      b.classList.toggle('on', !!r.disliked);
      b.textContent = small ? '👎' : r.disliked ? '👎 Не трапиться' : '👎 Більше не давати';
      b.title = r.disliked ? 'Зняти дизлайк' : 'Більше не давати цей трек у «Вгадай мелодію»';
    });
  }

  // ---------- розмітка ----------

  function timer(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.mgtime');
    const live = ctx.playing && (v.phase === 'play' || v.phase === 'reveal');
    box.style.visibility = live ? 'visible' : 'hidden';
    if (!live) return;
    const left = Math.max(0, new Date(v.until).getTime() - Date.now());
    box.querySelector('i').style.width = Math.max(0, Math.min(100, left / (v.totalMs || 1) * 100)) + '%';
    box.querySelector('i').classList.toggle('hot', v.phase === 'play' && left < 8000);
    const t = String(Math.ceil(left / 1000));
    const n = box.querySelector('span');
    if (n.textContent !== t) n.textContent = t;
  }

  function stage(root, ctx, v) {
    const el = root.querySelector('.mgstage');
    let key, html;
    if (ctx.room && ctx.room.status === 'lobby') {
      key = 'lobby';
      html = '<div class="mgdisc"></div><div class="mgwait">Господар тисне «Почати» — і звучить перший уривок</div>';
    } else if (v.phase === 'loading') {
      key = 'loading:' + v.round;
      html = '<div class="mgwait"><span class="spin"></span> ' + (v.round ? 'Наступний трек…' : 'Ріжемо уривки з кешу радіо…') + '</div>';
    } else if (v.phase === 'play') {
      key = 'play:' + v.round;
      html = '<div class="mgdisc"></div>'
        + '<div class="mgwave"><i></i></div>'
        + '<button type="button" class="primary mgplay">▶ Слухати</button>';
    } else if (v.phase === 'done' && (v.played || []).length) {
      key = 'done:' + v.played.map((t) => t.id).join(',');
      html = '<div class="mgwait">Що звучало. 👎 — більше не давати в цій грі</div><div class="mgplayed">'
        + v.played.map((t) => '<div class="mgpl">'
          + '<span class="mgmini"' + (t.thumb ? ' style="background-image:url(' + ctx.esc(t.thumb) + ')"' : '') + '></span>'
          + '<span class="mgpt"><b>' + ctx.esc(t.title) + '</b><i>' + ctx.esc(t.artist) + '</i></span>'
          + dislikeBtn(root, t.id, true) + '</div>').join('')
        + '</div>';
    } else if (v.phase === 'reveal' || (v.phase === 'done' && v.answer)) {
      const a = v.answer || {};
      key = 'reveal:' + v.round + ':' + (v.phase);
      html = '<div class="mgcover"' + (a.thumb ? ' style="background-image:url(' + ctx.esc(a.thumb) + ')"' : '') + '></div>'
        + '<div class="mganswer"><div class="mgtitle">' + ctx.esc(a.title || '') + '</div><div class="mgartist">' + ctx.esc(a.artist || '') + '</div></div>'
        + '<div class="mgrow"><button type="button" class="ghost mgplay">↻ Ще раз</button>' + (a.id ? dislikeBtn(root, a.id, false) : '') + '</div>';
    } else {
      key = 'done';
      html = v.error ? '<div class="mgwait">' + ctx.esc(v.error) + '</div>' : '';
    }
    if (el.dataset.key === key) return;
    el.dataset.key = key;
    el.innerHTML = html;
    el.querySelectorAll('.mgdis').forEach((b) => b.onclick = () => dislike(root, b.dataset.track));
    const btn = el.querySelector('.mgplay');
    if (btn) btn.onclick = () => {
      const a = player(root);
      if (a && !a.paused && !a.ended) a.pause(); else play(root);
    };
  }

  function audio(root, ctx, v) {
    const s = st(root);
    const a = player(root);
    const want = v.clip || '';
    if (want === s.clip) return;
    s.clip = want;
    s.played = false;
    a.pause();
    if (!want) { a.removeAttribute('src'); a.load(); duck(s, false); return; }
    a.src = want;
    // Уривок стартує сам, щойно раунд почався. Після розкриття — ні: там кнопка «Ще раз».
    if (v.phase === 'play') play(root);
  }

  function guessBox(root, ctx, v) {
    const form = root.querySelector('.mgguess');
    const input = form.querySelector('input');
    const me = v.me || {};
    const all = me.artist && me.title;
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'play' && !all;
    input.disabled = !can;
    form.querySelector('button').disabled = !can;
    input.placeholder = !ctx.mine ? 'Дивишся збоку'
      : v.phase !== 'play' ? 'Чекаємо на трек…'
        : all ? 'Усе вгадав! 🎉' : 'виконавець або назва…';
    const marks = root.querySelector('.mgmarks');
    const html = ctx.mine && ctx.playing && v.phase !== 'done'
      ? '<span class="chip' + (me.artist ? ' on' : '') + '">🎤 виконавець</span><span class="chip' + (me.title ? ' on' : '') + '">🎵 назва</span>'
      : '';
    if (marks.innerHTML !== html) marks.innerHTML = html;
    form.onsubmit = (e) => {
      e.preventDefault();
      const text = input.value.trim();
      const c = root._ctx;
      if (!text || !c) return;
      c.act('guess', { text }).then((r) => {
        if (r && (r.ok || r.message === 'Мимо')) input.value = '';
        input.focus();
      });
    };
  }

  /// Кнопка «Пропустити» і хто вже готовий. Кнопки нема тому, хто вгадав усе: йому пропускати нема чого.
  function skipBox(root, ctx, v) {
    const el = root.querySelector('.mgskip');
    const me = v.me || {};
    const skip = v.skip || [];
    const mineSkip = skip.indexOf(ctx.seat) >= 0;
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'play' && !(me.artist && me.title);
    const waiting = [];
    if (v.phase === 'play') {
      for (let i = 0; i < seatsOf(ctx); i++) {
        if (!ctx.nickOf(i) || (v.left || []).indexOf(i) >= 0) continue;
        const f = (v.found || []).find((x) => x.seat === i);
        if (f && f.artist && f.title) continue;
        waiting.push(i);
      }
    }
    const ready = skip.length ? '⏭ ' + skip.map((i) => ctx.esc(ctx.nickOf(i) || '')).join(', ') + ' — за пропуск' : '';
    const html = v.phase !== 'play' ? ''
      : (can ? '<button type="button" class="' + (mineSkip ? 'primary' : 'ghost') + ' mgskipbtn">'
        + (mineSkip ? '⏭ Готовий пропустити (' + skip.length + '/' + waiting.length + ')' : '⏭ Пропустити') + '</button>' : '')
        + (ready && !(can && mineSkip && skip.length === 1) ? '<span class="muted small">' + ready + '</span>' : '');
    if (el.innerHTML !== html) {
      el.innerHTML = html;
      const b = el.querySelector('.mgskipbtn');
      if (b) b.onclick = () => { const c = root._ctx; if (c) c.act('skip'); };
    }
  }

  function scores(root, ctx, v) {
    const found = {};
    for (const f of v.found || []) found[f.seat] = f;
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = ctx.nickOf(i);
      if (nick) rows.push({ i, nick, score: (v.scores || [])[i] || 0, f: found[i] });
    }
    rows.sort((a, b) => b.score - a.score);
    const left = v.left || [];
    const html = rows.map((r) => '<div class="mgsc' + (r.i === ctx.seat ? ' me' : '') + (left.indexOf(r.i) >= 0 ? ' off' : '') + '">'
      + '<span class="mgn">' + ctx.esc(r.nick) + '</span>'
      + '<span class="mgf">' + (r.f && r.f.artist ? '🎤' : '') + (r.f && r.f.title ? '🎵' : '')
      + (v.phase === 'play' && (v.skip || []).indexOf(r.i) >= 0 ? '⏭' : '') + '</span>'
      + (r.f && r.f.points && v.phase !== 'done' ? '<em>+' + r.f.points + '</em>' : '')
      + '<b>' + r.score + '</b></div>').join('');
    const el = root.querySelector('.mgscores');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  function render(root, ctx) {
    root._ctx = ctx;
    const v = ctx.view || {};
    const head = root.querySelector('.mghead');
    const text = v.phase === 'done' ? 'Партію зіграно' : v.round ? 'Трек ' + v.round + ' з ' + v.rounds : ctx.playing ? 'Готуємось' : '';
    if (head.textContent !== text) head.textContent = text;
    stage(root, ctx, v);
    audio(root, ctx, v);
    guessBox(root, ctx, v);
    skipBox(root, ctx, v);
    scores(root, ctx, v);
    timer(root, ctx);
    paintPlay(root);
  }

  HGames.register({
    id: 'melody',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root.innerHTML = '<div class="mgwrap">'
        + '<div class="mgtop"><div class="mghead muted small"></div><div class="mgtime"><i></i><span></span></div></div>'
        + '<div class="mgstage"></div>'
        + '<audio class="mgaudio" preload="auto"></audio>'
        + '<div class="mgvol"><button type="button" class="ghost" title="Вимкнути звук">🔉</button>'
        + '<input type="range" min="0" max="100" step="1" aria-label="Гучність уривка" title="Гучність уривка · колесо миші — по кроку"></div>'
        + '<div class="mgmarks"></div>'
        + '<div class="mgskip"></div>'
        + '<form class="mgguess"><input type="text" maxlength="80" autocomplete="off" spellcheck="false" enterkeyhint="send">'
        + '<button class="primary" type="submit">➤</button></form>'
        + '<div class="mgscores"></div>'
        + '</div>';
      const s = st(root);
      const a = player(root);
      bindVolume(root);
      const settle = () => { paintPlay(root); if (a.paused || a.ended) duck(s, false); };
      a.addEventListener('play', () => paintPlay(root));
      a.addEventListener('pause', settle);
      a.addEventListener('ended', settle);
      a.addEventListener('timeupdate', () => progress(root));
      s.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 250);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    unmount(root) {
      const s = root._mg;
      if (!s) return;
      clearInterval(s.timer);
      const a = player(root);
      if (a) a.pause();
      duck(s, false);
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? (v.error || 'Партію зіграно') : '';
      if (v.phase === 'loading') return 'Готуємо трек…';
      if (v.phase === 'reveal') return v.round >= v.rounds ? 'Рахуємо очки…' : 'Наступний трек за мить…';
      if (!ctx.mine) return 'Дивишся збоку';
      return 'Хто співає і як зветься пісня?';
    },
  });
})();
