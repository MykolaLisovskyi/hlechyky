/*
  Жива хата Гончарного кола (docs/games/specs/clicker-v7.md, пакет B1). Частина ядра clicker.js.

  Що тут:
  1) хата в розрізі 360×450 (шар .clk-house): над стріхою — небо й світ за хатою, що росте з драбиною верстатів
     (намет Сорочинського ярмарку, чумацький віз, артіль в Опішні, глинище, школа, чайка на річці, музей, Цар-глек);
     під стріхою — полиця, знаряддя на кілочках, вивіска з емблемами драбини, прикраси, горно з димарем, підмайстри,
     кіт на полиці. Малюється рядком лише тоді, коли змінився «підпис» стану (рівні сходинками, куплене, пора, погода);
  2) день і ніч за київським часом: кольори неба, стін, землі — CSS-змінні на сцені (раз на 20 с), сонце й місяць
     рухаються трансформом; пора року — калина, сніг, цвіт, соняхи; погода — з view.market.weather, якщо вона є;
  3) відчуття: руки гончаря біля кола, глина пружинить на клік, трус сцени на розбитому, спалах лише для рідкісного;
  4) звук: синтез Web Audio (жодних файлів), типово вимкнений, кнопка й гучність у куті сцени, пам'ять у localStorage.
     Підміняє api.sfx(name, opts); невідома назва — тихе «тук»;
  5) права панель: банер «Наступна ціль», значки рядків верстатів (api.upIcon), знаряддя й прикраси (api.toolIcon,
     api.decorIcon), вкладки в один рядок із прокруткою до активної;
  6) картка «Поки тебе не було» (view.away) — раз на запис.

  Перевірка дня й ночі: HClicker.sceneTime = '2026-12-20T22:00:00+02:00' (або ?clkTime=… в адресі) — сцена бере цей час.
*/
(() => {
  const SVGNS = 'http://www.w3.org/2000/svg';
  const REDUCED = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const SKY_MS = 20000;                   // як часто перераховуємо небо: сонце за 20 с зсувається на піксель
  const HANDS_MS = 900;                   // скільки руки лишаються біля кола після кліка
  const MAX_VOICES = 28;                  // стільки звуків водночас; решта (дрібні «шльоп») пропускаються

  /// Опорні точки сцени у viewBox 360×450 — для частин, що малюють у api.layer(st, 'back', id) (той самий viewBox)
  /// чи ставлять DOM у front-шар (тоді x% = x / 360 · 100, y% = y / 450 · 100).
  const SCENE = {
    w: 360, h: 450,
    sky: { y0: 0, y1: 130 },              // небо й світ за хатою (над стріхою)
    road: 112,                            // дорога, якою їде чумацький віз
    gate: { x: 96, y: 100, w: 24, h: 28 },  // хвіртка в тину — гість «біля воріт»
    roof: { y0: 122, y1: 152 },           // стріха
    shelf: 176,                           // верх полиці з сирцями (css: top 39 %)
    floor: 372,                           // де починається долівка
    wheel: { cx: 180, cy: 331, r: 101 },  // коло з кільцем розгону
    window: { x: 226, y: 190, w: 54, h: 46 },
    icon: { x: 292, y: 188, w: 34, h: 42 },
    kiln: { x: 282, y: 272, w: 70, h: 108 },
    board: { x: 6, y: 186, w: 68, h: 34 },   // вивіска з емблемами драбини
    tools: { x: 4, y: 226, w: 90, h: 110 },
    bench: { x: 4, y: 388, w: 90, h: 44 },
    chest: { x: 292, y: 402, w: 60, h: 42 },
  };

  // ---------- дрібні помічники ----------

  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };
  /// Колір як [r, g, b]: і «#a8402c», і «rgb(168,64,44)» (змішані кольори змішуються далі).
  const hex = (h) => {
    if (h[0] === '#') return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
    const m = /(\d+)\D+(\d+)\D+(\d+)/.exec(h);
    return m ? [+m[1], +m[2], +m[3]] : [0, 0, 0];
  };
  const mix = (a, b, t) => {
    const x = hex(a), y = hex(b);
    return 'rgb(' + x.map((v, i) => Math.round(v + (y[i] - v) * t)).join(',') + ')';
  };
  const f1 = (n) => (Math.round(n * 10) / 10).toString();
  /// Сталі «випадкові» числа: зорі й соломинки стріхи мусять лежати там само щоразу, інакше підпис хати мінявся б.
  function rng(seed) {
    let s = seed >>> 0;
    return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
  }

  // ---------- час, пора року, небо ----------

  const URL_TIME = (() => { try { return new URLSearchParams(location.search).get('clkTime'); } catch { return null; } })();

  /// «Зараз» для неба: підмінений час (перевірка) або серверний годинник гри.
  function sceneNow(st, api) {
    const o = window.HClicker && HClicker.sceneTime != null ? HClicker.sceneTime : URL_TIME;
    if (o != null) {
      const t = typeof o === 'number' ? o : Date.parse(o);
      if (Number.isFinite(t)) return t;
    }
    return st.viewNow ? api.serverNow(st) : Date.now();
  }

  let monthFmt = null;
  /// Місяць за Києвом (1…12): пора року перемикається київської півночі, а не UTC.
  function kyivMonth(ms) {
    try {
      monthFmt = monthFmt || new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', month: 'numeric' });
      return +monthFmt.format(new Date(ms));
    } catch { return new Date(ms).getMonth() + 1; }
  }
  const seasonOf = (m) => (m === 12 || m <= 2 ? 'winter' : m <= 5 ? 'spring' : m <= 8 ? 'summer' : 'autumn');

  /// Сонце над Києвом: тривалість дня від ~8 год (грудень) до ~16,4 год (червень), сонячний полудень ≈ 9:58 UTC
  /// (30,5° сх. д.). Точність — хвилини, для неба у віконці досить. alt: 1 — полудень, 0 — обрій, −1 — північ.
  function sunAt(ms) {
    const d = new Date(ms);
    const doy = (ms - Date.UTC(d.getUTCFullYear(), 0, 1)) / 864e5;
    const dayLen = 12.2 + 4.15 * Math.cos(((doy - 172) / 365.25) * 2 * Math.PI);
    const noon = 9.97;
    const h = (((ms % 864e5) + 864e5) % 864e5) / 36e5;
    const rise = noon - dayLen / 2;
    const since = (h - rise + 24) % 24;
    if (since <= dayLen) return { alt: Math.sin((Math.PI * since) / dayLen), day: since / dayLen, night: -1 };
    const nightLen = 24 - dayLen;
    const t = (since - dayLen) / nightLen;
    return { alt: -Math.sin(Math.PI * t), day: -1, night: t };
  }

  /// Вік місяця в добах (0 — молодик, ~14,8 — повня) від відомого молодика 6.01.2000 18:14 UTC.
  const moonAge = (ms) => ((((ms - Date.UTC(2000, 0, 6, 18, 14)) / 864e5) % 29.530588) + 29.530588) % 29.530588;

  const NIGHT = { sky1: '#060b1f', sky2: '#16224a', far: '#1a2836', river: '#1a2d4d', road: '#3a3024', wall: '#2b2019', wall2: '#1b140f',
    floor: '#19120d', thatch: '#4b3b22', disc: '#1d3629' };
  const DAY = { sky1: '#3d82c4', sky2: '#a8d3ee', far: '#6c957a', river: '#5b9ad0', road: '#a2835a', wall: '#6d513a', wall2: '#50392a',
    floor: '#45321f', thatch: '#b89150', disc: '#2b4c3c' };
  const GROUND = { spring: '#6aa94d', summer: '#88a743', autumn: '#a98b3d', winter: '#dce4ec' };
  const FAR = { spring: '#5f9270', summer: '#6f9463', autumn: '#8f8a55', winter: '#b9c6d3' };

  /// Небо й світло сцени — CSS-змінні на .clk-stage. Лише рядки, що справді змінились.
  function paintSky(st, ms) {
    const sn = sunAt(ms);
    const season = seasonOf(kyivMonth(ms));
    const L = smooth(-0.14, 0.22, sn.alt);                 // денне світло 0…1
    const tw = 1 - smooth(0, 0.2, Math.abs(sn.alt));        // сутінки: найсильніші на обрії
    const vars = {};
    for (const k of Object.keys(DAY)) vars[k] = mix(NIGHT[k], DAY[k], L);
    const sky2 = mix(NIGHT.sky2, DAY.sky2, L);
    vars.sky2 = tw > 0.02 ? blendRgb(sky2, '#e8875a', tw * 0.75) : sky2;
    vars.sky1 = tw > 0.02 ? blendRgb(vars.sky1, '#3a3b74', tw * 0.45) : vars.sky1;
    vars.near = mix(shade(GROUND[season], 0.32), GROUND[season], L);
    vars.far = mix(shade(FAR[season], 0.3), FAR[season], L);
    vars.lamp = f1(1 - L * 0.92);
    vars.stars = f1(clamp((0.08 - sn.alt) / 0.22, 0, 1));
    vars.sunop = f1(sn.day >= 0 ? smooth(-0.02, 0.08, sn.alt) : 0);
    vars.moonop = f1(clamp((0.1 - sn.alt) / 0.2, 0, 1));
    // Сонце — дугою від лівого краю до правого за день; місяць — так само за ніч.
    const p = sn.day >= 0 ? sn.day : 0.5;
    vars.sunx = Math.round(24 + 312 * p) + 'px';
    vars.suny = Math.round(104 - 88 * Math.max(0, sn.alt)) + 'px';
    const q = sn.night >= 0 ? sn.night : 0.5;
    vars.moonx = Math.round(30 + 300 * q) + 'px';
    vars.moony = Math.round(100 - 76 * Math.max(0, -sn.alt)) + 'px';
    const el = st.stage;
    const prev = st.scn.vars;
    for (const [k, val] of Object.entries(vars)) {
      if (prev[k] === val) continue;
      prev[k] = val;
      el.style.setProperty('--clks-' + k, val);
    }
    // Коло вночі темнішає разом із хатою (ядро фарбує його змінною --clk-disc).
    if (prev.discVar !== vars.disc) { prev.discVar = vars.disc; el.style.setProperty('--clk-disc', vars.disc); }
    const tod = L > 0.6 ? 'day' : L < 0.15 ? 'night' : 'dusk';
    if (el.dataset.tod !== tod) el.dataset.tod = tod;
    return { season, moon: Math.round((moonAge(ms) / 29.530588) * 16) % 16, night: L < 0.35 };
  }
  function shade(h, k) { return mix(h, '#0a0f1a', 1 - k); }
  const blendRgb = (a, b, t) => mix(a, b, t);

  // ---------- знаряддя (кілочок — у (0,0), висить донизу, ~16×46) ----------

  const WOOD = '#b07a45', WOOD2 = '#6b4423';
  const TOOL_ART = {
    paddle: '<path d="M-1.4 2h2.8v19h-2.8z" fill="#9a6a3a"/><path d="M0 19c5.5 0 7 7 7 13.5S4.6 46 0 46s-7-7-7-13.5S-5.5 19 0 19z" fill="' + WOOD + '" stroke="' + WOOD2 + '" stroke-width=".9"/>'
      + '<path d="M0 24v17" stroke="#8a5a30" stroke-width=".8"/><circle cy="4.5" r="1.3" fill="#2b1a0e"/>',
    string: '<path d="M0 0L-3 7" stroke="#8a6a4a" stroke-width=".8"/><path d="M-3 9C-10 20 9 27 3 39" stroke="#dfe5ea" stroke-width=".9" fill="none"/>'
      + '<rect x="-7" y="6.5" width="8" height="4.5" rx="2" fill="#9a6a3a" stroke="' + WOOD2 + '" stroke-width=".6"/><rect x="0" y="37.5" width="8" height="4.5" rx="2" fill="#9a6a3a" stroke="' + WOOD2 + '" stroke-width=".6"/>',
    sponge: '<path d="M0 0v9" stroke="#8a6a4a" stroke-width=".8"/><path d="M-7.5 13c3.5-3.5 12-3.5 15 0 3.5 4.5 2.5 12-1 15.5-4 3.4-9.5 3.4-13 0-3.4-3.5-4.4-11-1-15.5z" fill="#e3b84c" stroke="#a9812c" stroke-width=".7"/>'
      + '<g fill="#b58d33"><circle cx="-3" cy="17" r="1.2"/><circle cx="2.5" cy="20" r="1.5"/><circle cx="-1.5" cy="25" r="1"/><circle cx="4" cy="26" r=".9"/></g>',
    ribs: '<path d="M0 0v6" stroke="#8a6a4a" stroke-width=".8"/><path d="M-6 8c7-5 14 1 11 10l-4 21c-1.4 5.5-8.4 5.5-9.4 0-1.8-9-5.4-24 2.4-31z" fill="#c38c53" stroke="' + WOOD2 + '" stroke-width=".9"/>'
      + '<circle cx="-.5" cy="16" r="2.2" fill="#2b1a0e"/><path d="M-3 26l2 11" stroke="#9a6a3a" stroke-width=".7"/>',
    lantern: '<circle cy="16" r="15" fill="#ffc766" class="clks-glow"/><path d="M0 0v5" stroke="#555" stroke-width="1"/><path d="M-3.5 5a3.5 3 0 0 1 7 0" stroke="#555" fill="none"/>'
      + '<path d="M-6 8.5h12l-2.5-3.5h-7z" fill="#3b3b3b"/><rect x="-5.5" y="8.5" width="11" height="17" rx="1.2" fill="rgba(255,205,110,.45)" stroke="#2d2d2d" stroke-width="1"/>'
      + '<path d="M-5.5 17h11" stroke="#2d2d2d" stroke-width=".6"/><path class="clks-flick" d="M0 22c-2.4-2.2-2.4-5.6 0-8.6 2.4 3 2.4 6.4 0 8.6z" fill="#ffb83a"/><path d="M-6.5 25.5h13v2.5h-13z" fill="#3b3b3b"/>',
    apron: '<path d="M-5 1q5 4 10 0" stroke="#5a3a1e" stroke-width=".9" fill="none"/><path d="M-6 5h12l2.5 7-1.2 30q-7.3 3-14.6 0L-8.5 12z" fill="#7d4c27" stroke="#4a2a12" stroke-width=".9"/>'
      + '<path d="M-4 24h8v8h-8z" fill="none" stroke="#c99a5b" stroke-width=".7" stroke-dasharray="1.2 1"/><path d="M-8.5 12h17" stroke="#4a2a12" stroke-width="1"/>',
    bucket: '<path d="M-7.5 12Q0-1 7.5 12" stroke="#5c6168" fill="none" stroke-width="1"/><path d="M-8.5 12h17l-2.3 21h-12.4z" fill="#8c9299" stroke="#4d5258" stroke-width=".9"/>'
      + '<path d="M-8 17h16M-7.1 27.5h14.2" stroke="#5d6268" stroke-width="1"/><ellipse cy="12" rx="8.5" ry="1.6" fill="#5aa0d8"/>',
    whistle: '<path d="M0 0v12" stroke="#8a6a4a" stroke-width=".8"/><path d="M-8 24c0-6.5 5.5-9.5 9.5-8.5 1-3.4 4.5-4.4 6.6-2l-2.2 2.2c2.3 3.4 1.2 9.8-4.4 12-4.4 2.2-9.5 1.2-9.5-3.7z" fill="#c96d36" stroke="#6b3b1b" stroke-width=".8"/>'
      + '<path d="M-6 22q4 3 9 1M-5 26q4 2.5 8 .5" stroke="#3f7d3a" stroke-width="1" fill="none"/><circle cx="4.6" cy="15.6" r=".9" fill="#1b1310"/><path d="M8 11.5l2.5-2 .5 3z" fill="#d7372b"/>',
    scales: '<path d="M0 0v7M-10 8h20" stroke="#6b5a3a" stroke-width="1.3"/><path d="M-10 8l-4.5 13M-10 8l4.5 13M10 8l-4.5 13M10 8l4.5 13" stroke="#b9a37a" stroke-width=".6"/>'
      + '<path d="M-15 21h10q-1 4.5-5 4.5t-5-4.5zM5 21h10q-1 4.5-5 4.5t-5-4.5z" fill="#cda44a" stroke="#8a6a2a" stroke-width=".6"/><circle cy="8" r="1.4" fill="#cda44a"/>',
    iron: '<rect x="-2.2" y="1" width="4.4" height="10" rx="2" fill="#7a4a26"/><path d="M0 11v22" stroke="#4a4a4a" stroke-width="2.2"/>'
      + '<rect x="-7" y="32" width="14" height="8" rx="1.2" fill="#3a3a3a"/><path d="M0 33.6l1.1 2.3h2.5l-2 1.5.8 2.4L0 38.3l-2.4 1.5.8-2.4-2-1.5h2.5z" fill="#ff8a3d"/>',
  };
  const TOOL_ORDER = ['paddle', 'string', 'sponge', 'ribs', 'lantern', 'apron', 'bucket', 'whistle', 'scales', 'iron'];

  // ---------- емблеми (32×32): вивіска драбини в хаті й значки рядків верстатів ----------

  const JUG_32 = 'M13 7h6v1.4c0 .9-.8 1.3-.8 2.3 0 1.5 5.3 3.2 5.3 9.4 0 4.7-2.6 7.8-4.1 9H12.6c-1.5-1.2-4.1-4.3-4.1-9 0-6.2 5.3-7.9 5.3-9.4 0-1-.8-1.4-.8-2.3z';
  const EMBLEM = {
    fair: '<path d="M16 6l11.5 17h-23z" fill="#d7372b"/><path d="M16 6l4 17h-8z" fill="#f4efe3"/><path d="M16 6V2" stroke="#6b4423" stroke-width="1.2"/>'
      + '<path d="M16 2l6 2-6 2z" fill="#f2c230"/><path d="M4 23h24v5H4z" fill="#8a5a30"/><path d="M13.5 28v-5h5v5z" fill="#3a1e10"/>',
    artel: '<path d="M3 16L16 6l13 10v12H3z" fill="#efe6d2"/><path d="M1 17L16 5l15 12" stroke="#b08a50" stroke-width="3" fill="none" stroke-linejoin="round"/>'
      + '<rect x="7" y="19" width="5" height="9" fill="#6b4423"/><path d="M19 27c-2 0-3-2-3-4 0-3 2.4-3.6 2.4-4.6h2.2c0 1 2.4 1.6 2.4 4.6 0 2-1 4-3 4z" fill="#c56b35"/>'
      + '<path d="M16.6 22.5h5.8" stroke="#f6efe2" stroke-width=".8"/>',
    chumaks: '<path d="M5 8c1 5 5 7 9 7M27 8c-1 5-5 7-9 7" stroke="#efe4c8" stroke-width="2.6" fill="none" stroke-linecap="round"/>'
      + '<path d="M10.5 13.5h11l-1.5 9c-.5 4-2.2 6-4 6s-3.5-2-4-6z" fill="#8f6e4c"/><path d="M8 14.5l3 2.5M24 14.5l-3 2.5" stroke="#6e523a" stroke-width="2.4" stroke-linecap="round"/>'
      + '<circle cx="13.3" cy="18" r="1.1" fill="#1b1310"/><circle cx="18.7" cy="18" r="1.1" fill="#1b1310"/><ellipse cx="16" cy="25.5" rx="3" ry="2" fill="#c9a07a"/>',
    pit: '<path d="M2 28q14-15 28 0z" fill="#8f9bab"/><path d="M8 28q8-8 16 0z" fill="#6f7c8e"/><path d="M22 3l-7 15" stroke="#8a5a30" stroke-width="2.2" stroke-linecap="round"/>'
      + '<path d="M13 16.5l5.5 2.6-2.8 6.4-5.5-2.6z" fill="#b3bac2" stroke="#6b7079" stroke-width=".6"/>',
    school: '<path d="M16 9.5c-4.5-3.2-9.5-3.2-13-2v17.5c3.5-1.2 8.5-1.2 13 2 4.5-3.2 9.5-3.2 13-2V7.5c-3.5-1.2-8.5-1.2-13 2z" fill="#f4efe3" stroke="#6b4423" stroke-width="1"/>'
      + '<path d="M16 9.5v17.5" stroke="#6b4423"/><path d="M6 12.5h7M6 15.5h7M6 18.5h5M19 12.5h7M19 15.5h7M19 18.5h6" stroke="#b9a58a" stroke-width=".9"/><path d="M22 3v7l2-1.5 2 1.5V3z" fill="#d7372b"/>',
    chaika: '<path d="M3 21h26l-4.5 6.5H7.5z" fill="#6b4423"/><path d="M15.5 3v17" stroke="#3a2a1a" stroke-width="1.2"/>'
      + '<path d="M16.5 4c7.5 3.5 9.5 9.5 8.5 15h-8.5z" fill="#f4efe3"/><path d="M14.5 6.5c-5 3.2-7 8-6 12.5h6z" fill="#e2d6bb"/>'
      + '<path d="M2 29.5q3.5-2 7 0t7 0 7 0 7 0" stroke="#5b9ad0" stroke-width="1.3" fill="none"/>',
    museum: '<path d="M3 12.5L16 5l13 7.5z" fill="#ebe2cf" stroke="#9a8e76" stroke-width=".7"/><path d="M4.5 13h23v2.5h-23zM3.5 25.5h25v3.5h-25z" fill="#d5caaf"/>'
      + '<path d="M8 15.5v10M13 15.5v10M19 15.5v10M24 15.5v10" stroke="#ebe2cf" stroke-width="2.6"/>',
    tsar: '<path d="' + JUG_32 + '" fill="#f2c14e" stroke="#9a6a1a" stroke-width=".8"/><path d="M9.5 17.5h13" stroke="#c62f25" stroke-width="1.3"/>'
      + '<path d="M11 6.5l1.2-4 2 2.2L16 1.5l1.8 3.2 2-2.2 1.2 4z" fill="#f2c14e" stroke="#9a6a1a" stroke-width=".6"/><circle cx="16" cy="22.5" r="1.6" fill="#c62f25"/>',
    wheel: '<ellipse cx="16" cy="22" rx="13" ry="5" fill="#2b4c3c" stroke="#7a9a86" stroke-width="1"/><ellipse cx="16" cy="21" rx="8" ry="3" fill="none" stroke="#1c3328" stroke-width="1"/>'
      + '<path d="M12 20c0-6 2.4-7 2.4-9.5h3.2c0 2.5 2.4 3.5 2.4 9.5z" fill="#b5653a"/><path d="M14 10.5h4" stroke="#8a4a26" stroke-width="1.2"/><path d="M16 27v3" stroke="#6b4423" stroke-width="2"/>',
    apprentice: '<circle cx="16" cy="9" r="5" fill="#e0b48a"/><path d="M11 7.5q5-7 10 0q-5-2.5-10 0z" fill="#5a3a1e"/>'
      + '<path d="M6 30c0-9 4-13 10-13s10 4 10 13z" fill="#f4efe3"/><path d="M16 17v7" stroke="#d7372b" stroke-width="2"/><path d="M13 19.5h6" stroke="#d7372b" stroke-width="1"/>',
    kiln: '<path d="M5 29V16C5 9 10 5 16 5s11 4 11 11v13z" fill="#9a7552" stroke="#5c4530" stroke-width="1"/><path d="M10.5 29v-8a5.5 5.5 0 0 1 11 0v8z" fill="#2a1508"/>'
      + '<path d="M16 28c-4-3-3.2-7.5 0-10.5.8 2.4 2.6 2.8 1.8 6 1.6-1.6 2.4-3.6 1.6-6 3.4 3.2 3 8-3.4 10.5z" fill="#ff8a3d"/>',
    clay: '<path d="M4 25c0-7 5-12 12-12s12 5 12 12c0 3-3 4-12 4S4 28 4 25z" fill="#a8402c"/><path d="M9 19c2-2.4 5-3.4 8-3" stroke="rgba(255,255,255,.3)" stroke-width="1.6" fill="none" stroke-linecap="round"/>'
      + '<path d="M22 6l1.4 3.6L27 11l-3.6 1.4L22 16l-1.4-3.6L17 11l3.6-1.4z" fill="#f2c230"/>',
    flywheel: '<circle cx="16" cy="16" r="11" fill="none" stroke="#9aa3ad" stroke-width="3"/><circle cx="16" cy="16" r="3" fill="#9aa3ad"/>'
      + '<path d="M16 5v22M5 16h22M8.2 8.2l15.6 15.6M23.8 8.2L8.2 23.8" stroke="#9aa3ad" stroke-width="1.2"/><path d="M3 9a14 14 0 0 1 6-5.5M29 23a14 14 0 0 1-6 5.5" stroke="#ff8a3d" stroke-width="1.8" fill="none" stroke-linecap="round"/>',
    basket: '<path d="M4 14h24l-3 14H7z" fill="#b48a4a" stroke="#6b4423" stroke-width="1"/><path d="M5.5 19h21M6.5 24h19M11 14l1 14M16 14v14M21 14l-1 14" stroke="#7a5a2a" stroke-width=".9"/>'
      + '<path d="M9 14c0-7 14-7 14 0" stroke="#6b4423" stroke-width="1.8" fill="none"/><path d="M13 10c0-1.5-1-2-1-3h3c0 1-1 1.5-1 3z" fill="#c56b35"/>',
    workshop: '<path d="M4 16h24v13H4z" fill="#efe6d2"/><path d="M1 17L16 4l15 13z" fill="#b89150" stroke="#7a5a2a" stroke-width="1"/>'
      + '<rect x="7" y="19" width="6" height="5" fill="#5b9ad0" stroke="#6b4423" stroke-width=".8"/><rect x="18" y="20" width="6" height="9" fill="#6b4423"/><path d="M23 7v-4h3v6.5" fill="#8a5a30"/>',
  };
  const TIERS = ['fair', 'artel', 'chumaks', 'pit', 'school', 'chaika', 'museum', 'tsar'];

  // ---------- прикраси: значки для полиці «Хата» (32×32) ----------

  const DECOR_ICON = {
    towel: '<path d="M4 6h24v3H4z" fill="#8a5a30"/><path d="M6 9h20v19l-3 2H9l-3-2z" fill="#f4efe3"/><path d="M6 18h20M6 22h20" stroke="#d7372b" stroke-width="2"/><path d="M8 20l2-2 2 2 2-2 2 2 2-2 2 2 2-2 2 2" stroke="#2a1a12" stroke-width=".8" fill="none"/>',
    icon: '<rect x="7" y="4" width="18" height="24" rx="1.5" fill="#5a3a1a" stroke="#d9a92f" stroke-width="1.4"/><circle cx="16" cy="12" r="5" fill="#f4c542"/><circle cx="16" cy="12" r="3" fill="#e0b48a"/><path d="M11 26v-8c0-2 2-3 5-3s5 1 5 3v8z" fill="#8b3a22"/>',
    window: '<rect x="5" y="4" width="22" height="22" rx="1.5" fill="#5b9ad0" stroke="#6b4423" stroke-width="2.4"/><path d="M16 4v22M5 15h22" stroke="#6b4423" stroke-width="1.8"/><path d="M4 29q7-9 14-11" stroke="#4c9a3f" stroke-width="1.4" fill="none"/><g fill="#d7372b"><circle cx="12" cy="22" r="2"/><circle cx="15" cy="24" r="2"/><circle cx="10.5" cy="25" r="1.8"/></g>',
    rooster: '<path d="M8 22q-6-8-1-15 1 7 6 10z" fill="#2f5fa8"/><ellipse cx="16" cy="20" rx="8" ry="6" fill="#6a3a1a"/><circle cx="23" cy="12" r="3.6" fill="#6a3a1a"/><path d="M21 8.5l1.5-4 1.5 4 1.5-3.5 1 4z" fill="#d7372b"/><path d="M26.5 12.5l3.5 1-3.5 1.4z" fill="#f2c230"/><path d="M14 26v3M18 26v3" stroke="#f2c230" stroke-width="1.4"/>',
    dog: '<path d="M6 17q-4-4-1-9" stroke="#7a5a3e" stroke-width="2.4" fill="none" stroke-linecap="round"/><ellipse cx="14" cy="21" rx="9" ry="5.5" fill="#7a5a3e"/><circle cx="23" cy="15" r="5" fill="#7a5a3e"/><path d="M24 11l3-5 1 7z" fill="#5a4030"/><circle cx="24.5" cy="14.5" r=".9" fill="#111"/><circle cx="27.6" cy="16.6" r="1" fill="#111"/><path d="M9 26v3M18 26v3" stroke="#5a4030" stroke-width="2"/>',
    chest: '<rect x="3" y="11" width="26" height="17" rx="2" fill="#8b3a22" stroke="#4a1e10"/><path d="M3 13a13 6 0 0 1 26 0v3H3z" fill="#a54a2c" stroke="#4a1e10" stroke-width=".8"/><circle cx="16" cy="21" r="3" fill="#f2c230"/><circle cx="9" cy="22" r="1.8" fill="#4c9a3f"/><circle cx="23" cy="22" r="1.8" fill="#4c9a3f"/><path d="M14.5 15.5h3v3h-3z" fill="#e2c26a"/>',
  };

  const svg32 = (inner, cls) => '<svg viewBox="0 0 32 32" class="' + (cls || '') + '" aria-hidden="true">' + inner + '</svg>';

  // ---------- люди, звірі, речі ----------

  /// Підмайстер: ноги в (0,0), зріст ~62. shirt — колір сорочки, hat — бриль чи чуб; arms — вміст рук (анімується класом).
  function person(o) {
    const skin = o.skin || '#e2b68c';
    const pants = o.pants || '#3b3d52';
    return '<g class="clks-person ' + (o.cls || '') + '">'
      + '<g class="clks-legs"><path class="clks-leg l" d="M-3.5 -26v23" stroke="' + pants + '" stroke-width="5.4" stroke-linecap="round"/>'
      + '<path class="clks-leg r" d="M3.5 -26v23" stroke="' + pants + '" stroke-width="5.4" stroke-linecap="round"/>'
      + '<path d="M-7.5 -3h6.5v3.2h-8zM1 -3h7.5l.6 3.2H1z" fill="#3a2012"/></g>'
      + '<path d="M-9.5 -47q9.5-5 19 0l2.2 23h-23.4z" fill="' + (o.shirt || '#f4efe3') + '"/>'
      + '<path d="M0 -49v9" stroke="#d7372b" stroke-width="2.2"/><path d="M-3 -44h6" stroke="#d7372b" stroke-width="1"/>'
      + '<path d="M-10.5 -27h21.5v3.4H-10.5z" fill="' + (o.belt || '#c62f25') + '"/>'
      + '<circle cy="-55" r="7.2" fill="' + skin + '"/>'
      + (o.hat
        ? '<ellipse cy="-59.5" rx="11.5" ry="2.6" fill="#d9b45a"/><path d="M-5.6 -59.5q5.6-8.4 11.2 0z" fill="#e6c870"/><path d="M-5.4 -60.2h10.8" stroke="#8b3a22" stroke-width="1.2"/>'
        : '<path d="M-7.4 -56.5q7.4-10 14.8 0q-7.4-4-14.8 0z" fill="' + (o.hair || '#5a3a1e') + '"/>')
      + '<circle cx="2.8" cy="-55.5" r=".9" fill="#2b1a0e"/>'
      + '<g class="clks-arms">' + (o.arms || '') + '</g>'
      + '</g>';
  }

  /// Рука від плеча (−7,−44) до точки (x, y): рукав і долоня.
  const arm = (x, y, sleeve) => '<path d="M-6 -44L' + x + ' ' + y + '" stroke="' + (sleeve || '#f4efe3') + '" stroke-width="4.6" stroke-linecap="round"/>'
    + '<circle cx="' + x + '" cy="' + y + '" r="2.7" fill="#e2b68c"/>';

  function apprentices(lvl, night) {
    let s = '';
    // Перший — біля столика місить глину (руки ходять угору-вниз).
    if (lvl >= 1) {
      s += '<g transform="translate(58 424)"><path d="M-12 0v-14M12 0v-14" stroke="#5a3a1e" stroke-width="2.4"/><path d="M-16 -16h32v3h-32z" fill="#8a5a30"/>'
        + '<path class="clks-knead" d="M-9 -16c0-6 4-9 9-9s9 3 9 9z" fill="#a8583a"/></g>'
        + '<g transform="translate(84 446) scale(-1 1)">' + person({ cls: 'clks-kneader', arms: arm(18, -32) + arm(14, -30, '#e9e1cf'), hair: '#3a2410' }) + '</g>';
    }
    // Другий — носить кошик глини вздовж долівки, за колом.
    if (lvl >= 10) {
      const basket = '<g transform="translate(9 -40)"><path d="M-8 -6h16l-2.5 11h-11z" fill="#b48a4a" stroke="#6b4423" stroke-width=".8"/>'
        + '<path d="M-6 -6c1-4 11-4 12 0z" fill="#a8583a"/><path d="M-7 -1h14" stroke="#7a5a2a" stroke-width=".7"/></g>';
      s += '<g transform="translate(34 448)"><g class="clks-walker">' + person({ hat: true, shirt: '#efe6d2', pants: '#4a3a2a', arms: arm(9, -44) + basket }) + '</g></g>';
    }
    // Третій — підкидає дрова в горно.
    if (lvl >= 25) {
      const log = '<rect x="14" y="-38" width="12" height="4" rx="2" fill="#7a4a26" stroke="#4a2a12" stroke-width=".6"/>';
      s += '<g transform="translate(262 446)">' + person({ cls: 'clks-stoker', shirt: '#f1e7d4', pants: '#2f3f4f', hair: '#8a5a2a', arms: arm(16, -36) + log }) + '</g>';
    }
    // Четвертий (50+) — ще один носій глини, назустріч першому.
    if (lvl >= 50) {
      const basket = '<g transform="translate(9 -40)"><path d="M-8 -6h16l-2.5 11h-11z" fill="#9a7a44" stroke="#6b4423" stroke-width=".8"/>'
        + '<path d="M-6 -6c1-4 11-4 12 0z" fill="#8f9bab"/></g>';
      s += '<g transform="translate(34 448)"><g class="clks-walker w2">' + person({ shirt: '#f4efe3', pants: '#3a3a4a', hair: '#2b1a0e', belt: '#2f5fa8', arms: arm(9, -44) + basket }) + '</g></g>';
    }
    return s;
  }

  function cat(night) {
    // Сидить на правому краї полиці (верх полиці — y 176), хвіст звисає й гойдається.
    return '<g transform="translate(318 176)" class="clks-cat">'
      + '<path class="clks-tail" d="M6 -2c6 2 6 12 2 18" stroke="#3a3330" stroke-width="3.2" fill="none" stroke-linecap="round"/>'
      + '<path d="M-10 0c-1-9 2-15 8-16 6-1 10 4 10 10 0 3-1 5-2 6z" fill="#3a3330"/>'
      + '<circle cx="-4" cy="-19" r="7" fill="#3a3330"/><path d="M-10 -23l1-8 5 5zM2 -23l-1-8-5 5z" fill="#3a3330"/>'
      + '<g class="clks-eyes"><ellipse cx="-6.5" cy="-19.5" rx="1.5" ry="' + (night ? '1.6' : '1.1') + '" fill="' + (night ? '#f6d23a' : '#b9d36a') + '"/>'
      + '<ellipse cx="-1.5" cy="-19.5" rx="1.5" ry="' + (night ? '1.6' : '1.1') + '" fill="' + (night ? '#f6d23a' : '#b9d36a') + '"/></g>'
      + '<path d="M-4 -16.5l-1 1h2z" fill="#d88a8a"/></g>';
  }

  // ---------- світ за хатою (y 0…130) ----------

  const STARS = (() => {
    const r = rng(7);
    let s = '';
    for (let i = 0; i < 30; i++) {
      const x = Math.round(r() * 356 + 2), y = Math.round(r() * 70 + 3), big = r() > 0.8;
      s += '<circle cx="' + x + '" cy="' + y + '" r="' + (big ? 1.1 : 0.6) + '"' + (big ? ' class="clks-tw"' : '') + '/>';
    }
    return s;
  })();

  function moonSvg(step) {
    // 16 кроків фази: тінь-коло зсувається по диску (молодик — повна тінь, повня — тіні нема).
    const p = step / 16;
    const r = 7;
    const dx = p < 0.5 ? -2 * r * (p / 0.5) : 2 * r * (1 - (p - 0.5) / 0.5);
    return '<g class="clks-moon"><circle r="13" fill="#f1ecd6" opacity=".12"/><clipPath id="clks-moonclip"><circle r="' + r + '"/></clipPath>'
      + '<circle r="' + r + '" fill="#f1ecd6"/><circle cx="' + f1(dx) + '" r="' + (r + 0.4) + '" style="fill:var(--clks-sky1)" clip-path="url(#clks-moonclip)" opacity=".92"/></g>';
  }

  function tentSvg(x, y, s, a, b) {
    return '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')"><path d="M0 -20l13 18H-13z" fill="' + a + '"/><path d="M0 -20l4.5 18h-9z" fill="' + b + '"/>'
      + '<path d="M0 -20v-6" stroke="#6b4423" stroke-width="1"/><path class="clks-flag" d="M0 -26l7 2-7 2z" fill="#f2c230"/><path d="M-13 -2h26v2h-26z" fill="#6b4423"/></g>';
  }

  /// Хатка-мазанка під стріхою (артіль, школа): x — центр, y — долівка.
  function hutSvg(x, y, w, opts) {
    const h = opts.h || 12;
    let s = '<g transform="translate(' + x + ' ' + y + ')">'
      + '<rect x="' + (-w / 2) + '" y="' + (-h) + '" width="' + w + '" height="' + h + '" fill="' + (opts.wall || '#eee5d2') + '"/>'
      + '<path d="M' + (-w / 2 - 3) + ' ' + (-h + 1) + 'L0 ' + (-h - 11) + 'L' + (w / 2 + 3) + ' ' + (-h + 1) + 'z" fill="' + (opts.roof || '#a8844a') + '"/>';
    const wins = opts.windows || 2;
    for (let i = 0; i < wins; i++) {
      const wx = -w / 2 + (w / (wins + 1)) * (i + 1) - 2;
      s += '<rect x="' + f1(wx) + '" y="' + (-h + 3) + '" width="4" height="4" fill="#4a6a8a"/><rect class="clks-lit" x="' + f1(wx) + '" y="' + (-h + 3) + '" width="4" height="4" fill="#ffd27a"/>';
    }
    return s + (opts.extra || '') + '</g>';
  }

  function worldSvg(e) {
    const t = e.tiers;
    let s = '';
    // Небо, зорі, Чумацький Шлях, сонце й місяць.
    s += '<rect width="360" height="152" fill="url(#clks-skyg)"/>';
    if (t.chumaks >= 50) s += '<path class="clks-milky" d="M-10 70C60 40 120 34 190 22S320 -2 370 -6" stroke="#cdd6ff" stroke-width="18" fill="none" opacity=".12" style="opacity:calc(var(--clks-stars) * .16)"/>';
    s += '<g class="clks-stars" fill="#f4f1e0" style="opacity:var(--clks-stars)">' + STARS + '</g>';
    s += '<g class="clks-sunpos"><g style="opacity:var(--clks-sunop)"><circle r="17" fill="#ffe7a0" opacity=".22"/><circle r="9.5" fill="#ffd45a"/></g></g>';
    s += '<g class="clks-moonpos"><g style="opacity:var(--clks-moonop)">' + moonSvg(e.moon) + '</g></g>';
    // Хмари: завжди парочка, у хмарну чи дощову днину — густіші й сіріші.
    const grey = e.weather === 'rain' || e.weather === 'cloud';
    const cloud = (x, y, k, cls) => '<g transform="translate(' + x + ' ' + y + ') scale(' + k + ')"><g class="clks-cloud ' + cls + '"><path d="M0 0c-10 0-12-9-4-11 0-8 11-10 15-4 5-6 16-2 14 5 8 1 7 10 0 10z" fill="'
      + (grey ? '#8e98a6' : '#f4f6f8') + '" style="opacity:calc(' + (grey ? '.5' : '.25') + ' + var(--clks-sunop, 1) * .5)"/></g></g>';
    s += cloud(60, 30, 1, 'c1') + cloud(230, 20, 0.8, 'c2');
    if (grey) s += cloud(150, 38, 1.2, 'c3') + cloud(310, 42, 1, 'c1');
    // Далекі пагорби: музей, школа, Цар-глек.
    s += '<path d="M0 80C40 68 80 72 120 76S200 62 250 68 330 78 360 72V152H0z" style="fill:var(--clks-far)"/>';
    if (t.museum >= 1) {
      const lights = t.museum >= 50 ? '<g class="clks-lit" fill="#ffd27a"><rect x="-10" y="-9" width="2" height="5"/><rect x="-1" y="-9" width="2" height="5"/><rect x="8" y="-9" width="2" height="5"/></g>' : '';
      s += '<g transform="translate(34 76)"><path d="M-17 -12L0 -20l17 8z" fill="#e8dfcc"/><rect x="-16" y="-12" width="32" height="2" fill="#cfc4ae"/>'
        + '<path d="M-12 -10v9M-6 -10v9M0 -10v9M6 -10v9M12 -10v9" stroke="#e8dfcc" stroke-width="2.4"/><rect x="-17" y="-2" width="34" height="3" fill="#cfc4ae"/>' + lights
        + (t.museum >= 25 ? '<path d="M0 -20v-5" stroke="#6b4423"/><path d="M0 -25l5 1.5-5 1.5z" fill="#2f5fa8"/>' : '') + '</g>';
    }
    if (t.school >= 1) {
      s += hutSvg(108, 74, 28, { h: 11, roof: '#8f6e44', windows: 3,
        extra: '<rect x="8" y="-30" width="6" height="10" fill="#e8dfcc"/><path d="M6 -30l5-6 5 6z" fill="#8f6e44"/><circle cx="11" cy="-25" r="1.6" fill="#d9a92f"/>'
          + (t.school >= 25 ? '<path d="M-10 -22v-9" stroke="#6b4423"/><path class="clks-flag" d="M-10 -31l7 2-7 2z" fill="#2f5fa8"/><path d="M-10 -27l7 2-7 0z" fill="#f2c230"/>' : '') });
    }
    if (t.tsar >= 1) {
      const k = 0.7 + (t.tsar >= 25 ? 0.2 : 0) + (t.tsar >= 50 ? 0.2 : 0) + (t.tsar >= 100 ? 0.3 : 0);
      s += '<g transform="translate(262 74) scale(' + f1(k) + ')" class="clks-tsar"><ellipse cy="0" rx="16" ry="3" fill="rgba(0,0,0,.25)"/>'
        + '<path d="M-6 -44h12v3c0 2-1.6 2.6-1.6 4.8 0 3 11.6 7 11.6 20.2 0 9.4-5.4 14.6-8.6 16H-4.6C-7.8 -1.4 -13.2 -6.6 -13.2 -16c0-13.2 11.6-17.2 11.6-20.2 0-2.2-1.6-2.8-1.6-4.8z" fill="#e7b545" stroke="#8a5a1a" stroke-width="1"/>'
        + '<path d="M-12.6 -21h25.2" stroke="#c62f25" stroke-width="2"/><path d="M-11.5 -12h23" stroke="#2f7d3a" stroke-width="1.4"/>'
        + '<path class="clks-glint" d="M-8 -26c-2 4-2 10 0 14" stroke="#fff6cf" stroke-width="2" fill="none" stroke-linecap="round"/></g>';
    }
    // Річка з чайкою.
    s += '<path d="M0 88C80 83 160 92 240 87S330 85 360 88V96C300 94 220 99 140 95S40 97 0 98z" style="fill:var(--clks-river)"/>'
      + '<path class="clks-ripple" d="M30 92h14M120 93h10M200 91h16M290 92h12" stroke="#cfe6f6" stroke-width=".8" opacity=".5"/>';
    if (t.chaika >= 1) {
      const boat = (cls, k) => '<g class="clks-boat ' + cls + '"><g transform="scale(' + k + ')"><path d="M-14 -2h28l-5 5h-18z" fill="#6b4423"/><path d="M0 -2v-17" stroke="#3a2a1a" stroke-width="1"/>'
        + '<path d="M1 -18c7 3 9 8 8 14H1z" fill="#f4efe3"/>' + (t.chaika >= 25 ? '<path d="M3 -14l3 3M6 -14l-3 3" stroke="#d7372b" stroke-width=".8"/>' : '') + '</g></g>';
      s += '<g transform="translate(0 94)">' + boat('b1', 1) + (t.chaika >= 50 ? boat('b2', 0.8) : '') + '</g>';
    }
    // Ближня земля, дорога.
    s += '<path d="M0 98C60 94 140 100 220 97S320 94 360 97V152H0z" style="fill:var(--clks-near)"/>'
      + '<path d="M0 116C90 108 200 118 360 110V118C220 124 100 118 0 124z" style="fill:var(--clks-road)"/>';
    // Артіль в Опішні: мазанки з димком і глечиками біля дверей.
    if (t.artel >= 1) {
      const smoke = '<g class="clks-smoke sm" transform="translate(6 -26)"><circle r="2.4"/><circle r="2.4"/><circle r="2.4"/></g>';
      s += hutSvg(160, 106, 34, { h: 12, windows: 2, extra: '<rect x="4" y="-29" width="4" height="8" fill="#9a7a5a"/>' + smoke
        + '<path d="M-24 0c-2 0-3-2-3-3.6 0-2.4 2-3 2-3.8h2c0 .8 2 1.4 2 3.8 0 1.6-1 3.6-3 3.6z" fill="#c56b35"/><path d="M22 0c-2 0-3-2-3-3.6 0-2.4 2-3 2-3.8h2c0 .8 2 1.4 2 3.8 0 1.6-1 3.6-3 3.6z" fill="#2b2a2f"/>'
        + (t.artel >= 50 ? '<rect x="-9" y="-20" width="12" height="6" fill="#6b4423"/><path d="M-3 -19v4" stroke="#f2c230" stroke-width="1.4"/>' : '') });
      if (t.artel >= 25) s += hutSvg(128, 102, 22, { h: 10, windows: 1, roof: '#9a7a44' });
    }
    // Ярмарок у Сорочинцях: намети, більше — з віхами.
    if (t.fair >= 1) {
      s += tentSvg(214, 108, 1, '#d7372b', '#f4efe3');
      if (t.fair >= 25) s += tentSvg(240, 106, 0.85, '#2f5fa8', '#f2c230');
      if (t.fair >= 50) s += tentSvg(192, 104, 0.75, '#4c9a3f', '#f4efe3');
      if (t.fair >= 100) s += '<path d="M180 88q30 8 70 0" stroke="#8a6a4a" stroke-width=".6" fill="none"/><g fill="#f2c230"><path d="M190 89l3 4 3-3.6z"/><path d="M210 91l3 4 3-4z"/><path d="M230 90l3 4 3-4z"/></g>';
    }
    // Глинище: купа голубої глини, яма, драбина; «Кінний підйомник» — журавель із відром.
    if (t.pit >= 1) {
      s += '<g transform="translate(312 124)"><ellipse cx="-6" cy="-3" rx="14" ry="4.5" fill="#2a1f18"/><path d="M4 -2q9-13 22 0z" fill="#8f9bab"/>'
        + '<path d="M-14 -3l6 -14M-9 -3l6 -14M-12.6 -7h5.5M-11 -11h5.5" stroke="#8a6a4a" stroke-width="1"/>'
        + (t.pit >= 25 ? '<path d="M18 -2q6-8 14 0z" fill="#aab8c9"/>' : '')
        + (t.pit >= 50 ? '<path d="M-26 -3l6-22 6 22" stroke="#7a5a3a" stroke-width="1.4" fill="none"/><path class="clks-hoist" d="M-20 -25L2 -14" stroke="#7a5a3a" stroke-width="1.2"/><path d="M2 -14v6" stroke="#bba" stroke-width=".5"/><rect x="0" y="-8" width="4" height="4" fill="#6b7c8e"/>' : '')
        + '</g>';
    }
    // Чумацький віз із волами: проїжджає раз на хвилину (CSS), з віхами — обоз.
    if (t.chumaks >= 1) {
      const wagon = (cls) => '<g class="clks-wagon ' + cls + '">'
        + '<g class="clks-ox"><ellipse cx="22" cy="-8" rx="8" ry="5" fill="#d9ccb4"/><circle cx="31" cy="-11" r="3.4" fill="#d9ccb4"/><path d="M29 -14q-2-4 1-6M33 -14q2-4-1-6" stroke="#f4efe3" stroke-width="1.2" fill="none"/>'
        + '<path d="M17 -4v5M26 -4v5" stroke="#b8a88e" stroke-width="2"/></g>'
        + '<path d="M-14 -12h22l-2 7h-18z" fill="#8a5a30"/><path d="M-12 -12c2-6 16-6 18 0z" fill="#f4efe3"/><path d="M8 -8h8" stroke="#6b4423" stroke-width="1"/>'
        + '<circle class="clks-wh" cx="-9" cy="-3" r="4" fill="none" stroke="#4a2e18" stroke-width="1.4"/><circle class="clks-wh" cx="4" cy="-3" r="4" fill="none" stroke="#4a2e18" stroke-width="1.4"/>'
        + '<path d="M-9 -7v8M-13 -3h8M4 -7v8M0 -3h8" stroke="#4a2e18" stroke-width=".6"/>'
        + '<g transform="translate(-22 0)">' + '<circle cy="-17" r="2.8" fill="#e2b68c"/><path d="M-3 -14h6l1 9h-8z" fill="#f4efe3"/><path d="M-2 -5v5M2 -5v5" stroke="#3b3d52" stroke-width="1.6"/><path d="M-4.6 -19.5h9.2" stroke="#2b1a0e" stroke-width="2"/></g>'
        + '</g>';
      s += '<g transform="translate(0 118)">' + wagon('w1') + (t.chumaks >= 25 ? wagon('w2') : '') + '</g>';
    }
    // Тин із глечиками, хвіртка, півень, пора року.
    s += fenceSvg(e);
    // Погода: дощ чи сніг над світом (у морозну днину — сніжинки).
    if (e.weather === 'rain') s += '<g class="clks-rain" clip-path="url(#clks-bandclip)" stroke="#b8d4ea" stroke-width=".8" opacity=".6">' + streaks(40, 11) + '</g>';
    else if (e.weather === 'frost' || (e.season === 'winter' && e.weather === 'cloud')) s += '<g class="clks-snow" clip-path="url(#clks-bandclip)" fill="#fff" opacity=".85">' + flakes(34, 5) + '</g>';
    return s;
  }

  function streaks(n, seed) {
    const r = rng(seed);
    let s = '';
    for (let i = 0; i < n; i++) {
      const x = Math.round(r() * 380 - 10), y = Math.round(r() * 150 - 20);
      s += '<path d="M' + x + ' ' + y + 'l-3 9"/>';
    }
    return '<g class="clks-fall1">' + s + '</g><g class="clks-fall2">' + s + '</g>';
  }
  function flakes(n, seed) {
    const r = rng(seed);
    let s = '';
    for (let i = 0; i < n; i++) s += '<circle cx="' + Math.round(r() * 360) + '" cy="' + Math.round(r() * 150 - 10) + '" r="' + f1(0.7 + r() * 0.9) + '"/>';
    return '<g class="clks-fall1">' + s + '</g><g class="clks-fall2">' + s + '</g>';
  }

  function fenceSvg(e) {
    let s = '<g class="clks-fence">';
    // Кілки й плетіння тину.
    for (let x = 6; x <= 92; x += 14) s += '<path d="M' + x + ' 130V' + (102 - (x % 28 ? 0 : 3)) + '" stroke="#6b4a2a" stroke-width="2.6" stroke-linecap="round"/>';
    s += '<path d="M2 108q23 -3 46 0t46 0M2 114q23 3 46 0t46 0M2 120q23 -3 46 0t46 0M2 126q23 3 46 0t46 0" stroke="#8a6a3e" stroke-width="2" fill="none"/>';
    // Глечики на кілках — так сушили посуд на тинах.
    s += '<g transform="translate(20 99) scale(.34) translate(-16 -30)"><path d="' + JUG_32 + '" fill="#c56b35"/></g>'
      + '<g transform="translate(76 99) scale(.34) translate(-16 -30)"><path d="' + JUG_32 + '" fill="#2b2a2f"/></g>';
    // Хвіртка.
    s += '<path d="M98 130V104h20v26" stroke="#6b4a2a" stroke-width="2" fill="none"/><path d="M100 110h16M100 118h16M100 110l16 8" stroke="#8a6a3e" stroke-width="1.4"/>';
    if (e.decor.rooster) {
      s += '<g transform="translate(48 100)" class="clks-rooster"><path d="M-9 -2q-8-10-2-17 2 8 7 11z" fill="#2f5fa8"/><path d="M-10 -6q-6-6-1-12" stroke="#4c9a3f" stroke-width="1.4" fill="none"/>'
        + '<ellipse cx="0" cy="-5" rx="8" ry="5.4" fill="#6a3a1a"/><g class="clks-rhead"><circle cx="7" cy="-12" r="3.6" fill="#6a3a1a"/><path d="M5 -15.5l1.4-4 1.4 3.6 1.6-3.4 1 4z" fill="#d7372b"/>'
        + '<path d="M10.4 -12l3.6 1-3.6 1.4z" fill="#f2c230"/><circle cx="8" cy="-12.6" r=".7" fill="#111"/><path d="M8 -9.5q1 2.5-.5 3" stroke="#d7372b" stroke-width="1.2" fill="none"/></g>'
        + '<path d="M-2 0v3M2 0v3" stroke="#f2c230" stroke-width="1.2"/></g>';
    }
    // Пора року біля тину.
    const x0 = 132;
    if (e.season === 'summer') {
      for (const [dx, h] of [[0, 34], [12, 42], [24, 30]]) {
        s += '<g transform="translate(' + (x0 + dx) + ' 130)"><path d="M0 0V' + (-h) + '" stroke="#4c7a2a" stroke-width="1.6"/><path d="M0 ' + (-h / 2) + 'q-7-2-8 3 6 1 8-3z" fill="#5f9a3a"/>'
          + '<g class="clks-sunfl" transform="translate(0 ' + (-h) + ')"><circle r="6.5" fill="#f2c230"/><circle r="3" fill="#5a3a1a"/></g></g>';
      }
    } else if (e.season === 'autumn') {
      s += '<g transform="translate(' + (x0 + 12) + ' 130)"><path d="M0 0c-2-10-8-16-14-20M0 0c1-12 4-18 10-24M0 0c0-8-1-14 2-26" stroke="#6b4a2a" stroke-width="1.4" fill="none"/>'
        + '<g fill="#c99a2a"><ellipse cx="-12" cy="-24" rx="5" ry="3"/><ellipse cx="9" cy="-28" rx="5" ry="3"/><ellipse cx="2" cy="-30" rx="4" ry="2.6"/></g>'
        + '<g fill="#d7372b"><circle cx="-14" cy="-19" r="2"/><circle cx="-11" cy="-17" r="2"/><circle cx="-13" cy="-15" r="1.8"/><circle cx="10" cy="-22" r="2"/><circle cx="12" cy="-20" r="2"/><circle cx="8" cy="-19" r="1.8"/></g>'
        + '<g class="clks-leaf" fill="#d9a33a"><ellipse cx="0" cy="-20" rx="2" ry="1.2"/></g></g>';
    } else if (e.season === 'spring') {
      s += '<g transform="translate(' + (x0 + 12) + ' 130)"><path d="M0 0v-22M0 -14l-9-8M0 -18l8-9" stroke="#5a3a24" stroke-width="2" fill="none"/>'
        + '<g fill="#f6e1ea"><circle cx="-10" cy="-26" r="7"/><circle cx="2" cy="-32" r="8"/><circle cx="10" cy="-26" r="6"/><circle cx="-2" cy="-22" r="6"/></g>'
        + '<g fill="#e7a8bf"><circle cx="-8" cy="-27" r="1.2"/><circle cx="4" cy="-33" r="1.2"/><circle cx="9" cy="-25" r="1.2"/><circle cx="-1" cy="-21" r="1.1"/></g>'
        + '<g class="clks-petal" fill="#f6e1ea"><circle cx="0" cy="-20" r="1.1"/></g></g>';
    } else {
      s += '<g transform="translate(' + (x0 + 12) + ' 130)"><path d="M0 0c-2-10-8-16-14-20M0 0c1-12 4-18 10-24M0 0c0-8-1-14 2-26" stroke="#5a4a3a" stroke-width="1.4" fill="none"/>'
        + '<g fill="#d7372b"><circle cx="-13" cy="-18" r="1.8"/><circle cx="-11" cy="-16" r="1.8"/><circle cx="10" cy="-21" r="1.8"/></g>'
        + '<path d="M-16 -20q2-2 4 0M8 -24q2-2 4 0M0 -27q2-2 4 0" stroke="#fff" stroke-width="1.6" fill="none"/></g>';
      // Шапки снігу на кілках.
      for (let x = 6; x <= 92; x += 14) s += '<ellipse cx="' + x + '" cy="' + (101 - (x % 28 ? 0 : 3)) + '" rx="3" ry="1.4" fill="#fff"/>';
    }
    return s + '</g>';
  }

  // ---------- стріха, стіни, долівка ----------

  /// Стріха: хвилястий верх, зубчаста бахрома знизу, соломинки. Стала (зерно), бо входить у малюнок хати.
  const THATCH = (() => {
    const r = rng(21);
    let d = 'M-6 132';
    for (let x = -6; x < 366; x += 36) d += 'Q' + (x + 18) + ' ' + f1(122 + r() * 5) + ' ' + (x + 36) + ' ' + f1(128 + r() * 4);
    d += 'V150';
    for (let x = 366; x > -6; x -= 9) d += 'L' + f1(x - 4.5) + ' ' + f1(152 + r() * 6) + 'L' + (x - 9) + ' ' + f1(148 + r() * 2);
    d += 'z';
    let straw = '';
    for (let i = 0; i < 80; i++) {
      const x = Math.round(r() * 364 - 2), y = Math.round(130 + r() * 16);
      straw += 'M' + x + ' ' + y + 'l' + f1(r() * 3 - 1.5) + ' ' + f1(4 + r() * 5);
    }
    return { d, straw };
  })();

  function roofSvg(e) {
    const t = e.tiers;
    let s = '';
    // Комин хати (дим густіший узимку) і димар горна праворуч.
    s += '<path d="M56 130V100h16v30" fill="#d9ccb4" stroke="#8a7a60" stroke-width="1"/><path d="M54 100h20v4H54z" fill="#b8a88e"/>'
      + '<g class="clks-smoke ' + (e.season === 'winter' ? 'thick' : '') + '" transform="translate(64 96)"><circle r="4"/><circle r="4"/><circle r="4"/><circle r="4"/></g>';
    if (e.kiln >= 1) {
      s += '<path d="M337 132V96h9v36" fill="#5a5a5e" stroke="#2e2e32" stroke-width="1"/><path d="M335 96h13v3h-13z" fill="#3e3e42"/>'
        + '<g class="clks-smoke kiln" transform="translate(341 92)"><circle r="3.4"/><circle r="3.4"/><circle r="3.4"/><circle r="3.4"/></g>';
    }
    // Стріха: солом'яний навіс із нерівним краєм; «Новий дах» (Гончарня 25) — світліша й рівніша.
    const fresh = e.workshop >= 25;
    s += '<path d="' + THATCH.d + '" ' + (fresh ? 'fill="#caa35c"' : 'style="fill:var(--clks-thatch)"') + '/>'
      + '<path d="' + THATCH.straw + '" stroke="' + (fresh ? '#a98141' : 'rgba(60,40,15,.45)') + '" stroke-width=".8" fill="none"/>'
      + '<path d="M-6 150h372" stroke="rgba(40,25,10,.35)" stroke-width="2"/>';
    if (e.season === 'winter') s += '<path d="M-6 131Q60 120 120 126T240 125T366 128V133Q300 128 240 131T120 132T-6 136z" fill="#f4f7fb"/>';
    // «Вивіска на всю вулицю» (Гончарня 100) — дошка з глечиком на стрісі.
    if (e.workshop >= 100) {
      s += '<g transform="translate(206 145)"><path d="M-26 -1h52v-12h-52z" fill="#6b4423" stroke="#3a2412"/><path d="M-22 -7h44" stroke="#d9a92f" stroke-width="1.2" stroke-dasharray="3 2"/>'
        + '<g transform="translate(-6 -15) scale(.38)"><path d="' + JUG_32 + '" fill="#f2c14e"/></g></g>';
    }
    return s;
  }

  function interiorSvg(e) {
    let s = '';
    // Стіна, долівка, плінтус.
    s += '<rect y="148" width="360" height="226" fill="url(#clks-wallg)"/>'
      + '<path d="M0 200q60 -3 120 0t120 0 120 0M0 300q60 3 120 0t120 0 120 0" stroke="rgba(255,240,210,.035)" stroke-width="10" fill="none"/>'
      + '<path d="M0 372h360v78H0z" style="fill:var(--clks-floor)"/><path d="M0 372h360" stroke="rgba(0,0,0,.35)" stroke-width="3"/>'
      + '<path d="M0 396q180 -6 360 0M0 424q180 -8 360 0" stroke="rgba(255,255,255,.035)" stroke-width="1.4" fill="none"/>'
      + '<rect y="148" width="360" height="6" fill="#3a2412" opacity=".75"/>';
    // Каганець на полиці — світить уночі.
    s += '<circle cx="30" cy="170" r="70" fill="url(#clks-lampg)" style="opacity:var(--clks-lamp)"/>'
      + '<g transform="translate(30 176)"><path d="M-7 0c0-4 3-6 7-6s7 2 7 6z" fill="#a8583a"/><path d="M6 -3l5-1" stroke="#a8583a" stroke-width="2"/>'
      + '<path class="clks-flick" d="M10.5 -5c-2-2-2-5 .5-8 2 3 2 6-.5 8z" fill="#ffb83a" style="opacity:calc(.25 + var(--clks-lamp) * .75)"/></g>';
    // Вивіска драбини: вісім вирізьблених гнізд, у кожне — емблема купленого верстата.
    s += '<g transform="translate(' + SCENE.board.x + ' ' + SCENE.board.y + ')"><path d="M14 0l-4 -8M54 0l4 -8" stroke="#8a6a4a" stroke-width=".8"/>'
      + '<rect width="68" height="34" rx="3" fill="#5a3a1e" stroke="#3a2412" stroke-width="1.2"/><rect x="2" y="2" width="64" height="30" rx="2" fill="none" stroke="#8a5a30" stroke-width=".6" stroke-dasharray="2 1.5"/>';
    TIERS.forEach((k, i) => {
      const x = 9 + (i % 4) * 16.6, y = 9.5 + Math.floor(i / 4) * 15;
      s += e.tiers[k] >= 1
        ? '<g transform="translate(' + f1(x - 7) + ' ' + f1(y - 7) + ') scale(.4375)"><circle cx="16" cy="16" r="15.5" fill="#3a2412"/>' + EMBLEM[k] + '</g>'
        : '<circle cx="' + f1(x) + '" cy="' + f1(y) + '" r="6" fill="#3a2412" stroke="#6b4423" stroke-width=".6"/>';
    });
    s += '</g>';
    // Знаряддя на кілочках: планка й два ряди по п'ять.
    const tools = TOOL_ORDER.filter((k) => e.tools[k]);
    s += '<g transform="translate(0 ' + SCENE.tools.y + ')"><rect x="4" y="-3" width="90" height="4" rx="1.5" fill="#5a3a1e"/>'
      + (tools.length > 5 ? '<rect x="4" y="55" width="90" height="4" rx="1.5" fill="#5a3a1e"/>' : '');
    tools.forEach((k, i) => {
      const x = 14 + (i % 5) * 17.5, y = Math.floor(i / 5) * 58;
      s += '<circle cx="' + f1(x) + '" cy="' + (y - 1) + '" r="1.3" fill="#2b1a0e"/><g class="clks-tool t-' + k + '" transform="translate(' + f1(x) + ' ' + y + ') scale(.9)">' + TOOL_ART[k] + '</g>';
    });
    s += '</g>';
    // Рушник над колом: під полицею, з червоною вишивкою й китицями.
    if (e.decor.towel) {
      s += '<g class="clks-towel"><path d="M98 179Q153 204 212 179V185Q153 212 98 185z" fill="#f4efe3"/>'
        + '<path d="M103 190Q153 212 207 190" stroke="#d7372b" stroke-width="1.6" fill="none" stroke-dasharray="3 2"/>'
        + '<path d="M96 178h10v36h-10zM204 178h10v36h-10z" fill="#f4efe3"/><path d="M96 202h10M96 206h10M204 202h10M204 206h10" stroke="#d7372b" stroke-width="2"/>'
        + '<path d="M96 210h10M204 210h10" stroke="#2a1a12" stroke-width="1"/><path d="M97 214v4M100 214v4M103 214v4M105 214v4M205 214v4M208 214v4M211 214v4M213 214v4" stroke="#d7372b" stroke-width=".8"/></g>';
    }
    // Вікно з калиною: те саме небо, що й над хатою; взимку сніг на підвіконні, навесні — білий цвіт калини.
    if (e.decor.window) {
      const w = SCENE.window;
      s += '<g transform="translate(' + w.x + ' ' + w.y + ')"><rect x="-3" y="-3" width="' + (w.w + 6) + '" height="' + (w.h + 6) + '" rx="3" fill="#6b4423"/>'
        + '<rect width="' + w.w + '" height="' + w.h + '" fill="url(#clks-skyg)"/><g style="opacity:var(--clks-stars)" fill="#f4f1e0"><circle cx="10" cy="9" r=".8"/><circle cx="40" cy="7" r="1"/><circle cx="31" cy="18" r=".7"/></g>'
        + '<path d="M0 34C12 30 26 36 54 31V46H0z" style="fill:var(--clks-far)"/>'
        + '<path d="M' + (w.w / 2) + ' 0v' + w.h + 'M0 ' + (w.h / 2) + 'h' + w.w + '" stroke="#6b4423" stroke-width="2.6"/>'
        + '<rect x="-6" y="' + (w.h + 2) + '" width="' + (w.w + 12) + '" height="4" fill="#8a5a30"/>'
        + (e.season === 'winter' ? '<path d="M-6 ' + (w.h + 2) + 'q' + (w.w / 2 + 6) + ' -5 ' + (w.w + 12) + ' 0z" fill="#f4f7fb"/><path d="M2 2l6 6M46 2l-6 6M2 44l6-6" stroke="#dfeaf5" stroke-width=".8"/>' : '')
        + '<path d="M-4 ' + (w.h + 4) + 'q10 -26 30 -34" stroke="#4c7a2a" stroke-width="1.8" fill="none"/>'
        + (e.season === 'spring'
          ? '<g fill="#f6f3ea"><circle cx="16" cy="' + (w.h - 14) + '" r="3.6"/><circle cx="21" cy="' + (w.h - 10) + '" r="3"/><circle cx="12" cy="' + (w.h - 8) + '" r="2.8"/></g>'
          : '<g fill="' + (e.season === 'summer' ? '#8ab04a' : '#d7372b') + '"><circle cx="15" cy="' + (w.h - 13) + '" r="2.6"/><circle cx="20" cy="' + (w.h - 10) + '" r="2.6"/><circle cx="16" cy="' + (w.h - 8) + '" r="2.6"/><circle cx="12" cy="' + (w.h - 10) + '" r="2.3"/><circle cx="19" cy="' + (w.h - 15) + '" r="2.2"/></g>')
        + '<path d="M22 ' + (w.h - 20) + 'q6 -4 10 0q-6 3 -10 0z" fill="#5f9a3a"/>'
        + '<g transform="translate(40 ' + (w.h + 2) + ') scale(.42) translate(-16 -30)"><path d="' + JUG_32 + '" fill="#c56b35"/></g></g>';
    }
    // Ікона в куті з лампадкою.
    if (e.decor.icon) {
      const ic = SCENE.icon;
      s += '<g transform="translate(' + ic.x + ' ' + ic.y + ')"><circle cx="17" cy="20" r="30" fill="url(#clks-lampg)" style="opacity:calc(var(--clks-lamp) * .7)"/>'
        + '<rect width="34" height="42" rx="2" fill="#5a3a1a" stroke="#d9a92f" stroke-width="1.6"/><rect x="4" y="4" width="26" height="34" fill="#c99a3a"/>'
        + '<circle cx="17" cy="15" r="7.5" fill="#f4d46a"/><circle cx="17" cy="15" r="4.6" fill="#e0b48a"/><path d="M10 38v-11c0-3 3-5 7-5s7 2 7 5v11z" fill="#8b3a22"/><path d="M13 24h8" stroke="#2f5fa8" stroke-width="2"/>'
        + '<path d="M17 42v6" stroke="#8a6a4a" stroke-width=".6"/><path d="M13 48h8l-1.5 5h-5z" fill="#b0302a"/><path class="clks-flick" d="M17 49.5c-1-1-1-2.4 0-3.6 1 1.2 1 2.6 0 3.6z" fill="#ffcf5a"/></g>';
    }
    // Горно: сяйво, склепіння, челюсті з вогнем; що вищий рівень печі — то яскравіше.
    if (e.kiln >= 1) {
      const k = SCENE.kiln;
      const glow = f1(Math.min(0.9, 0.35 + e.kilnLvl / 60));
      s += '<ellipse cx="317" cy="352" rx="62" ry="52" fill="url(#clks-fireg)" style="opacity:calc(' + glow + ' * (.5 + var(--clks-lamp) * .5))"/>'
        + '<path d="M341 272V152h8v120" fill="#4a4a4e"/>'
        + '<path d="M' + k.x + ' 380V318q0-46 35-46t35 46v62z" fill="#9a7552" stroke="#5c4530" stroke-width="1.6"/>'
        + '<path d="M288 330q29-10 58 0M285 352q32-8 64 0" stroke="rgba(0,0,0,.18)" stroke-width="1.4" fill="none"/>'
        + '<path d="M301 380v-26a16 16 0 0 1 32 0v26z" fill="#241006"/>'
        + '<g class="clks-fire"><path class="clk-flame" d="M317 378c-11-9-9-22 0-30 2.4 7 7 8 5 17 5-5 7-10 5-17 9 9 8 23-10 30z" fill="#ff8a3d"/>'
        + '<path class="clk-flame f2" d="M317 377c-5-4.5-5-11 0-16 1.2 4.5 3.6 4.5 2.4 9 3.6-2.4 3.6-6 2.4-9 4.8 4.5 4.8 11-4.8 16z" fill="#f4c542"/></g>'
        + '<g class="clks-embers" fill="#ffb24a"><circle cx="312" cy="352" r="1"/><circle cx="321" cy="350" r=".8"/><circle cx="317" cy="348" r=".9"/></g>'
        + (e.kilnLvl >= 25 ? '<path d="M295 300h44" stroke="#5c4530" stroke-width="2"/><path d="M299 296h36" stroke="#b28a62" stroke-width="1"/>' : '')
        + '<path d="M296 380h42v5h-42z" fill="#7a5a3e"/>';
    }
    // Лава (завжди), собака під нею, скриня.
    if (e.decor.dog) {
      s += '<g transform="translate(4 400)"><rect width="88" height="6" rx="1.5" fill="#7a5230"/><path d="M8 6v36M80 6v36" stroke="#5a3a1e" stroke-width="4"/></g>';
      s += '<g transform="translate(46 428)" class="clks-dog"><path class="clks-dtail" d="M-18 -4q-9-4-8-12" stroke="#7a5a3e" stroke-width="3" fill="none" stroke-linecap="round"/>'
        + '<ellipse cx="0" cy="-2" rx="19" ry="7" fill="#7a5a3e"/><circle cx="19" cy="-6" r="6.5" fill="#7a5a3e"/><path d="M20 -11l6-6 1 9z" fill="#5a4030"/>'
        + '<path d="M18 -7q2 1.4 4 0" stroke="#111" stroke-width=".9" fill="none"/><circle cx="25" cy="-4.4" r="1.5" fill="#111"/>'
        + '<ellipse cx="-2" cy="5" rx="20" ry="2.4" fill="rgba(0,0,0,.25)"/></g>';
    }
    if (e.decor.chest) {
      const c = SCENE.chest;
      s += '<g transform="translate(' + c.x + ' ' + c.y + ')"><rect y="8" width="60" height="34" rx="3" fill="#8b3a22" stroke="#4a1e10"/>'
        + '<path d="M0 12a30 10 0 0 1 60 0v4H0z" fill="#a54a2c" stroke="#4a1e10" stroke-width=".8"/>'
        + '<path d="M8 30c4-8 10-8 12 0M40 30c4-8 10-8 12 0" stroke="#4c9a3f" stroke-width="1.6" fill="none"/>'
        + '<circle cx="30" cy="27" r="5" fill="#f2c230"/><circle cx="30" cy="27" r="2.2" fill="#d7372b"/><circle cx="14" cy="24" r="2.6" fill="#d7372b"/><circle cx="46" cy="24" r="2.6" fill="#d7372b"/>'
        + '<path d="M27 16h6v5h-6z" fill="#e2c26a"/><path d="M0 40h60" stroke="#4a1e10" stroke-width="2"/></g>';
    }
    if (e.cat) s += cat(e.night);
    s += apprentices(e.apprentice, e.night);
    return s;
  }

  const DEFS = '<defs>'
    + '<linearGradient id="clks-skyg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--clks-sky1)"/><stop offset="1" style="stop-color:var(--clks-sky2)"/></linearGradient>'
    + '<linearGradient id="clks-wallg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--clks-wall2)"/><stop offset=".35" style="stop-color:var(--clks-wall)"/><stop offset="1" style="stop-color:var(--clks-wall2)"/></linearGradient>'
    + '<radialGradient id="clks-lampg"><stop offset="0" stop-color="#ffc766" stop-opacity=".68"/><stop offset=".5" stop-color="#ffb04a" stop-opacity=".16"/><stop offset="1" stop-color="#ffb04a" stop-opacity="0"/></radialGradient>'
    + '<radialGradient id="clks-fireg"><stop offset="0" stop-color="#ff9a4a" stop-opacity=".7"/><stop offset=".6" stop-color="#ff7a2a" stop-opacity=".18"/><stop offset="1" stop-color="#ff7a2a" stop-opacity="0"/></radialGradient>'
    + '<clipPath id="clks-bandclip"><rect width="360" height="150"/></clipPath>'
    + '</defs>';

  /// Усе, від чого залежить малюнок хати, — сходинками, а не сирими числами: купівля ще одного рівня Підмайстра
  /// не перемальовує хату, а десятий — так (з'являється другий підмайстер).
  function envOf(st, v, sky) {
    const ups = v.upgrades || {};
    const lvl = (k) => (ups[k] && ups[k].level) || 0;
    const step = (n) => (n >= 100 ? 100 : n >= 50 ? 50 : n >= 25 ? 25 : n >= 1 ? 1 : 0);
    const tiers = {};
    for (const k of TIERS) tiers[k] = step(lvl(k));
    const hs = v.house || {};
    const tools = {};
    for (const t of hs.tools || []) if (t.owned) tools[t.key] = true;
    const decor = {};
    for (const d of hs.decor || []) if (d.owned) decor[d.key] = true;
    const a = lvl('apprentice');
    const weather = (v.market && typeof v.market.weather === 'string') ? v.market.weather : '';
    return {
      tiers, tools, decor, weather, season: sky.season, moon: sky.moon, night: sky.night,
      apprentice: a >= 50 ? 50 : a >= 25 ? 25 : a >= 10 ? 10 : a >= 1 ? 1 : 0,
      kiln: lvl('kiln') >= 1 ? 1 : 0, kilnLvl: Math.min(60, Math.floor(lvl('kiln') / 5) * 5), workshop: step(lvl('workshop')),
      cat: (v.secrets || []).some((s) => s.key === 'cat' && s.owned),
    };
  }

  function houseSvg(e) {
    return DEFS + worldSvg(e) + interiorSvg(e) + roofSvg(e);
  }

  function paintHouse(st) {
    const scn = st.scn;
    const v = st.lastView;
    if (!v || !st.house) return;
    const e = envOf(st, v, scn.sky);
    const sig = JSON.stringify(e);
    if (scn.houseSig === sig) return;
    scn.houseSig = sig;
    st.house.innerHTML = houseSvg(e);
    st.house.dataset.weather = e.weather || 'none';
    st.house.dataset.season = e.season;
  }

  // ---------- звук ----------

  const Snd = {
    on: false, vol: 0.6, ctx: null, out: null, noise: null, voices: 0,

    load(api) {
      this.on = api.storeGet('clk.sound', '0') === '1';
      const v = parseFloat(api.storeGet('clk.vol', '0.6'));
      this.vol = Number.isFinite(v) ? clamp(v, 0, 1) : 0.6;
    },

    /// AudioContext — лише після жесту гравця (інакше браузер його глушить і сварить у консоль).
    ensure() {
      if (!this.on) return null;
      if (!this.ctx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) return null;
        const ua = navigator.userActivation;
        if (ua && !ua.hasBeenActive) return null;
        try { this.ctx = new AC(); } catch { return null; }
        const ctx = this.ctx;
        const comp = ctx.createDynamicsCompressor();
        comp.threshold.value = -18;
        comp.ratio.value = 4;
        this.out = ctx.createGain();
        this.out.gain.value = this.level();
        this.out.connect(comp);
        comp.connect(ctx.destination);
        const len = Math.floor(ctx.sampleRate * 1.5);
        this.noise = ctx.createBuffer(1, len, ctx.sampleRate);
        const d = this.noise.getChannelData(0);
        for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
      }
      if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
      return this.ctx;
    },

    /// На сайті грає радіо: навіть «на повну» гра тихіша за нього.
    level() { return this.vol * this.vol * 0.55; },

    setOn(on, api) {
      this.on = on;
      api.storeSet('clk.sound', on ? '1' : '0');
      if (on) { this.ensure(); this.play('tap'); Mus.start(); }
      else { Mus.stop(); if (this.ctx) this.ctx.suspend().catch(() => {}); }
    },

    setVol(v, api) {
      this.vol = clamp(v, 0, 1);
      api.storeSet('clk.vol', String(Math.round(this.vol * 100) / 100));
      if (this.out) this.out.gain.setTargetAtTime(this.level(), this.ctx.currentTime, 0.05);
    },

    voice(ms) { this.voices++; setTimeout(() => { this.voices--; }, ms); },

    tone(type, f0, f1v, t, attack, peak, decay, dest) {
      const ctx = this.ctx;
      const o = ctx.createOscillator();
      o.type = type;
      o.frequency.setValueAtTime(f0, t);
      if (f1v && f1v !== f0) o.frequency.exponentialRampToValueAtTime(f1v, t + attack + decay);
      const g = ctx.createGain();
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(peak, t + attack);
      g.gain.exponentialRampToValueAtTime(0.0001, t + attack + decay);
      o.connect(g);
      g.connect(dest || this.out);
      o.start(t);
      o.stop(t + attack + decay + 0.05);
    },

    burst(t, dur, type, f0, f1v, peak, q) {
      const ctx = this.ctx;
      const src = ctx.createBufferSource();
      src.buffer = this.noise;
      const flt = ctx.createBiquadFilter();
      flt.type = type;
      flt.frequency.setValueAtTime(f0, t);
      if (f1v) flt.frequency.exponentialRampToValueAtTime(f1v, t + dur);
      flt.Q.value = q || 0.8;
      const g = ctx.createGain();
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(peak, t + Math.min(0.01, dur / 4));
      g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      src.connect(flt);
      flt.connect(g);
      g.connect(this.out);
      src.start(t, Math.random() * 1.2, dur + 0.05);
    },

    bell(t, f, peak, decay) {
      for (const [k, a] of [[1, 1], [2.76, 0.45], [5.4, 0.25], [8.93, 0.12]]) this.tone('sine', f * k, 0, t, 0.004, peak * a, decay / Math.sqrt(k));
    },

    /// Один звук за назвою. Невідома назва — тихе «тук»: пакети можуть кликати свої назви, гра від цього не падає.
    play(name, o) {
      if (!this.on || document.hidden) return;
      const ctx = this.ensure();
      if (!ctx || ctx.state !== 'running' && ctx.state !== 'suspended') return;
      if (this.voices > MAX_VOICES && name === 'clay') return;
      const t = ctx.currentTime + 0.005;
      const r = Math.random();
      const kind = ALIAS[name] || name;
      switch (kind) {
        case 'clay': {                     // «шльоп» мокрої глини
          const hot = o && o.hot;
          this.burst(t, 0.09, 'lowpass', 900 + r * 300, 180, hot ? 0.5 : 0.38, 1.2);
          this.tone('sine', 150 + r * 30, 55, t, 0.004, 0.32, 0.09);
          this.voice(150);
          break;
        }
        case 'tap':                        // тихе «тук» інтерфейсу
          this.tone('triangle', 1150 + r * 80, 700, t, 0.002, 0.12, 0.05);
          this.voice(80);
          break;
        case 'soft':
          this.tone('sine', 640, 520, t, 0.003, 0.06, 0.06);
          this.voice(90);
          break;
        case 'done':                       // «готово»: дерев'яне двозвуччя
          this.tone('sine', 587, 0, t, 0.003, 0.28, 0.25);
          this.tone('sine', 1174, 0, t, 0.003, 0.08, 0.15);
          this.tone('sine', 880, 0, t + 0.1, 0.003, 0.3, 0.35);
          this.tone('sine', 1760, 0, t + 0.1, 0.003, 0.07, 0.2);
          this.voice(600);
          break;
        case 'catch':                      // дзвін спійманого розписного
          this.bell(t, 988, 0.22, 1.4);
          this.bell(t + 0.09, 1319, 0.16, 1.2);
          this.coins(t + 0.05, 3, 0.1);
          this.voice(1500);
          break;
        case 'rare':                       // рідкісне: арпеджіо дзвоників
          [784, 988, 1175, 1568].forEach((f, i) => this.bell(t + i * 0.08, f, 0.18, 1.3));
          this.voice(1800);
          break;
        case 'grab':
          this.bell(t, 1175, 0.14, 0.7);
          this.coins(t + 0.04, 2, 0.1);
          this.voice(800);
          break;
        case 'coins':                      // монети на базарі
          this.coins(t, 4 + Math.floor(r * 3), 0.13);
          this.voice(500);
          break;
        case 'buy':
          this.tone('triangle', 900, 600, t, 0.002, 0.1, 0.05);
          this.coins(t + 0.03, 2, 0.1);
          this.voice(300);
          break;
        case 'break': {                    // тріск черепків
          for (let i = 0; i < 6; i++) this.burst(t + i * 0.022 + Math.random() * 0.02, 0.05, 'highpass', 1800 + Math.random() * 2500, 0, 0.35, 1.5);
          this.tone('sine', 120, 50, t, 0.004, 0.3, 0.14);
          this.voice(400);
          break;
        }
        case 'crackle':
          for (let i = 0; i < 4; i++) this.burst(t + Math.random() * 0.3, 0.03, 'bandpass', 2000 + Math.random() * 2000, 0, 0.12, 3);
          this.voice(400);
          break;
        case 'kiln':                       // вогонь у горні: подих полум'я й тріск дров, без низького гулу
          this.burst(t, 0.9, 'bandpass', 700, 300, 0.3, 0.7);
          this.burst(t + 0.05, 0.5, 'highpass', 2600, 1400, 0.12, 0.7);
          this.play('crackle');
          this.voice(1000);
          break;
        case 'prestige':                   // обпал майстерні: вогняний «ух» і дзвін на видиху
          this.burst(t, 1.1, 'bandpass', 350, 2400, 0.34, 0.9);
          this.bell(t + 0.5, 659, 0.16, 1.6);
          this.bell(t + 0.72, 988, 0.12, 1.4);
          this.voice(1300);
          break;
        case 'away':                       // «поки тебе не було»: м'який акорд
          [523, 659, 784].forEach((f, i) => this.tone('sine', f, 0, t + i * 0.07, 0.01, 0.12, 0.8));
          this.voice(1000);
          break;
        case 'swish':                      // пензель, ріжок, гачок
          this.burst(t, 0.22, 'bandpass', 1400, 3200, 0.18, 1.2);
          this.voice(300);
          break;
        case 'fail':
          this.tone('triangle', 330, 220, t, 0.004, 0.14, 0.22);
          this.tone('triangle', 247, 165, t + 0.12, 0.004, 0.12, 0.25);
          this.voice(500);
          break;
        case 'pop':
          this.tone('sine', 420, 900, t, 0.003, 0.14, 0.08);
          this.voice(120);
          break;
        default:
          this.tone('triangle', 1000, 700, t, 0.002, 0.07, 0.04);
          this.voice(80);
      }
    },

    coins(t, n, peak) {
      for (let i = 0; i < n; i++) {
        const at = t + i * (0.05 + Math.random() * 0.03);
        const f = 1500 + Math.random() * 900;
        this.tone('triangle', f, f * 0.985, at, 0.002, peak * 0.3, 0.08);
        this.tone('sine', f * 2.4, 0, at, 0.001, peak * 0.32, 0.11);
      }
    },
  };

  // Вкладку браузера сховали — контекст засинає; повернулись — звук жде першої події (Snd.ensure), а музика підхоплюється сама.
  document.addEventListener('visibilitychange', () => {
    if (!Snd.ctx) return;
    if (document.hidden) {
      Mus.stop();
      if (Snd.ctx.state === 'running') Snd.ctx.suspend().catch(() => {});
    } else if (Snd.on && Mus.on && HClicker.mounted.size) {
      Snd.ensure();
      Mus.start();
    }
  });

  // ---------- фонова музика: тиха капела на бандурі й сопілці ----------

  // Награвання синтезується наживо: жодних файлів, нескінченна фраза, що не повторюється точно.
  // Ля мінор натуральний із прохідною підвищеною IV — та сама фарба, що в награваннях, але без «циганщини».
  const MUS_ROOT = 220;                       // ля першої октави — тоніка
  const MUS_REF = 220;                        // висота, на якій зроблено зразок щипка
  const MUS_VOL = 0.6;                        // на слух це голосніше, ніж кажуть dBFS: рівень збито за скаргою, не за цифрою
  const BEAT = 60 / 66;                       // темп 66 — неквапом, під крок ноги коло гончарного кола
  const BAR = BEAT * 4;
  const MINOR = [0, 2, 3, 5, 7, 8, 10];       // ля сі до ре мі фа соль
  const CHORDS = {
    Am: [0, 3, 7, 12, 15, 19], Dm: [5, 8, 12, 17, 20], Em: [7, 10, 14, 19, 22],
    F: [8, 12, 15, 20, 24], G: [10, 14, 17, 22, 26], C: [3, 7, 10, 15, 19],
  };
  const ROOTS = { Am: 0, Dm: 5, Em: 7, F: 8, G: 10, C: 3 };
  const PROGS = [['Am', 'G', 'F', 'Em'], ['Am', 'Dm', 'G', 'Am'], ['Am', 'F', 'G', 'Am'], ['Dm', 'Am', 'Em', 'Am']];

  const hz = (s) => MUS_ROOT * Math.pow(2, s / 12);
  /// Найближчий до ноти звук акорду — щоб фраза сідала м'яко, а не стрибком через дві октави.
  const near = (tones, s) => {
    let best = s, dist = 99;
    for (const x of tones) for (let k = 0; k < 3; k++) {
      const c = 12 + (x % 12) + 12 * k;
      if (Math.abs(c - s) < dist) { dist = Math.abs(c - s); best = c; }
    }
    return best;
  };
  const rnd = (a, b) => a + Math.random() * (b - a);
  const pick = (a) => a[Math.floor(Math.random() * a.length)];

  const Mus = {
    on: false, timer: 0, at: 0, gain: null, buf: null, prog: null, step: 0,
    sing: 0, rest: 2, mel: 9, last: -1, ducked: true, heat: 0, want: 0,

    load(api) { this.on = api.storeGet('clk.music', '0') === '1'; },

    /// Щипок бандури робимо раз: Карплус-Стронг у звичайному масиві, далі та сама хвиля грає
    /// на різній швидкості — низькі струни виходять глухіші й довші, високі дзвінкі й короткі.
    pluckBuf(ctx) {
      const sr = ctx.sampleRate;
      const n = Math.floor(sr * 2.6);
      const N = Math.round(sr / MUS_REF);
      const buf = ctx.createBuffer(1, n, sr);
      const d = buf.getChannelData(0);
      const ring = new Float32Array(N);
      let seed = 0;
      for (let i = 0; i < N; i++) { seed = seed * 0.6 + (Math.random() * 2 - 1) * 0.4; ring[i] = seed; }
      let p = 0, soft = 0;
      for (let i = 0; i < n; i++) {
        const cur = ring[p];
        ring[p] = (cur + ring[(p + 1) % N]) * 0.4985;    // усереднення = струна глухне згори
        p = (p + 1) % N;
        soft = soft * 0.32 + cur * 0.68;                 // тіло інструмента: без різі на атаці
        d[i] = soft;
      }
      return buf;
    },

    /// Кімнатка, у якій стоїть коло: шум, що згасає. Без неї щипки сухі, як у телефоні.
    roomBuf(ctx) {
      const sr = ctx.sampleRate;
      const n = Math.floor(sr * 1.7);
      const buf = ctx.createBuffer(2, n, sr);
      for (let ch = 0; ch < 2; ch++) {
        const d = buf.getChannelData(ch);
        let lp = 0;
        for (let i = 0; i < n; i++) {
          lp = lp * 0.78 + (Math.random() * 2 - 1) * 0.22;
          d[i] = lp * Math.pow(1 - i / n, 2.6) * (i < sr * 0.012 ? i / (sr * 0.012) : 1);
        }
      }
      return buf;
    },

    start() {
      if (!this.on || !Snd.on) return;
      const ctx = Snd.ensure();
      if (!ctx) return;
      if (!this.gain) {
        this.buf = this.pluckBuf(ctx);
        this.gain = ctx.createGain();
        this.gain.gain.value = 0;
        const conv = ctx.createConvolver();
        conv.buffer = this.roomBuf(ctx);
        const wet = ctx.createGain();
        wet.gain.value = 0.25;
        this.gain.connect(Snd.out);
        this.gain.connect(wet);
        wet.connect(conv);
        conv.connect(Snd.out);
      }
      this.at = 0;
      this.ducked = true;                    // перший такт сам розкриє гучність
      if (!this.timer) this.timer = setInterval(() => this.tick(), 220);
    },

    stop() {
      clearInterval(this.timer);
      this.timer = 0;
      this.at = 0;
      this.ducked = true;
      if (this.gain && Snd.ctx && Snd.ctx.state !== 'closed') this.gain.gain.setTargetAtTime(0, Snd.ctx.currentTime, 0.2);
    },

    setOn(on, api) {
      this.on = on;
      api.storeSet('clk.music', on ? '1' : '0');
      if (on) this.start(); else this.stop();
    },

    /// Під ефір награвання ГРАЄ: раніше воно тут зводилось у нуль, а бо радіо на цьому сайті грає завжди,
    /// музики не чув ніхто жодного разу. Замовкаємо лише під голосове в чаті — чужу мову застеляти не годиться.
    busy() {
      const el = document.getElementById('voiceAudio');
      return !!(el && !el.paused && !el.ended);
    },

    /// Сцена каже кожен кадр, наскільки розкручене коло (0…1). Стежимо за ним повільно: темп,
    /// що стрибає від кожного клацання, звучить як зламана платівка; плавне наростання чується як розгін.
    drive(level) { this.want = clamp(level, 0, 1); },

    tick() {
      const ctx = Snd.ctx;
      if (!this.on || !Snd.on || !ctx || ctx.state !== 'running') return;
      const off = document.hidden || this.busy();
      if (off !== this.ducked) {
        this.ducked = off;
        this.gain.gain.setTargetAtTime(off ? 0 : MUS_VOL, ctx.currentTime, off ? 0.25 : 0.7);
      }
      if (off) { this.at = 0; return; }
      this.heat += (this.want - this.heat) * 0.06;          // приблизно чотири секунди на розгін і на спад
      const now = ctx.currentTime;
      if (!this.at || this.at < now) this.at = now + 0.15;   // повернулись — починаємо з чистого такту
      while (this.at < now + 1.5) this.at += this.bar(this.at);
    },

    // ---- голоси ----

    /// Бандура: зразок щипка на потрібній висоті, згори — обгортка, щоб нота не обірвалась клацанням.
    pluck(t, f, vel, dur) {
      const ctx = Snd.ctx;
      const src = ctx.createBufferSource();
      src.buffer = this.buf;
      src.playbackRate.value = f / MUS_REF;
      const g = ctx.createGain();
      g.gain.setValueAtTime(0, t);
      g.gain.linearRampToValueAtTime(vel, t + 0.005);
      g.gain.exponentialRampToValueAtTime(vel * 0.3, t + dur * 0.5);
      g.gain.exponentialRampToValueAtTime(0.0002, t + dur);
      g.gain.linearRampToValueAtTime(0, t + dur + 0.02);
      src.connect(g);
      g.connect(this.gain);
      src.start(t);
      src.stop(t + dur + 0.05);
    },

    /// Сопілка: два тони з легким вібрато й подихом на атаці — щоб не звучало як гудок.
    blow(t, f, vel, dur) {
      const ctx = Snd.ctx;
      const o = ctx.createOscillator();
      o.type = 'sine';
      o.frequency.setValueAtTime(f, t);
      const o2 = ctx.createOscillator();
      o2.type = 'triangle';
      o2.frequency.setValueAtTime(f, t);
      const lfo = ctx.createOscillator();
      lfo.frequency.setValueAtTime(rnd(4.4, 5.6), t);
      const lg = ctx.createGain();
      lg.gain.setValueAtTime(0, t);
      lg.gain.linearRampToValueAtTime(f * 0.006, t + dur * 0.5);
      lfo.connect(lg);
      lg.connect(o.frequency);
      lg.connect(o2.frequency);
      const thin = ctx.createGain();
      thin.gain.value = 0.3;
      const g = ctx.createGain();
      g.gain.setValueAtTime(0, t);
      g.gain.linearRampToValueAtTime(vel, t + 0.08);
      g.gain.setValueAtTime(vel, t + dur * 0.75);
      g.gain.linearRampToValueAtTime(0, t + dur);
      o.connect(g);
      o2.connect(thin);
      thin.connect(g);
      g.connect(this.gain);
      o.start(t); o2.start(t); lfo.start(t);
      o.stop(t + dur + 0.03); o2.stop(t + dur + 0.03); lfo.stop(t + dur + 0.03);
      // подих на атаці: коротке шелестіння на висоті ноти
      const air = ctx.createBufferSource();
      air.buffer = Snd.noise;
      const bp = ctx.createBiquadFilter();
      bp.type = 'bandpass';
      bp.frequency.value = f * 2;
      bp.Q.value = 1.4;
      const ag = ctx.createGain();
      ag.gain.setValueAtTime(0.0001, t);
      ag.gain.exponentialRampToValueAtTime(vel * 0.5, t + 0.03);
      ag.gain.exponentialRampToValueAtTime(0.0001, t + 0.16);
      air.connect(bp); bp.connect(ag); ag.connect(this.gain);
      air.start(t, Math.random() * 1.2, 0.2);
    },

    // ---- фраза ----

    /// Один такт: бас і щипки по акорду знизу, зверху — сопілка, що співає два-три такти й мовчить три-чотири.
    /// Розкручене коло жене темп і густоту щипків; віддає свою довжину, бо такти вже не однакові.
    bar(t) {
      const heat = this.heat;
      const beat = BEAT / (1 + 0.42 * heat);               // 66 → 94 ударів на розігрітому колі
      if (this.step % 4 === 0 && (!this.prog || Math.random() < 0.5)) this.prog = pick(PROGS);
      const name = this.prog[this.step % 4];
      const tones = CHORDS[name];
      const root = ROOTS[name];
      this.step++;

      this.pluck(t + rnd(0, 0.02), hz(root - 12), 0.2, 2.4);
      if (Math.random() < 0.45 + 0.3 * heat) this.pluck(t + beat * 2 + rnd(0, 0.03), hz(root - 12 + 7), 0.11, 1.8);
      for (const b of [0.5, 1, 1.5, 2, 2.5, 3, 3.5]) {
        if (Math.random() > 0.42 + 0.3 * heat) continue;
        let s = pick(tones);
        if (s === this.last) s = pick(tones);
        this.last = s;
        this.pluck(t + b * beat + rnd(-0.015, 0.03), hz(s), rnd(0.09, 0.16), rnd(1.1, 1.9));
      }

      if (this.sing > 0) { this.melody(t, tones, beat); this.sing--; if (!this.sing) this.rest = Math.floor(rnd(3, 5)); }
      else if (--this.rest <= 0) this.sing = Math.floor(rnd(2, 4));
      return beat * 4;
    },

    /// Мелодія ходить сходинками гами, стрибки рідкі; остання фраза сідає на звук акорду.
    melody(t, tones, beat) {
      const last = this.sing === 1;
      let b = 0;
      while (b < 3.9) {
        const dur = last && b >= 2 ? 4 - b : pick(this.heat > 0.5 ? [1, 0.5, 0.5, 0.5, 1.5] : [1, 1, 1, 0.5, 0.5, 1.5]);
        // Голос тягне до середини своєї смуги, інакше випадкова хода залипає під стелею чи на дні.
        const up = this.mel > 9 ? -1 : this.mel < 4 ? 1 : (Math.random() < 0.5 ? 1 : -1);
        this.mel = clamp(this.mel + (Math.random() < 0.75 ? up : up * pick([2, 3])), 0, 13);
        let s = 12 + MINOR[this.mel % 7] + 12 * Math.floor(this.mel / 7);
        if (last && b + dur >= 3.9) s = near(tones, s);
        if (Math.random() < 0.18) { b += dur; continue; }                 // пауза замість ноти — щоб фраза дихала
        this.blow(t + b * beat + rnd(-0.02, 0.02), hz(s), rnd(0.06, 0.1), dur * beat * 0.92);
        b += dur;
      }
    },
  };

  /// Для перевірки з консолі: чи ввімкнено звук, у якому стані AudioContext, скільки голосів звучить.
  window.HClicker.soundState = () => ({ on: Snd.on, vol: Snd.vol, ctx: Snd.ctx ? Snd.ctx.state : null, voices: Snd.voices, music: Mus.on, quiet: Mus.on && Mus.ducked });

  /// Назви, які можуть покликати інші частини, — на найближчий звук із набору.
  const ALIAS = {
    click: 'tap', ui: 'tap', tab: 'tap', open: 'tap', close: 'soft', select: 'tap', toggle: 'tap',
    spin: 'clay', slap: 'clay', form: 'clay',
    ware: 'done', formed: 'done', ready: 'done', ok: 'done', success: 'done', order: 'coins', paid: 'coins',
    ring: 'catch', bell: 'catch', golden: 'catch', gift: 'catch', guest: 'catch', album: 'catch', unlock: 'catch',
    ringing: 'rare', perfect: 'rare', rank: 'rare', wagon: 'rare', achievement: 'rare', q3: 'rare',
    crack: 'break', broken: 'break', shatter: 'break', miss: 'fail', wrong: 'fail', lose: 'fail',
    fire: 'kiln', stoke: 'kiln', light: 'kiln', roar: 'kiln', firing: 'kiln', wood: 'crackle', heat: 'crackle',
    coin: 'coins', sell: 'coins', bazaar: 'coins', trade: 'coins', market: 'coins', pay: 'coins',
    paint: 'swish', brush: 'swish', trace: 'swish', drop: 'pop',
    // Назви, які кличуть горно, альбом, ярмарок і цех (їхні звіти інтеграторові).
    'rank-up': 'rare', brag: 'pop', deal: 'coins', refuse: 'fail', event: 'catch', 'rep-up': 'done',
    find: 'rare', stove: 'done', glue: 'pop', 'kiln-light': 'kiln', 'kiln-roar': 'kiln', ding: 'rare',
  };

  // ---------- відчуття: пружина, руки, трус, спалах ----------

  const HANDS = (() => {
    // Ліва рука: рукав сорочки знизу зліва, долоня обіймає виріб збоку; права — дзеркально.
    const one = '<path d="M7 108L21 78" stroke="#f4efe3" stroke-width="9" stroke-linecap="round"/>'
      + '<path d="M19.4 81.4L21.8 76.4" stroke="#d7372b" stroke-width="9.4"/><path d="M19.4 81.4L21.8 76.4" stroke="#f2c230" stroke-width="9.4" stroke-dasharray="1 2"/>'
      + '<path d="M20 76c-1-6 2-11 6-13 3-1.4 5.4.6 5 3.6l-1 8c-.5 3.4-3.6 5.4-6.6 4.6z" fill="#e2b68c" stroke="#b8865a" stroke-width=".7"/>'
      + '<path d="M26.5 64.5c1.6-1.4 3.4-.6 3.2 1.2M27 69c1.4-1 3-.4 2.8 1M26.6 73.4c1.2-.8 2.6-.3 2.4.9" stroke="#b8865a" stroke-width=".6" fill="none"/>';
    return '<g class="clks-hands" aria-hidden="true"><g class="clks-hand l"><g class="clks-rub">' + one + '</g></g>'
      + '<g class="clks-hand r"><g class="clks-rub"><g transform="translate(100 0) scale(-1 1)">' + one + '</g></g></g></g>';
  })();

  function squash(st, big) {
    const el = st.scn && st.scn.squash;
    if (!el || REDUCED() || !el.animate) return;
    try {
      if (st.scn.squashAnim) st.scn.squashAnim.cancel();
      const k = big ? 1.6 : 1;
      st.scn.squashAnim = el.animate([
        { transform: 'scale(1, 1)' },
        { transform: 'scale(' + (1 + 0.1 * k) + ', ' + (1 - 0.12 * k) + ')', offset: 0.28 },
        { transform: 'scale(' + (1 - 0.05 * k) + ', ' + (1 + 0.07 * k) + ')', offset: 0.62 },
        { transform: 'scale(1, 1)' },
      ], { duration: big ? 460 : 260, easing: 'ease-out' });
    } catch { /* старий браузер без WAAPI на SVG — без пружини */ }
  }

  function shake(st) {
    if (REDUCED() || !st.stage.animate) return;
    try {
      st.stage.animate([
        { transform: 'translate(0, 0)' }, { transform: 'translate(-4px, 2px)' }, { transform: 'translate(4px, -2px)' },
        { transform: 'translate(-3px, -1px)' }, { transform: 'translate(2px, 1px)' }, { transform: 'translate(0, 0)' },
      ], { duration: 320, easing: 'ease-out' });
    } catch { /* без трусу */ }
  }

  function flash(st, x, y) {
    const el = document.createElement('div');
    el.className = 'clks-flash';
    el.style.setProperty('--fx', (x != null ? x : 50) + '%');
    el.style.setProperty('--fy', (y != null ? y : 55) + '%');
    st.api.fleeting(st.stage, el, 1200);
  }

  /// Подія гри → відчуття на сцені (звук грає окремо). sfx не знає st — сцена одна на картку, беремо всі змонтовані.
  function feel(name, o) {
    const kind = ALIAS[name] || name;
    for (const st of HClicker.mounted) {
      if (!st.scn || !st.el || !st.api.visible(st)) continue;
      if (kind === 'clay') { st.scn.clayAt = performance.now(); squash(st, false); }
      else if (kind === 'done') squash(st, true);
      else if (kind === 'break') shake(st);
      else if (kind === 'catch' || kind === 'rare') flash(st, o && o.x, o && o.y);
    }
  }

  // ---------- кнопка звуку в куті сцени ----------

  const SPK = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 9h4l5-4v14l-5-4H4z" fill="currentColor"/>'
    + '<g class="on"><path d="M16 8.5a5 5 0 0 1 0 7M18.5 6a8.5 8.5 0 0 1 0 12" stroke="currentColor" stroke-width="1.8" fill="none" stroke-linecap="round"/></g>'
    + '<g class="off"><path d="M16.5 9.5l5 5M21.5 9.5l-5 5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></g></svg>';

  function mountSound(st, api) {
    const box = document.createElement('div');
    box.className = 'clks-snd';
    // ♪ — сусідка 🔊, а не жилець поповера: поки вона ховалась усередині, награвання ніхто так і не ввімкнув.
    box.innerHTML = '<button type="button" class="clks-sndbtn" aria-label="Звук гри"></button>'
      + '<button type="button" class="clks-sndmus" aria-pressed="false" aria-label="Фонове награвання">♪</button>'
      + '<div class="clks-sndpop"><input type="range" min="0" max="100" step="1" aria-label="Гучність гри">'
      + '<button type="button" class="ghost small clks-sndoff">вимкнути</button></div>';
    st.stage.appendChild(box);
    const btn = box.querySelector('.clks-sndbtn');
    const range = box.querySelector('input');
    const off = box.querySelector('.clks-sndoff');
    const mus = box.querySelector('.clks-sndmus');
    btn.innerHTML = SPK;
    const hover = !!(window.matchMedia && window.matchMedia('(hover: hover)').matches);
    let closeAt = 0;
    const paint = () => {
      box.classList.toggle('on', Snd.on);
      btn.title = Snd.on ? 'Звук гри увімкнено' + (hover ? ' — клацни, щоб вимкнути' : '') : 'Звук гри вимкнено — клацни, щоб увімкнути';
      mus.classList.toggle('on', Mus.on);
      mus.setAttribute('aria-pressed', Mus.on ? 'true' : 'false');
      mus.title = Mus.on ? 'Награвання грає — клацни, щоб стихло' : 'Фонове награвання: бандура й сопілка — клацни, щоб заграло';
      range.value = String(Math.round(Snd.vol * 100));
    };
    const openPop = () => {
      box.classList.add('open');
      clearTimeout(closeAt);
      closeAt = setTimeout(() => box.classList.remove('open'), 4000);
    };
    btn.addEventListener('click', (e) => {
      if (!e.isTrusted) return;
      if (!Snd.on) { Snd.setOn(true, api); openPop(); }
      else if (hover || box.classList.contains('open')) { Snd.setOn(false, api); box.classList.remove('open'); }
      else openPop();
      paint();
    });
    off.addEventListener('click', () => { Snd.setOn(false, api); box.classList.remove('open'); paint(); });
    // Музика живе в тому самому контексті: вмикаєш її при вимкненому звуці — вмикається й звук.
    mus.addEventListener('click', () => {
      if (!Snd.on && !Mus.on) Snd.setOn(true, api);
      Mus.setOn(!Mus.on, api);
      openPop();
      paint();
    });
    range.addEventListener('input', () => { Snd.setVol(+range.value / 100, api); openPop(); });
    range.addEventListener('change', () => Snd.play('tap'));
    // Звук був увімкнений минулого разу — контекст створимо на перший жест у картці.
    const wake = () => { if (Snd.on) { Snd.ensure(); Mus.start(); } };
    st.el.addEventListener('pointerdown', wake, { capture: true });
    st.scn.sound = { box, paint, wake, closeAt: () => clearTimeout(closeAt) };
    paint();
  }

  // ---------- цілі для смуги «Шлях виробу» ----------

  /// Інші частини можуть додати свої цілі: HClicker.goals.push((st, v, api) => ({ icon, text, sub, pct, eta, tab, prio }) | null).
  const GOALS = (window.HClicker.goals = window.HClicker.goals || []);

  function goalOf(st, api) {
    const v = st.lastView;
    if (!v) return null;
    const pots = Math.max(0, st.shown);
    const total = (st.total || 0) + Math.max(0, st.shown - st.base);
    const rate = Math.max(0, st.baseSecond || 0);
    const eta = (left) => (left <= 0 ? 0 : rate > 0 ? left / rate : Infinity);
    const list = [];
    const c = v.craft;
    // Сушарня повна — це не ціль, а затор: підмайстри стоять.
    if (c && c.rack && c.rackSize && c.rack.length >= c.rackSize) {
      list.push({ icon: EMBLEM.kiln, text: 'Сушарня повна — обпали сухе', sub: 'підмайстри стоять, поки нема місця', pct: 100, eta: -1, tab: 'craft', prio: 0 });
    }
    // Верстати: що вже можна купити (найкоротша окупність) або що найближче за часом.
    let bestNow = null;
    let near = null;
    for (const [k, u] of Object.entries(st.ups || {})) {
      if (u.open === false || (u.max > 0 && u.level >= u.max)) continue;
      const left = u.price - pots;
      const g = { icon: EMBLEM[k] || EMBLEM.wheel, key: k, text: u.name + ' · рівень ' + (u.level + 1), pct: (pots / u.price) * 100, eta: eta(left), tab: 'shop', row: '[data-buy="' + k + '"]' };
      if (left <= 0) {
        const pay = u.gain > 0 ? u.price / u.gain : Infinity;
        if (!bestNow || pay < bestNow.pay) bestNow = Object.assign(g, { pay, sub: 'уже можна купити' + (Number.isFinite(pay) ? ' · окупиться за ' + api.span(pay) : '') });
      } else if (!near || g.eta < near.eta) near = g;
    }
    for (const m of st.markList || []) {
      const left = m.price - pots;
      const on = m.on || String(m.key).split(':')[0];
      const g = { icon: EMBLEM[on] || EMBLEM.wheel, text: 'Віха «' + m.name + '» · ' + m.desc, pct: (pots / m.price) * 100, eta: eta(left), tab: 'shop', row: '[data-mark="' + m.key + '"]' };
      // Віха ×2 — найвигідніше, що буває: якщо вже по кишені, пропонуємо першою.
      if (left <= 0) {
        if (!bestNow || bestNow.pay !== -1 || m.price < bestNow.price) bestNow = Object.assign(g, { pay: -1, price: m.price, sub: 'уже можна купити — ×2 назавжди' });
      }
      else if (!near || g.eta < near.eta) near = g;
    }
    if (bestNow) list.push(Object.assign(bestNow, { prio: 1 }));
    // Новий виріб на колі.
    if (c && c.wares) {
      const next = c.wares.filter((w) => !w.open && w.unlock > total).sort((a, b) => a.unlock - b.unlock)[0];
      if (next) {
        const left = next.unlock - total;
        const g = { icon: '<g transform="translate(16 30) scale(.36) translate(-50 -86)">' + (api.wareSvg(next.key, { wrap: false, quality: 1, slot: 'goal-' + next.key }) || '') + '</g>',
          text: 'Новий виріб: ' + next.name, sub: 'на ' + api.potsShort(next.unlock) + ' за весь час', pct: (total / next.unlock) * 100, eta: eta(left), tab: null, prio: 3 };
        if (!near || g.eta < near.eta * 0.6) near = g; else list.push(g);
      }
    }
    if (near) list.push(Object.assign(near, { prio: 2 }));
    for (const fn of GOALS) {
      try { const g = fn(st, v, api); if (g) list.push(g); } catch (e) { console.error('[clicker:scene] goal', e); }
    }
    list.sort((a, b) => (a.prio ?? 5) - (b.prio ?? 5) || (a.eta ?? Infinity) - (b.eta ?? Infinity));
    return list[0] || null;
  }

  /// Ціль віддаємо смузі «Шлях виробу» (clicker-craft.js): свого банера сцена більше не малює — один голос підказок.
  function shareGoal(api) {
    api.goalOf = (st) => {
      try { return st.mine ? goalOf(st, api) : null; } catch (e) { console.error('[clicker:scene] goal', e); return null; }
    };
  }

  // ---------- «Поки тебе не було» ----------

  function awaySpan(sec, api) {
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = Math.floor(sec % 60);
    if (h) return h + ' год' + (m ? ' ' + m + ' хв' : '');
    if (m) return m + ' хв' + (m < 10 && s ? ' ' + s + ' с' : '');
    return s + ' с';
  }

  function showAway(st, api) {
    const v = st.lastView;
    const a = v && v.away;
    if (!a || !a.at || !st.mine || !api.visible(st) || api.guardOn(st)) return false;
    if (api.storeGet('clk.awayAt', '') === String(a.at)) return true;
    if (api.overlayOpen(st)) return false;          // інша панель (мінігра) — покажемо, щойно закриють
    api.storeSet('clk.awayAt', String(a.at));
    const esc = (x) => api.esc(st, x);
    const offline = st.offlineMs / 1000;
    const capped = a.seconds > offline + 60;
    const notes = (a.notes || []).map((n) => '<li>' + esc(n) + '</li>').join('');
    const formed = a.formed > 0
      ? '<div class="clks-away-row">' + svg32(EMBLEM.apprentice) + '<span>Підмайстри виліпили <b>' + api.num(a.formed) + ' ' + api.plural(a.formed, 'виріб', 'вироби', 'виробів')
        + '</b> — сохнуть на сушарні</span></div>'
      : '';
    const night = st.stage.dataset.tod === 'night';
    const body = api.overlay(st, '<div class="clks-away">'
      + '<div class="clks-away-art">' + awayArt(night) + '</div>'
      + '<div class="clks-away-head">Поки тебе не було — ' + awaySpan(a.seconds, api) + '</div>'
      + '<div class="clks-away-pots">+' + esc(api.potsShort(a.pots)) + '</div>'
      + '<div class="muted small">' + (a.pots > 0 ? 'коло крутилось без тебе' + (capped ? ' (перші ' + Math.round(offline / 3600) + ' год — далі й підмайстри сплять)' : '') : 'підмайстрів ще нема — коло стояло') + '</div>'
      + formed + (notes ? '<ul class="clks-away-notes">' + notes + '</ul>' : '')
      + '<button type="button" class="primary clks-away-go">До кола!</button></div>', { cls: 'clks-awayov' });
    const go = body.querySelector('.clks-away-go');
    if (go) go.onclick = () => api.closeOverlay(st);
    api.sfx('away');
    return true;
  }

  function awayArt(night) {
    return '<svg viewBox="0 0 220 90" aria-hidden="true"><defs><linearGradient id="clks-awsky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + (night ? '#0b1430' : '#3d82c4') + '"/>'
      + '<stop offset="1" stop-color="' + (night ? '#2a2f5a' : '#e9a56a') + '"/></linearGradient></defs>'
      + '<rect width="220" height="90" rx="10" fill="url(#clks-awsky)"/>'
      + (night ? '<circle cx="176" cy="22" r="9" fill="#f1ecd6"/><circle cx="180" cy="19" r="8.5" fill="#0f1838"/><g fill="#f4f1e0"><circle cx="30" cy="16" r="1"/><circle cx="80" cy="10" r="1.2"/><circle cx="130" cy="24" r=".8"/></g>'
        : '<circle cx="176" cy="30" r="12" fill="#ffd45a"/>')
      + '<path d="M0 64C50 54 120 60 220 52V90H0z" fill="#3b5a3a"/>'
      + '<g transform="translate(40 76)"><path d="M-22 0V-22h44V0z" fill="#efe6d2"/><path d="M-27 -20L0 -40l27 20z" fill="#b89150"/><rect x="-6" y="-14" width="10" height="14" fill="#6b4423"/><rect x="8" y="-17" width="8" height="7" fill="#ffd27a"/></g>'
      + '<g transform="translate(118 80)"><ellipse rx="26" ry="4" fill="rgba(0,0,0,.25)"/>'
      + [[-18, '#c56b35'], [-6, '#2b2a2f'], [6, '#e6ddcb'], [18, '#8b3a22'], [-12, '#c56b35', -14], [0, '#d9884a', -14], [12, '#2b2a2f', -14]]
        .map(([x, c, y]) => '<g transform="translate(' + x + ' ' + ((y || 0) - 2) + ') scale(.42) translate(-16 -30)"><path d="' + JUG_32 + '" fill="' + c + '"/></g>').join('')
      + '</g><g transform="translate(64 60)"><path d="M-9 0c-1-6 1.5-10 5.5-10.5 4-.6 6.5 2.6 6.5 6.4 0 2-.6 3.2-1.4 4z" fill="#3a3330"/>'
      + '<circle cx="-5" cy="-12.5" r="4.5" fill="#3a3330"/><path d="M-9 -15l.6-5 3.2 3.2zM-1.6 -15l-.6-5-3.2 3.2z" fill="#3a3330"/><path d="M-7 -12.6h1.6M-4 -12.6h1.6" stroke="#f6d23a" stroke-width=".6"/>'
      + '<path d="M2 -1c5 1 7-3 5-6" stroke="#3a3330" stroke-width="2" fill="none" stroke-linecap="round"/></g></svg>';
  }

  // ---------- вкладки: активна завжди в полі зору ----------

  function scrollTabs(st) {
    const tabs = st.tabs;
    const b = tabs && tabs.querySelector('[data-tab].active');
    if (!b || tabs.scrollWidth <= tabs.clientWidth + 2) return;
    const left = b.offsetLeft - (tabs.clientWidth - b.offsetWidth) / 2;
    tabs.scrollTo({ left: Math.max(0, left), behavior: REDUCED() ? 'auto' : 'smooth' });
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'scene',
    order: 20,

    mount(st, api) {
      st.api = api;
      st.scn = {
        vars: {}, sky: { season: 'summer', moon: 8, night: false }, skyAt: 0, houseSig: '', tab: '', clayAt: 0,
        handsOn: false, awayPending: false, clay: null, squash: st.el.querySelector('.clk-squash'),
      };
      Snd.load(api);
      Mus.load(api);
      api.sfx = (name, o) => {
        try { feel(name, o); Snd.play(String(name || ''), o); } catch (e) { console.error('[clicker:scene] sfx', e); }
      };
      api.upIcon = (key) => svg32(EMBLEM[key] || EMBLEM.wheel);
      api.toolIcon = (key) => (TOOL_ART[key] ? '<svg viewBox="-16 -2 32 50" aria-hidden="true">' + TOOL_ART[key] + '</svg>' : '');
      api.decorIcon = (key) => (DECOR_ICON[key] ? svg32(DECOR_ICON[key]) : '');
      api.scene = SCENE;
      // Сцена поза екраном (прокрутили до майстерні чи чату) — CSS-анімації хати на паузі: це основна робота картки без дій.
      if (window.IntersectionObserver && st.stage) {
        st.scn.io = new IntersectionObserver((es) => { for (const e of es) st.stage.classList.toggle('clks-off', !e.isIntersecting); });
        st.scn.io.observe(st.stage);
      }
      /// Хата друга (цех): той самий малюнок із публічного знімка — драбина, знаряддя, прикраси; небо й пора — наші.
      api.houseSvg = (st2, h) => {
        const ups = {};
        for (const l of (h && h.ladder) || []) ups[l.key] = { level: l.level };
        const owned = (keys) => (keys || []).map((key) => ({ key, owned: true }));
        const fake = { upgrades: ups, house: { tools: owned(h && h.tools), decor: owned(h && h.decor) }, secrets: [] };
        const sky = (st2 && st2.scn && st2.scn.sky) || st.scn.sky;
        // Свої id (градієнти хати друга не мусять зникати, коли головну сцену сховало Око майстра) і ті самі змінні неба.
        const vars = [...((st.stage && st.stage.style) || [])].filter((k) => k.startsWith('--clks-'))
          .map((k) => k + ':' + st.stage.style.getPropertyValue(k)).join(';');
        const art = houseSvg(envOf(st2 || st, fake, sky)).replace(/(id="|url\(#|href="#)clks-/g, '$1clksf-');
        return '<svg class="clks-friend" viewBox="0 0 360 450" aria-hidden="true" style="' + vars + '">' + art + '</svg>';
      };
      // Руки гончаря — у SVG кола, над виробом (не обертаються з кругом).
      const wsvg = st.wheel && st.wheel.querySelector('svg');
      if (wsvg && !wsvg.querySelector('.clks-hands')) {
        const tmp = document.createElementNS(SVGNS, 'svg');
        tmp.innerHTML = HANDS;
        if (tmp.firstChild) wsvg.appendChild(tmp.firstChild);
      }
      st.scn.hands = st.wheelBox;
      mountSound(st, api);
      shareGoal(api);
      st.scn.skyAt = 0;
      st.scn.sky = paintSky(st, sceneNow(st, api));
      // Ядро вже перемалювало полиці до того, як ми підмінили значки: попросити їх ще раз з новими значками.
      if (st.shop) st.shop._sig = null;
      if (st.marks) st.marks._sig = null;
      if (st.housePane) st.housePane._sig = null;
    },

    update(st, v, api) {
      paintHouse(st);
      // Бризки й грудка — кольору глини на колі.
      const clay = st.clayBody || '';
      if (st.scn.clay !== clay) {
        st.scn.clay = clay;
        if (clay) st.sparks.style.setProperty('--clay', clay); else st.sparks.style.removeProperty('--clay');
      }
      if (v.away && api.storeGet('clk.awayAt', '') !== String(v.away.at)) st.scn.awayPending = true;
    },

    frame(st, api) {
      const scn = st.scn;
      const t = performance.now();
      // Руки біля кола: одразу після кліка і поки коло розігріте.
      const heat = st.heatFull ? Math.min(1, (st.heat * Math.exp(-Math.max(0, Date.now() - st.heatAt) / 1000 / (st.heatTau || 3))) / st.heatFull) : 0;
      const on = !api.guardOn(st) && (t - scn.clayAt < HANDS_MS || heat > 0.25);
      if (on !== scn.handsOn) { scn.handsOn = on; st.wheelBox.classList.toggle('clks-handson', on); }
      // Той самий жар, що й руки, жене темп награвання: розкрутив коло — музика підхоплює.
      if (Mus.on) Mus.drive(api.guardOn(st) ? 0 : heat);
    },

    slow(st, api, now) {
      const scn = st.scn;
      const t = Date.now();
      if (t - scn.skyAt > SKY_MS || (window.HClicker.sceneTime != null && scn.forced !== HClicker.sceneTime)) {
        scn.skyAt = t;
        scn.forced = window.HClicker.sceneTime;
        scn.sky = paintSky(st, sceneNow(st, api));
        paintHouse(st);
      }
      if (st.tab !== scn.tab) { scn.tab = st.tab; scrollTabs(st); }
      if (scn.awayPending && showAway(st, api)) scn.awayPending = false;
    },

    unmount(st, api) {
      if (!st.scn) return;
      if (st.scn.io) st.scn.io.disconnect();
      // Остання картка закрилась — музика й звук засинають зовсім.
      if (HClicker.mounted.size <= 1) {
        Mus.stop();
        if (Snd.ctx && Snd.ctx.state === 'running') Snd.ctx.suspend().catch(() => {});
      }
      if (st.scn.sound) { st.scn.sound.closeAt(); if (st.el) st.el.removeEventListener('pointerdown', st.scn.sound.wake, { capture: true }); }
      st.scn = null;
    },
  });
})();
