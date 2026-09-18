/*
  Танчики. Реалтайм: сервер тикає раз на 40 мс і шле кадр, ми його малюємо й згладжуємо.

  Кадр (Impl/Tanks.cs):
    { t, p: [{x, y, d, alive, shield, frags, reload, back}], s: [{i, x, y, d}], bricks: int[], phase, startIn, left }
  Координати танків (лівий верхній кут) і снарядів (точка) — у дванадцятих частках клітинки (SUB).
  Сталь не міняється за партію, тому її шле лише вид: { width, height, sub, walls, need, ... }.

  Ввід: Input('move', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору, -1 стоп (напрямок «тримають»);
  Input('fire') — постріл у напрямку дула.
*/
(() => {
  // Розмір поля каже вид (width/height): до чотирьох — 21×15, на п'ятьох-шістьох — 27×19.
  const PX = 22, SUB = 12, TICK_MS = 40;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="2" y="5" width="9" height="8" rx="2" fill="var(--accent)"/>'
    + '<rect x="9" y="8" width="6" height="2" fill="var(--clay)"/>'
    + '<rect x="1" y="4" width="11" height="2" fill="var(--clay)"/><rect x="1" y="12" width="11" height="2" fill="var(--clay)"/></svg>';

  const DIRS = {
    ArrowRight: 0, KeyD: 0, d: 0, в: 0,
    ArrowDown: 1, KeyS: 1, s: 1, і: 1,
    ArrowLeft: 2, KeyA: 2, a: 2, ф: 2,
    ArrowUp: 3, KeyW: 3, w: 3, ц: 3,
  };
  const dirOf = (e) => {
    const byCode = DIRS[e.code];
    if (byCode !== undefined) return byCode;
    return DIRS[String(e.key || '').toLowerCase()];
  };
  const isFire = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const DELTA = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  const SEATS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'], ['--tblue', '#6fb3e8'], ['--tpink', '#e88ac0']];
  const BOOM_MS = 380;

  const at = (cell, W) => [(cell % W) * PX, Math.floor(cell / W) * PX];
  const lerp = (a, b, t) => a + (b - a) * t;
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function palette(st) {
    return {
      bg2: st.css('--bg2', '#16291f'),
      steel: st.css('--panel3', '#2b4c3c'),
      edge: st.css('--line', '#2f4d3d'),
      clay: st.css('--clay', '#c5763a'),
      mortar: st.css('--tmortar', '#8f5527'),
      dark: st.css('--tdark', '#0d1a13'),
      accent: st.css('--accent', '#f4c542'),
      danger: st.css('--danger', '#e57373'),
      text: st.css('--text', '#ecf1ea'),
      shade: st.css('--gshade', 'rgba(15, 31, 24, .62)'),
      seats: SEATS.map(([name, fallback]) => st.css(name, fallback)),
    };
  }

  function drawWalls(pal, g, walls, W) {
    for (const cell of walls) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.steel;
      g.fillRect(x, y, PX, PX);
      g.fillStyle = pal.edge;
      g.fillRect(x, y, PX, 2);
      g.fillRect(x, y, 2, PX);
    }
  }

  function drawBricks(pal, g, cells, W) {
    for (const cell of cells || []) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.clay;
      g.fillRect(x + 1, y + 1, PX - 2, PX - 2);
      // кладка: два ряди, шов зі зсувом — щоб цегла читалась цеглою
      g.strokeStyle = pal.mortar;
      g.lineWidth = 1.4;
      g.beginPath();
      g.moveTo(x + 1, y + PX / 2); g.lineTo(x + PX - 1, y + PX / 2);
      g.moveTo(x + PX / 2, y + 1); g.lineTo(x + PX / 2, y + PX / 2);
      g.moveTo(x + PX / 4, y + PX / 2); g.lineTo(x + PX / 4, y + PX - 1);
      g.moveTo(x + 3 * PX / 4, y + PX / 2); g.lineTo(x + 3 * PX / 4, y + PX - 1);
      g.stroke();
    }
  }

  function drawTank(pal, g, m, color, now) {
    const px = (m.x / SUB) * PX, py = (m.y / SUB) * PX;
    const cx = px + PX / 2, cy = py + PX / 2;
    const [dx, dy] = DELTA[m.d] || DELTA[0];
    const horizontal = dy === 0;
    // гусениці — дві смуги по боках, перпендикулярно до руху
    g.fillStyle = pal.dark;
    if (horizontal) { g.fillRect(px + 2, py + 1, PX - 4, 5); g.fillRect(px + 2, py + PX - 6, PX - 4, 5); }
    else { g.fillRect(px + 1, py + 2, 5, PX - 4); g.fillRect(px + PX - 6, py + 2, 5, PX - 4); }
    // корпус
    g.fillStyle = color;
    g.beginPath();
    g.roundRect(px + 3, py + 3, PX - 6, PX - 6, 3);
    g.fill();
    // башта й дуло
    g.fillStyle = pal.dark;
    g.beginPath();
    g.arc(cx, cy, PX * 0.2, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = pal.dark;
    g.lineWidth = 3;
    g.lineCap = 'round';
    g.beginPath();
    g.moveTo(cx, cy);
    g.lineTo(cx + dx * PX * 0.55, cy + dy * PX * 0.55);
    g.stroke();
    if (m.shield > 0) {
      g.strokeStyle = pal.accent;
      g.lineWidth = 2;
      g.globalAlpha = 0.55 + 0.35 * Math.sin(now / 90);
      g.beginPath();
      g.arc(cx, cy, PX * 0.62, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    }
  }

  function drawShells(pal, g, shells) {
    for (const s of shells || []) {
      const x = (s.x / SUB) * PX, y = (s.y / SUB) * PX;
      const [dx, dy] = DELTA[s.d] || DELTA[0];
      g.strokeStyle = pal.accent;
      g.lineWidth = 2;
      g.globalAlpha = 0.5;
      g.beginPath();
      g.moveTo(x - dx * 7, y - dy * 7);
      g.lineTo(x, y);
      g.stroke();
      g.globalAlpha = 1;
      g.fillStyle = pal.text;
      g.beginPath();
      g.arc(x, y, 2.6, 0, Math.PI * 2);
      g.fill();
    }
  }

  function drawBooms(pal, g, booms, now) {
    for (const b of booms) {
      const k = (now - b.at) / BOOM_MS;
      if (k >= 1) continue;
      const r = PX * (0.3 + 0.9 * k);
      g.globalAlpha = 1 - k;
      g.fillStyle = pal.danger;
      g.beginPath();
      g.arc(b.x, b.y, r, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = pal.accent;
      g.beginPath();
      g.arc(b.x, b.y, r * 0.55, 0, Math.PI * 2);
      g.fill();
      g.globalAlpha = 1;
    }
  }

  function drawShade(pal, g, c, f, waiting) {
    if (f.phase === 'go' || (f.phase === 'start' && waiting)) return;
    g.fillStyle = pal.shade;
    g.fillRect(0, 0, c.w, c.h);
    if (f.phase !== 'start' || waiting) return;
    g.fillStyle = pal.text;
    g.font = '700 46px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
  }

  /// Кадри йдуть 25 разів на секунду — між ними танки й снаряди ведемо лінійно. Снаряди — за id:
  /// «третій у списку» після пострілу чи влучання — це вже інший снаряд.
  function shot(st) {
    const cur = st.interp.at();
    const f = (cur && cur.b) || st.last;
    if (!f) return null;
    const a = cur && cur.a;
    const smooth = a && a !== f && a.p && a.t <= f.t && a.phase === f.phase;
    if (!smooth) return { f, men: f.p || [], shells: f.s || [] };
    const k = cur.t;
    const was = new Map((a.s || []).map((s) => [s.i, s]));
    return {
      f,
      men: (f.p || []).map((m, i) => {
        const w = a.p[i];
        return !w || !w.alive || !m.alive ? m : Object.assign({}, m, { x: lerp(w.x, m.x, k), y: lerp(w.y, m.y, k) });
      }),
      shells: (f.s || []).map((s) => {
        const w = was.get(s.i);
        return !w ? s : Object.assign({}, s, { x: lerp(w.x, s.x, k), y: lerp(w.y, s.y, k) });
      }),
    };
  }

  function draw(st, waiting) {
    const c = st.cv;
    if (!c) return;
    const now = performance.now();
    const cur = shot(st);
    const g = c.ctx;
    const pal = palette(st);
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, c.w, c.h);
    drawWalls(pal, g, st.walls, st.W);
    if (!cur) return;
    const f = cur.f;
    drawBricks(pal, g, f.bricks, st.W);
    for (let i = 0; i < cur.men.length; i++) {
      const m = cur.men[i];
      if (m && m.alive) drawTank(pal, g, m, pal.seats[i] || SEATS[i][1], now);
    }
    drawShells(pal, g, cur.shells);
    drawBooms(pal, g, st.booms, now);
    drawShade(pal, g, c, f, waiting);
  }

  // ---------------------------------------------------------------------------------------------
  // Рядок над полем і керування пальцем
  // ---------------------------------------------------------------------------------------------

  function hud(root, ctx, f) {
    let el = root.querySelector(':scope > .thud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'thud';
      root.insertBefore(el, root.firstChild);
    }
    const men = (f && f.p) || [];
    let html = '';
    for (let i = 0; i < 6; i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const m = men[i] || {};
      html += '<span class="tchip s' + i + (m.alive === false ? ' out' : '') + '">'
        + ctx.esc(nick) + ' <b>' + (m.frags || 0) + '</b>' + (m.back > 0 ? ' <span class="tback">⌛</span>' : '') + '</span>';
    }
    if (f && f.phase === 'go') html += '<span class="tchip tclock">' + clock(f.left || 0) + '</span>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .tpad');
    if (!ctx.mine) {
      if (el) el.remove();
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'tpad';
      const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
      const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
      el.innerHTML = '<div class="tdirs">'
        + [3, 2, 0, 1].map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('')
        + '</div><button type="button" class="tfire" aria-label="постріл">💥</button>';
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        e.preventDefault();
        if (b.classList.contains('tfire')) { el._tanksCtx.input('fire'); return; }
        try { b.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер */ }
        st.pid = e.pointerId;
        st.held = +b.dataset.dir;
        el._tanksCtx.input('move', { dir: st.held });
      });
      const release = (e) => {
        if (st.pid !== e.pointerId) return;
        st.pid = null;
        if (st.held < 0) return;
        st.held = -1;
        el._tanksCtx.input('move', { dir: -1 });
      };
      el.addEventListener('pointerup', release);
      el.addEventListener('pointercancel', release);
      root.appendChild(el);
    }
    el._tanksCtx = ctx;
  }

  // ---------------------------------------------------------------------------------------------
  // Модуль
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    if (!root._tanks) {
      root._tanks = {
        cv: null, walls: [], last: null, held: -1, pid: null, fireDown: false, booms: [],
        raf: 0, keyup: null, phase: '', css: ctx.css, W: 21, H: 15,
      };
    }
    root._tanks.ctx = ctx;
    ctx._tanks = root._tanks;
    return root._tanks;
  }

  /// Підбиття бачимо як alive true→false між сусідніми кадрами — і малюємо спалах самі.
  function noteBooms(st, f) {
    const was = st.last;
    if (!was || !was.p || !f || !f.p) return;
    const now = performance.now();
    for (let i = 0; i < f.p.length; i++) {
      const a = was.p[i], b = f.p[i];
      if (a && a.alive && b && !b.alive && b.back > 0)
        st.booms.push({ x: (a.x / SUB) * PX + PX / 2, y: (a.y / SUB) * PX + PX / 2, at: now });
    }
    st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
  }

  function syncHeld(st) {
    const phase = (st.last && st.last.phase) || '';
    if (phase === 'go' && st.phase !== 'go' && st.held >= 0 && st.ctx && st.ctx.mine && st.ctx.playing)
      st.ctx.input('move', { dir: st.held });
    st.phase = phase;
  }

  function spin(st) {
    if (st.raf) return;
    const loop = () => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (st.cv.el.offsetParent) draw(st, !(st.ctx && st.ctx.playing));
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'tanks',
    icon: ICON,
    seatNames: ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий'],
    seatClass: ['x', 'o', 'c', 'd', 'tb', 'tp'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.interp = HGames.ui.Interp();
      st.cv = HGames.ui.canvas(root, { w: st.W * PX, h: st.H * PX, cls: 'tboard' });
      st.keyup = (e) => {
        if (isFire(e)) { st.fireDown = false; return; }
        if (dirOf(e) !== st.held || st.held < 0) return;
        st.held = -1;
        if (st.ctx && st.ctx.mine && st.ctx.playing) st.ctx.input('move', { dir: -1 });
      };
      document.addEventListener('keyup', st.keyup);
      if (ctx.mine && ctx.playing) ctx.input('move', { dir: -1 });
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv) return;
      const v = ctx.view;
      // На «Почати» мапа може стати більшою (шестеро) чи знову звичною (Ще раз учотирьох) — канвас за нею.
      if (v && v.width && v.height && (v.width !== st.W || v.height !== st.H)) {
        st.W = v.width;
        st.H = v.height;
        st.cv = HGames.ui.canvas(root, { w: st.W * PX, h: st.H * PX, cls: 'tboard' });
        st.interp.reset();
        st.booms = [];
      }
      if (v && v.walls && v.walls.length) st.walls = v.walls;
      if (v && v.p) {
        const jump = !st.last || v.t < (st.last.t || 0) || (st.last.bricks && v.bricks && st.last.bricks.length < v.bricks.length);
        noteBooms(st, v);
        st.last = v;
        if (jump) st.interp.reset();
        st.interp.push(v);
      }
      pad(root, ctx, st);
      hud(root, ctx, st.last);
      syncHeld(st);
      st.cv.resize();
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv || !f) return;
      noteBooms(st, f);
      st.last = f;
      st.interp.push(f);
      hud(root, ctx, f);
      syncHeld(st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = ctx._tanks;
      if (!st || !ctx.mine || !ctx.playing) return false;
      if (isFire(e)) {
        if (!st.fireDown) { st.fireDown = true; ctx.input('fire'); }
        return true;
      }
      const dir = dirOf(e);
      if (dir === undefined) return false;
      if (st.held !== dir) { st.held = dir; ctx.input('move', { dir }); }
      return true;
    },

    status(ctx) {
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f || !f.phase) return '';
      if (f.phase === 'start') return 'Готуйсь…';
      if (f.phase === 'over') return '';
      const need = (ctx.view && ctx.view.need) || 5;
      const how = HGames.ui.coarse() ? 'Хрестовина — їхати, 💥 — постріл' : 'Стрілки або WASD, пробіл — постріл';
      return (ctx.mine ? how : 'Дивишся збоку') + ' · до ' + need + ' фрагів';
    },

    unmount(root) {
      const st = root._tanks;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      root._tanks = null;
    },
  });
})();
