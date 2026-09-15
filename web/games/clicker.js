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
  5) показує Око майстра (view.guard): полицю-картинку, де треба торкнутись усіх глечиків (Act('answer', { taps })),
     або паузу кола з відліком. Де глечики — клієнт не знає: це знає лише сервер.

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
        guard: null, eye: null, taps: [], eyeBusy: false,
        raf: 0, timer: 0, boardAt: 0, board: null, ctx: null,
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
    const label = 'Усе (' + num(many) + ' 🏺)';
    if (st.all.textContent !== label) st.all.textContent = label;

    paintWheel(st);
    paintGolden(st);
    paintFall(st);

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
    if (st.tab === 'house' || st.tab === 'orders') paintCountdowns(st, sn, shown);
    // Купець повернувся, а гончар нічого не робив: сервер рахує повернення лише при дії чи виді, тож питаємо вид
    // самі — раз на купця, з запасом у дві секунди й лише коли картку видно (як look для глеків).
    for (const t of st.taken) {
      if (sn < t.payAt + 2000 || st.payLooked.has(t.id) || !st.ctx || !visible(st)) continue;
      st.payLooked.add(t.id);
      st.ctx.act('look');
    }
    paintEye(st);
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

  /// За що майстер питає не в чергу (why з виду) — перед проханням торкнутись глечиків.
  const DOUBT = {
    rhythm: 'Кліки йшли надто рівно, мов під метроном, — так клацає автоклікер. Покажи майстрові, що це рука: ',
    press: 'Кнопку відпускали миттєво, раз за разом, — так клацає автоклікер (або тачпад). Покажи майстрові, що це рука: ',
  };

  /// Панель майстра замість сцени: відлік паузи або полиця, де треба торкнутись глечиків.
  function paintEye(st) {
    const e = st.eye;
    const g = st.guard;
    const on = guardOn(st) && st.mine;
    if (st.stage.hidden !== on) st.stage.hidden = on;
    if (e.el.hidden === on) e.el.hidden = !on;
    if (!on) return;

    const left = g.lockUntil ? g.lockUntil - serverNow(st) : 0;
    const locked = left > 0;
    let text;
    if (locked) {
      const s = Math.ceil(left / 1000);
      text = '🔒 Коло стоїть ще ' + Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0') + '. Три полиці поспіль — не ті глеки.'
        + ' Пасив, покупки й прилавок працюють; кліки й глеки — ні. Після паузи майстер спитає ще раз.';
    } else {
      const ask = 'усіх глечиків на полиці — їх тут ' + g.count + '. Глечик — той, що з вузькою шийкою. Поки не відповіси, кліки не рахуються.';
      text = DOUBT[g.why] ? DOUBT[g.why] + 'торкнись ' + ask : 'Майстер дивиться, чи коло крутить рука, а не автоклікер. Торкнись ' + ask;
    }
    if (e.text.textContent !== text) e.text.textContent = text;
    const tries = locked ? '' : g.misses ? 'не ті — ось інша полиця · спроба ' + (g.misses + 1) + ' з ' + g.maxMisses : '';
    if (e.tries.textContent !== tries) e.tries.textContent = tries;
    if (e.pic.hidden !== locked) e.pic.hidden = locked;
    if (e.reset.hidden !== locked) e.reset.hidden = locked;

    // Картинку міняємо лише на нову полицю: вид летить на кожну дію, а base64 полиці між ними той самий.
    if (!locked && g.png && e.img._serial !== g.serial) {
      e.img._serial = g.serial;
      e.img.src = g.png;
      st.taps = [];
      st.eyeBusy = false;
    }
    const marks = st.taps.map((t, i) => '<i style="left:' + (t[0] / g.width * 100).toFixed(2) + '%;top:'
      + (t[1] / g.height * 100).toFixed(2) + '%">' + (i + 1) + '</i>').join('');
    if (e.marks._html !== marks) { e.marks._html = marks; e.marks.innerHTML = marks; }
    const off = st.eyeBusy || !st.taps.length;
    if (e.reset.disabled !== off) e.reset.disabled = off;
    e.pic.classList.toggle('busy', st.eyeBusy);
  }

  /// Торкання полиці. Лише справжні (isTrusted): скрипт, що тицяє в картинку dispatchEvent-ом, сюди не дійде —
  /// хоча він однаково не знає, куди тицяти. Координати — у пікселях картинки, як їх чекає сервер.
  function tapShelf(st, ev) {
    const g = st.guard;
    if (!ev.isTrusted || !g || !g.png || st.eyeBusy || !st.ctx) return;
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
        b.style.setProperty('--clk-drop', (st.stage.clientHeight * 0.92) + 'px');
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
    const label = gain < 1 ? '🔥 Обпалити — ще рано'
      : armed ? 'Точно? Глеки й верстати згорять — ще раз'
      : '🔥 Обпалити: +' + gain + ' ' + stampsWord(gain);
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

  /// Покупка й продаж рахуються від того, що вже долетіло до сервера, тож накопичені кліки шлемо першими.
  function order(st, action, payload) {
    if (!st.ctx || !st.mine) return;
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

  function setTab(st, tab) {
    st.tab = tab;
    storeSet('clk.tab', tab);
    for (const b of st.tabs.querySelectorAll('[data-tab]')) b.classList.toggle('active', b.dataset.tab === tab);
    for (const [k, p] of Object.entries(st.panes)) p.hidden = k !== tab;
    st.slowAt = 0;
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
      const pay = u.gain > 0 && !maxed ? 'окупиться за ' + span(u.price / u.gain) : '';
      const x2 = u.boost > 1 ? ' · ×' + u.boost : '';
      return '<button type="button" class="clk-up' + (k === best ? ' best' : '') + (u.kind === 'skill' ? ' skill' : '')
        + '" data-buy="' + esc(k) + '" disabled>'
        + '<b>' + esc(u.name) + '</b>'
        + '<span class="clk-lvl">' + (u.level ? 'рівень ' + u.level : 'ще не куплено') + (u.max > 0 ? ' з ' + u.max : '') + x2 + '</span>'
        + '<span class="muted small">' + esc(u.desc) + '</span>'
        + (pay ? '<span class="clk-pay">' + (k === best ? '★ ' : '') + pay + '</span>' : '')
        + '<span class="clk-price"></span>'
        + '</button>';
    }).join('');
    const more = teaser
      ? '<div class="clk-teaser muted small">Далі на драбині ще є верстати: наступний відкриється після першого рівня «'
        + esc(ups[prevIdle(ups, teaser)].name) + '»</div>'
      : '';
    if (swap(st.shop, cards + more)) {
      st.buys = [...st.shop.querySelectorAll('[data-buy]')];
      for (const b of st.buys) {
        b._price = b.querySelector('.clk-price');
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
        + marks.map((m) => '<button type="button" class="clk-mark" data-mark="' + esc(m.key) + '" data-price="' + m.price + '" disabled>'
          + '<b>' + esc(m.name) + '</b><span class="muted small">' + esc(m.desc) + '</span>'
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
      + '<span class="muted small">вільних для секретів: ' + num(st.stampsFree) + (v.firings ? ' · обпалів: ' + v.firings : '') + '</span></div>'
      + '<p class="muted small clk-note">Обпал спалює глеки, верстати й віхи. Натомість — клейма майстра за все, що наліпив '
      + 'за весь час: кожне дає +' + dec(st.stampBonus * 100) + ' % до всього назавжди. Розписи, секрети й таблиця лишаються. '
      + 'Кожні ' + STAMPS_PER_CAP + ' клейм — ще один черепок до денної стелі обміну'
      + (cap ? ' (зараз +' + cap + ')' : '') + '.</p>';
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
    const sig = st.wear + '|' + st.clayBody + '|' + shelf;
    if (st.jugBox._wear === sig) return;
    st.jugBox._wear = sig;
    const w = st.wear || '';
    const body = st.clayBody;
    st.jugBox.innerHTML = jug(w, 'wheel', body);
    st.fallJug.innerHTML = jugSvg(w, 'clk-fall-jug', 'fall', body);
    // На полиці — глечики: у розписі, що на колі, і прості (кольору глини); що більша гончарня, то повніша полиця.
    let s = '';
    for (let i = 0; i < shelf; i++) s += jugSvg(i % 2 ? '' : w, '', 'shelf-' + i, body);
    st.shelfJugs.innerHTML = s;
  }

  // ---------- хата: сцена, що росте від покупок ----------

  const TOOL_ICON = { paddle: '🥄', string: '🧵', sponge: '🧽', ribs: '📏', lantern: '🏮', apron: '🥼', bucket: '🪣', whistle: '🎶', scales: '⚖️', iron: '🔖' };
  const TIER_ICON = { fair: '🎪', artel: '🤝', chumaks: '🐂', pit: '⛏️', school: '📜', chaika: '⛵', museum: '🏛️', tsar: '👑' };

  /// Кругла бляшка з емодзі: знаряддя на гачку, емблема верстата на стіні.
  const badge = (x, y, icon, title, esc) => '<g class="clk-badge" transform="translate(' + x + ' ' + y + ')"><title>' + esc(title) + '</title>'
    + '<circle r="13"/><text dy=".36em" text-anchor="middle">' + icon + '</text></g>';

  /// Фігурка підмайстра біля кола.
  const figure = (x, y, coat) => '<circle cx="' + x + '" cy="' + y + '" r="8" fill="#e0b48a"/>'
    + '<path d="M' + (x - 11) + ' ' + (y + 8) + 'h22v30q0 4-4 4h-14q-4 0-4-4z" fill="' + coat + '"/>'
    + '<path d="M' + (x - 6) + ' ' + (y + 14) + 'v10M' + (x + 6) + ' ' + (y + 14) + 'v10" stroke="#f4efe3" stroke-width="1.2"/>';

  /// Усе, що видно в хаті понад полицю й коло: прапорці ярмарку, ікона, рушник, вікно, знаряддя на гачках,
  /// емблеми драбини, горно, підмайстри, півень, собака, скриня. Малюється у viewBox 360×396 поверх фону сцени.
  function houseSvg(st) {
    const esc = (st.ctx && st.ctx.esc) || ((x) => String(x));
    const ups = st.ups;
    const lvl = (k) => (ups[k] && ups[k].level) || 0;
    const has = (k) => st.decorList.some((d) => d.key === k && d.owned);
    let s = '';
    // Прапорці ярмарку в Сорочинцях — уздовж стелі.
    if (lvl('fair') > 0) {
      const colors = ['#d7372b', '#f2c230', '#2f5fa8', '#4c9a3f'];
      s += '<path d="M0 4q90 8 180 4t180-4" stroke="#8a6a4a" stroke-width="1.2" fill="none"/>';
      for (let x = 18, i = 0; x < 360; x += 30, i++) s += '<path d="M' + (x - 8) + ' 6l16 0-8 15z" fill="' + colors[i % 4] + '"/>';
    }
    if (has('icon')) {
      s += '<rect x="20" y="48" width="42" height="52" rx="3" fill="#5a3a1a" stroke="#d9a92f" stroke-width="1.5"/>'
        + '<circle cx="41" cy="66" r="9" fill="#f4c542" opacity=".9"/><circle cx="41" cy="66" r="5" fill="#e0b48a"/>'
        + '<rect x="33" y="76" width="16" height="18" rx="2" fill="#8b3a22"/>';
    }
    if (has('towel')) {
      for (const x of [8, 306]) {
        s += '<path d="M' + x + ' 30h46v54l-23 10-23-10z" fill="#f4efe3" stroke="#d9d2c2"/>'
          + '<path d="M' + (x + 4) + ' 58h38M' + (x + 4) + ' 64h38" stroke="#d7372b" stroke-width="2"/>'
          + '<path d="M' + (x + 4) + ' 70h38" stroke="#2a1a12" stroke-width="1.4"/>';
      }
    }
    if (has('window')) {
      s += '<rect x="262" y="46" width="76" height="74" rx="4" fill="#0c1a2b" stroke="#6b4423" stroke-width="4"/>'
        + '<path d="M300 46v74M262 83h76" stroke="#6b4423" stroke-width="3"/>'
        + '<g fill="#f4efe3" opacity=".8"><circle cx="278" cy="60" r="1.2"/><circle cx="322" cy="56" r="1"/><circle cx="312" cy="70" r="1.4"/></g>'
        + '<path d="M266 118q10-22 26-30" stroke="#4c9a3f" stroke-width="2" fill="none"/>'
        + '<g fill="#d7372b"><circle cx="286" cy="92" r="3"/><circle cx="292" cy="96" r="3"/><circle cx="288" cy="100" r="3"/><circle cx="294" cy="89" r="2.6"/></g>';
    }
    // Знаряддя на гачках лівої стіни, двома стовпчиками.
    let i = 0;
    for (const t of st.tools) {
      if (!t.owned) continue;
      s += badge(24 + (i % 2) * 28, 118 + Math.floor(i / 2) * 28, TOOL_ICON[t.key] || '🔧', t.name, esc);
      i++;
    }
    // Емблеми драбини на правій стіні.
    let j = 0;
    for (const k of Object.keys(TIER_ICON)) {
      if (lvl(k) <= 0) continue;
      s += badge(284 + (j % 3) * 28, 138 + Math.floor(j / 3) * 28, TIER_ICON[k], ups[k].name + ' · ' + lvl(k), esc);
      j++;
    }
    // Горно: що більше печей, то яскравіше горить.
    const kiln = lvl('kiln');
    if (kiln > 0) {
      const glow = Math.min(0.55, 0.15 + kiln / 80).toFixed(2);
      s += '<ellipse cx="321" cy="262" rx="34" ry="30" fill="rgba(255,138,61,' + glow + ')"/>'
        + '<path d="M292 302V214q0-24 29-24t29 24v88z" fill="#8f6d4b" stroke="#5c4530" stroke-width="1.5"/>'
        + '<rect x="307" y="248" width="28" height="34" rx="5" fill="#2a1508"/>'
        + '<path class="clk-flame" d="M321 280c-9-8-7-19 0-26 2 6 6 7 4 15 4-4 6-9 4-15 8 8 7 20-8 26z" fill="#ff8a3d"/>'
        + '<path class="clk-flame" d="M321 279c-4-4-4-10 0-14 1 4 3 4 2 8 3-2 3-5 2-8 4 4 4 10-4 14z" fill="#f4c542"/>';
    }
    // Підмайстри ліворуч від кола: один, з десятого рівня — двоє, з двадцять п'ятого — троє.
    const a = lvl('apprentice');
    if (a >= 1) s += figure(40, 232, '#7a4b2a');
    if (a >= 10) s += figure(22, 264, '#4a6b8a');
    if (a >= 25) s += figure(58, 268, '#8a4a6b');
    if (has('rooster')) {
      s += '<path d="M72 372h40" stroke="#6b4423" stroke-width="3"/>'
        + '<path d="M78 366q-8-12-2-20 2 10 8 14z" fill="#2f5fa8"/><ellipse cx="90" cy="362" rx="11" ry="7" fill="#3a2a1a"/>'
        + '<circle cx="101" cy="353" r="4.6" fill="#3a2a1a"/><path d="M99 348l2-5 2 5 2-4 1 5z" fill="#d7372b"/>'
        + '<path d="M105 354l5 1-5 2z" fill="#f2c230"/><path d="M86 369v5M94 369v5" stroke="#f2c230" stroke-width="1.5"/>';
    }
    if (has('dog')) {
      s += '<path d="M292 360q-10-8-4-18" stroke="#5a4636" stroke-width="3" fill="none"/>'
        + '<ellipse cx="314" cy="362" rx="22" ry="9" fill="#5a4636"/><circle cx="337" cy="354" r="8" fill="#5a4636"/>'
        + '<path d="M342 346l7-9v13z" fill="#3e2f24"/><circle cx="340" cy="353" r="1.4" fill="#111"/><circle cx="345" cy="357" r="1.6" fill="#111"/>';
    }
    if (has('chest')) {
      s += '<rect x="10" y="336" width="60" height="40" rx="5" fill="#8b3a22" stroke="#4a1e10"/><rect x="10" y="336" width="60" height="12" rx="5" fill="#a54a2c"/>'
        + '<circle cx="40" cy="360" r="4.5" fill="#f2c230"/><circle cx="26" cy="362" r="2.6" fill="#4c9a3f"/><circle cx="54" cy="362" r="2.6" fill="#4c9a3f"/>'
        + '<circle cx="40" cy="342" r="1.6" fill="#f4efe3"/>';
    }
    return s;
  }

  function paintHouse(st) {
    swap(st.house, houseSvg(st));
  }

  // ---------- хата: полиця з глиною, знаряддям і прикрасами ----------

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
        + '<span class="clk-ticon">' + (TOOL_ICON[t.key] || '🔧') + '</span><b>' + esc(t.name) + '</b><span class="muted small">' + esc(t.desc) + '</span>'
        + '<span class="clk-price' + (t.owned ? ' done' : '') + '">' + (t.owned ? '✓ на стіні' : short(t.price)) + '</span></button>').join('') + '</div>';
    const decor = '<div class="clk-sub">Прикраси хати<span class="muted small"> · +2 % до всього кожна, лишаються назавжди</span></div>'
      + '<div class="clk-tools">' + st.decorList.map((d) => '<button type="button" class="clk-tool decor' + (d.owned ? ' owned' : '')
        + '" data-house="adorn" data-key="' + esc(d.key) + '" data-price="' + d.price + '" data-owned="' + (d.owned ? 1 : 0) + '" disabled>'
        + '<b>' + esc(d.name) + '</b><span class="muted small">' + esc(d.desc) + '</span>'
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
    const head = '<div class="clk-sub">Дошка купців<span class="muted small"> · нові купці через <span class="clk-cd" data-at="' + st.refreshAt + '" data-done="ось-ось"></span></span></div>';
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
    const note = '<p class="muted small clk-note">Купець у дорозі повертає більше, ніж узяв: що довша дорога, то щедріше (5 хв — ×1,4, 30 хв — ×2,2). '
      + 'За розпис із колекції платить одразу ×1,6. Дошка оновлюється раз на 4 хвилини, кого не взяв — поїхав. '
      + 'Обпал спалює купців у дорозі разом із глеками.' + (st.tools.some((t) => t.key === 'scales' && t.owned) ? ' Ваги купця: +20 % до кожної плати.' : '') + '</p>';
    if (swap(st.ordersPane, head + board + taken + note)) {
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

  // ---------- модуль ----------

  HGames.register({
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
        // Хата, що росте від покупок: шар під полицею й колом (viewBox 360×396, тягнеться за сценою).
        + '<svg class="clk-house" viewBox="0 0 360 396" preserveAspectRatio="none" aria-hidden="true"></svg>'
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
        + '<g class="clk-jugbox"></g>'
        + '</svg></button>'
        + '<div class="clk-sparks"></div><div class="clk-pops"></div></div>'
        + '<button type="button" class="clk-gold" hidden aria-label="Розписний глек — лови!" title="Розписний глек — лови!">'
        + '<svg class="clk-gold-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>'
        + jugSvg('golden', 'clk-gold-jug', 'gold') + '</button>'
        + '<button type="button" class="clk-fall" hidden aria-label="Глек падає з полиці — лови!" title="Лови!"><span class="clk-fall-box"></span></button>'
        + '<div class="clk-fx"></div>'
        + '</div>'
        // Око майстра стає на місце сцени: відлік паузи або полиця з глечиками.
        + '<div class="clk-eye" hidden><div class="clk-eye-head"><b>👁 Око майстра</b><span class="clk-eye-tries small"></span></div>'
        + '<div class="clk-eye-text small"></div>'
        + '<div class="clk-eye-pic"><img alt="Полиця з глечиками, горщиками, мисками й черепками" draggable="false">'
        + '<div class="clk-eye-marks"></div></div>'
        + '<button type="button" class="ghost small clk-eye-reset" disabled>Скинути торкання</button></div>'
        + '<div class="clk-buffs" hidden></div>'
        + '<div class="clk-sell"><button type="button" class="primary clk-one" disabled></button>'
        + '<button type="button" class="ghost clk-all" data-pots="0" disabled></button></div>'
        + '<div class="clk-left muted small"></div>'
        + '</div>'
        // Праворуч (або нижче): вкладки з верстатами, розписами й обпалом.
        + '<div class="clk-side">'
        + '<div class="clk-tabs" role="tablist">'
        + '<button type="button" class="ghost" data-tab="shop">Майстерня</button>'
        + '<button type="button" class="ghost" data-tab="house">Хата</button>'
        + '<button type="button" class="ghost" data-tab="orders">Купці</button>'
        + '<button type="button" class="ghost" data-tab="styles">Розписи</button>'
        + '<button type="button" class="ghost" data-tab="fire">Обпал</button></div>'
        + '<div class="clk-pane" data-pane="shop">'
        + '<div class="clk-modes"><span class="muted small">купувати</span>'
        + '<button type="button" class="ghost" data-mode="1">×1</button>'
        + '<button type="button" class="ghost" data-mode="10">×10</button>'
        + '<button type="button" class="ghost" data-mode="max">макс</button></div>'
        + '<div class="clk-markbox"></div><div class="clk-shop"></div></div>'
        + '<div class="clk-pane" data-pane="house" hidden></div>'
        + '<div class="clk-pane" data-pane="orders" hidden></div>'
        + '<div class="clk-pane" data-pane="styles" hidden></div>'
        + '<div class="clk-pane" data-pane="fire" hidden>'
        + '<div class="clk-firebox"><div class="clk-bar"><i></i></div><div class="clk-next muted small"></div>'
        + '<button type="button" class="primary clk-fire" disabled></button><div class="clk-after small"></div></div>'
        + '<div class="clk-firestatic"></div></div>'
        + '</div>'
        + '</div></div>';
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
      st.panes = { shop: q('[data-pane="shop"]'), house: q('[data-pane="house"]'), orders: q('[data-pane="orders"]'), styles: q('[data-pane="styles"]'), fire: q('[data-pane="fire"]') };
      st.styles = st.panes.styles;
      st.housePane = st.panes.house;
      st.ordersPane = st.panes.orders;
      st.house = q('.clk-house');
      st.fire = q('.clk-firebox');
      st.fire._bar = q('.clk-bar i');
      st.fire._next = q('.clk-next');
      st.fire._btn = q('.clk-fire');
      st.fire._after = q('.clk-after');
      st.fire._static = q('.clk-firestatic');
      st.eye = { el: q('.clk-eye'), text: q('.clk-eye-text'), tries: q('.clk-eye-tries'), pic: q('.clk-eye-pic'),
        img: q('.clk-eye-pic img'), marks: q('.clk-eye-marks'), reset: q('.clk-eye-reset') };
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
      for (const b of st.tabs.querySelectorAll('[data-tab]')) b.onclick = () => setTab(st, b.dataset.tab);
      for (const b of st.modes.querySelectorAll('[data-mode]')) b.onclick = () => setMode(st, b.dataset.mode);
      if (!st.panes[st.tab]) st.tab = 'shop';
      setTab(st, st.tab);
      setMode(st, ['1', '10', 'max'].includes(st.mode) ? st.mode : '1');
      st.timer = setInterval(() => flush(st), BATCH_MS);
      if (!st.raf) loop(st);
    },

    update(root, ctx) {
      const st = state(root);
      if (!st.el) return;
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
      }
      const one = 'Продати ' + num(st.rateOf) + ' → 🏺1';
      if (st.one.textContent !== one) st.one.textContent = one;
      const left = st.canSell > 0
        ? 'сьогодні ще ' + num(st.canSell) + ' ' + shards(st.canSell) + ', по ' + num(st.rateOf) + ' глеків за черепок'
        : 'на сьогодні черепки скінчились, приходь завтра';
      if (st.left.textContent !== left) st.left.textContent = left;

      const owned = st.styleList.filter((s) => s.owned).length;
      const tabs = {
        shop: 'Майстерня', house: 'Хата', orders: 'Купці' + (st.taken.length ? ' · 🐴' + st.taken.length : ''),
        styles: 'Розписи ' + owned + '/' + (st.styleList.length || 8), fire: 'Обпал' + (st.stamps ? ' · 🔖' + st.stamps : ''),
      };
      for (const b of st.tabs.querySelectorAll('[data-tab]')) {
        const t = tabs[b.dataset.tab];
        if (b.textContent !== t) b.textContent = t;
      }
      wheelJug(st);
      paintSign(st);
      paintHouse(st);
      shop(st, ctx);
      housePane(st, ctx);
      ordersPane(st, ctx);
      styles(st, ctx);
      firePane(st, ctx);
      st.slowAt = 0;
      paint(st);
    },

    onKey(e, ctx) {
      if (e.code !== 'Space' || !ctx.mine || !ctx.clk) return false;
      const st = ctx.clk;
      // Фокус на іншій кнопці картки — пробіл належить їй: на верстаті чи прилавку ми б крутили коло замість
      // покупки й продажу. Сама кнопка кола — наша: її власний click ми кліком не рахуємо.
      const on = document.activeElement;
      if (on && on.tagName === 'BUTTON' && on !== st.wheel && st.el && st.el.contains(on)) return false;
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
      cancelAnimationFrame(st.raf);
      if (st.onKeyUp) document.removeEventListener('keyup', st.onKeyUp);
      const card = root.closest && root.closest('.gtable');
      if (card) card.classList.remove('clk-wide');
      st.raf = 0;
      st.el = null;
      root._clk = null;
    },
  });
})();
