/*
  Ремесло Гончарного кола (docs/games/specs/clicker-v7.md, пакет A). Частина ядра clicker.js.

  Що тут:
  1) силуети дванадцяти виробів (api.wareSvg) — ними малюють коло, полицю, комору, альбом, горно й цех;
  2) виріб на колі, що росте від кліків: грудка → центрування → відкривання → витягування → форма. Сервер рахує
     роботу (view.craft.work/need), клієнт лише передбачає її між видами: + кліки, що ще не полетіли, + підмайстри;
  3) сирці на полиці над колом (view.craft.rack): мокрі темніші, висохлі світлі;
  4) рядок ремесла під сценою: що ліпиться, скільки лишилось, сушарня — і вибір виробу (Act('form'));
  5) вкладка «Комора»: вироби з горна, базар (Act('bazaar')).
*/
(() => {
  const PROGRESS_STEPS = 48;              // стільки разів за виріб перемальовуємо силует на колі — не щокадру

  // ---------- силуети ----------

  /// Профільні вироби: півширина на висотах [y, w] від дна (y = 86) до вінець. Центр — x = 50.
  const PROFILE = {
    pot: [[86, 12], [83, 18], [74, 23], [62, 23], [53, 19], [48, 16], [45, 17.5], [43, 18]],
    bowl: [[86, 10], [84, 15], [80, 24], [75, 31], [71, 34], [69, 34.5]],
    jug: [[86, 11], [81, 17], [70, 21], [58, 19], [49, 11], [42, 7.5], [36, 7], [32, 8], [30, 9]],
    makitra: [[86, 11], [81, 17], [72, 24], [63, 29], [58, 31], [55, 33], [53, 33]],
    dish: [[86, 12], [85, 20], [82, 31], [79, 37], [77, 39]],
    candle: [[86, 20], [83, 19], [81, 6], [66, 4.5], [60, 5], [56, 12], [52, 13], [50, 12]],
    barrel: [[86, 12], [80, 19], [70, 24], [58, 24], [48, 20], [41, 12], [38, 6], [33, 5.5], [31, 6.5]],
  };
  /// Фігурні вироби: готовий силует — окремий шлях; поки ліпиться — округла «заготовка» того ж розміру.
  const FIGURE = {
    whistle: {
      path: 'M36 86l2-9h24l2 9zM34 76c-6-10-2-24 10-27 0-8 5-14 12-14 5 0 8 4 8 9l7 2-7 3c1 7-2 12-7 15 6-3 12-10 14-19 7 8 6 23-4 29-4 2-9 2-13 2z',
      stand: [[86, 14], [78, 18], [66, 20], [54, 16], [44, 10], [38, 7]],
    },
    tile: {
      path: 'M22 28h56v58H22z',
      stand: [[86, 26], [70, 27], [50, 27], [32, 26], [28, 25]],
    },
    kumanets: {
      path: 'M39 86l3-9h16l3 9zM50 27a25 25 0 1 1-.01 0zM50 42a10 10 0 1 0 .01 0zM45 17h10v11H45zM66 33l10-9 4 4-10 9z',
      evenodd: true,
      stand: [[86, 12], [78, 20], [64, 25], [50, 24], [38, 16], [30, 8], [22, 6]],
    },
    ram: {
      path: 'M32 86V74h7v12zM61 86V74h7v12zM22 60c0-13 9-18 22-18h16c9 0 14 4 17 10l9-2c-1 9-6 13-11 14 0 8-7 12-14 12H36c-9 0-14-6-14-16z',
      stand: [[86, 20], [76, 24], [64, 26], [52, 22], [44, 16], [40, 10]],
    },
    lion: {
      path: 'M30 86V61c0-11 6-17 12-20-5-4-7-9-7-14 0-9 7-15 15-15s15 6 15 15c0 5-2 10-7 14 6 3 12 9 12 20v25zM70 80c8-2 12-8 10-16 4 3 5 10 0 16z',
      stand: [[86, 20], [72, 22], [58, 20], [44, 14], [32, 15], [18, 12]],
    },
  };

  /// Деталі поверх силуету готового виробу: ручки, носики, отвори, рельєф.
  const DETAIL = {
    pot: '<path d="M32 50q-6 2-5 7M68 50q6 2 5 7" stroke="rgba(0,0,0,.32)" stroke-width="2.4" fill="none" stroke-linecap="round"/>',
    jug: '<path d="M58 40c11-2 15 6 12 14-2 5-6 7-10 8" stroke="var(--clkw-body)" stroke-width="4" fill="none" stroke-linecap="round"/>'
      + '<path d="M58 40c11-2 15 6 12 14-2 5-6 7-10 8" stroke="rgba(0,0,0,.25)" stroke-width="1" fill="none"/>',
    makitra: '<path d="M18 53h64" stroke="rgba(0,0,0,.28)" stroke-width="2.2"/>',
    candle: '<ellipse cx="50" cy="50.5" rx="12" ry="2.4" fill="rgba(0,0,0,.3)"/><path d="M50 36v12" stroke="#f4efe3" stroke-width="2.2"/>'
      + '<path class="clkw-flame" d="M50 27c-3 3-3 7 0 9 3-2 3-6 0-9z" fill="#f4c542"/>',
    barrel: '<path d="M27 62h46M29 74h42" stroke="rgba(0,0,0,.26)" stroke-width="1.6"/><path d="M64 44l9-5 2 3-8 6z" fill="var(--clkw-body)" stroke="rgba(0,0,0,.3)" stroke-width=".8"/>',
    tile: '<rect x="28" y="34" width="44" height="46" rx="2" fill="none" stroke="rgba(0,0,0,.3)" stroke-width="2"/>'
      + '<circle cx="50" cy="57" r="9" fill="none" stroke="rgba(0,0,0,.25)" stroke-width="1.6"/>',
    kumanets: '<circle cx="50" cy="52" r="10" fill="none" stroke="rgba(0,0,0,.35)" stroke-width="1.2"/>',
    whistle: '<path d="M60 38l3-5 2 5 3-4 1 5" fill="#c62f25"/><circle cx="60" cy="45" r="1.3" fill="#1b1310"/>',
    ram: '<path d="M68 52c6-4 11 1 8 6-2 3-6 2-6-1" stroke="rgba(0,0,0,.4)" stroke-width="2" fill="none"/><circle cx="78" cy="55" r="1.1" fill="#1b1310"/>'
      + '<path d="M30 56c3-3 6-3 8 0M40 52c3-3 6-3 8 0M50 56c3-3 6-3 8 0" stroke="rgba(255,255,255,.28)" stroke-width="1.4" fill="none"/>',
    lion: '<circle cx="44" cy="26" r="1.6" fill="#1b1310"/><circle cx="56" cy="26" r="1.6" fill="#1b1310"/><path d="M46 34q4 3 8 0" stroke="#1b1310" stroke-width="1.2" fill="none"/>'
      + '<path d="M50 14c-10 0-17 6-17 14M50 14c10 0 17 6 17 14" stroke="rgba(0,0,0,.25)" stroke-width="3" fill="none"/>',
  };

  /// Гладкий силует з профілю: права сторона знизу вгору, ліва — дзеркально, вінця — плоскі.
  function profilePath(pts) {
    const r = pts.map(([y, w]) => [50 + w, y]);
    const l = pts.slice().reverse().map(([y, w]) => [50 - w, y]);
    const smooth = (arr) => {
      let d = '';
      for (let i = 1; i < arr.length; i++) {
        const [x0, y0] = arr[i - 1];
        const [x1, y1] = arr[i];
        d += ' Q' + x0.toFixed(1) + ' ' + y0.toFixed(1) + ' ' + ((x0 + x1) / 2).toFixed(1) + ' ' + ((y0 + y1) / 2).toFixed(1);
      }
      const last = arr[arr.length - 1];
      return d + ' L' + last[0].toFixed(1) + ' ' + last[1].toFixed(1);
    };
    return 'M50 ' + pts[0][0] + ' L' + r[0][0].toFixed(1) + ' ' + r[0][1] + smooth(r) + ' L' + l[0][0].toFixed(1) + ' ' + l[0][1].toFixed(1)
      + smooth(l) + ' Z';
  }

  /// Півширина профілю на частці висоти t (0 — дно, 1 — вінця), лінійно між точками.
  function widthAt(pts, t) {
    const bottom = pts[0][0];
    const top = pts[pts.length - 1][0];
    const y = bottom - t * (bottom - top);
    for (let i = 1; i < pts.length; i++) {
      const [ya, wa] = pts[i - 1];
      const [yb, wb] = pts[i];
      if (y <= ya && y >= yb) return wa + (wb - wa) * ((ya - y) / (ya - yb || 1));
    }
    return pts[pts.length - 1][1];
  }

  /// Профіль будь-якої висоти й форми з N точок: так грудку, циліндр і готовий виріб можна змішувати.
  function resample(fn, height, n = 10) {
    const out = [];
    for (let i = 0; i < n; i++) {
      const t = i / (n - 1);
      out.push([86 - t * height, fn(t)]);
    }
    return out;
  }

  const targetOf = (ware) => PROFILE[ware] || (FIGURE[ware] && FIGURE[ware].stand) || PROFILE.pot;
  const heightOf = (pts) => pts[0][0] - pts[pts.length - 1][0];

  /// Форма на колі за часткою роботи p (0…1): грудка → центрування → відкривання → витягування → форма.
  function formingProfile(ware, p) {
    const target = targetOf(ware);
    const H = heightOf(target);
    const avg = target.reduce((s, [, w]) => s + w, 0) / target.length;
    const lump = (t, s) => Math.max(0.5, 17 * s * Math.sqrt(Math.max(0, 1 - t * t * 0.92)));
    if (p < 0.2) {
      const s = 0.75 + p * 1.25;
      return { pts: resample((t) => lump(t, s), 16 * s), open: false };
    }
    if (p < 0.42) {
      const k = (p - 0.2) / 0.22;
      const h = 16 + 6 * k;
      return { pts: resample((t) => lump(t, 1) * (1 - k) + 15 * k, h), open: k > 0.3 };
    }
    if (p < 0.7) {
      const k = (p - 0.42) / 0.28;
      const h = 22 + (H * 0.92 - 22) * k;
      const w = 15 + (avg - 15) * k;
      return { pts: resample(() => w, h), open: true };
    }
    const k = Math.min(1, (p - 0.7) / 0.3);
    const h = H * 0.92 + H * 0.08 * k;
    const w = 15 + (avg - 15);
    return { pts: resample((t) => w * (1 - k) + widthAt(target, t) * k, h), open: true };
  }

  /// Відблиск і сяйво за якістю: дзвінкий — золота обводка й іскорка, добрий — м'який полиск.
  function qualityMarks(q) {
    if (q >= 3) return '<path d="M36 44c-2 6-2 16 1 22" stroke="rgba(255,244,200,.55)" stroke-width="2.4" fill="none" stroke-linecap="round"/>'
      + '<path d="M77 20l2 5 5 2-5 2-2 5-2-5-5-2 5-2z" fill="#ffe28a"/>';
    if (q === 2) return '<path d="M37 46c-2 5-2 13 1 18" stroke="rgba(255,255,255,.35)" stroke-width="2" fill="none" stroke-linecap="round"/>';
    return '';
  }

  /// SVG виробу. opts: style (розпис), clay (колір глини для простого/сирця), quality (0 — сирець), raw (сирець:
  /// без розпису й полиску), dry (висохлий сирець світліший), progress (0…1 — ще ліпиться), slot (стале id clipPath),
  /// cls, wrap=false (лише вміст для чужого <svg>).
  function wareSvg(api, ware, o) {
    o = o || {};
    const STYLE = api.STYLE;
    const style = o.raw ? '' : (o.style || '');
    const s = STYLE[style] || STYLE[''];
    const clay = o.clay || '';
    const body = style ? s.body : (clay || 'var(--clay)');
    const id = 'clkw-' + (o.slot || ware + '-' + style + '-' + (o.quality || 0) + (o.raw ? 'r' : '') + (o.dry ? 'd' : ''));
    const forming = o.progress != null && o.progress < 1;
    let shape;
    let evenodd = false;
    if (forming) {
      const f = formingProfile(ware, Math.max(0, o.progress));
      shape = profilePath(f.pts);
    } else if (FIGURE[ware]) {
      shape = FIGURE[ware].path;
      evenodd = !!FIGURE[ware].evenodd;
    } else {
      shape = profilePath(PROFILE[ware] || PROFILE.pot);
    }
    const rule = evenodd ? ' fill-rule="evenodd" clip-rule="evenodd"' : '';
    const wet = o.raw && !o.dry;
    let g = '<g class="clkw' + (o.raw ? ' raw' : '') + (wet ? ' wet' : '') + '" style="--clkw-body:' + body + '">'
      + '<clipPath id="' + id + '"><path d="' + shape + '"' + rule + '/></clipPath>'
      + '<path d="' + shape + '" fill="' + body + '"' + rule + '/>';
    if (!forming && !o.raw && style) {
      // Орнамент розписів намальований для глечика (пояс 36…62): для низьких і широких виробів зсуваємо його до пуза.
      const shift = PROFILE[ware] ? Math.round(86 - heightOf(PROFILE[ware]) * 0.55 - 49) : 0;
      g += '<g clip-path="url(#' + id + ')"><g transform="translate(0 ' + shift + ')' + (ware === 'bowl' || ware === 'dish' ? ' scale(1 .7) translate(0 32)' : '') + '">'
        + s.decor + '</g></g>';
    }
    if (forming) {
      const f = formingProfile(ware, Math.max(0, o.progress));
      const top = f.pts[f.pts.length - 1];
      if (f.open) g += '<ellipse cx="50" cy="' + top[0].toFixed(1) + '" rx="' + Math.max(1, top[1] - 2.5).toFixed(1) + '" ry="2.4" fill="rgba(0,0,0,.35)"/>';
      // Мокра глина блищить борознами від пальців.
      g += '<g clip-path="url(#' + id + ')" stroke="rgba(255,255,255,.14)" stroke-width="1" fill="none">'
        + '<path d="M20 80h60M20 72h60M20 64h60M20 56h60M20 48h60M20 40h60"/></g>';
    } else if (DETAIL[ware]) {
      g += DETAIL[ware];
    }
    g += '<path d="' + shape + '" fill="none" stroke="rgba(0,0,0,.3)" stroke-width="1"' + rule + '/>';
    if (!o.raw && !forming) g += qualityMarks(o.quality || 1);
    g += '</g>';
    if (o.wrap === false) return g;
    return '<svg class="' + (o.cls || '') + '" viewBox="10 8 80 82" aria-hidden="true">' + g + '</svg>';
  }

  // ---------- стан і числа ----------

  const styleName = (st, key) => {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const QUALITY = ['', 'звичайний', 'добрий', 'дзвінкий'];
  const STARS = ['', '★', '★★', '★★★'];

  function wareName(st, key) {
    const c = st.craft;
    const w = c && c.wares.find((x) => x.key === key);
    return w ? w.name : key;
  }

  /// Скільки роботи вже є просто зараз: серверне число + кліки, що ще не полетіли або летять, + підмайстри.
  function workNow(st) {
    const c = st.craft;
    if (!c) return 0;
    const clicks = st.hands.length + st.inflight;
    const idle = c.rackFull ? 0 : (c.apprentice * Math.max(0, Date.now() - st.craftAt)) / 1000;
    return c.work + clicks + idle;
  }

  // ---------- малювання ----------

  function paintWheel(st, api) {
    const c = st.craft;
    if (!c || !st.jugBox) return;
    const need = Math.max(1, c.need);
    let w = workNow(st);
    // Передбачили готовий виріб — ефект один раз, а на колі вже нова грудка (якщо сушарня має місце).
    const done = Math.floor(w / need);
    if (done > st.craftDone) {
      st.craftDone = done;
      if (st.craftRackFree > 0) {
        st.craftRackFree--;
        st.craftFxAt = Date.now();
        const name = wareName(st, c.ware);
        api.popAt(st, '🏺 ' + name.toLowerCase() + ' — на сушарню', 'big', 50, 30);
        api.sparks(st, st.fx, 10, false, 50, 62);
        api.sfx('done');
      }
    }
    const full = st.craftRackFree <= 0 && w >= need;
    const p = full ? 1 : (w % need) / need;
    const step = full ? PROGRESS_STEPS : Math.floor(p * PROGRESS_STEPS);
    const sig = c.ware + '|' + step + '|' + st.clayBody;
    if (st.jugBox._craft !== sig) {
      st.jugBox._craft = sig;
      // Виріб на колі: основа на центрі круга. Готовий (сушарня повна) — фінальний силует простого виробу.
      const inner = wareSvg(api, c.ware, {
        progress: full ? 1 : step / PROGRESS_STEPS, raw: true, dry: false, clay: st.clayBody, slot: 'wheel-craft', wrap: false,
      });
      st.jugBox.innerHTML = '<g transform="translate(50 69) scale(.66) translate(-50 -86)">' + inner + '</g>';
    }
    if (st.craftUi) {
      const pct = Math.round(p * 1000) / 10 + '%';
      if (st.craftUi.bar.style.width !== pct) st.craftUi.bar.style.width = pct;
      const txt = full ? 'сушарня повна — обпали сухе в горні' : Math.floor(w % need) + ' / ' + need;
      if (st.craftUi.work.textContent !== txt) st.craftUi.work.textContent = txt;
      st.craftUi.el.classList.toggle('full', full);
    }
  }

  /// Сирці на полиці над колом: до десяти, решта — «+N». Мокрі темніші; висохлі — світлі й чекають горна.
  function paintShelf(st, api) {
    const c = st.craft;
    if (!c || !st.shelfJugs) return;
    const now = api.serverNow(st);
    const shown = c.rack.slice(0, 10);
    const sig = shown.map((r) => r.ware + (r.clay || '') + (r.dryAt <= now ? 'd' : 'w')).join(',') + '|' + c.rack.length;
    if (st.shelfJugs._craft === sig) return;
    st.shelfJugs._craft = sig;
    let s = shown.map((r, i) => '<span class="clkw-rack' + (r.dryAt <= now ? ' dry' : '') + '" title="' + wareName(st, r.ware)
      + (r.dryAt <= now ? ' — сухий, чекає горна' : ' — сохне') + '">'
      + wareSvg(api, r.ware, { raw: true, dry: r.dryAt <= now, clay: clayBody(st, r.clay), slot: 'rack-' + i }) + '</span>').join('');
    if (c.rack.length > shown.length) s += '<span class="clkw-more">+' + (c.rack.length - shown.length) + '</span>';
    if (!c.rack.length) s = '<span class="clkw-empty">сушарня порожня</span>';
    st.shelfJugs.innerHTML = s;
  }

  const clayBody = (st, key) => {
    const c = (st.clays || []).find((x) => x.key === key);
    return (c && c.body) || '';
  };

  function paintBar(st, api) {
    const c = st.craft;
    const ui = st.craftUi;
    if (!c || !ui) return;
    const name = wareName(st, c.ware);
    const now = api.serverNow(st);
    const dry = c.rack.filter((r) => r.dryAt <= now).length;
    const html = api.wareSvg(c.ware, { cls: 'clkw-ico', slot: 'bar-ico', clay: st.clayBody }) + '<b>' + api.esc(st, name) + '</b><span>▾</span>';
    api.swap(ui.pick, html);
    const rack = '🧺 сушарня ' + c.rack.length + '/' + c.rackSize + (dry ? ' · сухих ' + dry : '')
      + (c.apprentice > 0 ? ' · підмайстри ліплять самі' : '');
    if (ui.rack.textContent !== rack) ui.rack.textContent = rack;
    ui.pick.disabled = !st.mine;
  }

  // ---------- вибір виробу ----------

  function openPicker(st, api) {
    const c = st.craft;
    if (!c || !st.mine) return;
    const cards = c.wares.map((w) => {
      const on = w.key === c.ware;
      const cat = st.catalog && st.catalog.wares && st.catalog.wares.find((x) => x.key === w.key);
      const sec = cat ? cat.seconds : 0;
      return '<button type="button" class="clkw-card' + (on ? ' on' : '') + (w.open ? '' : ' locked') + '" data-ware="' + api.esc(st, w.key) + '"'
        + (w.open && !on ? '' : ' disabled') + '>'
        + api.wareSvg(w.key, { cls: 'clkw-big', slot: 'pick-' + w.key, clay: st.clayBody, quality: 1 })
        + '<b>' + api.esc(st, w.name) + '</b>'
        + (w.open
          ? '<span class="muted small">робота ' + w.need + (sec ? ' · ~' + api.potsShort(w.value) : '') + '</span>'
            + '<span class="muted small">обпалено ' + api.num(w.fired) + '</span>'
            + (on ? '<span class="clk-price done">на колі</span>' : '<span class="clk-price">ліпити</span>')
          : '<span class="muted small">відкриється на ' + api.short(w.unlock) + ' глеків за весь час</span>')
        + '</button>';
    }).join('');
    const body = api.overlay(st, '<div class="clk-sub">Що ліпити на колі</div>'
      + '<p class="muted small clk-note">Кожен зарахований клік — одна робота; підмайстри ліплять і без тебе. Готовий виріб сохне на '
      + 'сушарні, а висохлий обпалюють у горні — тоді він з розписом і якістю ляже в комору. Дорожчий виріб довше ліпити, зате '
      + 'він вартий більше. Ціна — простого звичайного, розпис і якість її множать.</p>'
      + '<div class="clkw-grid">' + cards + '</div>', { cls: 'clkw-picker' });
    for (const b of body.querySelectorAll('[data-ware]')) {
      b.onclick = () => { api.order(st, 'form', { ware: b.dataset.ware }); api.closeOverlay(st); };
    }
  }

  // ---------- комора ----------

  function paintStore(st, api) {
    const c = st.craft;
    const pane = st.storePane;
    if (!c || !pane) return;
    const esc = (x) => api.esc(st, x);
    const total = c.items.reduce((s, it) => s + it.n, 0);
    const sum = c.items.reduce((s, it) => s + it.value * it.n, 0);
    const head = '<div class="clk-sub">Комора · ' + total + ' з ' + c.storeCap
      + '<span class="muted small"> · вироби з горна; що не влізе — одразу на базар</span></div>'
      + (total ? '<button type="button" class="primary clkw-sellall"' + (st.mine ? '' : ' disabled') + '>🧺 Усе на базар · +' + api.short(sum) + '</button>' : '');
    const items = total
      ? '<div class="clkw-items">' + c.items.map((it) => '<div class="clkw-item q' + it.q + '">'
        + api.wareSvg(it.ware, { style: it.style, quality: it.q, cls: 'clkw-mid', slot: 'st-' + it.key.replace(/\|/g, '-') })
        + '<div class="clkw-itxt"><b>' + esc(wareName(st, it.ware)) + ' <span class="clkw-n">×' + it.n + '</span></b>'
        + '<span class="muted small">' + esc(styleName(st, it.style)) + ' · <span class="clkw-q">' + STARS[it.q] + ' ' + QUALITY[it.q] + '</span></span>'
        + '<span class="small">по ' + api.potsShort(it.value) + '</span></div>'
        + '<div class="clkw-btns"><button type="button" class="ghost small" data-sell="' + esc(it.key) + '" data-n="1"' + (st.mine ? '' : ' disabled') + '>Продати</button>'
        + (it.n > 1 ? '<button type="button" class="ghost small" data-sell="' + esc(it.key) + '" data-n="' + it.n + '"' + (st.mine ? '' : ' disabled') + '>Усі ' + it.n + '</button>' : '')
        + '</div></div>').join('') + '</div>'
      : '<div class="clk-teaser muted small">Комора порожня. Шлях виробу: виліпи на колі → він висохне на сушарні → обпали у вкладці «Горно» → '
        + 'тут він чекатиме купців, базару, воза цеху чи дарунка другові.</div>';
    const now = api.serverNow(st);
    const rack = c.rack.length
      ? '<div class="clk-sub">Сушарня · ' + c.rack.length + ' з ' + c.rackSize + '</div><div class="clkw-rackrow">'
        + c.rack.map((r, i) => {
          const dry = r.dryAt <= now;
          return '<span class="clkw-rackitem' + (dry ? ' dry' : '') + '">'
            + wareSvg(api, r.ware, { raw: true, dry, clay: clayBody(st, r.clay), slot: 'strack-' + i, cls: 'clkw-mini' })
            + '<span class="small">' + (dry ? 'сухий' : '<span class="clk-cd" data-at="' + r.dryAt + '" data-done="сухий"></span>') + '</span></span>';
        }).join('') + '</div>'
      : '';
    if (api.swap(st.storeBody, head + items + rack)) {
      const all = st.storeBody.querySelector('.clkw-sellall');
      if (all) {
        let armed = 0;
        all.onclick = () => {
          // Усе одним натиском — питаємо двічі: дзвінкі й розписні теж поїдуть.
          if (!armed || Date.now() > armed) { armed = Date.now() + 3000; all.textContent = 'Точно все? Ще раз'; return; }
          api.order(st, 'bazaar', { all: true });
        };
      }
      for (const b of st.storeBody.querySelectorAll('[data-sell]')) b.onclick = () => api.order(st, 'bazaar', { key: b.dataset.sell, n: +b.dataset.n });
      st.storeCds = [...st.storeBody.querySelectorAll('.clk-cd')];
    }
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'craft',
    order: 10,

    mount(st, api) {
      api.wareSvg = (ware, o) => wareSvg(api, ware, o);
      st.craftWheel = true;
      st.craftShelf = true;
      if (st.jugBox) st.jugBox._wear = null;
      st.craft = null;
      st.craftAt = Date.now();
      st.craftDone = 0;
      st.craftRackFree = 0;
      st.craftRackLen = -1;
      st.craftFxAt = 0;
      const bar = document.createElement('div');
      bar.className = 'clk-craft';
      bar.innerHTML = '<button type="button" class="ghost clk-craft-pick" title="Що ліпити на колі"></button>'
        + '<div class="clk-craft-mid"><div class="clk-craft-bar"><i></i></div>'
        + '<div class="clk-craft-line small"><span class="clk-craft-work"></span><span class="clk-craft-rack muted"></span></div></div>';
      st.stage.insertAdjacentElement('afterend', bar);
      st.craftUi = { el: bar, pick: bar.querySelector('.clk-craft-pick'), bar: bar.querySelector('.clk-craft-bar i'),
        work: bar.querySelector('.clk-craft-work'), rack: bar.querySelector('.clk-craft-rack') };
      st.craftUi.pick.onclick = () => openPicker(st, api);
      st.storePane = api.tab(st, 'store', 'Комора', 40);
      st.storeBody = document.createElement('div');
      st.storeBody.className = 'clkw-store';
      st.storePane.appendChild(st.storeBody);
      st.storeCds = [];
    },

    update(st, v, api) {
      const c = v.craft;
      if (!c) return;
      st.craft = {
        ware: c.ware, work: c.work || 0, need: c.need || 1, apprentice: c.apprentice || 0, rackFull: !!c.rackFull,
        rack: (c.rack || []).map((r) => ({ ware: r.ware, clay: r.clay || '', dryAt: Date.parse(r.dryAt) || 0 })),
        rackSize: c.rackSize || 8, wares: c.wares || [], items: c.items || [], storeCap: c.storeCap || 200,
        formed: c.formed || 0, fired: c.fired || 0,
      };
      st.craftAt = Date.now();
      st.craftDone = 0;
      st.craftRackFree = st.craft.rackSize - st.craft.rack.length;
      // Сервер доліпив виріб сам (підмайстри, або наша пачка долетіла раніше, ніж ми передбачили) — ефект, якщо ми ще не малювали.
      if (st.craftRackLen >= 0 && st.craft.rack.length > st.craftRackLen && Date.now() - st.craftFxAt > 2500) {
        api.sparks(st, st.fx, 6, false, 50, 62);
      }
      st.craftRackLen = st.craft.rack.length;
      const count = st.craft.items.reduce((s, it) => s + it.n, 0);
      api.tabLabel(st, 'store', count ? 'Комора · ' + count : 'Комора');
      st.shelfJugs._craft = null;
      paintBar(st, api);
      paintStore(st, api);
    },

    frame(st, api) {
      if (api.guardOn(st)) return;
      paintWheel(st, api);
    },

    slow(st, api, now) {
      paintShelf(st, api);
      paintBar(st, api);
      if (st.tab === 'store') {
        for (const el of st.storeCds) {
          const left = +el.dataset.at - now;
          const t = left > 0 ? api.mmss(left) : el.dataset.done;
          if (el.textContent !== t) el.textContent = t;
        }
        // Сирець висох — переставити картку в «сухий».
        if (st.craft && st.craft.rack.some((r) => r.dryAt <= now) && st.storeBody._dry !== st.craft.rack.filter((r) => r.dryAt <= now).length) {
          st.storeBody._dry = st.craft.rack.filter((r) => r.dryAt <= now).length;
          paintStore(st, api);
        }
      }
    },

    unmount(st) {
      st.craftUi = null;
      st.storePane = null;
    },
  });
})();
