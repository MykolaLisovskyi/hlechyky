/*
  Піктіонарі. Правила, слово, час і очки живуть на сервері (Impl/Pictionary.cs); модуль малює полотно,
  шле штрихи художника і здогадки решти.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'pick'|'draw'|'reveal'|'done', turn, drawer, turnNo, turns, round, rounds,
      choices: string[]|null (лише художникові, поки обирає), word: string|null (художнику, тим, хто вгадав,
      і всім після ходу), mask: 'к_т', until, totalMs, scores[], gained[], guessed[], left[],
      drawing: { ver, n, ops }, feed: [{ id, seat, kind: 'guess'|'ok'|'word'|'skip'|'left', text }], result }
  Кадр (подія 'frame', лише коли щось змінилось; слова в ньому нема):
    { ph, drawer, ver, from, n, ops, mask, until, guessed, feed }

  Операція малюнка — масив цілих: [вид, штрих, колір, товщина, x0, y0, x1, y1, …]; вид 0 — лінія,
  1 — заливка (x0, y0 — точка). Полотно логічне: 1000 × 750.
  Ходи: Act('pick', { i }), Act('guess', { text });
        Input('draw', { s, c, w, p: [x, y, …] }), Input('fill', { s, c, x, y }), Input('undo'), Input('clear'), Input('sync').
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 13.5c1.6 0 2.6-.6 3-2l6.8-6.8a1.5 1.5 0 0 0-2.1-2.1L3.4 9.4c-1.3.5-1.9 1.6-.9 4.1z" fill="none" stroke="var(--accent)" stroke-width="1.5" stroke-linejoin="round"/>'
    + '<path d="M9.4 3.4l2.2 2.2" stroke="var(--clay)" stroke-width="1.5"/></svg>';

  const W = 1000, H = 750;
  /// Та сама кількість, що Pictionary.Colors на сервері. 0 — білий, ним же стирає гумка.
  const PALETTE = [
    '#ffffff', '#000000', '#7f7f7f', '#c3c3c3', '#5d4037', '#8d5a2b', '#e53935', '#fb8c00', '#fdd835', '#fff59d',
    '#43a047', '#a5d6a7', '#00897b', '#1e88e5', '#90caf9', '#3949ab', '#8e24aa', '#f48fb1', '#e8b48a', '#ff7043',
  ];
  const SIZES = [3, 8, 16, 32];
  /// Шматок штриха летить щонайменше так часто (і не частіше: хаб бере 30 Input на секунду).
  const FLUSH_MS = 60;
  const CHUNK_MAX = 200;
  const SYNC_MS = 2000;

  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 10);

  // =========================================================================================
  // Стан картки
  // =========================================================================================

  function st(root) {
    if (!root._pc) {
      const buf = document.createElement('canvas');
      buf.width = W; buf.height = H;
      root._pc = {
        buf, bctx: buf.getContext('2d', { willReadFrequently: true }),
        ver: -1, ops: [], n: 0,
        tool: 'pen', color: 1, size: 1,
        stroke: 1, cur: null, flushAt: 0, flushTimer: 0,
        local: [],          // шматки, які художник уже намалював, а сервер ще не повернув: [{ s, i, op }]
        feed: new Map(),
        lastSync: 0, dirty: true, raf: 0, timer: 0,
      };
      clearBuf(root._pc);
    }
    return root._pc;
  }

  function clearBuf(s) {
    s.bctx.globalCompositeOperation = 'source-over';
    s.bctx.fillStyle = '#ffffff';
    s.bctx.fillRect(0, 0, W, H);
  }

  // =========================================================================================
  // Малювання операцій у буфер 1000 × 750
  // =========================================================================================

  function drawOp(c, op) {
    if (!op || op.length < 6) return;
    const color = PALETTE[op[2]] || '#000';
    if (op[0] === 1) { flood(c, op[4], op[5], color); return; }
    c.strokeStyle = color;
    c.fillStyle = color;
    c.lineWidth = op[3];
    c.lineCap = 'round';
    c.lineJoin = 'round';
    if (op.length === 6) {
      c.beginPath();
      c.arc(op[4], op[5], op[3] / 2, 0, Math.PI * 2);
      c.fill();
      return;
    }
    c.beginPath();
    c.moveTo(op[4], op[5]);
    for (let i = 6; i + 1 < op.length; i += 2) c.lineTo(op[i], op[i + 1]);
    c.stroke();
  }

  function hex(color) {
    const n = parseInt(color.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }

  /// Заливка рядками. Допуск — щоб згладжені краї ліній не лишали білої бахроми всередині.
  function flood(c, x0, y0, color) {
    x0 = Math.max(0, Math.min(W - 1, x0 | 0));
    y0 = Math.max(0, Math.min(H - 1, y0 | 0));
    const img = c.getImageData(0, 0, W, H);
    const d = img.data;
    const at = (y0 * W + x0) * 4;
    const tr = d[at], tg = d[at + 1], tb = d[at + 2];
    const [fr, fg, fb] = hex(color);
    if (Math.abs(tr - fr) + Math.abs(tg - fg) + Math.abs(tb - fb) < 12) return;
    const TOL = 90;
    const same = (i) => Math.abs(d[i] - tr) + Math.abs(d[i + 1] - tg) + Math.abs(d[i + 2] - tb) <= TOL;
    const seen = new Uint8Array(W * H);
    const stack = [x0, y0];
    while (stack.length) {
      const y = stack.pop(), x = stack.pop();
      let l = x;
      while (l >= 0 && !seen[y * W + l] && same((y * W + l) * 4)) l--;
      l++;
      let r = x;
      while (r < W && !seen[y * W + r] && same((y * W + r) * 4)) r++;
      for (let i = l; i < r; i++) {
        const p = y * W + i;
        seen[p] = 1;
        d[p * 4] = fr; d[p * 4 + 1] = fg; d[p * 4 + 2] = fb; d[p * 4 + 3] = 255;
        if (y > 0 && !seen[p - W] && same((p - W) * 4)) stack.push(i, y - 1);
        if (y < H - 1 && !seen[p + W] && same((p + W) * 4)) stack.push(i, y + 1);
      }
    }
    c.putImageData(img, 0, 0);
  }

  function redrawAll(s) {
    clearBuf(s);
    for (const op of s.ops) drawOp(s.bctx, op);
    s.dirty = true;
  }

  // =========================================================================================
  // Синхронізація малюнка: вид — повний, кадр — дописане або цілком нова версія
  // =========================================================================================

  function reset(root, s, drawing) {
    s.ver = drawing.ver;
    s.ops = (drawing.ops || []).slice();
    s.n = s.ops.length;
    // шматки, яких сервер уже не має (очистка, новий хід), художникові малювати більше не треба
    s.local = s.local.filter((l) => countOf(s, l.s) <= l.i);
    let maxStroke = 0;
    for (const op of s.ops) if (op[1] > maxStroke) maxStroke = op[1];
    if (s.stroke <= maxStroke) s.stroke = maxStroke + 1;
    redrawAll(s);
    paintSoon(root);
  }

  function append(root, s, ops) {
    for (const op of ops) {
      s.ops.push(op);
      drawOp(s.bctx, op);
    }
    s.n = s.ops.length;
    s.local = s.local.filter((l) => countOf(s, l.s) <= l.i);
    s.dirty = true;
    paintSoon(root);
  }

  /// Скільки шматків штриха stroke уже є на сервері.
  function countOf(s, stroke) {
    let k = 0;
    for (const op of s.ops) if (op[1] === stroke && op[0] === 0) k++;
    return k;
  }

  function fromView(root, ctx, v) {
    const s = st(root);
    const d = v.drawing;
    if (!d) return;
    // Вид міг скластися раніше за кадри, які вже прийшли: старішим малюнком новіший не затираємо.
    if (d.ver > s.ver || (d.ver === s.ver && d.n > s.n)) reset(root, s, d);
  }

  function fromFrame(root, ctx, f) {
    const s = st(root);
    if (f.ver < s.ver) return;          // запізнілий кадр старої версії
    if (f.ver > s.ver) {
      if (f.from === 0) reset(root, s, { ver: f.ver, ops: f.ops });
      else askSync(ctx, s);
      return;
    }
    if (f.from > s.n) { askSync(ctx, s); return; }
    if (f.n > s.n) append(root, s, f.ops.slice(s.n - f.from));
  }

  function askSync(ctx, s) {
    if (!ctx.mine) return;
    const now = Date.now();
    if (now - s.lastSync < SYNC_MS) return;
    s.lastSync = now;
    ctx.input('sync');
  }

  // =========================================================================================
  // Екранне полотно
  // =========================================================================================

  function paintSoon(root) {
    const s = st(root);
    if (s.raf) return;
    s.raf = requestAnimationFrame(() => { s.raf = 0; paint(root); });
  }

  function paint(root) {
    const s = st(root);
    const el = root.querySelector('.pccanvas');
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    const c = el.getContext('2d');
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.imageSmoothingEnabled = true;
    c.drawImage(s.buf, 0, 0, pw, ph);
    // Своє, ще не підтверджене сервером, — поверх: художник бачить лінію одразу, а не за тик.
    // шматок, який сервер так і не повернув (відмовив), за кілька секунд зникає сам
    const stale = Date.now() - 4000;
    s.local = s.local.filter((l) => l.t > stale);
    const pending = s.local.map((l) => l.op);
    if (s.cur && s.cur.p.length >= 2) pending.push([0, s.cur.s, s.cur.c, s.cur.w, ...s.cur.p]);
    if (pending.length) {
      c.setTransform(pw / W, 0, 0, ph / H, 0, 0);
      for (const op of pending) drawOp(c, op);
    }
    s.dirty = false;
  }

  // =========================================================================================
  // Художник: вказівник → шматки штриха
  // =========================================================================================

  function canDraw(ctx) {
    const v = ctx.view || {};
    return !!ctx.mine && !!ctx.playing && v.phase === 'draw' && v.drawer === ctx.seat;
  }

  function point(el, e) {
    const r = el.getBoundingClientRect();
    return [
      Math.round(Math.max(0, Math.min(W, (e.clientX - r.left) / r.width * W))),
      Math.round(Math.max(0, Math.min(H, (e.clientY - r.top) / r.height * H))),
    ];
  }

  function flush(root, ctx, final) {
    const s = st(root);
    clearTimeout(s.flushTimer);
    s.flushTimer = 0;
    const cur = s.cur;
    if (!cur || cur.p.length < 2) { if (final) s.cur = null; return; }
    if (cur.sent === cur.p.length && !final) return;
    const p = cur.p.slice();
    if (p.length >= 2) {
      ctx.input('draw', { s: cur.s, c: cur.c, w: cur.w, p });
      s.local.push({ s: cur.s, i: cur.chunks, t: Date.now(), op: [0, cur.s, cur.c, cur.w, ...p] });
      cur.chunks++;
    }
    // наступний шматок починається з останньої точки цього — лінія не рветься
    const lx = p[p.length - 2], ly = p[p.length - 1];
    if (final) s.cur = null;
    else { cur.p = [lx, ly]; cur.sent = 2; }
    paintSoon(root);
  }

  function bindCanvas(root, ctx) {
    const el = root.querySelector('.pccanvas');
    if (el._bound) return;
    el._bound = true;
    const s = st(root);

    // Довге натискання пальцем (Steam Deck / тач) інакше відкриває меню картинки
    el.addEventListener('contextmenu', (e) => e.preventDefault());

    el.addEventListener('pointerdown', (e) => {
      const c = root._ctx;
      if (!c || !canDraw(c)) return;
      e.preventDefault();
      const [x, y] = point(el, e);
      if (s.tool === 'fill') {
        c.input('fill', { s: s.stroke++, c: s.color, x, y });
        return;
      }
      try { el.setPointerCapture(e.pointerId); } catch { /* старі браузери */ }
      const color = s.tool === 'eraser' ? 0 : s.color;
      const w = SIZES[s.size] * (s.tool === 'eraser' ? 2 : 1);
      s.cur = { s: s.stroke++, c: color, w, p: [x, y], sent: 0, chunks: 0 };
      paintSoon(root);
    });

    el.addEventListener('pointermove', (e) => {
      const c = root._ctx;
      if (!s.cur || !c) return;
      const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
      for (const ev of events.length ? events : [e]) {
        const [x, y] = point(el, ev);
        const p = s.cur.p;
        const dx = x - p[p.length - 2], dy = y - p[p.length - 1];
        if (dx * dx + dy * dy < 4) continue;
        p.push(x, y);
      }
      if (s.cur.p.length / 2 >= CHUNK_MAX) flush(root, c, false);
      else if (!s.flushTimer) s.flushTimer = setTimeout(() => flush(root, c, false), FLUSH_MS);
      paintSoon(root);
    });

    const end = () => {
      const c = root._ctx;
      if (!s.cur || !c) return;
      flush(root, c, true);
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
    el.addEventListener('lostpointercapture', end);

    new ResizeObserver(() => paintSoon(root)).observe(el);
  }

  // =========================================================================================
  // Розмітка
  // =========================================================================================

  function toolbar(root, ctx) {
    const s = st(root);
    const bar = root.querySelector('.pctools');
    const on = canDraw(ctx);
    bar.hidden = !on;
    if (!on) return;
    if (!bar.firstChild) {
      bar.innerHTML = '<div class="pcpal">'
        + PALETTE.map((c, i) => '<button type="button" class="pcsw" data-c="' + i + '" style="background:' + c + '" title="Колір"></button>').join('')
        + '</div><div class="pcrow">'
        + SIZES.map((z, i) => '<button type="button" class="pcsz" data-z="' + i + '" title="Товщина ' + z + '"><i style="width:' + Math.max(4, z * .7) + 'px;height:' + Math.max(4, z * .7) + 'px"></i></button>').join('')
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pct" data-t="pen" title="Пензель">✏️</button>'
        + '<button type="button" class="pct" data-t="eraser" title="Гумка">🧽</button>'
        + '<button type="button" class="pct" data-t="fill" title="Заливка">🪣</button>'
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pca" data-a="undo" title="Скасувати (Ctrl+Z)">↶</button>'
        + '<button type="button" class="pca" data-a="clear" title="Очистити все">🗑</button>'
        + '</div>';
      bar.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        const c = root._ctx;
        if (!b || !c) return;
        if (b.dataset.c != null) { s.color = +b.dataset.c; if (s.tool === 'eraser') s.tool = 'pen'; }
        else if (b.dataset.z != null) s.size = +b.dataset.z;
        else if (b.dataset.t) s.tool = b.dataset.t;
        else if (b.dataset.a === 'undo') undo(root, c);
        else if (b.dataset.a === 'clear') { if (confirm('Стерти весь малюнок?')) { s.local = []; c.input('clear'); } }
        marks(root);
      });
    }
    marks(root);
  }

  function undo(root, ctx) {
    const s = st(root);
    if (s.cur) flush(root, ctx, true);
    const last = s.local.length ? s.local[s.local.length - 1].s : null;
    if (last != null) s.local = s.local.filter((l) => l.s !== last);
    ctx.input('undo');
    paintSoon(root);
  }

  function marks(root) {
    const s = st(root);
    root.querySelectorAll('.pcsw').forEach((b) => b.classList.toggle('on', +b.dataset.c === s.color && s.tool !== 'eraser'));
    root.querySelectorAll('.pcsz').forEach((b) => b.classList.toggle('on', +b.dataset.z === s.size));
    root.querySelectorAll('.pct').forEach((b) => b.classList.toggle('on', b.dataset.t === s.tool));
    const el = root.querySelector('.pccanvas');
    if (el) el.dataset.tool = s.tool;
  }

  /// Найсвіжіший кадр цієї ж фази — або нічого.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.ph === v.phase && f.drawer === v.drawer ? f : null;
  }

  function untilOf(ctx, v) {
    const f = fresh(ctx, v);
    return new Date((f && f.until) || v.until || 0).getTime();
  }

  function wordLine(root, ctx, v) {
    const f = fresh(ctx, v);
    const el = root.querySelector('.pcword');
    let html;
    if (v.phase === 'pick') {
      html = v.drawer === ctx.seat && ctx.mine
        ? '<span class="muted">Обери слово</span>'
        : '<span class="muted">' + ctx.esc(ctx.nickOf(v.drawer) || 'Художник') + ' обирає слово…</span>';
    } else if (v.word && v.phase === 'draw') {
      html = '<b class="pcreal">' + ctx.esc(v.word) + '</b>';
    } else if (v.phase === 'draw') {
      const mask = (f && f.mask) || v.mask || '';
      const parts = mask.split(/([ \-])/);
      html = parts.map((p) => {
        if (p === ' ') return '<span class="pcgap"></span>';
        if (p === '-') return '<span class="pcl">-</span>';
        return '<span class="pcw">' + p.split('').map((ch) => '<span class="pcl' + (ch === '_' ? ' hid' : '') + '">'
          + (ch === '_' ? '' : ctx.esc(ch)) + '</span>').join('') + '<sub>' + p.length + '</sub></span>';
      }).join('');
    } else if (v.word) {
      html = '<span class="muted">Слово:</span> <b class="pcreal">' + ctx.esc(v.word) + '</b>';
    } else {
      html = '';
    }
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  function head(root, ctx, v) {
    const el = root.querySelector('.pchead');
    const text = v.phase === 'done' ? 'Партію зіграно'
      : v.turns ? 'Коло ' + (v.round || 1) + ' з ' + (v.rounds || 1) + ' · малює ' + (ctx.nickOf(v.drawer) || '—') : '';
    if (el.textContent !== text) el.textContent = text;
  }

  function timer(root, ctx) {
    const v = ctx.view || {};
    const bar = root.querySelector('.pctime i');
    const num = root.querySelector('.pcsec');
    const live = ctx.playing && (v.phase === 'draw' || v.phase === 'pick' || v.phase === 'reveal');
    root.querySelector('.pctime').style.visibility = live ? 'visible' : 'hidden';
    if (!live) { num.textContent = ''; return; }
    const total = v.totalMs || 1;
    const left = Math.max(0, untilOf(ctx, v) - Date.now());
    bar.style.width = Math.max(0, Math.min(100, left / total * 100)) + '%';
    bar.classList.toggle('hot', v.phase === 'draw' && left < 10000);
    const t = String(Math.ceil(left / 1000));
    if (num.textContent !== t) num.textContent = t;
  }

  function scores(root, ctx, v) {
    const f = fresh(ctx, v);
    const guessed = (f && f.guessed) || v.guessed || [];
    const left = v.left || [];
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      rows.push({ i, nick, score: (v.scores || [])[i] || 0, gained: (v.gained || [])[i] || 0 });
    }
    rows.sort((a, b) => b.score - a.score);
    const html = rows.map((r) => {
      const drawing = (v.phase === 'draw' || v.phase === 'pick') && r.i === v.drawer;
      const ok = guessed.indexOf(r.i) >= 0;
      const mark = drawing ? '✏️' : ok ? '✅' : '';
      const plus = v.phase === 'reveal' && r.gained ? '<em>+' + r.gained + '</em>' : '';
      return '<div class="pcsc' + (ok ? ' ok' : '') + (drawing ? ' drw' : '') + (r.i === ctx.seat ? ' me' : '')
        + (left.indexOf(r.i) >= 0 ? ' off' : '') + '">'
        + '<span class="pcn">' + ctx.esc(r.nick) + '</span><span class="pcm">' + mark + '</span>' + plus
        + '<b>' + r.score + '</b></div>';
    }).join('');
    const el = root.querySelector('.pcscores');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  function mergeFeed(s, items, replace) {
    if (!items) return;
    if (replace) {
      // порожня стрічка у виді — нова партія: старе прибираємо все
      const max = items.length ? items.reduce((m, x) => Math.max(m, x.id), 0) : Infinity;
      for (const id of [...s.feed.keys()]) if (id <= max) s.feed.delete(id);
    }
    for (const it of items) s.feed.set(it.id, it);
    const ids = [...s.feed.keys()].sort((a, b) => a - b);
    while (ids.length > 40) s.feed.delete(ids.shift());
  }

  function feed(root, ctx) {
    const s = st(root);
    const list = [...s.feed.values()].sort((a, b) => a.id - b.id);
    const who = (i) => ctx.esc(ctx.nickOf(i) || 'хтось');
    const html = list.map((it) => {
      switch (it.kind) {
        case 'ok': return '<div class="pcf ok">✅ <b>' + who(it.seat) + '</b> вгадує!</div>';
        case 'word': return '<div class="pcf word">Слово було: <b>' + ctx.esc(it.text || '') + '</b></div>';
        case 'skip': return '<div class="pcf word">Хід пропущено</div>';
        case 'left': return '<div class="pcf muted">' + who(it.seat) + ' встає з-за столу</div>';
        default: return '<div class="pcf"><b>' + who(it.seat) + ':</b> ' + ctx.esc(it.text || '') + '</div>';
      }
    }).join('') || '<div class="pcf muted">Тут з\'являться здогадки</div>';
    const el = root.querySelector('.pcfeed');
    if (el.innerHTML !== html) {
      const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 30;
      el.innerHTML = html;
      if (atBottom || !el._seen) el.scrollTop = el.scrollHeight;
      el._seen = true;
    }
  }

  function guessBox(root, ctx, v) {
    const f = fresh(ctx, v);
    const guessed = (f && f.guessed) || v.guessed || [];
    const form = root.querySelector('.pcguess');
    const input = form.querySelector('input');
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'draw' && v.drawer !== ctx.seat && guessed.indexOf(ctx.seat) < 0;
    const drawer = ctx.mine && v.drawer === ctx.seat && v.phase === 'draw';
    input.disabled = !can;
    form.querySelector('button').disabled = !can;
    input.placeholder = can ? 'Твоя здогадка…'
      : drawer ? 'Ти малюєш — вгадують інші'
        : guessed.indexOf(ctx.seat) >= 0 && v.phase === 'draw' ? 'Вгадано! Чекаємо інших'
          : !ctx.mine ? 'Дивишся збоку' : 'Зараз не вгадують';
    form.onsubmit = (e) => {
      e.preventDefault();
      const text = input.value.trim();
      const c = root._ctx;
      if (!text || !c) return;
      c.act('guess', { text }).then((r) => {
        if (!r || r.ok) input.value = '';
        input.focus();
      });
    };
  }

  function overlay(root, ctx, v) {
    const el = root.querySelector('.pcover');
    let html = '';
    if (v.phase === 'pick' && ctx.mine && v.drawer === ctx.seat && v.choices) {
      html = '<div class="pcbox"><div class="pctitle">Що малюватимеш?</div><div class="pcchoices">'
        + v.choices.map((w, i) => '<button type="button" class="primary" data-i="' + i + '">' + ctx.esc(w) + '</button>').join('')
        + '</div></div>';
    } else if (v.phase === 'pick') {
      html = '<div class="pcbox"><div class="pctitle">✏️ ' + ctx.esc(ctx.nickOf(v.drawer) || 'Художник') + ' обирає слово…</div></div>';
    } else if (v.phase === 'reveal') {
      const got = [];
      for (let i = 0; i < seatsOf(ctx); i++) if ((v.gained || [])[i] > 0) got.push({ i, g: v.gained[i] });
      got.sort((a, b) => b.g - a.g);
      html = '<div class="pcbox"><div class="muted small">Слово було</div><div class="pcbig">' + ctx.esc(v.word || '—') + '</div>'
        + (got.length
          ? '<div class="pcgot">' + got.map((x) => '<span class="chip">' + ctx.esc(ctx.nickOf(x.i) || '') + ' <b>+' + x.g + '</b></span>').join('') + '</div>'
          : '<div class="muted">Ніхто не вгадав</div>')
        + '</div>';
    } else if (v.phase === 'done') {
      const rows = [];
      for (let i = 0; i < seatsOf(ctx); i++) if (ctx.nickOf(i)) rows.push({ i, s: (v.scores || [])[i] || 0 });
      rows.sort((a, b) => b.s - a.s);
      const medal = ['🥇', '🥈', '🥉'];
      html = '<div class="pcbox"><div class="pctitle">Партію зіграно</div><div class="pcfinal">'
        + rows.map((r, k) => '<div><span>' + (medal[k] || (k + 1) + '.') + ' ' + ctx.esc(ctx.nickOf(r.i)) + '</span><b>' + r.s + '</b></div>').join('')
        + '</div></div>';
    }
    if (el._html !== html) {
      el._html = html;
      el.innerHTML = html;
      el.hidden = !html;
      el.querySelectorAll('[data-i]').forEach((b) => b.onclick = () => {
        const c = root._ctx;
        if (c) c.act('pick', { i: +b.dataset.i });
      });
    }
  }

  function render(root, ctx) {
    root._ctx = ctx;
    ctx.pcRoot = root;
    const v = ctx.view || {};
    const s = st(root);
    if (v.phase) {
      fromView(root, ctx, v);
      mergeFeed(s, v.feed, true);
    }
    head(root, ctx, v);
    wordLine(root, ctx, v);
    timer(root, ctx);
    scores(root, ctx, v);
    feed(root, ctx);
    guessBox(root, ctx, v);
    overlay(root, ctx, v);
    toolbar(root, ctx);
    const el = root.querySelector('.pccanvas');
    el.classList.toggle('can', canDraw(ctx));
    if (!canDraw(ctx)) { s.cur = null; s.local = []; }
    paintSoon(root);
  }

  HGames.register({
    id: 'pictionary',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      root.innerHTML = '<div class="pcwrap">'
        + '<div class="pctop"><div class="pchead muted small"></div><div class="pcword"></div>'
        + '<div class="pctime"><i></i><span class="pcsec"></span></div></div>'
        + '<div class="pcmain">'
        + '<div class="pcstage"><canvas class="pccanvas"></canvas><div class="pcover" hidden></div></div>'
        + '<div class="pcside"><div class="pcscores"></div><div class="pcfeed"></div>'
        + '<form class="pcguess"><input type="text" maxlength="40" autocomplete="off" spellcheck="false" enterkeyhint="send">'
        + '<button class="primary" type="submit">➤</button></form></div>'
        + '<div class="pctools" hidden></div>'
        + '</div></div>';
      bindCanvas(root, ctx);
      const s = st(root);
      s.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 200);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    frame(root, ctx, f) {
      if (!f) return;
      root._ctx = ctx;
      const s = st(root);
      fromFrame(root, ctx, f);
      mergeFeed(s, f.feed, false);
      const v = ctx.view || {};
      wordLine(root, ctx, v);
      scores(root, ctx, v);
      feed(root, ctx);
      guessBox(root, ctx, v);
    },

    unmount(root) {
      const s = root._pc;
      if (!s) return;
      clearInterval(s.timer);
      clearTimeout(s.flushTimer);
      if (s.raf) cancelAnimationFrame(s.raf);
    },

    onKey(e, ctx) {
      if (!canDraw(ctx)) return false;
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyZ') {
        if (ctx.pcRoot) { undo(ctx.pcRoot, ctx); return true; }
      }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing) return v.phase === 'done' ? 'Партію зіграно' : '';
      // Секунд тут нема: статус перемальовується лише з кадрами, а відлік живе на смужці таймера.
      if (v.phase === 'pick') return v.drawer === ctx.seat && ctx.mine ? 'Обери слово' : 'Художник обирає слово…';
      if (v.phase === 'reveal') return v.turnNo >= v.turns ? 'Рахуємо очки…' : 'Зараз малюватиме наступний…';
      if (v.phase !== 'draw') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      if (v.drawer === ctx.seat) return 'Малюй! Вгадують інші';
      const f = fresh(ctx, v);
      const guessed = (f && f.guessed) || v.guessed || [];
      return guessed.indexOf(ctx.seat) >= 0 ? 'Вгадано! Чекаємо решту' : 'Вгадуй, що малюють';
    },
  });
})();
