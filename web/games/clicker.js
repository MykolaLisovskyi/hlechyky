/*
  Гончарне коло. Соло-клікер: тиснеш на коло — ліпиш глеки, купуєш верстати й віхи, ловиш розписні глеки та глеки,
  що падають з полиці, збираєш розписи, обпалюєш майстерню за клейма майстра, міняєш глеки на черепки.

  Правила рахує сервер (Impl/Clicker.cs). Клієнт понад малювання робить рівно п'ять речей:
  1) батчить кліки — збирає відбитки справжніх натисків і шле Act('spin', { c: [[dt, press, x, y, src], …] }) раз на
     700 мс, а не двадцять разів за секунду. Рахуються лише isTrusted-натиски на коло (pointerdown → pointerup) і
     пробіл без автоповтору (keydown → keyup): el.click() чи dispatchEvent зі скрипта кліком не стають, а сервер за
     відбитками впізнає мишачий софт (Impl/ClickerGuard.cs);
  2) доліковує лічильник між подіями 'room' — за view.baseSecond і ярмарком, зі стелею офлайну, як на сервері.
     Простій беремо серверний (view.now − view.lastSync) і додаємо лише те, що натікало ВІД отримання виду, —
     так збитий годинник у гравця не малює неіснуючих глеків. Прийшов новий вид — беремо його число, а не своє;
  3) веде той самий рахунок розгону, що й сервер (view.heat спадає за heatTau, множник — від heatFull і momentumMax),
     щоб «+N» над колом і лічильник обіцяли те, що сервер справді дорахує;
  4) показує розписний глек у його вікні (view.golden) і глек з полиці (view.fall) у його три секунди польоту; коли
     той чи той утік/розбився — питає наступний розклад Act('look');
  5) показує Око майстра (view.guard): інструкцію з кнопкою «Показати полицю», за нею полицю-картинку, де треба
     торкнутись усіх глечиків (Act('answer', { taps })), або паузу кола з відліком. Доки кнопку не натиснуто,
     торкання картинки не рахуються (інакше швидкі кліки по колу проклацували полиці наосліп). Де глечики —
     клієнт не знає: це знає лише сервер.

  Вид (Impl/Clicker.cs): { pots, total, perClick, clickBase, perSecond, baseSecond,
    upgrades: { key: { level, price, name, desc, max, kind, gain, growth, marks, open } }, marks: [...],
    canSellToday, soldToday, cap, rate, lastSync, now, offlineHours, golden: { at, until, x, y }, caught,
    fair: { until, mult }, inspire: { until, mult }, allMult, stamps, stampsFree, stampsReady, nextStampAt,
    stampBonus, stampCap, firings, secrets: [...], styles: [...], wear,
    heat, heatFull, heatTau, momentum, momentumMax, fall: { at, until, x, streak, gain }, grabbed,
    guard: null | { serial, count, png, width, height, misses, maxMisses, lockUntil, why } }.
  Дії: spin { c }, buy { key, n }, mark { key }, sell { pots }, catch, grab, look, fire, secret { key }, paint { key },
    wear { key }, answer { taps: [[x, y], …] }.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<ellipse cx="8" cy="12.2" rx="6.2" ry="2.3" fill="none" stroke="var(--muted)" stroke-width="1.3"/>'
    + '<path d="M5.6 10.8V7.4c0-1 .8-1.3.8-2.1V3.6h3.2v1.7c0 .8.8 1.1.8 2.1v3.4z" fill="var(--clay)"/></svg>';

  const BATCH_MS = 700;                   // як часто злітає накопичена пачка кліків
  const MAX_BATCH = 12;                   // рівно стільки сервер приймає за секунду
  const MAX_BUY = 1000;                   // стільки рівнів сервер купує одним натиском
  const STAMP_UNIT = 1e9;                 // клейма = ⌊√(усього / мільярд)⌋, як на сервері
  const STAMPS_PER_CAP = 10;              // +1 черепок до денної стелі за кожні 10 клейм
  const CATCH_GRACE_MS = 2000;            // той самий запас, що й на сервері: після нього глек уже не спіймати
  const BOARD_MS = 60 * 1000;             // як часто перепитуємо таблицю «Гончарне коло» для рядка про суперника
  const SLOW_MS = 200;                    // таймери бонусів, прогрес клейм — не частіше, ніж так
  const HOLD_MS = 3000;                   // тримали довше — це вже не клік
  const RING = 295.3;                     // довжина кільця розгону (2π · 47)
  /// Чим клацнули: ті самі номери, що й ClickerGuard.Source на сервері.
  const SRC = { mouse: 0, touch: 1, pen: 2, key: 3 };

  /// Розділи Майстерні: колишні вкладки, що тепер згортаються всередині неї. `open` — коли розділ варто
  /// розгорнути самому (доки гравець не вирішив інакше й не лишив по собі clk.sec.<key>).
  const SECTIONS = [
    { key: 'house', title: '🏠 Хата', open: (st) => st.tools.some((t) => t.owned) || st.clays.some((c) => !c.owned && c.price > 0 && c.price <= st.shown) },
    { key: 'styles', title: '🎨 Розписи', open: (st) => st.styleList.some((x) => x.owned) },
    { key: 'orders', title: '🐴 Вклад купцям', open: (st) => st.taken.length > 0 },
  ];

  // ---------- частини (сьоме оновлення, docs/games/specs/clicker-v7.md §3) ----------

  /// Ремесло, жива хата, горно, альбом, ярмарок і цех живуть в окремих файлах clicker-<id>.js (+ .css): інакше
  /// цей файл виріс би втричі, а паралельні роботи бились би в одному місці. Частина кличе HClicker.part({...}) і
  /// дістає ті самі st, що й ядро, плюс спільний api. Каркас ігор знає лише clicker.js — частини вантажимо самі.
  const PART_IDS = ['craft', 'scene', 'kiln', 'album', 'fair', 'guild'];
  const H = window.HClicker = window.HClicker || { parts: [], mounted: new Set(), loaded: false };

  /// Одна частина впала — решта гри живе далі: помилку в консоль, а не білу картку.
  function callPart(p, hook, ...args) {
    if (typeof p[hook] !== 'function') return;
    try { p[hook](...args); } catch (e) { console.error('[clicker:' + p.id + '] ' + hook, e); }
  }

  function mountPart(st, p) {
    if (st.parts.has(p.id)) return;
    st.parts.add(p.id);
    callPart(p, 'mount', st, H.api);
    // Частина догнала вже відкриту картку: віддати їй останній вид і перемалювати картку — ремесло й хата дають
    // іншим частинам силуети й значки, і без цього гравець без дій так і дивився б на заглушки.
    if (st.lastView) { callPart(p, 'update', st, st.lastView, H.api); refreshCard(st); }
  }

  /// Перемалювати картку з останнім видом: скинути підписи swap() і прогнати update ядра й частин. Раз на пачку запізнілих.
  function refreshCard(st) {
    if (st.refreshT) return;
    st.refreshT = setTimeout(() => {
      st.refreshT = 0;
      if (!st.el || !st.ctx || !st.lastView || !st.root) return;
      for (const el of st.el.querySelectorAll('*')) if (el._sig !== undefined) el._sig = null;
      if (st.jugBox) st.jugBox._wear = null;
      MOD.update(st.root, st.ctx);
    }, 60);
  }

  H.part = (p) => {
    if (!p || !p.id || H.parts.some((x) => x.id === p.id)) return;
    H.parts.push(p);
    H.parts.sort((a, b) => (a.order || 50) - (b.order || 50));
    for (const st of H.mounted) if (st.el) mountPart(st, p);
  };

  if (!H.loaded) {
    H.loaded = true;
    for (const id of PART_IDS) {
      if (!document.querySelector('link[data-clk-part="' + id + '"]')) {
        const l = document.createElement('link');
        l.rel = 'stylesheet';
        l.href = '/games/clicker-' + id + '.css';
        l.dataset.clkPart = id;
        document.head.appendChild(l);
      }
      const sc = document.createElement('script');
      sc.async = false;                // виконуються в порядку PART_IDS: ремесло й хата раніше за тих, хто малює їхнім api
      sc.src = '/games/clicker-' + id + '.js';
      sc.onerror = () => console.warn('[clicker] частина ' + id + ' не завантажилась');
      document.head.appendChild(sc);
    }
  }

  // ---------- числа й слова ----------

  const num = (n) => Math.round(n).toLocaleString('uk-UA');
  /// «0,5» замість «0.5»: десяткова кома в нас усюди українська.
  const dec = (n) => (Math.round(n * 10) / 10).toLocaleString('uk-UA', { maximumFractionDigits: 1 });
  const plural = (n, one, few, many) => {
    n = Math.floor(Math.abs(n));
    return n % 100 >= 11 && n % 100 <= 14 ? many : n % 10 === 1 ? one : n % 10 >= 2 && n % 10 <= 4 ? few : many;
  };
  const shards = (n) => plural(n, 'черепок', 'черепки', 'черепків');
  const stampsWord = (n) => plural(n, 'клеймо', 'клейма', 'клейм');
  const potsWord = (n) => (n % 1 ? 'глека' : plural(n, 'глек', 'глеки', 'глеків'));
  /// «1,47 млн глеків», а не «1,47 млн глеки»: після скорочення слово узгоджується з «млн», а не з останньою цифрою.
  const potsShort = (n) => short(n) + ' ' + (Math.abs(n) >= 1e6 ? 'глеків' : potsWord(n));

  const BIG = ['млн', 'млрд', 'трлн', 'квдрлн', 'квнтлн'];
  /// «1,09 млн» замість «1 093 232»: мільярди цифрами не читаються. До мільйона — повне число, як на сервері.
  function short(n) {
    if (!Number.isFinite(n)) return '∞';
    if (Math.abs(n) < 1e6) return n % 1 ? dec(n) : num(n);
    const i = Math.min(BIG.length - 1, Math.floor(Math.log10(Math.abs(n)) / 3) - 2);
    const v = n / Math.pow(1000, i + 2);
    const digits = v < 10 ? 2 : v < 100 ? 1 : 0;
    return (Math.floor(v * Math.pow(10, digits)) / Math.pow(10, digits)).toLocaleString('uk-UA', { maximumFractionDigits: digits })
      + ' ' + BIG[i];
  }
  /// Великий лічильник: до трильйона кожна цифра (видно, як коло крутиться; «3 млрд» стояло б годинами),
  /// далі — коротко, але з трьома знаками.
  function big(n) {
    if (n < 1e12) return num(n);
    const i = Math.min(BIG.length - 1, Math.floor(Math.log10(n) / 3) - 2);
    const v = n / Math.pow(1000, i + 2);
    return (Math.floor(v * 1000) / 1000).toLocaleString('uk-UA', { maximumFractionDigits: 3 }) + ' ' + BIG[i];
  }

  /// «за 40 с», «за 12 хв», «за 3 год», «за 2 дні».
  function span(sec) {
    if (!Number.isFinite(sec) || sec <= 0) return '';
    if (sec < 90) return Math.ceil(sec) + ' с';
    if (sec < 90 * 60) return Math.round(sec / 60) + ' хв';
    if (sec < 36 * 3600) return dec(sec / 3600) + ' год';
    const d = Math.round(sec / 86400);
    return d + ' ' + plural(d, 'день', 'дні', 'днів');
  }

  /// Довгий абзац у значок ⓘ: прочитати можна, займати екран — не мусить. Тим самим користуються частини.
  const info = (text) => '<details class="clk-info"><summary>і</summary><p>' + text + '</p></details>';

  const storeGet = (k, dflt) => { try { return localStorage.getItem(k) || dflt; } catch { return dflt; } };
  const storeSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — пам'ятати нема де */ } };

  // ---------- глеки й розписи ----------

  /// Силует глечика в квадраті 100×100: вінця, тонка шийка, пузо, дно. Центр — x = 50.
  const JUG = 'M43 25h14v2.5c0 1.6-1.4 2.2-1.4 4 0 2.6 9.4 5.6 9.4 16.5 0 8.3-4.6 13.8-7.3 16H42.3'
    + 'C39.6 61.8 35 56.3 35 48c0-10.9 9.4-13.9 9.4-16.5 0-1.8-1.4-2.4-1.4-4z';

  const dots = (y, from, to, step, r, fill) => {
    let s = '';
    for (let x = from; x <= to + 0.01; x += step) s += '<circle cx="' + x.toFixed(1) + '" cy="' + y + '" r="' + r + '"/>';
    return '<g fill="' + fill + '">' + s + '</g>';
  };
  const flower = (red, heart, leaf) =>
    '<g fill="' + red + '"><circle cx="50" cy="44.6" r="2.4"/><circle cx="46.2" cy="47.4" r="2.4"/>'
    + '<circle cx="53.8" cy="47.4" r="2.4"/><circle cx="47.6" cy="51.6" r="2.4"/><circle cx="52.4" cy="51.6" r="2.4"/></g>'
    + '<circle cx="50" cy="48.6" r="1.7" fill="' + heart + '"/>'
    + '<path d="M50 54.5c0 3-2 5.5-5 6.5M50 54.5c0 3 2 5.5 5 6.5" stroke="' + leaf + '" stroke-width="1.2" fill="none"/>'
    + '<path d="M40.5 57c1.5-3 4-3.6 5.2-2.5-1 1.6-3.1 3-5.2 2.5zM59.5 57c-1.5-3-4-3.6-5.2-2.5 1 1.6 3.1 3 5.2 2.5z" fill="' + leaf + '"/>';

  /// Розписи з реальних осередків: колір тіла глека й орнамент поверх нього (обрізається по силуету).
  const STYLE = {
    '': { body: 'var(--clay)', decor: '<path d="M44 29.5h12" stroke="rgba(0,0,0,.18)" stroke-width="1"/>' },
    gavarets: {
      body: '#2b2a2f',
      decor: '<path d="M35.5 41l4.3 3.5 4.3-3.5 4.3 3.5 4.3-3.5 4.3 3.5 4.3-3.5 4.3 3.5" fill="none" stroke="#a19eab" stroke-width="1.1"/>'
        + '<path d="M35 53h30M35 55.6h30" stroke="#74717d" stroke-width=".8"/><path d="M44 29.5h12" stroke="#7d7a86" stroke-width=".9"/>',
    },
    vasylkiv: {
      body: '#f2e9d6',
      decor: '<path d="M35 38.5h30M35 60h30" stroke="#2f5fa8" stroke-width="2"/>'
        + '<g fill="#2f5fa8"><ellipse cx="50" cy="44" rx="1.8" ry="3"/><ellipse cx="50" cy="54" rx="1.8" ry="3"/>'
        + '<ellipse cx="45" cy="49" rx="3" ry="1.8"/><ellipse cx="55" cy="49" rx="3" ry="1.8"/></g>'
        + '<circle cx="50" cy="49" r="2.4" fill="#d99a2b"/>'
        + '<path d="M40.5 55c2-3 4-3 5-6M59.5 55c-2-3-4-3-5-6" stroke="#4f8a3a" stroke-width="1.1" fill="none"/>'
        + '<path d="M44 29.5h12" stroke="#d99a2b" stroke-width="1.2"/>',
    },
    bubnivka: {
      body: '#8b3a22',
      decor: dots(40.5, 37, 63, 3.25, 1.05, '#f4ead6')
        + '<path d="M35 49q3.75-4.2 7.5 0t7.5 0 7.5 0 7.5 0" stroke="#63a543" stroke-width="1.7" fill="none"/>'
        + dots(55.5, 38.5, 61.5, 4.6, 1.1, '#efc13a') + '<path d="M44 29.5h12" stroke="#f4ead6" stroke-width="1"/>',
    },
    kosiv: {
      body: '#ecdfc2',
      decor: '<path d="M35 39h30M35 59.5h30" stroke="#6b3b1b" stroke-width="1.4"/>'
        + '<path d="M35 42.4q3-2.2 6 0t6 0 6 0 6 0 6 0" stroke="#d6a21e" stroke-width="1.3" fill="none"/>'
        + '<path d="M37.5 57l3.2-11 3.2 11zM46.8 57l3.2-11 3.2 11zM56.1 57l3.2-11 3.2 11z" fill="#3f7d3a" stroke="#6b3b1b" stroke-width=".7"/>'
        + '<path d="M44 29.5h12" stroke="#3f7d3a" stroke-width="1.1"/>',
    },
    opishnia: {
      body: '#c56b35',
      decor: '<path d="M50 43v14" stroke="#f6efe2" stroke-width="1.2"/><ellipse cx="50" cy="41.5" rx="2" ry="3.1" fill="#3e7c3a"/>'
        + '<path d="M41 51.5c0-4.2 5.4-4.2 5.4 0 0 2.4-3.2 2.8-3.2.4M59 51.5c0-4.2-5.4-4.2-5.4 0 0 2.4 3.2 2.8 3.2.4" stroke="#f6efe2" stroke-width="1.3" fill="none"/>'
        + '<g fill="#4a2513"><circle cx="39.5" cy="44.5" r=".95"/><circle cx="60.5" cy="44.5" r=".95"/><circle cx="50" cy="59.5" r=".95"/></g>'
        + '<path d="M35 61.5h30" stroke="#f6efe2" stroke-width="1.1" stroke-dasharray="2 1.5"/>',
    },
    mezhyhirya: {
      body: '#f6f5ef',
      decor: '<path d="M35 37.5h30M35 61h30" stroke="#2b4f9e" stroke-width="2.2"/><path d="M35 40.6h30M35 58h30" stroke="#2b4f9e" stroke-width=".7"/>'
        + '<path d="M42.5 52.5c3.2-6.4 11.8-6.4 15 0-3.2-2-11.8-2-15 0z" fill="#2b4f9e"/><circle cx="50" cy="45.6" r="1.7" fill="#2b4f9e"/>',
    },
    petrykivka: {
      body: '#1e1c1d',
      decor: flower('#d7372b', '#f2c230', '#4c9a3f')
        + '<g fill="#f2c230"><circle cx="40" cy="42.4" r="1"/><circle cx="60" cy="42.4" r="1"/><circle cx="42.2" cy="39.8" r=".8"/><circle cx="57.8" cy="39.8" r=".8"/></g>',
    },
    trypillia: {
      body: '#d9884a',
      decor: '<path d="M35 36.5h30M35 62h30" stroke="#2a1a12" stroke-width="1.6"/><path d="M35 39.8h30M35 58.7h30" stroke="#f1e4cc" stroke-width=".8"/>'
        + '<path d="M37.6 49.4c0-5.2 7.2-5.2 7.2 0 0 3-4 3.4-4 .8M62.4 48.6c0 5.2-7.2 5.2-7.2 0 0-3 4-3.4 4-.8" stroke="#2a1a12" stroke-width="1.5" fill="none"/>'
        + '<path d="M44.8 49.4c2.6-4.4 7.8 3.6 10.4-.8" stroke="#2a1a12" stroke-width="1.5" fill="none"/>',
    },
    /// Той, що з'являється на колі й чекає, щоб його впіймали: золотий із петриківською квіткою.
    golden: {
      body: '#f2c14e',
      decor: flower('#c62f25', '#fff3c4', '#2f7d3a') + '<path d="M35 38.5h30" stroke="#c62f25" stroke-width="1.4"/>',
    },
  };

  /// Глек у SVG-групі: тіло, орнамент по силуету, обведення й відблиск. <slot> — постійне ім'я місця
  /// (колесо, картка розпису, розписний глек, полиця): id для clipPath мусить бути сталим, інакше HTML полиці
  /// щоразу виходив би новим і swap() перемальовував би її на кожну пачку кліків.
  /// <body> — колір глини на колі: простий глек без розпису беруть саме її кольору (червона, біла, чорна).
  function jug(style, slot, body) {
    const s = STYLE[style] || STYLE[''];
    const id = 'clkjug-' + slot;
    return '<g class="clk-jug"><clipPath id="' + id + '"><path d="' + JUG + '"/></clipPath>'
      + '<path d="' + JUG + '" fill="' + (!style && body ? body : s.body) + '"/>'
      + '<g clip-path="url(#' + id + ')">' + s.decor + '</g>'
      + '<path d="' + JUG + '" fill="none" stroke="rgba(0,0,0,.28)" stroke-width=".8"/>'
      + '<path d="M39.6 42.5c-1.6 3.8-1.6 10.6.4 15" stroke="rgba(255,255,255,.22)" stroke-width="1.8" fill="none" stroke-linecap="round"/></g>';
  }
  /// Окремий глек (картка розпису, розписний глек, глек з полиці): видноколо обрізане по самому глеку.
  const jugSvg = (style, cls, slot, body) => '<svg class="' + (cls || '') + '" viewBox="31 22 38 45" aria-hidden="true">' + jug(style, slot, body) + '</svg>';

  /// «12:34» для відліків купців і глини.
  function mmss(ms) {
    const s = Math.max(0, Math.ceil(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  // ---------- стан модуля ----------

  function state(root) {
    if (!root._clk) {
      root._clk = {
        el: null, count: null, rate: null, rival: null, sign: null, stage: null, wheelBox: null, wheel: null, turn: null,
        jugBox: null, heatRing: null, pops: null, sparks: null, fx: null, buffs: null, gold: null, fallEl: null, fallJug: null,
        shelfJugs: null, one: null, all: null, left: null, tabs: null, panes: {}, shop: null, modes: null, marks: null,
        styles: null, fire: null,
        buys: [], markBtns: [], styleBtns: [], secretBtns: [],
        base: 0, total: 0, lastSync: 0, viewNow: 0, recvAt: Date.now(), offlineMs: 8 * 3600 * 1000,
        clickBase: 1, baseSecond: 0, fairUntil: 0, fairMult: 7, inspireUntil: 0, inspireMult: 25,
        rateOf: 100, canSell: 0, mine: false, wear: null,
        golden: null, goldenGone: 0, lookedFor: 0,
        fall: null, fallGone: 0, fallBroke: 0, fallLooked: 0, fallGain: 0, fallStreak: 0,
        // Хата: глина, знаряддя, прикраси, дошка купців (view.house) і сцена, що від них росте.
        house: null, housePane: null, ordersPane: null, clays: [], clay: '', clayBody: '', clayRestUntil: 0,
        tools: [], decorList: [], orders: [], taken: [], paidSeen: null, payLooked: new Set(), refreshAt: 0, maxTaken: 3,
        houseBtns: [], clayBtns: [], orderBtns: [], cds: [],
        heat: 0, heatAt: Date.now(), heatFull: 18, heatTau: 3, momentumMax: 1,
        angle: 0, angleAt: 0, ringOff: -1, glow: -1, jugScale: -1,
        stamps: 0, stampsFree: 0, stampBonus: 0.02, fireArmed: 0,
        ups: {}, markList: [], styleList: [], secretList: [],
        tab: storeGet('clk.tab', 'shop'), mode: storeGet('clk.mode', '1'),
        hands: [], handsGain: 0, inflight: 0, inflightGain: 0, tokens: MAX_BATCH, tokensAt: Date.now(), shown: -1, slowAt: 0,
        downs: new Map(), keyDown: 0, lastDown: 0, onKeyUp: null,
        guard: null, eye: null, taps: [], eyeBusy: false, eyeKey: '', eyeOpen: false, eyeAt: 0, eyeArm: 0,
        raf: 0, timer: 0, boardAt: 0, board: null, ctx: null,
        // Частини (clicker-<id>.js): які вже змонтовані, підписи їхніх вкладок, останній вид для запізнілих.
        parts: new Set(), tabText: {}, lastView: null, catalog: null, catalogAsked: false, front: null, back: null, ov: null,
      };
    }
    return root._clk;
  }

  /// Картку справді видно: вона в документі, панель ігор не схована і вкладка браузера на передньому плані.
  const visible = (st) => !!st.el && st.el.isConnected && !document.hidden && st.el.getClientRects().length > 0;

  /// Серверне «зараз» у мс: мітка з виду плюс те, що минуло на нашому годиннику від його отримання.
  const serverNow = (st) => st.viewNow + (Date.now() - st.recvAt);

  /// Пасив від мітки сервера, округлений УНИЗ: у сервера ще лежить дробовий залишок, тож це чесна нижня межа.
  /// Ярмарок множить лише ту частину проміжку, яку він справді тривав, — рівно як Sync() на сервері.
  function passive(st) {
    const to = serverNow(st);
    const idle = Math.min(Math.max(0, to - st.lastSync), st.offlineMs);
    let fair = st.fairUntil > st.lastSync ? Math.min(st.fairUntil, to) - st.lastSync : 0;
    fair = Math.min(Math.max(0, fair), idle);
    return Math.floor(((idle + (st.fairMult - 1) * fair) / 1000) * st.baseSecond);
  }

  /// Те, що сервер уже точно має: його число плюс пасив. Від нього рахуємо продаж.
  const firm = (st) => st.base + passive(st);

  function clickNow(st) {
    const now = serverNow(st);
    return st.clickBase * (now < st.inspireUntil ? st.inspireMult : 1) * (now < st.fairUntil ? st.fairMult : 1);
  }
  const secondNow = (st) => st.baseSecond * (serverNow(st) < st.fairUntil ? st.fairMult : 1);

  /// Розгін просто зараз — той самий спад, що й на сервері: у e разів за heatTau секунд.
  const heatNow = (st) => st.heat * Math.exp(-Math.max(0, Date.now() - st.heatAt) / 1000 / st.heatTau);
  /// Множник кліка від розгону: ×1 на холодному колі, стеля маховика — на heatFull гарячих кліках.
  const momentumOf = (st, heat) => 1 + (st.momentumMax - 1) * Math.min(1, Math.max(0, heat) / st.heatFull);
  const heatFrac = (st) => Math.min(1, heatNow(st) / st.heatFull);

  /// Скільки рівнів влазить у глеки і скільки вони коштують. Геометрична сума: на сервері кожна ціна
  /// округлюється вгору, тож тут це оцінка — купує однаково сервер, і рівно стільки, скільки влізе.
  function afford(u, pots, want) {
    const g = u.growth || 1.5;
    const room = u.max > 0 ? Math.max(0, u.max - u.level) : MAX_BUY;
    let n;
    if (want === 'max') {
      n = pots < u.price ? 0 : Math.floor(Math.log(1 + (pots * (g - 1)) / u.price) / Math.log(g));
    } else n = +want;
    n = Math.max(0, Math.min(n, room, MAX_BUY));
    const cost = n ? (u.price * (Math.pow(g, n) - 1)) / (g - 1) : u.price;
    return { n, cost };
  }

  // ---------- малювання ----------

  /// Кличеться на кожен кадр: і число, і кнопки мусять оживати самі, поки коло крутиться без кліків.
  function paint(st) {
    const now0 = Date.now();
    // Підтверджене число рахуємо один раз: від нього і лічильник (з нашими ще не відправленими кліками),
    // і кнопки прилавка (уже без них).
    const sure = firm(st);
    let n = sure + st.handsGain + st.inflightGain;
    // Дрібний відкат — це не витрата, а різниця округлень між нашим доліком і сервером: не смикаємо число.
    if (st.shown >= 0 && n < st.shown && st.shown - n <= 2) n = st.shown;
    if (n !== st.shown) {
      st.shown = n;
      const text = big(n);
      st.count.textContent = text;
      // «999 999 999 999» на телефоні не влазить у звичний кегль — зменшуємо, а не переносимо.
      const long = text.length > 11;
      if (st.count.classList.contains('long') !== long) st.count.classList.toggle('long', long);
    }

    for (const b of st.buys) {
      const u = st.ups[b.dataset.buy];
      if (!u) continue;
      const maxed = u.max > 0 && u.level >= u.max;
      const a = afford(u, n, st.mode);
      const off = maxed || !st.mine || a.n < 1 || (st.mode !== 'max' && n < a.cost);
      if (b.disabled !== off) b.disabled = off;
      const label = maxed ? 'досить' : (a.n > 1 ? '×' + a.n + ' · ' : '') + short(Math.ceil(a.cost));
      if (b._price.textContent !== label) b._price.textContent = label;
      // Смужка «скільки ціни вже є» — кроком у 2 %, щоб не писати стиль щокадру.
      if (b._bar) {
        const pct = maxed ? 100 : Math.min(100, Math.floor((n / Math.max(1, st.mode === 'max' ? u.price : a.cost)) * 50) * 2);
        if (b._pct !== pct) { b._pct = pct; b._bar.style.width = pct + '%'; }
      }
    }
    for (const b of st.markBtns) {
      const off = !st.mine || n < +b.dataset.price;
      if (b.disabled !== off) b.disabled = off;
    }
    for (const b of st.styleBtns) {
      const off = !st.mine || (b.dataset.owned !== '1' && n < +b.dataset.price);
      if (b.disabled !== off) b.disabled = off;
    }
    // Знаряддя й прикраси — одноразові: куплене лишається сірим, некуплене чекає глеків.
    for (const b of st.houseBtns) {
      const off = !st.mine || b.dataset.owned === '1' || n < +b.dataset.price;
      if (b.disabled !== off) b.disabled = off;
    }
    // Купці: замовлення на розпис — лише за розпис із колекції; купців у дорозі — не більше трьох.
    for (const b of st.orderBtns) {
      const invest = b.dataset.kind === 'invest';
      const off = !st.mine || b.dataset.can !== '1' || n < +b.dataset.need || (invest && st.taken.length >= st.maxTaken);
      if (b.disabled !== off) b.disabled = off;
    }

    // Продаж — від підтвердженого числа, а не від намальованого: у st.hands може лежати хвіст кліків,
    // які цієї миті ще не долетіли, і кнопка обіцяла б сервером не наліплені глеки.
    const ready = Math.floor(sure / st.rateOf);
    const many = Math.min(ready, st.canSell);
    const one = !(st.mine && ready >= 1 && st.canSell >= 1);
    if (st.one.disabled !== one) st.one.disabled = one;
    const all = !(st.mine && many > 1);
    if (st.all.disabled !== all) st.all.disabled = all;
    const pots = String(many * st.rateOf);
    if (st.all.dataset.pots !== pots) st.all.dataset.pots = pots;
    const label = 'Обміняти все (' + num(many) + ' 🏺)';
    if (st.all.textContent !== label) st.all.textContent = label;

    paintWheel(st);
    paintGolden(st);
    paintFall(st);
    for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'frame', st, H.api, now0);

    const now = Date.now();
    if (now - st.slowAt >= SLOW_MS) {
      st.slowAt = now;
      paintSlow(st, n);
    }
  }

  /// Коло крутиться від пасиву й від розгону, кільце навколо нього — це розгін, сяйво — теж. Усе за кадр і
  /// лише різницями: стилі пишемо тоді, коли число справді зрушило.
  function paintWheel(st) {
    const t = performance.now();
    const dt = st.angleAt ? Math.min(0.1, (t - st.angleAt) / 1000) : 0;
    st.angleAt = t;
    const frac = heatFrac(st);
    const sec = secondNow(st);
    // Градусів за секунду: без підмайстрів коло стоїть, з піччю повільно пливе, а від швидких кліків розкручується.
    const speed = (sec > 0 ? 30 + 30 * Math.log10(1 + sec) : 0) + 420 * frac;
    if (speed > 0 && dt > 0) {
      st.angle = (st.angle + speed * dt) % 360;
      st.turn.style.transform = 'rotate(' + st.angle.toFixed(1) + 'deg)';
    }
    const off = Math.round(RING * (1 - frac) * 10) / 10;
    if (off !== st.ringOff) { st.ringOff = off; st.heatRing.style.strokeDashoffset = off; }
    const glow = Math.round(frac * 50) / 50;
    if (glow !== st.glow) {
      st.glow = glow;
      st.wheel.style.setProperty('--clk-glow', glow);
      st.wheelBox.classList.toggle('hot', frac >= 0.98 && st.momentumMax > 1);
    }
    const scale = Math.round((1 + 0.1 * frac) * 100) / 100;
    if (scale !== st.jugScale) { st.jugScale = scale; st.jugBox.style.transform = 'scale(' + scale + ')'; }
  }

  /// Те, що не мусить жити шістдесят разів на секунду: рядок швидкості, бонуси, суперник, прогрес клейм.
  function paintSlow(st, shown) {
    const sn = serverNow(st);
    const sec = secondNow(st);
    const heat = heatNow(st);
    const mom = momentumOf(st, heat);
    const rate = 'за клік +' + short(clickNow(st))
      + (st.momentumMax > 1 ? ' · розгін до ×' + dec(st.momentumMax) : '')
      + (sec > 0 ? ' · без тебе +' + short(sec) + ' за секунду' : ' · підмайстрів ще нема');
    if (st.rate.textContent !== rate) st.rate.textContent = rate;

    let buffs = '';
    if (sn < st.fairUntil) buffs += '<span class="clk-buff fair">🎪 Ярмарок ×' + st.fairMult + ' · ' + Math.ceil((st.fairUntil - sn) / 1000) + ' с</span>';
    if (sn < st.inspireUntil) buffs += '<span class="clk-buff inspire">✨ Натхнення: клік ×' + st.inspireMult + ' · ' + Math.ceil((st.inspireUntil - sn) / 1000) + ' с</span>';
    if (st.momentumMax > 1 && mom > 1.05) buffs += '<span class="clk-buff heat">🌀 Розгін ×' + dec(mom) + '</span>';
    if (st.fallStreak > 1) buffs += '<span class="clk-buff streak">🤲 Серія ' + st.fallStreak + ' · глек з полиці +' + Math.min(100, st.fallStreak * 10) + ' %</span>';
    if (st.buffs._html !== buffs) { st.buffs._html = buffs; st.buffs.innerHTML = buffs; st.buffs.hidden = !buffs; }
    const fair = sn < st.fairUntil, inspire = sn < st.inspireUntil;
    if (st.stage.classList.contains('fair') !== fair) st.stage.classList.toggle('fair', fair);
    if (st.stage.classList.contains('inspire') !== inspire) st.stage.classList.toggle('inspire', inspire);

    const liveTotal = st.total + (shown - st.base);
    if (visible(st)) loadBoard(st);
    paintRival(st, liveTotal);
    if (st.tab === 'fire') paintFire(st, liveTotal);
    // Хата й дошка купців — розділи Майстерні (v8), а не свої вкладки: ціни глини, «замісити» й відліки
    // малюються, поки відкрита Майстерня.
    if (st.tab === 'shop') paintCountdowns(st, sn, shown);
    // Купець повернувся, а гончар нічого не робив: сервер рахує повернення лише при дії чи виді, тож питаємо вид
    // самі — раз на купця, з запасом у дві секунди й лише коли картку видно (як look для глеків).
    for (const t of st.taken) {
      if (sn < t.payAt + 2000 || st.payLooked.has(t.id) || !st.ctx || !visible(st)) continue;
      st.payLooked.add(t.id);
      st.ctx.act('look');
    }
    paintEye(st);
    for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'slow', st, H.api, sn);
  }

  /// Відліки в хаті й на дошці: глина відлежується, купець повертається, дошка оновлюється.
  function paintCountdowns(st, sn, shown) {
    for (const el of st.cds) {
      const at = +el.dataset.at;
      const left = at - sn;
      const text = left > 0 ? mmss(left) : el.dataset.done || '0:00';
      if (el.textContent !== text) el.textContent = text;
    }
    const resting = sn < st.clayRestUntil;
    for (const b of st.clayBtns) {
      const owned = b.dataset.owned === '1';
      const on = b.dataset.on === '1';
      const off = !st.mine || on || (owned ? resting : shown < +b.dataset.price);
      if (b.disabled !== off) b.disabled = off;
      const label = on ? 'на колі' : owned ? (resting ? 'відлежується ' + mmss(st.clayRestUntil - sn) : 'замісити') : short(+b.dataset.price);
      if (b._price.textContent !== label) b._price.textContent = label;
    }
  }

  // ---------- Око майстра ----------

  /// Майстер щось хоче: коло стоїть або чекає відповіді на полицю. Кліки тоді не рахуються — і не малюються.
  const guardOn = (st) => !!st.guard;

  /// Скільки після появи полиці кнопка «Показати полицю» ще не тисне: черга швидких кліків по колу, на місце
  /// якого стала панель, не має ні натиснути її, ні тим паче потрапити в картинку.
  const EYE_ARM_MS = 1000;

  /// За що майстер питає не в чергу (why з виду) — перед інструкцією, як пройти полицю.
  const DOUBT = {
    rhythm: 'Кліки йшли надто рівно, мов під метроном, — так клацає автоклікер. Покажи майстрові, що це рука. ',
    press: 'Кнопку відпускали миттєво, раз за разом, — так клацає автоклікер (або тачпад). Покажи майстрові, що це рука. ',
  };

  /// Панель майстра замість сцени: відлік паузи, або інструкція з кнопкою, або полиця, де треба торкнутись глечиків.
  /// Полиця відкривається лише кнопкою: доти картинка за завісою й торкань не приймає. Інакше той, хто швидко
  /// клацав коло, проклацував три полиці поспіль, не встигши їх побачити, і діставав десять хвилин паузи.
  function paintEye(st) {
    const e = st.eye;
    const g = st.guard;
    const on = guardOn(st) && st.mine;
    if (st.stage.hidden !== on) st.stage.hidden = on;
    if (e.el.hidden === on) e.el.hidden = !on;
    if (!on) return;

    const left = g.lockUntil ? g.lockUntil - serverNow(st) : 0;
    const locked = left > 0;
    // Нова полиця (чи та сама після паузи): завіса знову опущена, торкання скинуто, кнопка озброїться за секунду.
    const key = g.serial + (locked ? ':lock' : ':shelf');
    if (st.eyeKey !== key) {
      st.eyeKey = key;
      st.eyeOpen = false;
      st.eyeAt = Date.now();
      st.taps = [];
      st.eyeBusy = false;
      // Озброїти кнопку своїм таймером, а не лише циклом малювання: у схованій вкладці rAF не крутиться.
      clearTimeout(st.eyeArm);
      clearTimeout(st.refreshT);
      st.eyeArm = setTimeout(() => paintEye(st), EYE_ARM_MS + 30);
    }
    const open = st.eyeOpen && !locked;

    let text;
    if (locked) {
      const s = Math.ceil(left / 1000);
      text = '🔒 Коло стоїть ще ' + Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0') + '. Три полиці поспіль — не ті глеки.'
        + ' Пасив, покупки й прилавок працюють; кліки й глеки — ні. Після паузи майстер спитає ще раз.';
    } else if (!open) {
      text = (DOUBT[g.why] || 'Майстер дивиться, чи коло крутить рука, а не автоклікер. ')
        + 'Як пройти: натисни «Показати полицю», а тоді торкнись на картинці кожного глечика — їх там ' + g.count
        + '. Глечик — той, що з вузькою шийкою; горщики, миски й черепки не чіпай. Торкнувся не туди — «Скинути торкання».'
        + ' Три полиці поспіль не ті — коло стане на 10 хвилин. Поки не відповіси, кліки не рахуються.';
    } else {
      text = 'Торкнись кожного глечика — їх тут ' + g.count + '. Глечик — той, що з вузькою шийкою. Торкнувся не туди — «Скинути торкання».';
    }
    if (e.text.textContent !== text) e.text.textContent = text;
    const tries = locked ? '' : g.misses ? 'не ті — ось інша полиця · спроба ' + (g.misses + 1) + ' з ' + g.maxMisses : '';
    if (e.tries.textContent !== tries) e.tries.textContent = tries;
    if (e.pic.hidden !== locked) e.pic.hidden = locked;
    if (e.veil.hidden !== open) e.veil.hidden = open;
    if (e.pic.classList.contains('veiled') === open) e.pic.classList.toggle('veiled', !open);
    if (e.reset.hidden !== !open) e.reset.hidden = !open;
    // Кнопка озброюється за секунду після появи: черга кліків, що летіла в коло, не має її натиснути.
    const armed = !locked && !open && Date.now() - st.eyeAt >= EYE_ARM_MS;
    if (e.go.disabled !== !armed) e.go.disabled = !armed;

    // Картинку міняємо лише на нову полицю: вид летить на кожну дію, а base64 полиці між ними той самий.
    if (!locked && g.png && e.img._serial !== g.serial) {
      e.img._serial = g.serial;
      e.img.src = g.png;
    }
    const marks = st.taps.map((t, i) => '<i style="left:' + (t[0] / g.width * 100).toFixed(2) + '%;top:'
      + (t[1] / g.height * 100).toFixed(2) + '%">' + (i + 1) + '</i>').join('');
    if (e.marks._html !== marks) { e.marks._html = marks; e.marks.innerHTML = marks; }
    const off = st.eyeBusy || !st.taps.length;
    if (e.reset.disabled !== off) e.reset.disabled = off;
    e.pic.classList.toggle('busy', st.eyeBusy);
  }

  /// Кнопка «Показати полицю»: лише справжній натиск і лише озброєної кнопки. Відтак торкання картинки рахуються.
  function openShelf(st, ev) {
    if (!ev.isTrusted || !st.guard || st.eye.go.disabled) return;
    st.eyeOpen = true;
    paintEye(st);
  }

  /// Торкання полиці. Лише справжні (isTrusted): скрипт, що тицяє в картинку dispatchEvent-ом, сюди не дійде —
  /// хоча він однаково не знає, куди тицяти. І лише відкритої кнопкою полиці: під завісою торкань нема.
  /// Координати — у пікселях картинки, як їх чекає сервер.
  function tapShelf(st, ev) {
    const g = st.guard;
    if (!ev.isTrusted || !g || !g.png || !st.eyeOpen || st.eyeBusy || !st.ctx) return;
    if (g.lockUntil && g.lockUntil > serverNow(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const r = st.eye.img.getBoundingClientRect();
    if (!r.width || !r.height) return;
    const x = Math.round(((ev.clientX - r.left) / r.width) * g.width * 10) / 10;
    const y = Math.round(((ev.clientY - r.top) / r.height) * g.height * 10) / 10;
    st.taps.push([x, y]);
    if (st.taps.length >= g.count) {
      st.eyeBusy = true;
      const serial = g.serial;
      const taps = st.taps.slice();
      st.ctx.act('answer', { taps }).then((res) => {
        // Невдача без нової полиці (зіпсовані торкання, зв'язок) — дати спробувати ще раз ту саму.
        if (st.guard && st.guard.serial === serial && !(res && res.ok)) { st.taps = []; st.eyeBusy = false; }
        paintEye(st);
      }, () => { st.taps = []; st.eyeBusy = false; paintEye(st); });
    }
    paintEye(st);
  }

  // ---------- розписний глек і глек з полиці ----------

  /// Розписний глек: стоїть у своєму вікні; утік — один раз питаємо сервер про наступний.
  function paintGolden(st) {
    const g = st.golden;
    const b = st.gold;
    // Поки майстер чекає, глек не ловиться (сервер відмовить) — тож і не показуємо.
    if (!g || !st.mine || guardOn(st)) { if (!b.hidden) b.hidden = true; return; }
    const now = serverNow(st);
    const show = now >= g.at && now <= g.until && st.goldenGone !== g.at;
    if (b.hidden === show) {
      b.hidden = !show;
      if (show) {
        b.style.left = g.x + '%';
        b.style.top = g.y + '%';
        b.style.setProperty('--clk-left', Math.max(0, (g.until - now) / 1000) + 's');
        b.classList.remove('run');
        void b.offsetWidth;            // перезапустити кільце-таймер для нового глека
        b.classList.add('run');
      }
    }
    // Один раз на глек і з запасом у п'ять секунд: наш «серверний час» — оцінка, і сервер мусить уже точно
    // вважати глек утеклим, інакше розкладу не змінить. І лише коли картку видно: схована картка не підписана
    // на кімнату, нового виду не дочекається, а кожен look — це запис у базу й «живий» гончар, якого
    // прибиральник ніколи не прибере.
    if (now > g.until + CATCH_GRACE_MS + 5000 && st.lookedFor !== g.until && st.ctx && visible(st)) {
      st.lookedFor = g.until;
      st.ctx.act('look');
    }
  }

  /// Глек з полиці: три секунди летить від полиці до долівки (CSS-анімація, а від'ємна затримка дає стати в
  /// політ посередині, якщо картку відкрили пізно). Спіймали — «+N» і золоті бризки; долетів — черепки.
  function paintFall(st) {
    const f = st.fall;
    const b = st.fallEl;
    if (!f || !st.mine || guardOn(st)) { if (!b.hidden) b.hidden = true; return; }
    const now = serverNow(st);
    const show = now >= f.at && now <= f.until && st.fallGone !== f.at;
    if (b.hidden === show) {
      b.hidden = !show;
      if (show) {
        b.style.left = f.x + '%';
        b.style.setProperty('--clk-fallms', (f.until - f.at) + 'ms');
        // Летить від полиці (її висоту задає css) до долівки: центр глека стає на 92 % висоти сцени.
        b.style.setProperty('--clk-drop', Math.max(40, st.stage.clientHeight * 0.92 - b.offsetTop - b.offsetHeight / 2) + 'px');
        b.style.animationDelay = (-(now - f.at)) + 'ms';
        b.classList.remove('run');
        void b.offsetWidth;
        b.classList.add('run');
      }
    }
    // Розбився — але лише той, що розбився щойно і на очах: після довгої відсутності черепків не малюємо.
    if (now > f.until && now - f.until < 1500 && st.fallGone !== f.at && st.fallBroke !== f.at) {
      st.fallBroke = f.at;
      shatter(st, f.x);
    }
    if (now > f.until + CATCH_GRACE_MS + 5000 && st.fallLooked !== f.until && st.ctx && visible(st)) {
      st.fallLooked = f.until;
      st.ctx.act('look');
    }
  }

  function paintRival(st, liveTotal) {
    const rows = st.board;
    let text = '';
    if (rows && rows.length) {
      const me = (st.ctx && st.ctx.me && st.ctx.me.nick || '').trim().toLowerCase();
      const others = rows.filter((r) => (r.nick || '').trim().toLowerCase() !== me && r.best > 0)
        .sort((a, b) => b.best - a.best);
      if (others.length) {
        const above = others.filter((r) => r.best > liveTotal);
        const place = above.length + 1;
        const medal = ['🥇', '🥈', '🥉'][place - 1] || '#' + place;
        // Нік не відмінюється («до Микола»), тож будуємо речення з ним у називному.
        if (above.length) {
          const next = above[above.length - 1];
          text = medal + ' ' + next.nick + ' попереду на ' + short(next.best - liveTotal);
        } else {
          text = medal + ' ти перший · ' + others[0].nick + ' позаду на ' + short(liveTotal - others[0].best);
        }
      }
    }
    if (st.rival.textContent !== text) { st.rival.textContent = text; st.rival.hidden = !text; }
  }

  function paintFire(st, liveTotal) {
    const all = Math.floor(Math.sqrt(Math.max(0, liveTotal) / STAMP_UNIT));
    const gain = Math.max(0, all - st.stamps);
    const from = all * all * STAMP_UNIT;
    const to = (all + 1) * (all + 1) * STAMP_UNIT;
    const pct = Math.max(0, Math.min(100, ((liveTotal - from) / (to - from)) * 100));
    const f = st.fire;
    const bar = pct.toFixed(1) + '%';
    if (f._bar.style.width !== bar) f._bar.style.width = bar;
    const next = 'наступне клеймо — на ' + short(to) + ' глеків за весь час (зараз ' + short(liveTotal) + ')';
    if (f._next.textContent !== next) f._next.textContent = next;

    const armed = st.fireArmed && Date.now() < st.fireArmed;
    if (!armed) st.fireArmed = 0;
    const label = gain < 1 ? '🔥 Почати наново — ще рано'
      : armed ? 'Точно? Глеки й верстати згорять — ще раз'
      : '🔥 Почати наново: +' + gain + ' ' + stampsWord(gain);
    if (f._btn.textContent !== label) f._btn.textContent = label;
    const off = !st.mine || gain < 1;
    if (f._btn.disabled !== off) f._btn.disabled = off;
    f._btn.classList.toggle('armed', !!armed);
    const after = gain < 1 ? '' : 'після обпалу: +' + dec((st.stamps + gain) * st.stampBonus * 100) + ' % до всього назавжди';
    if (f._after.textContent !== after) f._after.textContent = after;
  }

  /// Цикл живе від mount до unmount. Картку каркас монтує ще до того, як вставить у сторінку (повторне
  /// відкриття з готовим видом), тож «не в документі» — це не кінець, а «ще не видно»: чекаємо, не малюючи.
  /// Раніше цикл на цьому й зупинявся — і пасив між діями не тікав, а розписний глек так і не з'являвся б.
  function loop(st) {
    if (!st.el) { st.raf = 0; return; }
    if (visible(st)) paint(st);
    st.raf = requestAnimationFrame(() => loop(st));
  }

  /// Елемент, що живе рівно доти, доки триває його анімація. У фоновій вкладці анімації не крутяться, а отже й
  /// animationend не прилетить — прибираємо і за часом, інакше «+N» назбирувались би там сотнями до самого повернення.
  function fleeting(host, el, ms) {
    el.addEventListener('animationend', () => el.remove());
    setTimeout(() => el.remove(), ms);
    host.appendChild(el);
  }

  /// «+12», що злітає над колом; при розкрученому колі — гарячіше й більше.
  function pop(st, amount, cls) {
    if (st.pops.childElementCount > 12) return;   // палець швидший за око: більше однаково не роздивитись
    const el = document.createElement('span');
    el.className = 'clk-pop' + (cls ? ' ' + cls : '');
    el.textContent = '+' + short(amount);
    el.style.left = (32 + Math.random() * 36) + '%';
    fleeting(st.pops, el, 2000);
  }

  /// Напис у довільному місці сцени (x, y — у відсотках): «+N» над спійманим глеком, «трісь» над розбитим.
  function popAt(st, text, cls, x, y) {
    const el = document.createElement('span');
    el.className = 'clk-pop ' + cls;
    el.textContent = text;
    el.style.left = x + '%';
    el.style.top = y + '%';
    fleeting(st.fx, el, 2500);
  }

  /// Бризки глини (або золоті іскри) з точки (x, y у відсотках host-а; без них — з центру кола).
  function sparks(st, host, count, gold, x, y) {
    if (host.childElementCount > 48) return;
    for (let i = 0; i < count; i++) {
      const el = document.createElement('i');
      el.className = 'clk-spark' + (gold ? ' gold' : '');
      if (x != null) { el.style.left = x + '%'; el.style.top = y + '%'; }
      const a = Math.random() * Math.PI * 2;
      const d = (gold ? 50 : 34) + Math.random() * (gold ? 70 : 40);
      el.style.setProperty('--dx', (Math.cos(a) * d).toFixed(0) + 'px');
      el.style.setProperty('--dy', (Math.sin(a) * d - 24).toFixed(0) + 'px');
      fleeting(host, el, 1200);
    }
  }

  /// Глек долетів до долівки: черепки навсібіч і тихе «трісь».
  function shatter(st, x) {
    for (let i = 0; i < 8; i++) {
      const el = document.createElement('i');
      el.className = 'clk-shard';
      el.style.left = x + '%';
      el.style.top = '90%';
      const a = -Math.PI * (0.15 + Math.random() * 0.7);
      const d = 24 + Math.random() * 46;
      el.style.setProperty('--dx', (Math.cos(a) * d).toFixed(0) + 'px');
      el.style.setProperty('--dy', (Math.sin(a) * d + 40).toFixed(0) + 'px');
      el.style.setProperty('--rot', Math.round(Math.random() * 360 - 180) + 'deg');
      fleeting(st.fx, el, 1500);
    }
    H.api.sfx('break', { x });
    popAt(st, 'трісь… серія обірвалась', 'miss', Math.min(70, Math.max(20, x)), 78);
  }

  // ---------- дії ----------

  function flush(st) {
    if (!st.hands.length || !st.ctx) return;
    // Поки майстер чекає, сервер кліків однаково не зарахує: не шлемо і не обіцяємо їх на лічильнику.
    if (guardOn(st)) { st.hands.length = 0; st.handsGain = 0; return; }
    const c = st.hands.splice(0, MAX_BATCH);
    const n = c.length;
    // Скільки з обіцяного на лічильнику полетіло з цією пачкою: усе, якщо це був увесь хвіст, інакше частка.
    const g = st.hands.length ? Math.min(st.handsGain, c.reduce((s, h) => s + (h[5] || 0), 0)) : st.handsGain;
    st.handsGain = Math.max(0, st.handsGain - g);
    // Серверу — лише п'ять полів відбитка; шосте (наша оцінка глеків за клік) лишається тут.
    const wire = c.map((h) => h.slice(0, 5));
    st.inflight += n;
    st.inflightGain += g;
    const back = () => { st.inflight = Math.max(0, st.inflight - n); st.inflightGain = Math.max(0, st.inflightGain - g); };
    // Кліки, що вже полетіли, знімає з рахунку сам вид (див. update): вид і відповідь приходять різними
    // кадрами вебсокета, і якби ми чекали відповіді, між ними лічильник встигав би показати їх двічі.
    // Лишається тільки невдача: тоді виду не буде взагалі, і порахувати назад мусимо ми.
    st.ctx.act('spin', { c: wire }).then((r) => { if (!r || !r.ok) back(); }, back);
  }

  /// Точка на колі 0…1000 — так її чекає сервер.
  function spot(st, ev) {
    const r = st.wheel.getBoundingClientRect();
    const at = (v, from, size) => Math.max(0, Math.min(1000, Math.round(((v - from) / (size || 1)) * 1000)));
    return [at(ev.clientX, r.left, r.width), at(ev.clientY, r.top, r.height)];
  }

  /// Натиснули на коло: запам'ятовуємо мить і точку, а кліком це стане, коли відпустять.
  function pressWheel(st, ev) {
    if (!ev.isTrusted || (ev.pointerType === 'mouse' && ev.button !== 0)) return;
    const [x, y] = spot(st, ev);
    st.downs.set(ev.pointerId, { t: ev.timeStamp, x, y, src: SRC[ev.pointerType] ?? SRC.mouse });
  }

  /// Відпустили: це клік, якщо натискали саме на коло, справжньою рукою й не довше за HOLD_MS. Два пальці по черзі —
  /// два кліки: кожен палець має свій pointerId.
  function releaseWheel(st, ev) {
    const d = st.downs.get(ev.pointerId);
    if (!d) return;
    st.downs.delete(ev.pointerId);
    if (!ev.isTrusted || ev.type === 'pointercancel' || ev.timeStamp - d.t > HOLD_MS) return;
    spin(st, d.t, ev.timeStamp - d.t, d.x, d.y, d.src);
  }

  /// Один справжній клік: відбиток у пачку, розгін +1, «+N» над колом і бризки. downAt і press — у мс шкали event.timeStamp.
  function spin(st, downAt, press, x, y, src) {
    if (!st.ctx || !st.mine || guardOn(st)) return;
    const dt = st.lastDown ? Math.max(0, Math.min(60000, Math.round(downAt - st.lastDown))) : 60000;
    st.lastDown = downAt;
    // Те саме відро дозволів, що й на сервері: понад дванадцять кліків за секунду він однаково не візьме,
    // тож і малювати їх не варто — інакше лічильник обіцяв би те, чого потім не дорахується.
    const now = Date.now();
    st.tokens = Math.min(MAX_BATCH, st.tokens + ((now - st.tokensAt) / 1000) * MAX_BATCH);
    st.tokensAt = now;
    if (st.tokens < 1) return;
    st.tokens -= 1;
    // Розгін — як на сервері: спад від останнього кліка, множник на півкліка вперед, потім +1 гарячий.
    const heat = heatNow(st);
    const mult = momentumOf(st, heat + 0.5);
    st.heat = heat + 1;
    st.heatAt = now;
    const gain = clickNow(st) * mult;
    st.hands.push([dt, Math.max(0, Math.round(press)), x, y, src, gain]);
    st.handsGain += gain;
    // Більше трьох пачок не копимо: якщо зв'язок завис, хвіст однаково не долетів би.
    if (st.hands.length > MAX_BATCH * 3) {
      const dropped = st.hands.splice(0, st.hands.length - MAX_BATCH * 3);
      st.handsGain = Math.max(0, st.handsGain - dropped.reduce((s, h) => s + (h[5] || 0), 0));
    }
    const hot = st.momentumMax > 1 && mult >= 1 + (st.momentumMax - 1) * 0.9;
    pop(st, gain, hot ? 'hot' : '');
    sparks(st, st.sparks, hot ? 5 : 3, hot);
    H.api.sfx('clay', { hot, heat: Math.min(1, st.heat / st.heatFull) });
    // Сервер ціною кліка вважає мить, коли пачка ДОЛЕТІЛА. Під кінець натхнення чи ярмарку 700 мс чекання
    // перетворили б «+25×» на екрані на «+1×» на сервері — тож останні півтори секунди бонусу шлемо одразу.
    const sn = serverNow(st);
    const ends = [st.inspireUntil, st.fairUntil].filter((t) => t > sn);
    if (ends.length && Math.min(...ends) - sn < 1500) flush(st);
    st.wheel.classList.remove('hit');
    void st.wheel.offsetWidth;         // перезапуск анімації «стуку»: без цього другий клік поспіль її не покаже
    st.wheel.classList.add('hit');
    paint(st);
  }

  /// Який звук дає дія гравця (жива хата озвучує; без неї — тиша).
  const ORDER_SFX = {
    buy: 'buy', mark: 'buy', paint: 'buy', tool: 'buy', adorn: 'buy', knead: 'buy', secret: 'buy',
    sell: 'coins', take: 'coins', bazaar: 'coins', wear: 'tap', form: 'tap', catch: 'catch', grab: 'grab', fire: 'prestige',
  };

  /// Покупка й продаж рахуються від того, що вже долетіло до сервера, тож накопичені кліки шлемо першими.
  function order(st, action, payload) {
    if (!st.ctx || !st.mine) return;
    if (ORDER_SFX[action]) H.api.sfx(ORDER_SFX[action], { action });
    flush(st);
    st.ctx.act(action, payload);
  }

  function catchGolden(st, ev) {
    if (!ev.isTrusted || !st.golden || !st.mine || guardOn(st)) return;
    st.goldenGone = st.golden.at;      // ховаємо одразу: другий клік по тому самому глеку — лише червоний тост
    st.gold.hidden = true;
    order(st, 'catch');
  }

  /// Спіймали глек з полиці: ховаємо, малюємо «+N» (суму знає вид — fall.gain) і золоті іскри там, де він був.
  function grabFall(st, ev) {
    if (!ev.isTrusted || !st.fall || !st.mine || guardOn(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const f = st.fall;
    st.fallGone = f.at;
    const sr = st.stage.getBoundingClientRect();
    const r = st.fallEl.getBoundingClientRect();
    st.fallEl.hidden = true;
    if (sr.width && sr.height) {
      const x = ((r.left + r.width / 2 - sr.left) / sr.width) * 100;
      const y = ((r.top + r.height / 2 - sr.top) / sr.height) * 100;
      popAt(st, '+' + short(st.fallGain), 'big', x, Math.max(6, y - 8));
      sparks(st, st.fx, 14, true, x, y);
    }
    order(st, 'grab');
  }

  function fire(st) {
    if (!st.mine) return;
    if (!st.fireArmed || Date.now() > st.fireArmed) {
      // Обпал не відкотиш — тож перший натиск лише питає.
      st.fireArmed = Date.now() + 4000;
      paintFire(st, st.total + (st.shown - st.base));
      return;
    }
    st.fireArmed = 0;
    order(st, 'fire');
  }

  function setTab(st, tab, remember = true) {
    if (!st.panes[tab] || isGated(st, tab)) tab = 'shop';
    st.tab = tab;
    if (remember) storeSet('clk.tab', tab);
    for (const b of st.tabs.querySelectorAll('[data-tab]')) b.classList.toggle('active', b.dataset.tab === tab);
    for (const [k, p] of Object.entries(st.panes)) p.hidden = k !== tab;
    // Подивився — світитись більше не треба.
    const b = st.tabs.querySelector('[data-tab="' + tab + '"]');
    if (b && b.classList.contains('fresh')) { b.classList.remove('fresh'); storeSet('clk.seen.' + tab, '1'); }
    st.slowAt = 0;
  }

  const isGated = (st, key) => {
    const b = st.tabs && st.tabs.querySelector('[data-tab="' + key + '"]');
    return !!b && b.hidden;
  };

  /// Підпис ярлика = базова назва вкладки плюс короткі нотатки частин, за абеткою їхніх ключів.
  function labelTab(st, key) {
    const b = st.tabs && st.tabs.querySelector('[data-tab="' + key + '"]');
    if (!b) return;
    const notes = st.tabNotes[key] || {};
    const best = Object.keys(notes).sort().map((k) => notes[k]).filter((n) => n.text)
      .sort((a, b) => a.prio - b.prio)[0];
    const text = (st.tabText[key] || key) + (best ? ' · ' + best.text : '');
    if (b.textContent !== text) b.textContent = text;
  }

  /// Перебрати гейти: закриті вкладки ховаємо, щойно відкриті — світимо й кажемо про це в стрічці подій.
  /// Перший прохід (гравець із прогресом) нічого не оголошує: у нього все й так уже відкрите.
  function gateTabs(st, v) {
    if (!st.tabs) return;
    for (const [key, fn] of Object.entries(st.gates)) {
      const b = st.tabs.querySelector('[data-tab="' + key + '"]');
      if (!b) continue;
      let on = true;
      try { on = !!fn(st, v || {}); } catch (e) { console.error('[clicker] гейт ' + key, e); on = true; }
      if (b.hidden !== !on) {
        b.hidden = !on;
        if (!on) { if (st.tab === key) setTab(st, 'shop', false); continue; }
        if (storeGet('clk.seen.' + key, '') !== '1') {
          b.classList.add('fresh');
          if (st.gateReady) H.api.feed(st, 'відкрилось: ' + (st.tabText[key] || key), 'open');
        }
      }
    }
    st.gateReady = true;
  }

  function setMode(st, mode) {
    st.mode = mode;
    storeSet('clk.mode', mode);
    for (const b of st.modes.querySelectorAll('[data-mode]')) b.classList.toggle('active', b.dataset.mode === mode);
    paint(st);
  }

  // ---------- полиці ----------

  /// Перемалювати секцію лише тоді, коли її HTML справді змінився: кнопки під пальцем не мають зникати щопачки.
  function swap(el, html) {
    if (el._sig === html) return false;
    el._sig = html;
    el.innerHTML = html;
    return true;
  }

  function shop(st, ctx) {
    const esc = ctx.esc;
    const ups = st.ups;
    const keys = Object.keys(ups);
    // «Найвигідніше» — найкоротша окупність серед того, що видно й ще можна купити.
    let best = '', bestPay = Infinity;
    for (const k of keys) {
      const u = ups[k];
      if (!u.open || !(u.gain > 0) || (u.max > 0 && u.level >= u.max)) continue;
      const pay = u.price / u.gain;
      if (pay < bestPay) { bestPay = pay; best = k; }
    }
    let teaser = '';
    const cards = keys.filter((k) => {
      // !== false, а не просто open: старий сервер (хвилина деплою) цього поля не шле — показуємо все.
      if (ups[k].open !== false) return true;
      if (!teaser && ups[k].kind === 'idle') teaser = k;
      return false;
    }).map((k) => {
      const u = ups[k];
      const maxed = u.max > 0 && u.level >= u.max;
      const pay = u.gain > 0 && !maxed ? span(u.price / u.gain) : '';
      const x2 = u.boost > 1 ? '<span class="clk-x2">×' + u.boost + '</span> · ' : '';
      // Компактний рядок: значок · назва з рівнем і описом · ціна; смужка знизу — скільки ціни вже назбирано.
      const icon = H.api.upIcon ? H.api.upIcon(k) : '';
      return '<button type="button" class="clk-up' + (k === best ? ' best' : '') + (u.kind === 'skill' ? ' skill' : '') + (maxed ? ' maxed' : '')
        + '" data-buy="' + esc(k) + '" title="' + esc(u.name + ' — ' + u.desc + (pay ? ' · окупиться за ' + pay : '')) + '" disabled>'
        // Рівень — плашкою на значку (як лічильник будівель), щоб назва мала весь рядок.
        + '<span class="clk-uico">' + icon + '<span class="clk-lvl' + (u.level ? '' : ' zero') + '">' + (u.level ? u.level + (u.max > 0 ? '/' + u.max : '') : '0') + '</span></span>'
        + '<span class="clk-umain"><span class="clk-uname"><b>' + esc(u.name) + '</b></span>'
        + '<span class="clk-udesc">' + x2 + esc(u.desc) + '</span></span>'
        // Праворуч ціна, під нею дрібно — за скільки окупиться (★ — найвигідніше зараз).
        + '<span class="clk-uright"><span class="clk-price"></span>'
        + (pay ? '<span class="clk-pay">' + (k === best ? '★ ' : '') + 'окуп. ' + pay + '</span>' : '') + '</span>'
        + '<i class="clk-ubar"><i></i></i>'
        + '</button>';
    }).join('');
    const more = teaser
      ? '<div class="clk-teaser muted small">' + (H.api.upIcon ? '<span class="clk-uico locked">' + H.api.upIcon(teaser) + '</span>' : '')
        + 'Далі на драбині ще є верстати: наступний відкриється після першого рівня «'
        + esc(ups[prevIdle(ups, teaser)].name) + '»</div>'
      : '';
    if (swap(st.shop, cards + more)) {
      st.buys = [...st.shop.querySelectorAll('[data-buy]')];
      for (const b of st.buys) {
        b._price = b.querySelector('.clk-price');
        b._bar = b.querySelector('.clk-ubar i');
        b._pct = -1;
        b.onclick = () => {
          const u = st.ups[b.dataset.buy];
          const a = afford(u, st.shown, st.mode);
          order(st, 'buy', { key: b.dataset.buy, n: st.mode === 'max' ? MAX_BUY : Math.max(1, a.n) });
        };
      }
      paint(st);
    }

    const marks = st.markList.slice().sort((a, b) => a.price - b.price);
    const mhtml = marks.length
      ? '<div class="clk-sub">Віхи<span class="muted small"> · одноразово, ×2 назавжди (до обпалу)</span></div><div class="clk-marks">'
        + marks.map((m) => '<button type="button" class="clk-mark" data-mark="' + esc(m.key) + '" data-price="' + m.price + '" title="'
          + esc(m.name + ' — ' + m.desc) + '" disabled>'
          + (H.api.upIcon ? '<span class="clk-uico">' + H.api.upIcon(m.on || String(m.key).split(':')[0]) + '</span>' : '')
          + '<span class="clk-umain"><b>' + esc(m.name) + '</b><span class="clk-udesc">' + esc(m.desc) + '</span></span>'
          + '<span class="clk-price">' + short(m.price) + '</span></button>').join('')
        + '</div>'
      : '';
    if (swap(st.marks, mhtml)) {
      st.markBtns = [...st.marks.querySelectorAll('[data-mark]')];
      for (const b of st.markBtns) b.onclick = () => order(st, 'mark', { key: b.dataset.mark });
      paint(st);
    }
  }

  function prevIdle(ups, key) {
    const keys = Object.keys(ups);
    for (let i = keys.indexOf(key) - 1; i >= 0; i--) if (ups[keys[i]].kind === 'idle') return keys[i];
    return key;
  }

  function styles(st, ctx) {
    const esc = ctx.esc;
    const list = st.styleList;
    const owned = list.filter((s) => s.owned).length;
    const html = '<div class="clk-sub">Розписи · ' + owned + ' з ' + list.length
      + '<span class="muted small"> · кожен +5 % до всього, лишаються й після обпалу</span></div>'
      + '<div class="clk-styles">'
      + list.map((s) => {
        const on = st.wear === s.key;
        return '<button type="button" class="clk-style' + (s.owned ? ' owned' : '') + (on ? ' on' : '') + '" data-style="' + esc(s.key)
          + '" data-owned="' + (s.owned ? 1 : 0) + '" data-price="' + s.price + '" disabled>'
          // Некуплений розпис видно приглушеним: купують те, що бачать, а не сірий силует.
          + jugSvg(s.key, 'clk-mini' + (s.owned ? '' : ' locked'), 's-' + s.key)
          + '<b>' + esc(s.name) + '</b>'
          + '<span class="clk-price' + (s.owned ? ' done' : '') + '">' + (on ? 'на колі' : s.owned ? 'поставити' : short(s.price)) + '</span>'
          + '</button>';
      }).join('')
      + '</div>'
      + (owned ? '<button type="button" class="ghost small clk-plain"' + (st.wear ? '' : ' disabled') + '>Простий глиняний на колі</button>' : '');
    if (swap(st.styles, html)) {
      st.styleBtns = [...st.styles.querySelectorAll('[data-style]')];
      for (const b of st.styleBtns) {
        b.onclick = () => (b.dataset.owned === '1'
          ? order(st, 'wear', { key: st.wear === b.dataset.style ? '' : b.dataset.style })
          : order(st, 'paint', { key: b.dataset.style }));
      }
      const plain = st.styles.querySelector('.clk-plain');
      if (plain) plain.onclick = () => order(st, 'wear', { key: '' });
      paint(st);
    }
  }

  function firePane(st, ctx) {
    const esc = ctx.esc;
    const v = ctx.view || {};
    const bonus = dec(st.stamps * st.stampBonus * 100);
    const cap = v.stampCap || 0;
    const head = '<div class="clk-stamps"><b>🔖 ' + num(st.stamps) + ' ' + stampsWord(st.stamps) + '</b>'
      + '<span>+' + bonus + ' % до всього</span>'
      + '<span class="muted small">вільних для секретів: ' + num(st.stampsFree) + (v.firings ? ' · починав наново: ' + v.firings : '') + '</span></div>'
      + info('Почати наново — це спалити глеки, верстати й віхи, а натомість узяти клейма майстра за все, що наліпив '
        + 'за весь час: кожне дає +' + dec(st.stampBonus * 100) + ' % до всього назавжди. Розписи, секрети, альбом і таблиця '
        + 'лишаються. Кожні ' + STAMPS_PER_CAP + ' клейм — ще один черепок до денної стелі обміну'
        + (cap ? ' (зараз +' + cap + ')' : '') + '.');
    const secrets = '<div class="clk-sub">Родинні секрети<span class="muted small"> · за клейма, назавжди</span></div><div class="clk-secrets">'
      + st.secretList.map((s) => '<button type="button" class="clk-secret' + (s.owned ? ' owned' : '') + '" data-secret="' + esc(s.key)
        + '" data-price="' + s.price + '"' + (s.owned || !st.mine || st.stampsFree < s.price ? ' disabled' : '') + '>'
        + '<b>' + esc(s.name) + '</b><span class="muted small">' + esc(s.desc) + '</span>'
        + '<span class="clk-price stamp' + (s.owned ? ' done' : '') + '">' + (s.owned ? '✓ знаєш' : '🔖 ' + s.price) + '</span></button>').join('')
      + '</div>';
    if (swap(st.fire._static, head + secrets)) {
      st.secretBtns = [...st.fire._static.querySelectorAll('[data-secret]')];
      for (const b of st.secretBtns) b.onclick = () => order(st, 'secret', { key: b.dataset.secret });
    }
    st.slowAt = 0;
  }

  /// Глек на колі, глек на полиці й глечики над полицею: перемальовуємо лише тоді, коли гончар поставив інший
  /// розпис, замісив іншу глину чи добудував гончарню (полиця повніша).
  function wheelJug(st) {
    const shelf = Math.min(9, 3 + Math.floor(((st.ups.workshop && st.ups.workshop.level) || 0) / 4));
    const sig = st.wear + '|' + st.clayBody + '|' + shelf + '|' + (st.craftWheel ? 1 : 0) + (st.craftShelf ? 1 : 0);
    if (st.jugBox._wear === sig) return;
    st.jugBox._wear = sig;
    const w = st.wear || '';
    const body = st.clayBody;
    // Коло й полицю малює ремесло (clicker-craft.js), щойно воно завантажилось: виріб на колі й сирці на полиці.
    if (!st.craftWheel) st.jugBox.innerHTML = jug(w, 'wheel', body);
    st.fallJug.innerHTML = jugSvg(w, 'clk-fall-jug', 'fall', body);
    if (st.craftShelf) return;
    // На полиці — глечики: у розписі, що на колі, і прості (кольору глини); що більша гончарня, то повніша полиця.
    let s = '';
    for (let i = 0; i < shelf; i++) s += jugSvg(i % 2 ? '' : w, '', 'shelf-' + i, body);
    st.shelfJugs.innerHTML = s;
  }

  // ---------- хата: сцена, що росте від покупок ----------

  /// Саму хату (стіни, знаряддя, прикраси, підмайстрів, світ за хатою, день і ніч) малює жива хата — clicker-scene.js
  /// у свій шар .clk-house. Тут лишились тільки запасні значки для полиці «Хата», поки та частина не завантажилась.
  const TOOL_ICON = { paddle: '🥄', string: '🧵', sponge: '🧽', ribs: '📏', lantern: '🏮', apron: '🥼', bucket: '🪣', whistle: '🎶', scales: '⚖️', iron: '🔖' };

  // ---------- хата: полиця з глиною, знаряддям і прикрасами ----------

  /// Підписи згорнутих розділів Майстерні: скільки там уже є, і чи варто розгорнути самим.
  function paintSections(st, ownedStyles) {
    const count = {
      house: st.tools.filter((t) => t.owned).length + st.clays.filter((c) => c.owned && c.price > 0).length,
      styles: ownedStyles ? ownedStyles + '/' + (st.styleList.length || 8) : '',
      orders: st.taken.length ? '🐴' + st.taken.length : '',
    };
    for (const x of SECTIONS) {
      const sec = st.el.querySelector('.clk-sec[data-sec="' + x.key + '"]');
      if (!sec) continue;
      const sum = String(count[x.key] || '');
      const text = x.title + (sum ? ' · ' + sum : '');
      const sm = sec.firstElementChild;
      if (sm.textContent !== text) sm.textContent = text;
      // Розділ став у пригоді, а гравець його ще не чіпав — розгортаємо раз, самі.
      if (!sec.open && storeGet('clk.sec.' + x.key, '') === '' && x.open(st)) {
        sec._auto = true;
        sec.open = true;
        sec._auto = false;
      }
    }
  }

  function housePane(st, ctx) {
    const esc = ctx.esc;
    const clays = '<div class="clk-sub">Глина на колі<span class="muted small"> · купується раз; замішана відлежується 10 хв</span></div>'
      + '<div class="clk-clays">' + st.clays.map((c) => '<button type="button" class="clk-clay' + (c.on ? ' on' : '') + (c.owned ? ' owned' : '')
        + '" data-clay="' + esc(c.key) + '" data-price="' + c.price + '" data-owned="' + (c.owned ? 1 : 0) + '" data-on="' + (c.on ? 1 : 0) + '" disabled>'
        + jugSvg('', 'clk-mini', 'clay-' + (c.key || 'plain'), c.body || '')
        + '<b>' + esc(c.name) + '</b><span class="muted small">' + esc(c.desc) + '</span>'
        + '<span class="clk-price' + (c.owned ? ' done' : '') + '"></span></button>').join('') + '</div>';
    const tools = '<div class="clk-sub">Знаряддя гончаря<span class="muted small"> · раз і назавжди, видно на стіні</span></div>'
      + '<div class="clk-tools">' + st.tools.map((t) => '<button type="button" class="clk-tool' + (t.owned ? ' owned' : '')
        + '" data-house="tool" data-key="' + esc(t.key) + '" data-price="' + t.price + '" data-owned="' + (t.owned ? 1 : 0) + '" disabled>'
        + '<span class="clk-ticon">' + ((H.api.toolIcon && H.api.toolIcon(t.key)) || TOOL_ICON[t.key] || '🔧') + '</span><b>' + esc(t.name) + '</b><span class="muted small">' + esc(t.desc) + '</span>'
        + '<span class="clk-price' + (t.owned ? ' done' : '') + '">' + (t.owned ? '✓ на стіні' : short(t.price)) + '</span></button>').join('') + '</div>';
    const decor = '<div class="clk-sub">Прикраси хати<span class="muted small"> · +2 % до всього кожна, лишаються назавжди</span></div>'
      + '<div class="clk-tools">' + st.decorList.map((d) => '<button type="button" class="clk-tool decor' + (d.owned ? ' owned' : '')
        + '" data-house="adorn" data-key="' + esc(d.key) + '" data-price="' + d.price + '" data-owned="' + (d.owned ? 1 : 0) + '" disabled>'
        + (H.api.decorIcon ? '<span class="clk-ticon">' + H.api.decorIcon(d.key) + '</span>' : '') + '<b>' + esc(d.name) + '</b><span class="muted small">' + esc(d.desc) + '</span>'
        + '<span class="clk-price' + (d.owned ? ' done' : '') + '">' + (d.owned ? '✓ у хаті' : short(d.price)) + '</span></button>').join('') + '</div>';
    if (swap(st.housePane, clays + tools + decor)) {
      st.clayBtns = [...st.housePane.querySelectorAll('[data-clay]')];
      for (const b of st.clayBtns) {
        b._price = b.querySelector('.clk-price');
        b.onclick = () => order(st, 'knead', { kind: b.dataset.clay });
      }
      st.houseBtns = [...st.housePane.querySelectorAll('[data-house]')];
      for (const b of st.houseBtns) b.onclick = () => order(st, b.dataset.house, { key: b.dataset.key });
      collectCountdowns(st);
      st.slowAt = 0;
    }
  }

  // ---------- дошка купців ----------

  function ordersPane(st, ctx) {
    const esc = ctx.esc;
    const note = info('Купець у дорозі повертає більше, ніж узяв: що довша дорога, то щедріше (5 хв — ×1,4, 30 хв — ×2,2). '
      + 'За розпис із колекції платить одразу ×1,6. Дошка оновлюється раз на 4 хвилини, кого не взяв — поїхав. '
      + 'Клейма спалюють купців у дорозі разом із глеками.'
      + (st.tools.some((t) => t.key === 'scales' && t.owned) ? ' Ваги купця: +20 % до кожної плати.' : ''));
    const head = '<div class="clk-sub">Дошка купців<span class="muted small"> · нові купці через <span class="clk-cd" data-at="' + st.refreshAt + '" data-done="ось-ось"></span></span>' + note + '</div>';
    const board = st.orders.length
      ? '<div class="clk-orders">' + st.orders.map((o) => {
        const invest = o.kind === 'invest';
        const text = invest
          ? 'Візьме ' + short(o.need) + ' глеків у дорогу і за ' + o.minutes + ' хв поверне <b>' + short(o.pay) + '</b>'
          : 'Купить ' + short(o.need) + ' глеків у розписі «' + esc(o.styleName) + '» за <b>' + short(o.pay) + '</b> одразу'
            + (o.can ? '' : ' <span class="clk-no">(цього розпису ще нема)</span>');
        return '<div class="clk-order' + (invest ? '' : ' style') + '"><div class="clk-oname">' + (invest ? '🐴 ' : '🧺 ') + esc(o.merchant) + '</div>'
          + '<div class="small clk-otext">' + text + '</div>'
          + '<button type="button" class="primary small clk-take" data-take="' + o.id + '" data-kind="' + esc(o.kind) + '" data-need="' + o.need
          + '" data-can="' + (o.can ? 1 : 0) + '" disabled>' + (invest ? 'Відправити' : 'Продати') + ' · ' + short(o.need) + ' 🏺</button></div>';
      }).join('') + '</div>'
      : '<div class="clk-teaser muted small">Усіх купців уже взято — нові прийдуть із новою дошкою</div>';
    const taken = st.taken.length
      ? '<div class="clk-sub">У дорозі · ' + st.taken.length + ' з ' + st.maxTaken + '</div><div class="clk-takens">'
        + st.taken.map((t) => '<div class="clk-taken"><span>🐴 ' + esc(t.merchant) + '</span><span class="muted small">повернеться через <span class="clk-cd" data-at="'
          + t.payAt + '" data-done="уже на порозі"></span> з <b>' + short(t.pay) + '</b></span></div>').join('') + '</div>'
      : '';
    if (swap(st.ordersPane, head + board + taken)) {
      st.orderBtns = [...st.ordersPane.querySelectorAll('[data-take]')];
      for (const b of st.orderBtns) b.onclick = () => order(st, 'take', { id: +b.dataset.take });
      collectCountdowns(st);
      st.slowAt = 0;
    }
  }

  /// Усі відліки обох панелей — щоб paintCountdowns не шукав їх щоп'ятої секунди.
  function collectCountdowns(st) {
    st.cds = [...st.housePane.querySelectorAll('.clk-cd'), ...st.ordersPane.querySelectorAll('.clk-cd')];
  }

  /// Купець повернувся між видами: «+N» над сценою й тост. Перший вид лише запам'ятовує, що вже було.
  function paidLately(st, ctx, paid) {
    if (!st.paidSeen) { st.paidSeen = new Set(paid.map((p) => p.id)); return; }
    for (const p of paid) {
      if (st.paidSeen.has(p.id)) continue;
      st.paidSeen.add(p.id);
      const at = Date.parse(p.at);
      if (!Number.isFinite(at) || serverNow(st) - at > 120000) continue;
      popAt(st, '+' + short(p.pay), 'big', 50, 40);
      H.api.sfx('coins');
      sparks(st, st.fx, 10, true, 50, 44);
      if (ctx.toast) ctx.toast('🐴 ' + p.merchant + ' повернувся: +' + short(p.pay) + ' ' + potsWord(p.pay), 'ok');
    }
  }

  /// Вивіска над лічильником: найвищий верстат драбини, що вже куплений.
  function paintSign(st) {
    let text = '';
    for (const k of Object.keys(st.ups)) {
      const u = st.ups[k];
      if (u.kind === 'idle' && u.level > 0) text = u.name + ' · ' + u.level;
    }
    text = text ? '🏠 ' + text : '🏠 Хата гончаря';
    if (st.sign.textContent !== text) st.sign.textContent = text;
  }

  /// Таблицю тягнемо з paintSlow — тобто лише тоді, коли картку видно: схована картка суперника однаково не
  /// покаже, а таймер крутився б і на вкладці «Ефір».
  function loadBoard(st) {
    if (Date.now() - st.boardAt < BOARD_MS) return;
    st.boardAt = Date.now();
    const nick = (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
    fetch('/api/games/leaderboard?game=clicker&period=all', { headers: { 'X-Nick': encodeURIComponent(nick) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (d && Array.isArray(d.rows)) { st.board = d.rows; st.slowAt = 0; } })
      .catch(() => { /* без таблиці просто не буде рядка про суперника */ });
  }

  // ---------- api для частин ----------

  /// Новий вузол вмісту вікна щоразу: відповідь сервера, що запізнилась (хата друга, мінігра), перевіряє
  /// body.isConnected — і більше не перепише чуже вікно, відкрите за цей час.
  function freshBody(st) {
    const nb = document.createElement('div');
    nb.className = 'clk-ov-body';
    st.ov.body.replaceWith(nb);
    st.ov.body = nb;
  }

  H.api = {
    short, num, dec, big, span, plural, potsWord, potsShort, shards, mmss, jug, jugSvg, STYLE, swap, fleeting, serverNow, visible,
    storeGet, storeSet,
    guardOn: (st) => guardOn(st),
    info,
    esc: (st, x) => ((st.ctx && st.ctx.esc) || ((y) => String(y)))(x),
    order: (st, action, payload) => order(st, action, payload),
    /// Дія з відповіддю (Promise): для мінігор, яким треба знати результат. Накопичені кліки летять першими.
    act: (st, action, payload) => { if (!st.ctx || !st.mine) return Promise.resolve(null); flush(st); return st.ctx.act(action, payload); },
    popAt: (st, text, cls, x, y) => popAt(st, text, cls, x, y),
    sparks: (st, host, n, gold, x, y) => sparks(st, host || st.fx, n, gold, x, y),
    toast: (st, text, kind) => { if (st.ctx && st.ctx.toast) st.ctx.toast(text, kind || 'ok'); },
    /// Звук: без живої хати (clicker-scene.js) — тиша; сцена підміняє цю функцію.
    sfx: () => {},
    /// Силует виробу: малює ремесло (clicker-craft.js); доти — глечик.
    wareSvg: (ware, o) => jugSvg((o && o.style) || '', (o && o.cls) || '', (o && o.slot) || 'w-' + ware, o && o.clay),
    /// Вкладка частини: кнопка стає за порядком order серед наявних, панель — у праву колонку. Повертає панель.
    tab(st, key, label, order) {
      if (st.panes[key]) return st.panes[key];
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'ghost';
      b.dataset.tab = key;
      b.dataset.order = String(order || 50);
      b.textContent = label;
      const after = [...st.tabs.querySelectorAll('[data-tab]')].find((x) => +(x.dataset.order || 50) > (order || 50));
      st.tabs.insertBefore(b, after || null);
      b.onclick = () => { H.api.sfx('tap'); setTab(st, key); };
      const pane = document.createElement('div');
      pane.className = 'clk-pane';
      pane.dataset.pane = key;
      pane.hidden = true;
      st.el.querySelector('.clk-side').appendChild(pane);
      st.panes[key] = pane;
      st.tabText[key] = label;
      // Гравець лишив цю вкладку відкритою минулого разу — повернути, щойно вона з'явилась.
      if (storeGet('clk.tab', 'shop') === key) setTab(st, key);
      return pane;
    },
    tabLabel(st, key, text) {
      st.tabText[key] = text;
      labelTab(st, key);
    },
    /// Коротка нотатка на ярлику («🔥0:12», «📜2», «🛒»): кожна частина пише свою, а показуємо лише найважливішу —
    /// п'ять ярликів мусять улізти в 375 px, тож два-три хвости на одному з них цього не варті.
    tabNote(st, key, id, text, prio) {
      const notes = st.tabNotes[key] || (st.tabNotes[key] = {});
      const was = notes[id];
      if (was && was.text === (text || '') && was.prio === (prio || 5)) return;
      notes[id] = { text: text || '', prio: prio || 5 };
      labelTab(st, key);
    },
    /// Гейт вкладки: поки функція каже «ще ні» — ярлика нема. Умова читається з виду, тож гравець із прогресом
    /// бачить усе своє одразу, а новачок — лише те, що вже може робити.
    showWhen(st, key, fn) {
      st.gates[key] = fn;
      gateTabs(st, st.lastView);
    },
    /// Стрічка подій під смугою (її веде ярмарок, clicker-fair.js): ядро лише каже, що відкрилось.
    feed: () => {},
    /// Іменоване місце всередині чужої панелі: так горно, комора й замовлення живуть в одному «Ремеслі».
    slot: (st, name) => (st.el ? st.el.querySelector('[data-slot="' + name + '"]') : null),
    showTab: (st, key) => setTab(st, key),
    /// Свій шар частини: back — <g> у SVG під колом (viewBox 360×450, опорні точки — api.scene з clicker-scene.js),
    /// front — <div> над сценою (координати у відсотках сцени).
    layer(st, name, id) {
      const host = name === 'back' ? st.back : st.front;
      let el = host.querySelector('[data-part="' + id + '"]');
      if (!el) {
        el = name === 'back' ? document.createElementNS('http://www.w3.org/2000/svg', 'g') : document.createElement('div');
        el.setAttribute('data-part', id);
        host.appendChild(el);
      }
      return el;
    },
    /// Модальна панель поверх картки. Одна за раз: нова закриває попередню (з її onClose).
    /// opts.keep — функція «зараз не на часі закривати» (мінігра в розпалі): Око майстра її перечекає.
    overlay(st, html, opts) {
      if (!st.ov.el.hidden) H.api.closeOverlay(st);
      freshBody(st);
      st.ovDownBack = true;
      st.ov.body.innerHTML = html;
      st.ov.el.className = 'clk-overlay' + (opts && opts.cls ? ' ' + opts.cls : '');
      st.ov.onClose = (opts && opts.onClose) || null;
      st.ov.keep = (opts && opts.keep) || null;
      st.ov.el.hidden = false;
      // Картка буває вища за екран: вікно стає там, куди гравець зараз дивиться, а не вгорі картки.
      const r = st.el.getBoundingClientRect();
      const box = st.ov.el.firstElementChild;
      box.style.marginTop = Math.max(0, Math.min(-r.top + 12, r.height - 160)) + 'px';
      box.style.maxHeight = Math.max(240, window.innerHeight - 24) + 'px';
      return st.ov.body;
    },
    closeOverlay(st) {
      if (!st.ov || st.ov.el.hidden) return;
      st.ov.el.hidden = true;
      const f = st.ov.onClose;
      st.ov.onClose = null;
      st.ov.keep = null;
      freshBody(st);
      if (f) try { f(); } catch (e) { console.error(e); }
    },
    overlayOpen: (st) => !!st.ov && !st.ov.el.hidden,
    /// Вікно просить не чіпати себе (мінігра, де вже водять пальцем).
    overlayBusy(st) {
      if (!st.ov || st.ov.el.hidden || !st.ov.keep) return false;
      try { return !!st.ov.keep(); } catch (e) { console.error(e); return false; }
    },
  };

  // ---------- модуль ----------

  /// Стіл вищий за те, що лишається під лобі й рядком стола, тому перший вид після входу підкручує картку
  /// під шапку сайту (scroll-margin-top у css): коло, смуга «Шлях виробу» і полиця верстатів стають
  /// в один екран і гортати нічого не треба. Каркас монтує картку один раз і далі лише ховає її,
  /// тому міра — не mount, а мить, коли картка знову стала видною.
  function placeInView(st) {
    const card = st.root && st.root.closest && st.root.closest('.gtable');
    if (!card) return;
    if (card.hidden || !card.isConnected) { st.inView = false; return; }
    if (st.inView) return;
    st.inView = true;
    if (!window.matchMedia('(min-width: 1000px) and (min-height: 700px)').matches) return;
    // Через кадр-другий: на першому виді полиця ще порожня (картка низька), а app.js на зміну адреси
    // ще й скидає сторінку вгору — раніше міряти немає чого.
    setTimeout(() => {
      if (card.hidden || !card.isConnected) return;
      const r = card.getBoundingClientRect();
      if (r.bottom <= window.innerHeight) return;   // і так усе видно
      if (r.top < 0) return;                        // гравець уже гортав сам — не смикаємо
      card.scrollIntoView({ block: 'start', behavior: 'auto' });
    }, 150);
  }

  const MOD = {
    id: 'clicker',
    icon: ICON,
    seatNames: ['гончар'],
    seatClass: ['c'],

    mount(root, ctx) {
      const st = state(root);
      // Картка — на всю ширину сітки столів (див. .clk-wide у css): інакше сцена й полиці лягали б одним стовпчиком.
      const card = root.closest && root.closest('.gtable');
      if (card) card.classList.add('clk-wide');
      root.innerHTML = '<div class="clk"><div class="clk-lay">'
        // Ліворуч (або зверху на телефоні): вивіска, лічильник, сцена з полицею й колом, бонуси, прилавок.
        + '<div class="clk-scene">'
        + '<div class="clk-sign"></div>'
        + '<div class="clk-head"><b class="clk-count">0</b><span class="muted small">глеків</span></div>'
        + '<div class="clk-rate muted small"></div>'
        + '<div class="clk-rival small" hidden></div>'
        + '<div class="clk-stage">'
        // Хата, що росте від покупок: шар під полицею й колом (viewBox 360×450 — сцена 4:5; малює clicker-scene.js).
        + '<svg class="clk-house" viewBox="0 0 360 450" preserveAspectRatio="none" aria-hidden="true"></svg>'
        // Шари для частин (api.layer): back — SVG під колом у тих самих координатах, що й хата; front — DOM над сценою.
        + '<svg class="clk-layer-back" viewBox="0 0 360 450" preserveAspectRatio="none" aria-hidden="true"></svg>'
        + '<div class="clk-shelf"><div class="clk-shelf-jugs"></div></div>'
        + '<div class="clk-wheelbox">'
        + '<svg class="clk-heat" viewBox="0 0 100 100" aria-hidden="true"><circle class="bg" cx="50" cy="50" r="47"/>'
        + '<circle class="fg" cx="50" cy="50" r="47"/></svg>'
        + '<button type="button" class="clk-wheel" aria-label="Крутити коло">'
        // Крутиться сам круг із борознами й цяткою (без неї обертання ідеального кола не видно),
        // а глек стоїть рівно: гончар його тримає.
        + '<svg viewBox="0 0 100 100" aria-hidden="true">'
        + '<g class="clk-turn"><circle class="clk-disc" cx="50" cy="50" r="46"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="35"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="24"/>'
        + '<circle class="clk-speck" cx="50" cy="12" r="2.6"/></g>'
        // Обгортка для «пружини» глини на клік (жива хата): у самого .clk-jugbox transform уже зайнятий розгоном.
        + '<g class="clk-squash"><g class="clk-jugbox"></g></g>'
        + '</svg></button>'
        + '<div class="clk-sparks"></div><div class="clk-pops"></div></div>'
        + '<button type="button" class="clk-gold" hidden aria-label="Розписний глек — лови!" title="Розписний глек — лови!">'
        + '<svg class="clk-gold-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>'
        + jugSvg('golden', 'clk-gold-jug', 'gold') + '</button>'
        + '<button type="button" class="clk-fall" hidden aria-label="Глек падає з полиці — лови!" title="Лови!"><span class="clk-fall-box"></span></button>'
        + '<div class="clk-fx"></div>'
        + '<div class="clk-layer-front"></div>'
        + '</div>'
        // Око майстра стає на місце сцени: відлік паузи або полиця з глечиками.
        + '<div class="clk-eye" hidden><div class="clk-eye-head"><b>👁 Око майстра</b><span class="clk-eye-tries small"></span></div>'
        + '<div class="clk-eye-text small"></div>'
        + '<div class="clk-eye-pic veiled"><img alt="Полиця з глечиками, горщиками, мисками й черепками" draggable="false">'
        + '<div class="clk-eye-marks"></div>'
        // Завіса над полицею: доки не натиснуто кнопку, картинки не видно й торкання в неї не йдуть.
        + '<div class="clk-eye-veil"><span class="small">Полиця відкриється після кнопки — випадкові кліки не рахуються</span>'
        + '<button type="button" class="primary clk-eye-go" disabled>👁 Показати полицю</button></div></div>'
        + '<button type="button" class="ghost small clk-eye-reset" hidden disabled>Скинути торкання</button></div>'
        + '<div class="clk-buffs" hidden></div>'
        + '<div class="clk-sell"><button type="button" class="primary clk-one" disabled></button>'
        + '<button type="button" class="ghost clk-all" data-pots="0" disabled></button></div>'
        + '<div class="clk-left muted small"></div>'
        + '</div>'
        // Праворуч (або нижче): вкладки з верстатами, розписами й обпалом.
        + '<div class="clk-side">'
        // П'ять вкладок (v8): Ремесло · Майстерня · Альбом · Село · Клейма. «Ремесло», «Альбом» і «Село»
        // додають частини, тож тут — лише свої дві; ярлики зайвого гравець не бачить, поки не доросте (gateTabs).
        + '<div class="clk-tabs" role="tablist">'
        + '<button type="button" class="ghost" data-tab="shop" data-order="20">🔨 Майстерня</button>'
        + '<button type="button" class="ghost" data-tab="fire" data-order="50">🔥 Клейма</button></div>'
        + '<div class="clk-pane" data-pane="shop">'
        + '<div class="clk-modes"><span class="muted small">купувати</span>'
        + '<button type="button" class="ghost" data-mode="1">×1</button>'
        + '<button type="button" class="ghost" data-mode="10">×10</button>'
        + '<button type="button" class="ghost" data-mode="max">макс</button></div>'
        + '<div class="clk-markbox"></div><div class="clk-shop"></div>'
        // Колишні вкладки «Хата», «Розписи» й «Купці» — згорнуті розділи Майстерні: усе, що купують за глеки, в одному місці.
        + SECTIONS.map((x) => '<details class="clk-sec" data-sec="' + x.key + '"><summary>' + x.title + '</summary></details>').join('')
        + '</div>'
        + '<div class="clk-pane" data-pane="house" hidden></div>'
        + '<div class="clk-pane" data-pane="orders" hidden></div>'
        + '<div class="clk-pane" data-pane="styles" hidden></div>'
        + '<div class="clk-pane" data-pane="fire" hidden>'
        + '<div class="clk-firebox"><div class="clk-bar"><i></i></div><div class="clk-next muted small"></div>'
        + '<button type="button" class="primary clk-fire" disabled></button><div class="clk-after small"></div></div>'
        + '<div class="clk-firestatic"></div></div>'
        + '</div>'
        + '</div>'
        // Модальна панель частин (мінігри, дарунки, хата друга): одна за раз, поверх усієї картки.
        + '<div class="clk-overlay" hidden><div class="clk-ov-box" role="dialog"><button type="button" class="ghost clk-ov-x" aria-label="Закрити">✕</button>'
        + '<div class="clk-ov-body"></div></div></div>'
        + '</div>';
      const q = (s) => root.querySelector(s);
      st.el = q('.clk');
      st.count = q('.clk-count');
      st.rate = q('.clk-rate');
      st.rival = q('.clk-rival');
      st.sign = q('.clk-sign');
      st.stage = q('.clk-stage');
      st.wheelBox = q('.clk-wheelbox');
      st.wheel = q('.clk-wheel');
      st.turn = q('.clk-turn');
      st.jugBox = q('.clk-jugbox');
      st.jugBox._wear = null;
      st.heatRing = q('.clk-heat .fg');
      st.pops = q('.clk-pops');
      st.sparks = q('.clk-sparks');
      st.fx = q('.clk-fx');
      st.buffs = q('.clk-buffs');
      st.gold = q('.clk-gold');
      st.fallEl = q('.clk-fall');
      st.fallJug = q('.clk-fall-box');
      st.shelfJugs = q('.clk-shelf-jugs');
      st.one = q('.clk-one');
      st.all = q('.clk-all');
      st.left = q('.clk-left');
      st.tabs = q('.clk-tabs');
      st.modes = q('.clk-modes');
      st.marks = q('.clk-markbox');
      st.shop = q('.clk-shop');
      st.panes = { shop: q('[data-pane="shop"]'), fire: q('[data-pane="fire"]') };
      st.styles = q('[data-pane="styles"]');
      st.housePane = q('[data-pane="house"]');
      st.ordersPane = q('[data-pane="orders"]');
      st.tabNotes = {};
      st.gates = {};
      st.gateReady = false;
      // Хата, розписи й вклад купцям — усередину Майстерні. Елементи ті самі (їх малює той самий код),
      // але вони більше не вкладки, тож setTab їх не ховає.
      for (const x of SECTIONS) {
        const sec = q('.clk-sec[data-sec="' + x.key + '"]');
        const pane = q('[data-pane="' + x.key + '"]');
        pane.hidden = false;
        pane.classList.remove('clk-pane');
        pane.classList.add('clk-secbody');
        sec.appendChild(pane);
        sec.open = storeGet('clk.sec.' + x.key, '') === '1';
        sec.addEventListener('toggle', () => { if (!sec._auto) storeSet('clk.sec.' + x.key, sec.open ? '1' : '0'); });
      }
      st.house = q('.clk-house');
      st.fire = q('.clk-firebox');
      st.fire._bar = q('.clk-bar i');
      st.fire._next = q('.clk-next');
      st.fire._btn = q('.clk-fire');
      st.fire._after = q('.clk-after');
      st.fire._static = q('.clk-firestatic');
      st.eye = { el: q('.clk-eye'), text: q('.clk-eye-text'), tries: q('.clk-eye-tries'), pic: q('.clk-eye-pic'),
        img: q('.clk-eye-pic img'), marks: q('.clk-eye-marks'), veil: q('.clk-eye-veil'), go: q('.clk-eye-go'), reset: q('.clk-eye-reset') };
      st.eyeKey = '';
      st.eyeOpen = false;
      st.ringOff = -1;
      st.glow = -1;
      st.jugScale = -1;
      st.angleAt = 0;
      st.ctx = ctx;
      ctx.clk = st;                     // щоб onKey дістався до стану: там є лише ctx
      // Клік — це пара справжніх pointerdown/pointerup на колі, а не подія click: її дає і el.click() зі скрипта,
      // і клавіатура, і в ній нема ні миті натискання, ні тривалості.
      st.wheel.addEventListener('pointerdown', (e) => pressWheel(st, e));
      st.wheel.addEventListener('pointerup', (e) => releaseWheel(st, e));
      st.wheel.addEventListener('pointercancel', (e) => releaseWheel(st, e));
      st.wheel.addEventListener('contextmenu', (e) => e.preventDefault());   // довгий тап на телефоні — не меню
      st.gold.addEventListener('click', (e) => catchGolden(st, e));
      // Глек, що падає, ловимо на pointerdown: за час між натиском і відпусканням він устигає посунутись, і click
      // на рухомій кнопці міг би не спрацювати.
      st.fallEl.addEventListener('pointerdown', (e) => grabFall(st, e));
      st.fallEl.addEventListener('contextmenu', (e) => e.preventDefault());
      st.eye.go.addEventListener('click', (e) => openShelf(st, e));
      st.eye.img.addEventListener('pointerdown', (e) => tapShelf(st, e));
      st.eye.img.addEventListener('contextmenu', (e) => e.preventDefault());
      st.eye.reset.onclick = () => { st.taps = []; paintEye(st); };
      // Пробіл: натиснули (onKey) → відпустили (тут). Слухаємо весь документ: фокус між ними міг утекти.
      if (st.onKeyUp) document.removeEventListener('keyup', st.onKeyUp);
      st.onKeyUp = (e) => {
        if (e.code !== 'Space' || !st.keyDown) return;
        const down = st.keyDown;
        st.keyDown = 0;
        if (!e.isTrusted || e.timeStamp - down > HOLD_MS) return;
        spin(st, down, e.timeStamp - down, -1, -1, SRC.key);
      };
      document.addEventListener('keyup', st.onKeyUp);
      st.one.onclick = () => order(st, 'sell', { pots: st.rateOf });
      st.all.onclick = () => order(st, 'sell', { pots: +st.all.dataset.pots });
      st.fire._btn.onclick = () => fire(st);
      for (const b of st.tabs.querySelectorAll('[data-tab]')) b.onclick = () => { H.api.sfx('tap'); setTab(st, b.dataset.tab); };
      for (const b of st.modes.querySelectorAll('[data-mode]')) b.onclick = () => setMode(st, b.dataset.mode);
      // Вкладка частини (горно, альбом…) з'явиться, коли частина завантажиться: доти — майстерня, а пам'ять не чіпаємо.
      // Клейма (престиж) показуємо тоді, коли до них лишилось кілька кроків, а не з нульового рахунку.
      H.api.showWhen(st, 'fire', (st2) => st2.stamps > 0 || (st2.total || 0) + Math.max(0, st2.shown - st2.base) >= 1e8);
      setTab(st, st.panes[st.tab] ? st.tab : 'shop', false);
      setMode(st, ['1', '10', 'max'].includes(st.mode) ? st.mode : '1');
      st.timer = setInterval(() => flush(st), BATCH_MS);
      st.front = q('.clk-layer-front');
      st.back = q('.clk-layer-back');
      st.ov = { el: q('.clk-overlay'), body: q('.clk-ov-body'), x: q('.clk-ov-x'), onClose: null };
      st.ov.x.onclick = () => H.api.closeOverlay(st);
      // Закриваємо на click, а не на pointerdown: інакше на телефоні вікно зникало від дотику, а click того ж тапу
      // натискав кнопку, що була під затемненням (продати виріб, відкрити іншу клітинку).
      // Але click зринає на спільному предку натиснення й відпускання: штрих мінігри, що почався на полотні й
      // з'їхав за край коробки, давав click саме на затемненні — і розпис обривався посеред роботи. Тож закриваємо
      // лише тоді, коли палець і ліг на затемнення.
      st.ov.el.addEventListener('pointerdown', (e) => { st.ovDownBack = e.target === st.ov.el; });
      st.ov.el.addEventListener('click', (e) => { if (e.target === st.ov.el && st.ovDownBack !== false) H.api.closeOverlay(st); });
      st.root = root;
      H.mounted.add(st);
      for (const p of H.parts) mountPart(st, p);
      if (!st.raf) loop(st);
    },

    update(root, ctx) {
      const st = state(root);
      if (!st.el) return;
      placeInView(st);
      st.ctx = ctx;
      ctx.clk = st;
      st.mine = !!ctx.mine;
      const v = ctx.view;
      if (v && v.pots != null) {
        // Сервер — джерело правди: беремо його число і його мітку часу, від них доліковуємо далі.
        // Усе, що вже полетіло, у цьому числі вже враховано — свій запас відпущених кліків обнуляємо.
        st.inflight = 0;
        st.inflightGain = 0;
        st.base = v.pots;
        st.total = v.total || 0;
        const now = Date.parse(v.now);
        st.viewNow = Number.isFinite(now) ? now : Date.now();
        const sync = Date.parse(v.lastSync);
        st.lastSync = Number.isFinite(sync) ? sync : st.viewNow;
        st.recvAt = Date.now();
        st.offlineMs = (v.offlineHours || 8) * 3600 * 1000;
        st.clickBase = v.clickBase || v.perClick || 1;
        st.baseSecond = v.baseSecond != null ? v.baseSecond : v.perSecond || 0;
        st.fairUntil = (v.fair && Date.parse(v.fair.until)) || 0;
        st.fairMult = (v.fair && v.fair.mult) || 7;
        st.inspireUntil = (v.inspire && Date.parse(v.inspire.until)) || 0;
        st.inspireMult = (v.inspire && v.inspire.mult) || 25;
        st.rateOf = v.rate || 100;
        st.canSell = v.canSellToday || 0;
        st.ups = v.upgrades || {};
        st.markList = v.marks || [];
        st.styleList = v.styles || [];
        st.secretList = v.secrets || [];
        st.wear = v.wear || '';
        st.stamps = v.stamps || 0;
        st.stampsFree = v.stampsFree || 0;
        st.stampBonus = v.stampBonus || 0.02;
        // Розгін — серверний, плюс наші кліки, що ще не полетіли (сервер про них не знає).
        st.heatFull = v.heatFull || 18;
        st.heatTau = v.heatTau || 3;
        st.momentumMax = v.momentumMax || 1;
        if (v.heat != null) { st.heat = Math.max(0, v.heat) + st.hands.length; st.heatAt = Date.now(); }
        if (v.golden) {
          const at = Date.parse(v.golden.at);
          const until = Date.parse(v.golden.until);
          if (Number.isFinite(at) && Number.isFinite(until)) st.golden = { at, until, x: v.golden.x || 0, y: v.golden.y || 0 };
        }
        if (v.fall) {
          const at = Date.parse(v.fall.at);
          const until = Date.parse(v.fall.until);
          if (Number.isFinite(at) && Number.isFinite(until)) st.fall = { at, until, x: v.fall.x || 40 };
          st.fallGain = v.fall.gain || 0;
          st.fallStreak = v.fall.streak || 0;
        }
        // Хата: глина, знаряддя, прикраси й купці. Старий сервер (хвилина деплою) house не шле — тоді все порожнє.
        const hs = v.house || {};
        st.clays = hs.clays || [];
        st.clay = hs.clay || '';
        st.clayBody = hs.clayBody || '';
        st.clayRestUntil = Date.parse(hs.clayRestUntil) || 0;
        st.tools = hs.tools || [];
        st.decorList = hs.decor || [];
        const od = hs.orders || {};
        st.orders = od.board || [];
        st.taken = (od.taken || []).map((t) => ({ id: t.id, merchant: t.merchant, pay: t.pay, payAt: Date.parse(t.payAt) || 0 }));
        st.refreshAt = Date.parse(od.refreshAt) || 0;
        st.maxTaken = od.maxTaken || 3;
        paidLately(st, ctx, od.paid || []);
        const g = v.guard;
        st.guard = g ? {
          serial: g.serial || 0, count: g.count || 0, png: g.png || '', width: g.width || 400, height: g.height || 250,
          misses: g.misses || 0, maxMisses: g.maxMisses || 3, lockUntil: (g.lockUntil && Date.parse(g.lockUntil)) || 0, why: g.why || '',
        } : null;
        // Майстер спитав — усе, що ще не полетіло, однаково не зарахується: не малюємо цих глеків на лічильнику.
        if (st.guard) { st.hands.length = 0; st.handsGain = 0; }
        // Під вікном частини Око майстра було б невидиме, а кліки — не зараховані: майстер важливіший за вікно.
        // Виняток — мінігра розпису, де вже водять пальцем: вона однаково скінчиться за кілька секунд, а обірвати
        // її посеред штриха означало б згаяти всю роботу. Майстер зачекає — кола ми в ці секунди й не крутимо.
        if (st.guard && H.api.overlayOpen(st) && !H.api.overlayBusy(st)) H.api.closeOverlay(st);
        // Каталоги (тексти виробів, подій…) сервер шле лише до першої дії — кешуємо; нема в кеші — просимо раз.
        if (v.catalog) st.catalog = v.catalog;
        else if (!st.catalog && !st.catalogAsked && ctx.mine && ctx.act) { st.catalogAsked = true; ctx.act('look', { catalog: true }); }
        st.lastView = v;
      }
      const one = 'Обміняти ' + num(st.rateOf) + ' → 🏺1';
      if (st.one.textContent !== one) st.one.textContent = one;
      const left = st.canSell > 0
        ? 'сьогодні ще ' + num(st.canSell) + ' ' + shards(st.canSell) + ', по ' + num(st.rateOf) + ' глеків за черепок'
        : 'на сьогодні черепки скінчились, приходь завтра';
      if (st.left.textContent !== left) st.left.textContent = left;

      const owned = st.styleList.filter((s) => s.owned).length;
      st.tabText.shop = '🔨 Майстерня';
      st.tabText.fire = '🔥 Клейма';
      H.api.tabNote(st, 'fire', 'stamps', st.stamps ? '🔖' + st.stamps : '', 1);
      labelTab(st, 'shop');
      labelTab(st, 'fire');
      paintSections(st, owned);
      wheelJug(st);
      paintSign(st);
      shop(st, ctx);
      housePane(st, ctx);
      ordersPane(st, ctx);
      styles(st, ctx);
      firePane(st, ctx);
      if (v && v.pots != null) for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'update', st, v, H.api);
      gateTabs(st, v);
      st.slowAt = 0;
      paint(st);
    },

    onKey(e, ctx) {
      if (e.code !== 'Space' || !ctx.mine || !ctx.clk) return false;
      const st = ctx.clk;
      // Фокус на іншій кнопці картки — пробіл належить їй: на верстаті чи прилавку ми б крутили коло замість
      // покупки й продажу. Сама кнопка кола — наша: її власний click ми кліком не рахуємо.
      const on = document.activeElement;
      if (on && on !== st.wheel && st.el && st.el.contains(on) && on.closest('button,summary,a,input,select,textarea,[tabindex]')) return false;
      if (H.api.overlayOpen(st)) return false;
      // Затиснутий пробіл сипле keydown з repeat — це не клацання, а автоповтор клавіатури.
      if (!e.isTrusted || e.repeat || guardOn(st)) return true;
      if (!st.keyDown) st.keyDown = e.timeStamp;
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.total == null) return '';
      return 'усього наліплено ' + short(v.total) + ' · розписних спіймано ' + num(v.caught || 0)
        + ' · з полиці ' + num(v.grabbed || 0) + ' · обміняно сьогодні ' + num(v.soldToday || 0);
    },

    unmount(root) {
      const st = root._clk;
      if (!st) return;
      clearInterval(st.timer);
      clearTimeout(st.eyeArm);
      cancelAnimationFrame(st.raf);
      if (st.onKeyUp) document.removeEventListener('keyup', st.onKeyUp);
      for (const p of H.parts) if (st.parts && st.parts.has(p.id)) callPart(p, 'unmount', st, H.api);
      H.mounted.delete(st);
      if (st.ov) H.api.closeOverlay(st);
      const card = root.closest && root.closest('.gtable');
      if (card) card.classList.remove('clk-wide');
      st.raf = 0;
      st.el = null;
      root._clk = null;
    },
  };
  HGames.register(MOD);
})();
