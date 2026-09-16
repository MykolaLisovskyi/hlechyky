/*
  Зіпсований телефон. Кроки, таймери, ланцюжки й ❤ живуть на сервері (Impl/Telephone.cs); модуль показує
  кожному його завдання, малює полотно й гортає показ.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'step'|'reveal'|'done', step, steps, until, totalMs,
      task: null | { kind: 'phrase'|'draw'|'describe', chain, prompt: null | { kind: 'text'|'drawing', text, ops },
                     ready, text, n, ops, ideas: string[] },
      ready: seat[], waiting: seat[],
      reveal: null | { chain, owner, no, chains, shown, total,
                       entries: [{ index, seat, kind: 'text'|'drawing', text, ops, likes, liked }] },
      likes: number[], left: seat[], result }
  Кадрів нема. Ходи: Input('text', { text }) — чернетка; Act('done', { text } | { n }), Act('edit');
  Input('draw', { s, c, w, p }), Input('fill', { s, c, x, y }), Input('undo'), Input('clear');
  Act('next'), Act('like', { chain, index }).

  Малюнок — як у Піктіонарі: операції [вид, штрих, колір, товщина, x0, y0, …] на полотні 1000 × 750.
  Тут художник один на своєму полотні, тож правда про малюнок — у браузері, а на сервер летить копія.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M3 2.5h3l1 3-2 1.2a8 8 0 0 0 4.3 4.3l1.2-2 3 1v3a1.5 1.5 0 0 1-1.6 1.5A11.5 11.5 0 0 1 1.5 4.1 1.5 1.5 0 0 1 3 2.5z" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/>'
    + '<path d="M10 2.5c1.8.4 3.1 1.7 3.5 3.5" stroke="var(--clay)" stroke-width="1.4" fill="none" stroke-linecap="round"/></svg>';

  const W = 1000, H = 750;
  /// Та сама палітра, що в pictionary.js (Sketch.Colors на сервері).
  const PALETTE = [
    '#ffffff', '#000000', '#7f7f7f', '#c3c3c3', '#5d4037', '#8d5a2b', '#e53935', '#fb8c00', '#fdd835', '#fff59d',
    '#43a047', '#a5d6a7', '#00897b', '#1e88e5', '#90caf9', '#3949ab', '#8e24aa', '#f48fb1', '#e8b48a', '#ff7043',
  ];
  const SIZES = [3, 8, 16, 32];
  const FLUSH_MS = 80;
  const CHUNK_MAX = 200;
  const DRAFT_MS = 600;

  // =========================================================================================
  // Малювання операцій
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

  function flood(c, x0, y0, color) {
    x0 = Math.max(0, Math.min(W - 1, x0 | 0));
    y0 = Math.max(0, Math.min(H - 1, y0 | 0));
    const img = c.getImageData(0, 0, W, H);
    const d = img.data;
    const at = (y0 * W + x0) * 4;
    const tr = d[at], tg = d[at + 1], tb = d[at + 2];
    const n = parseInt(color.slice(1), 16);
    const fr = (n >> 16) & 255, fg = (n >> 8) & 255, fb = n & 255;
    if (Math.abs(tr - fr) + Math.abs(tg - fg) + Math.abs(tb - fb) < 12) return;
    const same = (i) => Math.abs(d[i] - tr) + Math.abs(d[i + 1] - tg) + Math.abs(d[i + 2] - tb) <= 90;
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

  /// Готова картинка з операцій: canvas 1000 × 750. Кешуємо за самим масивом — показ перемальовується часто.
  const rendered = new WeakMap();
  function picture(ops) {
    if (rendered.has(ops)) return rendered.get(ops);
    const cv = document.createElement('canvas');
    cv.width = W; cv.height = H;
    const c = cv.getContext('2d', { willReadFrequently: true });
    c.fillStyle = '#fff';
    c.fillRect(0, 0, W, H);
    for (const op of ops || []) drawOp(c, op);
    rendered.set(ops, cv);
    return cv;
  }

  /// Показати малюнок у <canvas> будь-якого розміру.
  function show(el, ops) {
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    el.getContext('2d').drawImage(picture(ops), 0, 0, pw, ph);
  }

  // =========================================================================================
  // Своє полотно
  // =========================================================================================

  function pad(root) {
    if (!root._tp) {
      const buf = document.createElement('canvas');
      buf.width = W; buf.height = H;
      root._tp = {
        buf, bctx: buf.getContext('2d', { willReadFrequently: true }),
        ops: [], key: '', tool: 'pen', color: 1, size: 1, stroke: 1,
        cur: null, flushTimer: 0, draftTimer: 0, raf: 0, timer: 0, idea: 0,
        seen: new Map(),        // chain → entries, які вже показали: з них гортаємо альбом після партії
      };
      wipe(root._tp);
    }
    return root._tp;
  }

  function wipe(p) {
    p.bctx.fillStyle = '#fff';
    p.bctx.fillRect(0, 0, W, H);
  }

  function rebuild(p) {
    wipe(p);
    for (const op of p.ops) drawOp(p.bctx, op);
  }

  function paintSoon(root) {
    const p = pad(root);
    if (p.raf) return;
    p.raf = requestAnimationFrame(() => { p.raf = 0; paintPad(root); });
  }

  function paintPad(root) {
    const p = pad(root);
    const el = root.querySelector('.tpcanvas');
    if (!el || el.offsetParent === null) return;
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    const c = el.getContext('2d');
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.drawImage(p.buf, 0, 0, pw, ph);
    if (p.cur && p.cur.p.length >= 2) {
      c.setTransform(pw / W, 0, 0, ph / H, 0, 0);
      drawOp(c, [0, p.cur.s, p.cur.c, p.cur.w, ...p.cur.p]);
    }
  }

  function canDraw(ctx) {
    const t = (ctx.view || {}).task;
    return !!ctx.playing && !!t && t.kind === 'draw' && !t.ready;
  }

  function point(el, e) {
    const r = el.getBoundingClientRect();
    return [
      Math.round(Math.max(0, Math.min(W, (e.clientX - r.left) / r.width * W))),
      Math.round(Math.max(0, Math.min(H, (e.clientY - r.top) / r.height * H))),
    ];
  }

  /// Відправити накопичений шматок штриха. Локально він одразу стає операцією — правда тут, у браузері.
  function flush(root, final) {
    const p = pad(root);
    const ctx = root._ctx;
    clearTimeout(p.flushTimer);
    p.flushTimer = 0;
    const cur = p.cur;
    if (!cur || !ctx) return;
    // один-єдиний «хвіст» із попереднього шматка слати нема чого; крапку (клік без руху) — так
    if (cur.p.length > 2 || (cur.fresh && final)) {
      const op = [0, cur.s, cur.c, cur.w, ...cur.p];
      p.ops.push(op);
      drawOp(p.bctx, op);
      ctx.input('draw', { s: cur.s, c: cur.c, w: cur.w, p: cur.p });
      const lx = cur.p[cur.p.length - 2], ly = cur.p[cur.p.length - 1];
      cur.p = [lx, ly];
      cur.fresh = false;
    }
    if (final) p.cur = null;
    paintSoon(root);
  }

  function bindCanvas(root) {
    const el = root.querySelector('.tpcanvas');
    if (!el || el._bound) return;
    el._bound = true;
    const p = pad(root);

    el.addEventListener('pointerdown', (e) => {
      const ctx = root._ctx;
      if (!ctx || !canDraw(ctx)) return;
      e.preventDefault();
      const [x, y] = point(el, e);
      if (p.tool === 'fill') {
        const op = [1, p.stroke++, p.color, 0, x, y];
        p.ops.push(op);
        drawOp(p.bctx, op);
        ctx.input('fill', { s: op[1], c: op[2], x, y });
        paintSoon(root);
        return;
      }
      try { el.setPointerCapture(e.pointerId); } catch { /* старі браузери */ }
      const eraser = p.tool === 'eraser';
      p.cur = { s: p.stroke++, c: eraser ? 0 : p.color, w: SIZES[p.size] * (eraser ? 2 : 1), p: [x, y], fresh: true };
      paintSoon(root);
    });

    el.addEventListener('pointermove', (e) => {
      if (!p.cur) return;
      const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
      for (const ev of events.length ? events : [e]) {
        const [x, y] = point(el, ev);
        const q = p.cur.p;
        const dx = x - q[q.length - 2], dy = y - q[q.length - 1];
        if (dx * dx + dy * dy < 4) continue;
        q.push(x, y);
      }
      if (p.cur.p.length / 2 >= CHUNK_MAX) flush(root, false);
      else if (!p.flushTimer) p.flushTimer = setTimeout(() => flush(root, false), FLUSH_MS);
      paintSoon(root);
    });

    const end = () => { if (p.cur) flush(root, true); };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
    el.addEventListener('lostpointercapture', end);
    new ResizeObserver(() => paintSoon(root)).observe(el);
  }

  function undo(root) {
    const p = pad(root);
    const ctx = root._ctx;
    if (!ctx || !canDraw(ctx)) return;
    if (p.cur) flush(root, true);
    if (!p.ops.length) return;
    const s = p.ops[p.ops.length - 1][1];
    while (p.ops.length && p.ops[p.ops.length - 1][1] === s) p.ops.pop();
    rebuild(p);
    ctx.input('undo');
    paintSoon(root);
  }

  function tools(root) {
    const p = pad(root);
    const bar = root.querySelector('.tptools');
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
        const ctx = root._ctx;
        if (!b || !ctx) return;
        if (b.dataset.c != null) { p.color = +b.dataset.c; if (p.tool === 'eraser') p.tool = 'pen'; }
        else if (b.dataset.z != null) p.size = +b.dataset.z;
        else if (b.dataset.t) p.tool = b.dataset.t;
        else if (b.dataset.a === 'undo') undo(root);
        else if (b.dataset.a === 'clear' && canDraw(ctx) && confirm('Стерти весь малюнок?')) {
          p.ops = [];
          p.cur = null;
          rebuild(p);
          ctx.input('clear');
          paintSoon(root);
        }
        marks(root);
      });
    }
    marks(root);
  }

  function marks(root) {
    const p = pad(root);
    root.querySelectorAll('.tptools .pcsw').forEach((b) => b.classList.toggle('on', +b.dataset.c === p.color && p.tool !== 'eraser'));
    root.querySelectorAll('.tptools .pcsz').forEach((b) => b.classList.toggle('on', +b.dataset.z === p.size));
    root.querySelectorAll('.tptools .pct').forEach((b) => b.classList.toggle('on', b.dataset.t === p.tool));
    const el = root.querySelector('.tpcanvas');
    if (el) el.dataset.tool = p.tool;
  }

  // =========================================================================================
  // Розмітка фаз
  // =========================================================================================

  const nick = (ctx, i) => ctx.esc(ctx.nickOf(i) || 'хтось');

  function timer(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.pctime');
    if (!box) return;
    const live = ctx.playing && v.phase === 'step';
    box.style.visibility = live ? 'visible' : 'hidden';
    if (!live) return;
    const left = Math.max(0, new Date(v.until).getTime() - Date.now());
    const bar = box.querySelector('i');
    bar.style.width = Math.max(0, Math.min(100, left / (v.totalMs || 1) * 100)) + '%';
    bar.classList.toggle('hot', left < 10000);
    const t = String(Math.ceil(left / 1000));
    const num = box.querySelector('.pcsec');
    if (num.textContent !== t) num.textContent = t;
  }

  function stepScreen(root, ctx, v) {
    const t = v.task;
    const title = !t ? (ctx.mine ? 'Чекаємо, поки всі здадуть…' : 'Гравці працюють…')
      : t.kind === 'phrase' ? 'Напиши фразу, яку намалює сусід'
        : t.kind === 'draw' ? 'Намалюй це'
          : 'Опиши, що бачиш на малюнку';
    const key = [v.step, t ? t.kind + ':' + t.chain : 'none'].join('|');
    const body = root.querySelector('.tpbody');

    if (body.dataset.key !== key) {
      body.dataset.key = key;
      const p = pad(root);
      let html = '<div class="tptitle">' + ctx.esc(title) + '</div>';
      if (t && t.prompt && t.prompt.kind === 'text') html += '<div class="tpprompt">«' + ctx.esc(t.prompt.text) + '»</div>';
      if (t && t.prompt && t.prompt.kind === 'drawing') html += '<canvas class="tpshow big"></canvas>';
      if (t && t.kind === 'draw') html += '<canvas class="tpcanvas"></canvas><div class="tptools"></div>';
      if (t && t.kind !== 'draw') {
        html += '<form class="tpform"><input type="text" maxlength="80" autocomplete="off" spellcheck="false" enterkeyhint="done" placeholder="'
          + (t.kind === 'phrase' ? 'кіт їде на велосипеді…' : 'що це таке?') + '">'
          + (t.kind === 'phrase' ? '<button type="button" class="ghost tproll" title="Підкинути ідею">🎲</button>' : '')
          + '</form>';
      }
      if (t) html += '<div class="tpact"></div>';
      html += '<div class="tpwho"></div>';
      body.innerHTML = html;

      if (t && t.kind === 'draw') {
        // Нове завдання — чисте полотно. Після F5 посеред кроку беремо копію з сервера.
        p.ops = (t.ops || []).map((o) => o.slice());
        p.stroke = p.ops.reduce((m, o) => Math.max(m, o[1]), 0) + 1;
        p.cur = null;
        rebuild(p);
        bindCanvas(root);
        tools(root);
        paintSoon(root);
      }
      if (t && t.kind !== 'draw') {
        const input = body.querySelector('input');
        input.value = t.text || '';
        input.addEventListener('input', () => {
          clearTimeout(p.draftTimer);
          p.draftTimer = setTimeout(() => { const c = root._ctx; if (c) c.input('text', { text: input.value }); }, DRAFT_MS);
        });
        body.querySelector('form').onsubmit = (e) => { e.preventDefault(); submit(root); };
        const roll = body.querySelector('.tproll');
        if (roll) roll.onclick = () => {
          const c = root._ctx;
          const ideas = (c && c.view && c.view.task && c.view.task.ideas) || [];
          if (!ideas.length) return;
          input.value = ideas[p.idea++ % ideas.length];
          input.dispatchEvent(new Event('input'));
        };
      }
      if (t && t.prompt && t.prompt.kind === 'drawing') {
        const el = body.querySelector('.tpshow');
        requestAnimationFrame(() => show(el, t.prompt.ops));
        new ResizeObserver(() => show(el, t.prompt.ops)).observe(el);
      }
    }

    // Те, що міняється без нового завдання: здано / змінити, хто ще працює.
    if (t) {
      const act = body.querySelector('.tpact');
      const html = t.ready
        ? '<span class="tpok">✅ Здано</span><button type="button" class="ghost" data-do="edit">Змінити</button>'
        : '<button type="button" class="primary" data-do="done">Готово</button>';
      if (act.innerHTML !== html) {
        act.innerHTML = html;
        act.querySelectorAll('[data-do]').forEach((b) => b.onclick = () => {
          if (b.dataset.do === 'done') submit(root);
          else { const c = root._ctx; if (c) c.act('edit'); }
        });
      }
      const input = body.querySelector('.tpform input');
      if (input) input.disabled = !!t.ready;
      const tb = body.querySelector('.tptools');
      if (tb) tb.hidden = !!t.ready;
      const cv = body.querySelector('.tpcanvas');
      if (cv) cv.classList.toggle('can', !t.ready);
    }
    const waiting = (v.waiting || []).map((i) => nick(ctx, i));
    const who = waiting.length ? 'Ще працюють: ' + waiting.join(', ') : 'Усі здали — зараз далі';
    const whoEl = body.querySelector('.tpwho');
    if (whoEl.innerHTML !== who) whoEl.innerHTML = who;
  }

  function submit(root) {
    const ctx = root._ctx;
    const t = ctx && ctx.view && ctx.view.task;
    if (!t || t.ready) return;
    const p = pad(root);
    if (t.kind === 'draw') {
      if (p.cur) flush(root, true);
      ctx.act('done', { n: p.ops.length }).then((r) => {
        // сервер отримав не все — довіряємо його копії, людина подивиться й здасть ще раз
        if (r && !r.ok && /не цілком/.test(r.message || '')) {
          const body = root.querySelector('.tpbody');
          if (body) body.dataset.key = '';
        }
      });
      return;
    }
    const input = root.querySelector('.tpform input');
    clearTimeout(p.draftTimer);
    ctx.act('done', { text: input ? input.value : '' });
  }

  function entryHtml(ctx, e, chain, last) {
    const who = '<div class="tpby">' + nick(ctx, e.seat) + (e.kind === 'drawing' ? ' малює:' : e.index === 0 ? ' починає:' : ' бачить:') + '</div>';
    const body = e.kind === 'drawing'
      ? '<canvas class="tpshow" data-chain="' + chain + '" data-index="' + e.index + '"></canvas>'
      : '<div class="tptext">«' + ctx.esc(e.text || '') + '»</div>';
    const own = e.seat === ctx.seat;
    const like = '<button type="button" class="tplike' + (e.liked ? ' on' : '') + '" data-chain="' + chain + '" data-index="' + e.index + '"'
      + (own || !ctx.mine || !ctx.playing ? ' disabled' : '') + '>❤ ' + (e.likes || 0) + '</button>';
    return '<div class="tpentry' + (last ? ' fresh' : '') + '">' + who + body + like + '</div>';
  }

  function revealScreen(root, ctx, v) {
    const r = v.reveal;
    const p = pad(root);
    if (r) p.seen.set(r.chain, { owner: r.owner, entries: r.entries });
    const body = root.querySelector('.tpbody');
    const key = 'reveal|' + (r ? r.chain + ':' + r.shown + ':' + r.entries.map((e) => e.likes + (e.liked ? 'y' : 'n')).join(',') : '');
    if (body.dataset.key === key) return;
    const scrollToEnd = !r || body.dataset.chain !== String(r.chain) || +body.dataset.shown < r.shown;
    body.dataset.key = key;
    if (!r) { body.innerHTML = ''; return; }
    body.dataset.chain = String(r.chain);
    body.dataset.shown = String(r.shown);
    const more = r.shown < r.total ? 'Далі ▸' : r.no < r.chains ? 'Наступний ланцюжок ▸' : 'Підсумки ▸';
    body.innerHTML = '<div class="tptitle">Ланцюжок ' + r.no + ' з ' + r.chains + ' · від ' + nick(ctx, r.owner) + '</div>'
      + '<div class="tpchain">' + r.entries.map((e, i) => entryHtml(ctx, e, r.chain, i === r.entries.length - 1)).join('') + '</div>'
      + (ctx.mine ? '<div class="tpact"><button type="button" class="primary" data-do="next">' + more + '</button></div>'
        : '<div class="tpwho">Гравці гортають ланцюжок</div>');
    wireChain(root, body, r.entries, r.chain);
    const next = body.querySelector('[data-do="next"]');
    if (next) next.onclick = () => { const c = root._ctx; if (c) c.act('next'); };
    if (scrollToEnd) {
      const last = body.querySelector('.tpentry.fresh');
      if (last) requestAnimationFrame(() => last.scrollIntoView({ block: 'nearest', behavior: 'smooth' }));
    }
  }

  function wireChain(root, body, entries, chain) {
    body.querySelectorAll('canvas.tpshow').forEach((el) => {
      const e = entries.find((x) => x.index === +el.dataset.index);
      if (!e) return;
      requestAnimationFrame(() => show(el, e.ops));
    });
    body.querySelectorAll('.tplike').forEach((b) => b.onclick = () => {
      const c = root._ctx;
      if (c) c.act('like', { chain, index: +b.dataset.index });
    });
  }

  function doneScreen(root, ctx, v) {
    const p = pad(root);
    const body = root.querySelector('.tpbody');
    const likes = v.likes || [];
    const rows = [];
    for (let i = 0; i < (ctx.room && ctx.room.seats ? ctx.room.seats.length : 10); i++) if (ctx.nickOf(i)) rows.push({ i, n: likes[i] || 0 });
    rows.sort((a, b) => b.n - a.n);
    const chains = [...p.seen.keys()].sort((a, b) => a - b);
    const pick = p.album != null && p.seen.has(p.album) ? p.album : chains[0];
    const key = 'done|' + likes.join(',') + '|' + pick + '|' + chains.join(',');
    if (body.dataset.key === key) return;
    body.dataset.key = key;
    const medal = ['🥇', '🥈', '🥉'];
    let html = '<div class="tptitle">Партію зіграно</div><div class="pcfinal">'
      + rows.map((r, k) => '<div><span>' + (medal[k] || (k + 1) + '.') + ' ' + nick(ctx, r.i) + '</span><b>❤ ' + r.n + '</b></div>').join('')
      + '</div>';
    if (chains.length) {
      html += '<div class="tpalbum">' + chains.map((c) => '<button type="button" class="' + (c === pick ? 'primary' : 'ghost') + '" data-album="' + c + '">'
        + nick(ctx, p.seen.get(c).owner) + '</button>').join('') + '</div>';
      const ch = p.seen.get(pick);
      html += '<div class="tpchain">' + ch.entries.map((e) => entryHtml(ctx, e, pick, false)).join('') + '</div>';
    }
    body.innerHTML = html;
    body.querySelectorAll('[data-album]').forEach((b) => b.onclick = () => { p.album = +b.dataset.album; const c = root._ctx; if (c) doneScreen(root, c, c.view || {}); });
    if (chains.length) wireChain(root, body, p.seen.get(pick).entries, pick);
  }

  function render(root, ctx) {
    root._ctx = ctx;
    ctx.tpRoot = root;
    const v = ctx.view || {};
    const head = root.querySelector('.tphead');
    const text = v.phase === 'step' ? 'Крок ' + v.step + ' з ' + v.steps
      : v.phase === 'reveal' ? 'Показ' : v.phase === 'done' ? 'Альбом' : '';
    if (head.textContent !== text) head.textContent = text;
    if (v.phase === 'step') stepScreen(root, ctx, v);
    else if (v.phase === 'reveal') revealScreen(root, ctx, v);
    else if (v.phase === 'done') doneScreen(root, ctx, v);
    timer(root, ctx);
  }

  HGames.register({
    id: 'telephone',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      root.innerHTML = '<div class="tpwrap">'
        + '<div class="tptop"><div class="tphead muted small"></div><div class="pctime"><i></i><span class="pcsec"></span></div></div>'
        + '<div class="tpbody"></div></div>';
      const p = pad(root);
      p.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 250);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    unmount(root) {
      const p = root._tp;
      if (!p) return;
      clearInterval(p.timer);
      clearTimeout(p.flushTimer);
      clearTimeout(p.draftTimer);
      if (p.raf) cancelAnimationFrame(p.raf);
    },

    onKey(e, ctx) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyZ' && ctx.tpRoot && canDraw(ctx)) { undo(ctx.tpRoot); return true; }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? 'Гортай ланцюжки — кнопки з іменами' : '';
      if (v.phase === 'reveal') return ctx.mine ? 'Став ❤ смішним записам і тисни «Далі»' : 'Дивишся збоку';
      if (!ctx.mine) return 'Дивишся збоку';
      const t = v.task;
      if (!t) return 'Чекаємо на інших';
      if (t.ready) return 'Здано — чекаємо на інших';
      return t.kind === 'phrase' ? 'Пиши фразу' : t.kind === 'draw' ? 'Малюй!' : 'Опиши малюнок';
    },
  });
})();
