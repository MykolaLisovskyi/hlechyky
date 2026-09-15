/*
  Горно й розпис Гончарного кола (docs/games/specs/clicker-v7-kiln.md, пакет B2). Частина ядра clicker.js.

  Правила — на сервері (Impl/ClickerKiln.cs). Тут:
  1) вкладка «Горно»: велике SVG-горно (вироби в камері, полум'я, дим, заслінка), сухі сирці → горно (kiln/load),
     розпис партії, солома, «палити самому» / «хай підмайстер палить» (kiln/light);
  2) мінігра жару: та сама модель, що й KilnHeat на сервері, крок 100 мс, рядок у рядок та сама арифметика (лише + − × ÷,
     min/max; пориви й поліна — xorshift32 із зерна view.kiln.seed). Клієнт записує СВОЇ дії [мс від розпалу, дія] і після
     30 с шле їх разом на kiln/open — сервер проганяє модель сам. Таймлайн лежить у localStorage: F5 посеред обпалу не губить дій;
  3) мінігри розпису (overlay): ріжкування (коло крутиться), ритування (контур), фляндрування (штрихи через смуги),
     мармурування (краплі й закрутка), лощіння (натирати смуги). Точки — лише з isTrusted-подій, у полотні 1000×1000, пласким
     масивом [dt, x, y, …] (dt < 0 — початок штриха), бо кімната не бере payload понад 8 КБ. Красу рахує сервер (kiln/decor);
  4) відкриття горна — подія в overlay: вироби з'являються по одному, дзвінкі блищать, тріснуті розсипаються;
  5) світло й дим від печі в хаті (api.layer back, піч праворуч ~x 292–350, y 190–302).
  Звуки: kiln-light, kiln-roar (кожні 3 с, поки палає), stoke, damper, crack, ding, open, brush.
*/
(() => {
  /// Та сама модель, що й KilnHeat.cs, — на випадок, коли каталогу ще нема (сервер його шле першим видом).
  const MODEL = {
    stepMs: 100, steps: 300, warm: 80, maxActs: 80, amb: 20, fuel0: 1, log: 0.4, fuelMax: 2.6, chill: 15,
    burn: [0.045, 0.12], heat: [60, 240], loss: [0.12, 0.21], gust: 2,
    lo: 830, hi: 1010, hi0: 350, softUnder: 100, softOver: 60, crackFree: 300, crackScale: 3000, crackMax: 0.5,
  };
  const STOKE = 0, OPEN = 1, CLOSE = 2;
  const T_MAX = 1300;                       // верх термометра
  const OPEN_AFTER_MS = 350;                // після кінця обпалу — трохи зачекати, щоб серверне «зараз» точно дійшло
  const MAX_POINTS = 480;                   // сервер бере до 500
  const SAMPLE_MS = 24;                     // не частіше за стільки — інакше за 12 с упремось у стелю точок
  const TECH_ICON = { rizh: '🌀', flyand: '🌲', marble: '💧', ryt: '✒️', losk: '🪨' };
  const STARS = ['💥', '', '★', '★★★'];
  const QNAME = ['тріснув', 'звичайний', 'добрий', 'дзвінкий'];
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ---------- модель жару ----------

  function xorshift(seed) {
    let x = (seed >>> 0) || 0x9E3779B9;
    return () => {
      x ^= x << 13; x >>>= 0;
      x ^= x >>> 17; x >>>= 0;
      x ^= x << 5; x >>>= 0;
      return x;
    };
  }

  function gustsOf(seed, m) {
    const next = xorshift(seed);
    const list = [];
    let t = 40 + (next() % 30);
    while (t < 290) {
      const d = 15 + (next() % 16);
      list.push([t, Math.min(m.steps, t + d)]);
      t += d + 40 + (next() % 50);
    }
    return list;
  }

  function logsOf(seed, m) {
    const next = xorshift(((seed >>> 0) ^ 0x5bd1e995) >>> 0);
    const logs = [];
    for (let i = 0; i < m.maxActs; i++) logs.push(60 + (next() % 81));
    return logs;
  }

  function simNew(seed, m) {
    return { T: m.amb, F: m.fuel0, open: true, over: 0, score: 0, i: 0, j: 0, k: 0, g: 0, gusts: gustsOf(seed, m), logs: logsOf(seed, m) };
  }

  /// Один крок 100 мс. Порядок операцій — як у KilnHeat.Run, інакше double розійдеться в останньому знаку.
  function simStep(s, m, acts) {
    const i = s.i;
    while (s.j < acts.length && Math.floor(acts[s.j][0] / m.stepMs) <= i) {
      const a = acts[s.j][1];
      if (a === STOKE) {
        if (s.F < m.fuelMax && s.k < s.logs.length) {
          s.F = Math.min(m.fuelMax, s.F + m.log * s.logs[s.k] / 100);
          s.k++;
          s.T = Math.max(m.amb, s.T - m.chill);
        }
      } else if (a === OPEN) s.open = true;
      else if (a === CLOSE) s.open = false;
      s.j++;
    }
    while (s.g < s.gusts.length && s.gusts[s.g][1] <= i) s.g++;
    const gust = s.g < s.gusts.length && s.gusts[s.g][0] <= i;
    const loss = (s.open ? m.loss[1] : m.loss[0]) * (gust ? m.gust : 1);
    s.F -= s.F * (s.open ? m.burn[1] : m.burn[0]) * 0.1;
    s.T += (s.F * (s.open ? m.heat[1] : m.heat[0]) - (s.T - m.amb) * loss) * 0.1;
    const n = i + 1;
    const hi = n < m.warm ? m.hi0 + (m.hi - m.hi0) * n / m.warm : m.hi;
    if (s.T > hi) s.over += (s.T - hi) * 0.1;
    if (n >= m.warm) {
      if (s.T > m.hi + m.softOver) { /* перегрів — нуль */ }
      else if (s.T > m.hi) s.score += 1 - (s.T - m.hi) / m.softOver;
      else if (s.T >= m.lo) s.score += 1;
      else if (s.T >= m.lo - m.softUnder) s.score += (s.T - (m.lo - m.softUnder)) / m.softUnder;
    }
    s.gustNow = gust;
    s.i++;
  }

  /// Для перевірки в консолі: HClicker.kilnRun(12345, [[300,0],…]) → ті самі числа, що й тест KilnHeat на сервері.
  function simRun(seed, acts, m) {
    m = m || MODEL;
    const s = simNew(seed, m);
    while (s.i < m.steps) simStep(s, m, acts);
    return { heat: s.score / (m.steps - m.warm + 1), over: s.over, temp: s.T, logs: s.k };
  }
  HClicker.kilnRun = simRun;

  const crackOf = (over, m) => Math.max(0, Math.min(m.crackMax, (over - m.crackFree) / m.crackScale));

  // ---------- стан і дрібниці ----------

  const cat = (st) => (st.catalog && st.catalog.kiln) || null;
  const model = (st) => (cat(st) && cat(st).model) || MODEL;
  const burnMs = (st) => (cat(st) && cat(st).burnMs) || 30000;
  const techInfo = (st, key) => ((cat(st) && cat(st).techs) || []).find((t) => t.key === key) || { key, name: key, desc: '', unlock: '', home: '' };

  function wareName(st, key) {
    const w = st.craft && st.craft.wares && st.craft.wares.find((x) => x.key === key);
    return w ? w.name : key;
  }
  function styleName(st, key) {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key);
    return s ? s.name : key;
  }

  function loadTimeline(st, litAt) {
    try {
      const raw = JSON.parse(localStorage.getItem('clk.kiln.t') || 'null');
      if (raw && raw.at === litAt && Array.isArray(raw.t)) return raw.t.filter((a) => Array.isArray(a) && a.length === 2);
    } catch { /* зіпсований запис — починаємо з чистого */ }
    return [];
  }
  function saveTimeline(st) {
    try { localStorage.setItem('clk.kiln.t', JSON.stringify({ at: st.kb.litAt, seed: st.kb.seed, t: st.kb.acts })); } catch { /* приватне вікно */ }
  }

  // ---------- горно (SVG) ----------

  const FLAME = 'M0 0C-7-6-6-15-1-24c1 5 4 7 4 11 2-3 2-6 1-9 6 6 8 16 1 22z';

  function kilnSvg() {
    return '<svg class="clkk-svg" viewBox="0 -34 260 248" role="img" aria-label="Горно">'
      + '<defs>'
      + '<linearGradient id="clkk-clay" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#c47a4a"/><stop offset=".6" stop-color="#9c5634"/><stop offset="1" stop-color="#6e3a22"/></linearGradient>'
      + '<radialGradient id="clkk-fire" cx=".5" cy=".85" r=".8"><stop offset="0" stop-color="#fff2b0"/><stop offset=".35" stop-color="#ffb347"/><stop offset=".75" stop-color="#e2521d"/><stop offset="1" stop-color="#7a1d0c" stop-opacity="0"/></radialGradient>'
      + '<radialGradient id="clkk-halo" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#ff9a3c" stop-opacity=".55"/><stop offset="1" stop-color="#ff9a3c" stop-opacity="0"/></radialGradient>'
      + '<clipPath id="clkk-chamber"><path d="M76 150V110Q76 70 130 67Q184 70 184 110V150Z"/></clipPath>'
      + '</defs>'
      + '<ellipse class="clkk-halo" cx="130" cy="120" rx="128" ry="100" fill="url(#clkk-halo)"/>'
      + '<g class="clkk-smoke">' + [0, 1, 2, 3].map((i) => '<circle cx="190" cy="16" r="7" style="animation-delay:' + (i * 0.9) + 's"/>').join('') + '</g>'
      + '<rect x="178" y="10" width="24" height="44" rx="2" fill="#7b4228" stroke="#4a2615" stroke-width="1.5"/>'
      + '<rect x="175" y="6" width="30" height="7" rx="2" fill="#5d3019"/>'
      + '<g class="clkk-damper"><rect x="172" y="28" width="36" height="5" rx="2" fill="#39302b" stroke="#1d1714"/><circle cx="208" cy="30.5" r="3.4" fill="#caa46a"/></g>'
      + '<rect x="24" y="172" width="212" height="28" rx="5" fill="#6b3d25" stroke="#43240f" stroke-width="1.5"/>'
      + '<path d="M40 204h180" stroke="rgba(0,0,0,.35)" stroke-width="6" stroke-linecap="round"/>'
      + '<path d="M40 174V106Q40 30 130 26Q220 30 220 106V174Z" fill="url(#clkk-clay)" stroke="#4a2615" stroke-width="2"/>'
      + '<path class="clkk-bricks" d="M52 88Q130 40 208 88M44 122h172M44 150h172M70 122v28M100 150v24M160 150v24M190 122v28M130 122v28" fill="none" stroke="rgba(40,18,8,.35)" stroke-width="1.3"/>'
      + '<path d="M58 60Q90 34 130 32" fill="none" stroke="rgba(255,230,190,.25)" stroke-width="3" stroke-linecap="round"/>'
      + '<path d="M70 154V110Q70 64 130 61Q190 64 190 110V154Z" fill="#4a2615"/>'
      + '<path d="M76 150V110Q76 70 130 67Q184 70 184 110V150Z" fill="#1a100b"/>'
      + '<g clip-path="url(#clkk-chamber)"><rect class="clkk-glow" x="70" y="60" width="120" height="96" fill="url(#clkk-fire)" opacity="0"/>'
      + '<g class="clkk-inner-fl">' + [92, 118, 144, 168].map((x, i) => '<g transform="translate(' + x + ' 152) scale(1.3)"><path d="' + FLAME + '" style="animation-delay:-' + (i * 0.23) + 's"/></g>').join('') + '</g>'
      + '<g class="clkk-wares"></g></g>'
      + '<path d="M104 174V163Q104 150 130 149Q156 150 156 163V174Z" fill="#120a07" stroke="#4a2615" stroke-width="2"/>'
      + '<g class="clkk-flames"><g class="clkk-fuel">' + [118, 130, 142].map((x, i) => '<g transform="translate(' + x + ' 173)"><path d="' + FLAME + '" style="animation-delay:-' + (i * 0.31) + 's"/></g>').join('') + '</g></g>'
      + '<g class="clkk-sparks">' + [0, 1, 2, 3, 4].map((i) => '<circle cx="' + (118 + i * 6) + '" cy="160" r="1.4" style="animation-delay:' + (i * 0.37) + 's"/>').join('') + '</g>'
      + '<g class="clkk-straw" opacity="0"><path d="M50 172l10-14 6 14zM196 172l8-12 8 12z" fill="#d8b456"/><path d="M52 170l12-10M200 170l8-8" stroke="#a88630"/></g>'
      + '</svg>';
  }

  /// Вироби в камері горна: до 24, рядами по 8, знизу вгору.
  function paintWares(st, api, batch, lit) {
    const g = st.kUi.wares;
    const sig = batch.join(',') + '|' + (lit ? 1 : 0) + '|' + st.kView.style;
    if (g._sig === sig) return;
    g._sig = sig;
    const per = batch.length > 16 ? 8 : batch.length > 6 ? 6 : 4;
    const size = per === 8 ? 13 : per === 6 ? 17 : 24;
    let html = '';
    batch.slice(0, 24).forEach((w, i) => {
      const row = Math.floor(i / per);
      const col = i % per;
      const inRow = Math.min(per, batch.length - row * per);
      const x = 130 - (inRow * size) / 2 + col * size;
      const y = 148 - (row + 1) * size * 1.05;
      html += '<g transform="translate(' + x.toFixed(1) + ' ' + y.toFixed(1) + ') scale(' + (size / 82).toFixed(3) + ') translate(-10 -8)">'
        + api.wareSvg(w, { raw: true, dry: true, wrap: false, slot: 'kiln-' + i }) + '</g>';
    });
    g.innerHTML = html;
  }

  // ---------- вкладка ----------

  function mountTab(st, api) {
    const pane = api.tab(st, 'kiln', 'Горно', 30);
    pane.innerHTML = '<div class="clkk">'
      + '<div class="clkk-top"><div class="clkk-stage">' + kilnSvg() + '<div class="clkk-gust" hidden>💨 порив вітру</div></div>'
      + '<div class="clkk-gauge" hidden><div class="clkk-tube"><i class="clkk-band"></i><i class="clkk-mercury"></i><i class="clkk-mark"></i></div>'
      + '<b class="clkk-temp">20°</b><span class="clkk-trend small"></span></div></div>'
      + '<div class="clkk-status"><b class="clkk-state"></b><span class="clkk-clock muted small"></span></div>'
      + '<div class="clkk-bar" hidden><i></i></div>'
      + '<div class="clkk-play" hidden>'
      + '<div class="clkk-meters small"><span class="clkk-score"></span><span class="clkk-risk"></span><span class="clkk-next"></span></div>'
      + '<div class="clkk-btns"><button type="button" class="primary clkk-stoke">🪵 Поліно</button>'
      + '<button type="button" class="ghost clkk-damp">Заслінка</button></div>'
      + '<div class="muted small clkk-keys">Тримай жар у зеленій смузі. Відкрита заслінка — жаркіше, але дрова згоряють утричі швидше; '
      + 'прикрита душить вогонь. Клавіші: ↑ — поліно, ↓ — заслінка.</div></div>'
      + '<div class="clkk-prep"></div>'
      + '<div class="clkk-last"></div>'
      + '<details class="clkk-help small"><summary>Як це працює</summary>'
      + '<p>Виліплений виріб сохне на сушарні; сухий — у горно. Горно палиш сам (мінігра на пів хвилини: чим довше жар у зеленій смузі, '
      + 'тим більше добрих і дзвінких виробів) або доручаєш підмайстрові — тоді всі звичайні, зате без тріщин. Перегрів тріскає глину, '
      + 'солома в горні ділить цей ризик на чотири. Після відкриття горно холоне хвилину — завантажувати й розписувати можна й тоді.</p>'
      + '<p>Розпис лягає на всю партію. Техніка-мінігра дає «красу» 0–100: вона підвищує шанс доброї й дзвінкої якості (але не множить ціну). '
      + 'Дзвінкий виріб вартий ×2,6, добрий ×1,6. Косівське ритування й гаварецьке лощіння в «рідному» розписі дають +10 краси.</p></details>'
      + '</div>';
    const q = (s) => pane.querySelector(s);
    st.kUi = {
      pane, svg: q('.clkk-svg'), wares: q('.clkk-wares'), glow: q('.clkk-glow'), gust: q('.clkk-gust'),
      gauge: q('.clkk-gauge'), band: q('.clkk-band'), mercury: q('.clkk-mercury'), mark: q('.clkk-mark'), temp: q('.clkk-temp'), trend: q('.clkk-trend'),
      state: q('.clkk-state'), clock: q('.clkk-clock'), bar: q('.clkk-bar'), barFill: q('.clkk-bar i'),
      play: q('.clkk-play'), score: q('.clkk-score'), risk: q('.clkk-risk'), next: q('.clkk-next'),
      stoke: q('.clkk-stoke'), damp: q('.clkk-damp'), prep: q('.clkk-prep'), last: q('.clkk-last'), top: q('.clkk-top'),
    };
    st.kUi.stoke.addEventListener('pointerdown', (e) => { if (e.isTrusted && (e.pointerType !== 'mouse' || e.button === 0)) { e.preventDefault(); doAct(st, api, STOKE); } });
    st.kUi.damp.addEventListener('pointerdown', (e) => { if (e.isTrusted && (e.pointerType !== 'mouse' || e.button === 0)) { e.preventDefault(); doAct(st, api, st.kb && st.kb.sim.open ? CLOSE : OPEN); } });
    for (const b of [st.kUi.stoke, st.kUi.damp]) b.addEventListener('contextmenu', (e) => e.preventDefault());
    st.kKey = (e) => {
      if (!e.isTrusted || e.repeat || st.tab !== 'kiln' || !st.kb || api.overlayOpen(st)) return;
      const tag = (e.target && e.target.tagName) || '';
      if (tag === 'INPUT' || tag === 'TEXTAREA') return;
      if (e.code === 'ArrowUp' || e.code === 'KeyW') { e.preventDefault(); doAct(st, api, STOKE); }
      else if (e.code === 'ArrowDown' || e.code === 'KeyS') { e.preventDefault(); doAct(st, api, st.kb.sim.open ? CLOSE : OPEN); }
    };
    document.addEventListener('keydown', st.kKey);
  }

  // ---------- мінігра жару ----------

  /// Почати (або продовжити після F5) ручний обпал: модель із нуля до «зараз» разом із записаними діями.
  function burnBegin(st, api, k) {
    const litAt = Date.parse(k.litAt);
    if (st.kb && st.kb.litAt === litAt) return;
    const m = model(st);
    const acts = loadTimeline(st, litAt);
    st.kb = { litAt, seed: k.seed, acts, sim: simNew(k.seed, m), sentAt: 0, roarAt: 0, prevT: m.amb, done: false };
    api.sfx('kiln-light');
  }

  function burnEnd(st) {
    st.kb = null;
  }

  function doAct(st, api, a) {
    const kb = st.kb;
    if (!kb || kb.done || api.guardOn(st)) return;
    const m = model(st);
    let ms = Math.floor(api.serverNow(st) - kb.litAt);
    if (ms < 0) ms = 0;
    if (ms >= m.steps * m.stepMs) return;
    if (kb.acts.length >= m.maxActs) { api.toast(st, 'Досить метушні: за обпал — не більше ' + m.maxActs + ' дій', 'err'); return; }
    const last = kb.acts.length ? kb.acts[kb.acts.length - 1][0] : -1;
    if (ms <= last) ms = last + 1;
    // Дія, яку модель уже пройшла б (крок прорахували раніше, ніж натиск доїхав), лягає на наступний крок — як і на сервері.
    const minMs = kb.sim.i * m.stepMs;
    if (ms < minMs) ms = minMs;
    kb.acts.push([ms, a]);
    saveTimeline(st);
    if (a === STOKE) {
      api.sfx('stoke');
      const svg = st.kUi.svg;
      svg.classList.remove('stoked');
      void svg.getBoundingClientRect();
      svg.classList.add('stoked');
    } else api.sfx('damper');
    paintBurn(st, api, true);
  }

  /// Дорахувати модель до «зараз» і намалювати термометр, полум'я, пориви. Кличеться щокадру, поки палає.
  function paintBurn(st, api, force) {
    const kb = st.kb;
    if (!kb) return;
    const m = model(st);
    const elapsed = api.serverNow(st) - kb.litAt;
    const target = Math.max(0, Math.min(m.steps, Math.floor(elapsed / m.stepMs)));
    const before = kb.sim.i;
    while (kb.sim.i < target) {
      kb.prevT = kb.sim.T;
      simStep(kb.sim, m, kb.acts);
    }
    if (!force && kb.sim.i === before && st.kUi._burnPaintAt && Date.now() - st.kUi._burnPaintAt < 90) return;
    st.kUi._burnPaintAt = Date.now();
    const s = kb.sim;
    const ui = st.kUi;
    const n = Math.max(1, s.i);
    const hi = n < m.warm ? m.hi0 + (m.hi - m.hi0) * n / m.warm : m.hi;
    const lo = n < m.warm ? 0 : m.lo;
    const pct = (t) => Math.max(0, Math.min(100, (t / T_MAX) * 100));
    ui.band.style.bottom = pct(lo) + '%';
    ui.band.style.height = (pct(hi) - pct(lo)) + '%';
    ui.band.classList.toggle('warm', n < m.warm);
    ui.mercury.style.height = pct(s.T) + '%';
    ui.mark.style.bottom = pct(s.T) + '%';
    const hot = s.T > hi;
    const cold = n >= m.warm && s.T < m.lo;
    ui.gauge.classList.toggle('hot', hot);
    ui.gauge.classList.toggle('cold', cold);
    ui.gauge.classList.toggle('ok', !hot && !cold);
    const t = Math.round(s.T) + '°';
    if (ui.temp.textContent !== t) ui.temp.textContent = t;
    const d = s.T - kb.prevT;
    const trend = d > 2 ? '▲' : d < -2 ? '▼' : '•';
    if (ui.trend.textContent !== trend) ui.trend.textContent = trend;
    // Полум'я й світло камери — від жару й дров.
    const glow = Math.max(0, Math.min(1, (s.T - 150) / 950));
    ui.svg.style.setProperty('--clkk-heat', glow.toFixed(3));
    ui.svg.style.setProperty('--clkk-fuel', Math.max(0.35, Math.min(1.5, 0.45 + s.F * 0.4)).toFixed(3));
    ui.svg.classList.toggle('closed', !s.open);
    ui.glow.setAttribute('opacity', (0.2 + glow * 0.8).toFixed(2));
    ui.gust.hidden = !s.gustNow;
    const damp = s.open ? 'Заслінка: відкрита ↓' : 'Заслінка: прикрита ↑';
    if (ui.damp.textContent !== damp) ui.damp.textContent = damp;
    const inBand = s.i > m.warm ? Math.round((s.score / (s.i - m.warm + 1)) * 100) : null;
    const score = inBand == null ? '🔥 розігрів: не перегрій' : '🟩 у смузі ' + inBand + ' %';
    if (ui.score.textContent !== score) ui.score.textContent = score;
    const risk = crackOf(s.over, m) * ((st.kView && st.kView.strawOn) ? (cat(st) ? cat(st).strawCrack : 0.25) : 1);
    const riskText = risk > 0 ? '💥 тріщини ' + Math.round(risk * 100) + ' %' : '';
    if (ui.risk.textContent !== riskText) ui.risk.textContent = riskText;
    const size = s.logs[s.k];
    const nextText = s.F >= m.fuelMax - 0.05 ? 'топка повна' : size == null ? '' : 'наступне поліно: ' + (size >= 115 ? 'товсте' : size <= 85 ? 'тонке' : 'середнє');
    if (ui.next.textContent !== nextText) ui.next.textContent = nextText;
    const left = Math.max(0, m.steps * m.stepMs - elapsed);
    const clock = '⏳ ' + api.mmss(left);
    if (ui.clock.textContent !== clock) ui.clock.textContent = clock;
    ui.barFill.style.width = Math.min(100, (elapsed / (m.steps * m.stepMs)) * 100).toFixed(1) + '%';
    ui.barFill.parentElement.classList.toggle('hot', hot);
    if (Date.now() - kb.roarAt > 3000 && elapsed < m.steps * m.stepMs) { kb.roarAt = Date.now(); api.sfx('kiln-roar'); }
    // Кінець обпалу: відкриваємо самі. Відмову «ще палає» (пінг) повторюємо; майстер хоче глянути — чекаємо, поки пропустить.
    if (elapsed >= m.steps * m.stepMs + OPEN_AFTER_MS && !api.guardOn(st) && Date.now() - kb.sentAt > 2500) {
      kb.sentAt = Date.now();
      kb.done = true;
      const acts = kb.acts.slice(0, m.maxActs);
      api.act(st, 'kiln', { op: 'open', t: acts }).then((r) => {
        if (st.kb === kb && (!r || !r.ok)) kb.done = false;
        if (st.kb === kb && r && r.ok) kb.done = false;          // «майстер хоче глянути»: вид покаже, чи горно ще палає
      });
    }
  }

  // ---------- підготовка партії ----------

  function paintPrep(st, api) {
    const k = st.kView;
    const ui = st.kUi;
    if (!k || !ui) return;
    const esc = (x) => api.esc(st, x);
    const mine = st.mine;
    const burning = k.state === 'burning';
    const cooling = k.state === 'cooling';
    if (burning) { api.swap(ui.prep, ''); return; }
    const free = k.slots - k.batch.length;
    const counts = {};
    for (const w of k.batch) counts[w] = (counts[w] || 0) + 1;
    const batchText = k.batch.length
      ? Object.keys(counts).map((w) => esc(wareName(st, w)).toLowerCase() + ' ×' + counts[w]).join(', ')
      : 'порожнє';
    const load = '<div class="clkk-row"><div><b>У горні ' + k.batch.length + ' з ' + k.slots + '</b><div class="muted small">' + batchText + '</div></div>'
      + '<button type="button" class="ghost clkk-load"' + (mine && free > 0 && k.dry > 0 ? '' : ' disabled') + '>🧱 Скласти сухі'
      + (k.dry > 0 ? ' · ' + Math.min(k.dry, Math.max(0, free)) : '') + '</button></div>'
      + (k.dry === 0 && !k.batch.length ? '<div class="muted small clkk-hint">Сухих сирців нема: виліпи виріб на колі й дай йому висохнути на сушарні (півтори хвилини).</div>' : '');

    // Розпис: простий + колекція; техніки — відкриті й замкнені.
    const owned = (st.styleList || []).filter((s) => s.owned);
    const styles = '<div class="clkk-chips">' + [{ key: '', name: 'Простий' }].concat(owned).map((s) => '<button type="button" class="clkk-chip'
      + (s.key === k.style ? ' on' : '') + '" data-style="' + esc(s.key) + '"' + (mine ? '' : ' disabled') + '>'
      + api.jugSvg(s.key, 'clkk-chipjug', 'kst-' + (s.key || 'plain')) + '<span>' + esc(s.name) + '</span></button>').join('') + '</div>';
    const allTechs = (cat(st) && cat(st).techs) || [];
    const techs = '<div class="clkk-techs">' + allTechs.map((t) => {
      const open = k.techs.includes(t.key);
      const home = t.home && t.home === k.style;
      return '<button type="button" class="clkk-tech' + (open ? '' : ' locked') + (k.tech === t.key ? ' on' : '') + '" data-tech="' + esc(t.key) + '"'
        + (open && mine ? '' : ' disabled') + ' title="' + esc(open ? t.desc : 'Відкриється ' + t.unlock) + '">'
        + '<span class="clkk-ticon">' + (open ? TECH_ICON[t.key] || '🎨' : '🔒') + '</span><b>' + esc(t.name) + '</b>'
        + '<span class="muted small">' + (open ? (home ? 'рідна техніка +10' : 'мінігра') : esc(t.unlock)) + '</span></button>';
    }).join('') + '</div>';
    const beauty = k.beauty > 0
      ? '<div class="clkk-beauty"><span>Краса розпису</span><i style="--b:' + k.beauty + '%"></i><b>' + k.beauty + '</b></div>'
      : '<div class="muted small">Краса 0 — розпис ляже й так, але мінігра підвищує шанс добрих і дзвінких.</div>';
    const paint = '<div class="clk-sub">🎨 Розпис партії · ' + esc(styleName(st, k.style)) + '</div>' + styles + techs + beauty;

    const price = k.strawPrice;
    const strawOn = api.storeGet('clk.kiln.straw', '1') === '1';
    const straw = '<div class="clkk-row"><div><b>🌾 Солома: ' + k.straw + '</b><div class="muted small">в\'язка в горні — тріщин учетверо менше</div></div>'
      + '<div class="clkk-strawbtns"><label class="small"><input type="checkbox" class="clkk-strawon"' + (strawOn ? ' checked' : '') + (k.straw > 0 ? '' : ' disabled') + '> класти</label>'
      + '<button type="button" class="ghost small clkk-buystraw"' + (mine && k.straw < 20 ? '' : ' disabled') + '>+1 · ' + esc(api.potsShort(price)) + '</button></div></div>';

    const can = mine && !cooling && (k.batch.length > 0 || k.dry > 0);
    const light = '<div class="clkk-fire">'
      + '<button type="button" class="primary clkk-light"' + (can ? '' : ' disabled') + '>🔥 Палити самому</button>'
      + '<button type="button" class="ghost clkk-helper"' + (can ? '' : ' disabled') + '>🧑‍🏭 Хай підмайстер палить</button></div>'
      + (cooling ? '<div class="muted small">Горно ще гаряче — розпалити можна, як вихолоне. Складати й розписувати — вже.</div>' : '');

    const html = load + paint + '<div class="clk-sub">Обпал</div>' + straw + light;
    if (!api.swap(ui.prep, html)) return;
    const b = (s) => ui.prep.querySelector(s);
    if (b('.clkk-load')) b('.clkk-load').onclick = () => api.order(st, 'kiln', { op: 'load' });
    for (const el of ui.prep.querySelectorAll('[data-style]')) el.onclick = () => api.order(st, 'kiln', { op: 'paint', style: el.dataset.style });
    for (const el of ui.prep.querySelectorAll('[data-tech]')) el.onclick = () => startPaint(st, api, el.dataset.tech);
    b('.clkk-buystraw').onclick = () => api.order(st, 'kiln', { op: 'straw', n: 1 });
    b('.clkk-strawon').onchange = (e) => api.storeSet('clk.kiln.straw', e.target.checked ? '1' : '0');
    b('.clkk-light').onclick = () => {
      const useStraw = st.kView.straw > 0 && api.storeGet('clk.kiln.straw', '1') === '1';
      api.act(st, 'kiln', { op: 'light', straw: useStraw }).then((r) => { if (r && r.ok) api.showTab(st, 'kiln'); });
    };
    b('.clkk-helper').onclick = () => api.order(st, 'kiln', { op: 'light', helper: true });
  }

  function paintLast(st, api) {
    const k = st.kView;
    const ui = st.kUi;
    const l = k && k.last;
    if (!l || k.state === 'burning') { api.swap(ui.last, ''); return; }
    const cnt = [0, 0, 0, 0];
    for (const it of l.items) cnt[it[1]]++;
    const html = '<div class="clkk-lastbox"><div class="clk-sub">Останнє горно</div><div class="clkk-lastitems">'
      + l.items.slice(0, 24).map((it, i) => '<span class="clkk-li q' + it[1] + '" title="' + api.esc(st, wareName(st, it[0]) + ' — ' + QNAME[it[1]]) + '">'
        + (it[1] === 0 ? shardSvg() : api.wareSvg(it[0], { style: l.style, quality: it[1], slot: 'kl-' + i, cls: 'clkk-lisvg' })) + '</span>').join('')
      + '</div><div class="small">' + summary(cnt) + '</div>'
      + '<div class="muted small">' + (l.helper ? 'палив підмайстер' : 'жар у смузі ' + l.heat + ' %' + (l.beauty ? ' · краса ' + l.beauty : '') + (l.straw ? ' · солома' : ''))
      + (l.shards ? ' · черепки +' + api.potsShort(l.shards) : '') + (l.sold ? ' · базар +' + api.potsShort(l.sold) : '') + '</div>'
      + '<button type="button" class="ghost small clkk-replay">Показати відкриття</button></div>';
    if (api.swap(ui.last, html)) ui.last.querySelector('.clkk-replay').onclick = () => reveal(st, api, l, true);
  }

  function summary(cnt) {
    const parts = [];
    if (cnt[3]) parts.push('★ ' + cnt[3] + ' ' + HClicker.api.plural(cnt[3], 'дзвінкий', 'дзвінкі', 'дзвінких'));
    if (cnt[2]) parts.push(cnt[2] + ' ' + HClicker.api.plural(cnt[2], 'добрий', 'добрі', 'добрих'));
    if (cnt[1]) parts.push(cnt[1] + ' ' + HClicker.api.plural(cnt[1], 'звичайний', 'звичайні', 'звичайних'));
    if (cnt[0]) parts.push('💥 ' + cnt[0] + ' ' + HClicker.api.plural(cnt[0], 'тріснув', 'тріснули', 'тріснуло'));
    return parts.join(' · ');
  }

  const shardSvg = () => '<svg viewBox="0 0 40 40" class="clkk-shard" aria-hidden="true"><path d="M6 30l8-12 5 6 6-10 9 16z" fill="#9c5634" stroke="rgba(0,0,0,.4)"/>'
    + '<path d="M10 34l6-4 4 4z" fill="#7a4028"/></svg>';

  // ---------- стан горна ----------

  function paintState(st, api, now) {
    const k = st.kView;
    const ui = st.kUi;
    if (!k || !ui) return;
    const m = model(st);
    const burning = k.state === 'burning';
    const manual = burning && !k.helper;
    let label;
    let clock = '';
    let barPct = null;
    if (burning && k.helper) {
      const left = Date.parse(k.litAt) + burnMs(st) - now;
      label = '🔥 Підмайстер палить горно';
      clock = '⏳ ' + api.mmss(left);
      barPct = 100 - Math.max(0, Math.min(100, (left / burnMs(st)) * 100));
      ui.svg.style.setProperty('--clkk-heat', '0.75');
      ui.svg.style.setProperty('--clkk-fuel', '1');
      ui.glow.setAttribute('opacity', '.8');
      if (Date.now() - (st.kRoarAt || 0) > 3000 && left > 0) { st.kRoarAt = Date.now(); api.sfx('kiln-roar'); }
    } else if (manual) {
      label = '🔥 Горно палає — тримай жар!';
    } else if (k.state === 'cooling') {
      const left = Date.parse(k.coolUntil) - now;
      label = '♨ Горно холоне';
      clock = api.mmss(left);
      const c = Math.max(0, Math.min(1, left / ((cat(st) && cat(st).coolMs) || 60000)));
      ui.svg.style.setProperty('--clkk-heat', (c * 0.45).toFixed(3));
      ui.glow.setAttribute('opacity', (c * 0.5).toFixed(2));
    } else {
      label = k.state === 'loaded' ? '🧱 Горно завантажене — можна палити' : '❄ Горно холодне';
      ui.svg.style.setProperty('--clkk-heat', '0');
      ui.glow.setAttribute('opacity', '0');
    }
    if (ui.state.textContent !== label) ui.state.textContent = label;
    if (!manual && ui.clock.textContent !== clock) ui.clock.textContent = clock;
    ui.bar.hidden = !(manual || barPct != null);
    if (barPct != null) ui.barFill.style.width = barPct.toFixed(1) + '%';
    ui.play.hidden = !manual || !st.mine;
    ui.gauge.hidden = !manual;
    ui.top.classList.toggle('playing', manual);
    ui.svg.classList.toggle('burning', burning);
    ui.svg.classList.toggle('cooling', k.state === 'cooling');
    ui.svg.classList.toggle('helper', burning && !!k.helper);
    ui.svg.querySelector('.clkk-straw').setAttribute('opacity', burning && k.strawOn ? '1' : '0');
    if (!manual) { ui.gust.hidden = true; ui.svg.classList.remove('closed'); }
    paintWares(st, api, k.batch, burning);
    // Вкладка: стан одним поглядом.
    let tab = 'Горно';
    if (burning) tab = 'Горно · 🔥 ' + api.mmss(Date.parse(k.litAt) + burnMs(st) - now);
    else if (k.state === 'cooling') tab = 'Горно · ♨';
    else if (k.batch.length) tab = 'Горно · ' + k.batch.length + '/' + k.slots;
    else if (k.dry) tab = 'Горно · сухих ' + k.dry;
    api.tabLabel(st, 'kiln', tab);
    paintScene(st, api, k, m);
  }

  /// Світло й дим від печі в хаті: піч праворуч ~x 292–350, y 190–302 (координати сцени 360×396).
  function paintScene(st, api, k) {
    const mode = k.state === 'burning' ? 'burn' : k.state === 'cooling' ? 'cool' : '';
    if (st.kScene === mode) return;
    st.kScene = mode;
    const g = api.layer(st, 'back', 'kiln');
    g.setAttribute('class', 'clkk-house ' + mode);
    g.innerHTML = mode
      ? '<ellipse class="clkk-hglow" cx="321" cy="262" rx="52" ry="46" fill="url(#clkk-hhalo)"/>'
        + '<defs><radialGradient id="clkk-hhalo"><stop offset="0" stop-color="#ffab4a" stop-opacity=".75"/><stop offset="1" stop-color="#ff7a2a" stop-opacity="0"/></radialGradient></defs>'
        + '<g class="clkk-hsmoke">' + [0, 1, 2].map((i) => '<circle cx="' + (314 + i * 7) + '" cy="186" r="6" style="animation-delay:' + (i * 1.1) + 's"/>').join('') + '</g>'
      : '';
  }

  // ---------- розпис (мінігри) ----------

  function startPaint(st, api, tech) {
    const k = st.kView;
    if (!k || !st.mine) return;
    st.kPaintWant = tech;
    api.act(st, 'kiln', { op: 'paint', style: k.style, tech }).then((r) => { if (!r || !r.ok) st.kPaintWant = null; });
  }

  /// Прийшов вид із новим візерунком, який ми самі попросили, — відкрити мінігру.
  function maybeOpenPaint(st, api) {
    const k = st.kView;
    if (!k || !k.pattern || !st.kPaintWant || k.pattern.tech !== st.kPaintWant) return;
    const at = k.pattern.at;
    if (st.kPaintAt === at) return;
    st.kPaintAt = at;
    st.kPaintWant = null;
    openPaint(st, api, k.pattern);
  }

  const PLATE = (fill, stroke) => '<circle cx="500" cy="500" r="470" fill="' + fill + '" stroke="' + stroke + '" stroke-width="14"/>';

  function paintScene2(p, st) {
    const s = p.shape;
    const style = api0().STYLE[st.kView.style] || api0().STYLE[''];
    const body = st.kView.style ? style.body : '#b8693f';
    switch (p.tech) {
      case 'rizh': {
        let d = '';
        for (let i = 0; i <= 180; i++) {
          const th = (i / 180) * Math.PI * 2;
          const r = s.r0 + s.amp * Math.sin(s.k * th + (s.phase * Math.PI) / 180);
          d += (i ? 'L' : 'M') + (500 + r * Math.cos(th)).toFixed(1) + ' ' + (500 + r * Math.sin(th)).toFixed(1);
        }
        return '<g class="clkk-disc">' + PLATE(body, '#4a2615') + '<circle cx="500" cy="500" r="90" fill="rgba(0,0,0,.12)"/>'
          + '<path d="' + d + '" class="clkk-guide"/><g class="clkk-trail"></g><circle cx="500" cy="60" r="10" fill="#f4ead6"/></g>'
          + '<g class="clkk-horn" transform="translate(500 ' + (500 - s.r0) + ')"><path d="M-12-150l24 0-6 120h-12z" fill="#e7d7b4" stroke="#6b4a2a" stroke-width="4"/>'
          + '<circle r="9" fill="#fff" opacity=".7"/></g>';
      }
      case 'ryt': {
        let d = '';
        for (let i = 0; i <= 200; i++) {
          const th = (i / 200) * Math.PI * 2;
          const r = s.r0 + s.amp * Math.cos(s.k * th + (s.phase * Math.PI) / 180);
          d += (i ? 'L' : 'M') + (500 + r * Math.cos(th)).toFixed(1) + ' ' + (500 + r * Math.sin(th)).toFixed(1);
        }
        return PLATE('#8b3a22', '#4a2615') + '<circle cx="500" cy="500" r="440" fill="#efe4cc"/>'
          + '<path d="' + d + '" class="clkk-guide ryt"/><g class="clkk-trail"></g>';
      }
      case 'flyand': {
        const colors = ['#f1e4cc', '#2f5fa8', '#c62f25', '#3f7d3a', '#d99a2b', '#1e1c1d'];
        let bands = '<rect x="40" y="200" width="920" height="600" rx="40" fill="' + body + '"/>';
        const h = 400 / s.bands;
        for (let i = 0; i < s.bands; i++) bands += '<rect x="40" y="' + (300 + i * h).toFixed(1) + '" width="920" height="' + (h * 0.62).toFixed(1) + '" fill="' + colors[i % colors.length] + '"/>';
        const marks = s.marks.map((m) => '<g class="clkk-fmark" transform="translate(' + m[0] + ' ' + (m[1] === 1 ? 250 : 750) + ')">'
          + '<path d="' + (m[1] === 1 ? 'M-22-20h44L0 22z' : 'M-22 20h44L0-22z') + '" fill="#f4c542"/></g>'
          + '<path d="M' + m[0] + ' 290V710" class="clkk-fguide"/>').join('');
        return bands + marks + '<g class="clkk-trail"></g>';
      }
      case 'marble': {
        return PLATE('#3a2419', '#1e120b') + '<g class="clkk-swirl"><g class="clkk-trail"></g></g>'
          + s.drops.map((d) => '<circle class="clkk-drop" cx="' + d[0] + '" cy="' + d[1] + '" r="48"/>').join('')
          + '<circle cx="500" cy="500" r="330" class="clkk-spinhint"/>';
      }
      default: {
        const stripes = s.stripes.map((x) => '<rect class="clkk-lstripe" x="' + (x[0] - x[1] / 2) + '" y="' + s.top + '" width="' + x[1] + '" height="' + (s.bottom - s.top) + '" rx="12"/>').join('');
        return '<path d="M500 120C300 120 180 260 180 480S300 900 500 900 820 700 820 480 700 120 500 120z" fill="#2b2a2f" stroke="#111" stroke-width="10"/>'
          + '<path d="M380 120h240v40H380z" fill="#222"/>' + stripes + '<g class="clkk-shine"></g><g class="clkk-trail"></g>';
      }
    }
  }

  const api0 = () => HClicker.api;

  const HOWTO = {
    rizh: 'Торкнись біля ріжка й тримай: коло закрутиться. Веди палець угору-вниз, щоб ріжок ішов по пунктиру, — один оберт.',
    ryt: 'Проведи по пунктирному контуру, не відриваючи руки, — усе коло. Мимо контуру — подряпина на білому.',
    flyand: 'На кожній позначці протягни рівний штрих через усі смуги — у бік стрілки.',
    marble: 'Торкнись кожної позначки (крапля), а тоді різко крутни пальцем коло навколо центру.',
    losk: 'Натирай пунктирні смуги — водь пальцем туди-сюди, поки не заблищать. Поза смугами не три.',
  };

  function openPaint(st, api, p) {
    const info = techInfo(st, p.tech);
    const body = api.overlay(st, '<div class="clkk-paint">'
      + '<div class="clk-sub">' + (TECH_ICON[p.tech] || '🎨') + ' ' + api.esc(st, info.name) + ' · ' + api.esc(st, styleName(st, st.kView.style)) + '</div>'
      + '<p class="muted small clkk-howto">' + api.esc(st, HOWTO[p.tech] || info.desc) + '</p>'
      + '<div class="clkk-cwrap"><svg class="clkk-canvas t-' + p.tech + '" viewBox="0 0 1000 1000">' + paintScene2(p, st) + '</svg></div>'
      + '<div class="clkk-pbar"><i></i></div>'
      + '<div class="clkk-prow"><span class="muted small clkk-pinfo"></span>'
      + '<button type="button" class="ghost clkk-again">Спочатку</button><button type="button" class="primary clkk-done" disabled>Готово</button></div>'
      + '</div>', { cls: 'clkk-ov', onClose: () => { st.kPaint = null; } });
    const svg = body.querySelector('.clkk-canvas');
    const g = {
      p, svg, trail: svg.querySelector('.clkk-trail'), disc: svg.querySelector('.clkk-disc'), shine: svg.querySelector('.clkk-shine'),
      swirl: svg.querySelector('.clkk-swirl'), bar: body.querySelector('.clkk-pbar i'), info: body.querySelector('.clkk-pinfo'),
      done: body.querySelector('.clkk-done'), pts: [], t0: 0, last: null, down: false, pointer: null, strokes: 0, sent: false,
      brushAt: 0, len: new Map(), spun: false, limit: p.tech === 'rizh' ? p.shape.period + 1200 : 14000, curStroke: null,
    };
    st.kPaint = g;
    body.querySelector('.clkk-again').onclick = () => startPaint(st, api, p.tech);
    g.done.onclick = () => submitPaint(st, api);
    const pos = (e) => {
      const r = svg.getBoundingClientRect();
      return [((e.clientX - r.left) / r.width) * 1000, ((e.clientY - r.top) / r.height) * 1000];
    };
    svg.addEventListener('pointerdown', (e) => {
      if (!e.isTrusted || g.sent || (e.pointerType === 'mouse' && e.button !== 0)) return;
      e.preventDefault();
      try { svg.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      g.down = true;
      g.pointer = e.pointerId;
      const [x, y] = pos(e);
      addPoint(st, api, g, e.timeStamp, x, y, true);
    });
    svg.addEventListener('pointermove', (e) => {
      if (!e.isTrusted || !g.down || e.pointerId !== g.pointer || g.sent) return;
      e.preventDefault();
      const [x, y] = pos(e);
      g.lastPos = [x, y];
      if (g.last && e.timeStamp - g.last[0] < SAMPLE_MS) return;
      addPoint(st, api, g, e.timeStamp, x, y, false);
    });
    const up = (e) => {
      if (!e.isTrusted || e.pointerId !== g.pointer) return;
      g.down = false;
      g.lastPos = null;
      afterStroke(st, api, g);
    };
    svg.addEventListener('pointerup', up);
    svg.addEventListener('pointercancel', up);
    svg.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  function addPoint(st, api, g, ts, x, y, down) {
    if (g.pts.length >= MAX_POINTS) return;
    if (!g.t0) g.t0 = ts;
    const ms = Math.max(0, ts - g.t0);
    if (g.last && ms < g.last[0]) return;
    x = Math.max(0, Math.min(1000, x));
    y = Math.max(0, Math.min(1000, y));
    g.pts.push([ms, x, y, down ? 1 : 0]);
    const prev = g.last;
    g.last = [ms, x, y];
    if (down) g.strokes++;
    g.done.disabled = g.pts.length < 12;
    // Слід на полотні: у ріжкуванні — у системі кола (фарба крутиться разом із ним).
    let px = x;
    let py = y;
    if (g.p.tech === 'rizh') {
      const a = -g.p.shape.dir * 2 * Math.PI * ms / g.p.shape.period;
      const dx = x - 500;
      const dy = y - 500;
      px = 500 + dx * Math.cos(a) - dy * Math.sin(a);
      py = 500 + dx * Math.sin(a) + dy * Math.cos(a);
    }
    if (down || !g.curStroke) {
      g.curStroke = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
      g.curStroke.setAttribute('class', 'clkk-stroke');
      g.curStroke.setAttribute('points', '');
      g.trail.appendChild(g.curStroke);
      if (g.p.tech === 'marble') {
        const c = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        c.setAttribute('cx', px.toFixed(0));
        c.setAttribute('cy', py.toFixed(0));
        c.setAttribute('r', '34');
        c.setAttribute('class', 'clkk-blob b' + (g.strokes % 4));
        g.trail.appendChild(c);
      }
    }
    g.curStroke.setAttribute('points', g.curStroke.getAttribute('points') + ' ' + px.toFixed(0) + ',' + py.toFixed(0));
    if (g.p.tech === 'losk' && prev && !down) rub(g, prev[1], prev[2], x, y);
    if (Date.now() - g.brushAt > 260) { g.brushAt = Date.now(); api.sfx('brush'); }
    if (g.pts.length >= MAX_POINTS) submitPaint(st, api);
  }

  /// Лощіння: той самий розклад шляху по клітинках 40×40, що й на сервері, — клітинка блищить від 100 одиниць.
  function rub(g, x0, y0, x1, y1) {
    const d = Math.hypot(x1 - x0, y1 - y0);
    if (d <= 0 || d > 200) return;
    const steps = Math.ceil(d / 5);
    for (let k = 0; k < steps; k++) {
      const t = (k + 0.5) / steps;
      const cx = Math.floor((x0 + (x1 - x0) * t) / 40);
      const cy = Math.floor((y0 + (y1 - y0) * t) / 40);
      const key = cy * 25 + cx;
      const was = g.len.get(key) || 0;
      const now = was + d / steps;
      g.len.set(key, now);
      if (was < 100 && now >= 100) {
        const r = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        r.setAttribute('x', cx * 40);
        r.setAttribute('y', cy * 40);
        r.setAttribute('width', 40);
        r.setAttribute('height', 40);
        const s = g.p.shape;
        const inZone = cy * 40 + 20 >= s.top && cy * 40 + 20 <= s.bottom && s.stripes.some((st) => Math.abs(cx * 40 + 20 - st[0]) * 2 <= st[1]);
        r.setAttribute('class', inZone ? 'clkk-cell' : 'clkk-cell bad');
        g.shine.appendChild(r);
      }
    }
  }

  function afterStroke(st, api, g) {
    if (g.sent || !g.last) return;
    const p = g.p;
    if (p.tech === 'marble') {
      // Закрутка: штрих, що обійшов центр хоч на пів оберта, — фарби розходяться, і розпис готовий.
      const stroke = [];
      for (let i = g.pts.length - 1; i >= 0; i--) { stroke.unshift(g.pts[i]); if (g.pts[i][3]) break; }
      let tr = 0;
      for (let i = 1; i < stroke.length; i++) {
        let d = Math.atan2(stroke[i][2] - 500, stroke[i][1] - 500) - Math.atan2(stroke[i - 1][2] - 500, stroke[i - 1][1] - 500);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        tr += d;
      }
      if (Math.abs(tr) >= Math.PI) {
        g.swirl.classList.add(tr > 0 ? 'spun' : 'spun-back');
        setTimeout(() => submitPaint(st, api), reduced() ? 50 : 900);
      }
    }
    if (p.tech === 'flyand' && g.strokes >= p.shape.marks.length) setTimeout(() => submitPaint(st, api), 350);
  }

  function paintFrame(st, api) {
    const g = st.kPaint;
    if (!g || g.sent) return;
    const now = performance.now();
    // Палець стоїть на місці (подій руху нема), а коло крутиться — точка все одно потрібна.
    if (g.down && g.last && g.t0 && now - g.t0 - g.last[0] > 45 && g.p.tech === 'rizh') {
      addPoint(st, api, g, now, g.lastPos ? g.lastPos[0] : g.last[1], g.lastPos ? g.lastPos[1] : g.last[2], false);
    }
    const t = g.t0 ? now - g.t0 : 0;
    if (g.disc) {
      const deg = g.t0 ? (g.p.shape.dir * 360 * t) / g.p.shape.period : 0;
      g.disc.setAttribute('transform', 'rotate(' + (deg % 360).toFixed(2) + ' 500 500)');
    }
    g.bar.style.width = Math.min(100, (t / g.limit) * 100).toFixed(1) + '%';
    const info = g.t0 ? Math.max(0, Math.ceil((g.limit - t) / 1000)) + ' с' : 'чекаю на дотик';
    if (g.info.textContent !== info) g.info.textContent = info;
    if (g.t0 && t >= g.limit && !g.down) submitPaint(st, api);
    if (g.t0 && t >= g.limit + 3000) submitPaint(st, api);
  }

  function encode(pts) {
    const out = [];
    let prev = null;
    for (const p of pts) {
      const dt = prev ? Math.round(p[0] - prev[0]) : 0;
      out.push(!prev || p[3] ? -dt - 1 : dt, Math.round(p[1]), Math.round(p[2]));
      prev = p;
    }
    return out;
  }

  function submitPaint(st, api) {
    const g = st.kPaint;
    if (!g || g.sent) return;
    if (g.pts.length < 12) { api.closeOverlay(st); return; }
    g.sent = true;
    g.done.disabled = true;
    api.act(st, 'kiln', { op: 'decor', path: encode(g.pts) }).then(() => {
      if (st.kPaint === g) api.closeOverlay(st);
    });
  }

  // ---------- відкриття горна ----------

  function maybeReveal(st, api) {
    const k = st.kView;
    const l = k && k.last;
    if (!l || !st.mine) return;
    const at = Date.parse(l.at);
    if (!Number.isFinite(at)) return;
    if (st.kSeen == null) st.kSeen = +api.storeGet('clk.kiln.seen', '0') || 0;
    if (at <= st.kSeen) return;
    st.kSeen = at;
    api.storeSet('clk.kiln.seen', String(at));
    // Давнє відкриття (підмайстер без нас годину тому) — лише в «Останньому горні», без події.
    if (api.serverNow(st) - at > 10 * 60 * 1000) return;
    if (api.overlayOpen(st) && st.kPaint) return;
    reveal(st, api, l, false);
  }

  function reveal(st, api, l, replay) {
    const cnt = [0, 0, 0, 0];
    for (const it of l.items) cnt[it[1]]++;
    const quick = reduced();
    const step = quick ? 0 : Math.max(120, Math.min(320, 3200 / Math.max(1, l.items.length)));
    const body = api.overlay(st, '<div class="clkk-reveal' + (quick ? ' quick' : '') + '">'
      + '<div class="clk-sub">' + (l.helper ? '🧑‍🏭 Підмайстер відкрив горно' : '🔥 Горно відкрите') + '</div>'
      + '<div class="muted small">' + (l.helper ? 'усі звичайні, без тріщин' : 'жар у смузі ' + l.heat + ' %' + (l.beauty ? ' · краса ' + l.beauty : '') + (l.straw ? ' · солома' : ''))
      + ' · ' + api.esc(st, styleName(st, l.style)) + '</div>'
      + '<div class="clkk-rv-door"><div class="clkk-rv-grid">'
      + l.items.map((it, i) => '<div class="clkk-rv q' + it[1] + '" style="animation-delay:' + (i * step) + 'ms">'
        + (it[1] === 0 ? shardSvg() : api.wareSvg(it[0], { style: l.style, quality: it[1], slot: 'rv-' + i, cls: 'clkk-rvsvg' }))
        + '<span class="small">' + (STARS[it[1]] || '') + ' ' + api.esc(st, wareName(st, it[0])) + '</span></div>').join('')
      + '</div></div>'
      + '<div class="clkk-rv-sum" style="animation-delay:' + (l.items.length * step + 200) + 'ms"><b>' + summary(cnt) + '</b>'
      + (l.shards ? '<div class="small muted">черепки на засипку: +' + api.potsShort(l.shards) + '</div>' : '')
      + (l.sold ? '<div class="small muted">комора повна — на базар: +' + api.potsShort(l.sold) + '</div>' : '')
      + (!l.helper && l.items.length >= 4 && cnt[3] === l.items.length ? '<div class="clkk-perfect">🔔 Усе горно дзвінке!</div>' : '')
      + '<button type="button" class="primary clkk-tostore">🧺 В комору</button></div>'
      + '</div>', { cls: 'clkk-ov' });
    body.querySelector('.clkk-tostore').onclick = () => { api.closeOverlay(st); api.showTab(st, 'store'); };
    if (replay) return;
    api.sfx('open');
    if (quick) return;
    const timers = [];
    l.items.forEach((it, i) => {
      if (it[1] === 3 || it[1] === 0) timers.push(setTimeout(() => api.sfx(it[1] === 3 ? 'ding' : 'crack'), i * step + 150));
    });
    const prev = st.ov.onClose;
    st.ov.onClose = () => { timers.forEach(clearTimeout); if (prev) prev(); };
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'kiln',
    order: 30,

    mount(st, api) {
      st.kView = null;
      st.kb = null;
      st.kPaint = null;
      st.kScene = null;
      st.kSeen = null;
      mountTab(st, api);
    },

    update(st, v, api) {
      const k = v.kiln;
      if (!k || !st.kUi) return;
      st.kView = k;
      if (k.state === 'burning' && !k.helper && k.seed) burnBegin(st, api, k);
      else if (st.kb) burnEnd(st);
      if (st.kb) st.kb.done = false;
      paintPrep(st, api);
      paintLast(st, api);
      paintState(st, api, api.serverNow(st));
      maybeOpenPaint(st, api);
      maybeReveal(st, api);
    },

    frame(st, api) {
      if (st.kb && st.tab === 'kiln') paintBurn(st, api, false);
      else if (st.kb) {
        // Вкладку сховали посеред обпалу — модель однаково доходить до кінця й відкриває горно.
        const m = model(st);
        if (api.serverNow(st) - st.kb.litAt >= m.steps * m.stepMs + OPEN_AFTER_MS) paintBurn(st, api, true);
      }
      if (st.kPaint) paintFrame(st, api);
    },

    slow(st, api, now) {
      if (!st.kView) return;
      paintState(st, api, now);
      // Підмайстер відкриває горно в Sync на сервері, а сервер сам виду не шле: попросити свіжий, коли час вийшов.
      if (st.kView.state === 'burning' && st.kView.helper && st.mine && now >= Date.parse(st.kView.litAt) + burnMs(st) + 300
        && Date.now() - (st.kLookAt || 0) > 3000) {
        st.kLookAt = Date.now();
        api.order(st, 'look');
      }
      // Сирці висохли, горно вихолонуло — кнопки мусять це побачити без нового виду.
      const k = st.kView;
      const dry = st.craft ? st.craft.rack.filter((r) => r.dryAt <= now).length : k.dry;
      const coolDone = k.state === 'cooling' && Date.parse(k.coolUntil) <= now;
      if (dry !== k.dry || coolDone) {
        st.kView = Object.assign({}, k, { dry, state: coolDone ? (k.batch.length ? 'loaded' : 'cold') : k.state, coolUntil: coolDone ? null : k.coolUntil });
        paintPrep(st, api);
      }
    },

    unmount(st) {
      if (st.kKey) document.removeEventListener('keydown', st.kKey);
      st.kUi = null;
      st.kb = null;
      st.kPaint = null;
    },
  });
})();
