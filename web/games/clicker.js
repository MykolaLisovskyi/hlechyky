/*
  Гончарне коло. Соло-клікер: тиснеш на коло — ліпиш глеки, купуєш верстати й віхи, ловиш розписні глеки,
  збираєш розписи, обпалюєш майстерню за клейма майстра, міняєш глеки на черепки.

  Правила рахує сервер (Impl/Clicker.cs). Клієнт понад малювання робить рівно три речі:
  1) батчить кліки — рахує їх локально й шле Act('spin', { n }) раз на 700 мс, а не двадцять разів за секунду;
  2) доліковує лічильник між подіями 'room' — за view.baseSecond і ярмарком, зі стелею офлайну, як на сервері.
     Простій беремо серверний (view.now − view.lastSync) і додаємо лише те, що натікало ВІД отримання виду, —
     так збитий годинник у гравця не малює неіснуючих глеків. Прийшов новий вид — беремо його число, а не своє;
  3) показує розписний глек у його вікні (view.golden) і, коли той утік, питає наступний розклад Act('look').

  Вид (Impl/Clicker.cs): { pots, total, perClick, clickBase, perSecond, baseSecond,
    upgrades: { key: { level, price, name, desc, max, kind, gain, growth, marks, open } }, marks: [...],
    canSellToday, soldToday, cap, rate, lastSync, now, offlineHours, golden: { at, until, x, y }, caught,
    fair: { until, mult }, inspire: { until, mult }, allMult, stamps, stampsFree, stampsReady, nextStampAt,
    stampBonus, stampCap, firings, secrets: [...], styles: [...], wear }.
  Дії: spin { n }, buy { key, n }, mark { key }, sell { pots }, catch, look, fire, secret { key }, paint { key }, wear { key }.
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
  /// (колесо, картка розпису, розписний глек): id для clipPath мусить бути сталим, інакше HTML полиці
  /// щоразу виходив би новим і swap() перемальовував би її на кожну пачку кліків.
  function jug(style, slot) {
    const s = STYLE[style] || STYLE[''];
    const id = 'clkjug-' + slot;
    return '<g class="clk-jug"><clipPath id="' + id + '"><path d="' + JUG + '"/></clipPath>'
      + '<path d="' + JUG + '" fill="' + s.body + '"/>'
      + '<g clip-path="url(#' + id + ')">' + s.decor + '</g>'
      + '<path d="' + JUG + '" fill="none" stroke="rgba(0,0,0,.28)" stroke-width=".8"/>'
      + '<path d="M39.6 42.5c-1.6 3.8-1.6 10.6.4 15" stroke="rgba(255,255,255,.22)" stroke-width="1.8" fill="none" stroke-linecap="round"/></g>';
  }
  /// Окремий глек (картка розпису, розписний глек): видноколо обрізане по самому глеку.
  const jugSvg = (style, cls, slot) => '<svg class="' + (cls || '') + '" viewBox="31 22 38 45" aria-hidden="true">' + jug(style, slot) + '</svg>';

  // ---------- стан модуля ----------

  function state(root) {
    if (!root._clk) {
      root._clk = {
        el: null, count: null, rate: null, rival: null, wheel: null, jugBox: null, pops: null, buffs: null, gold: null,
        one: null, all: null, left: null, tabs: null, panes: {}, shop: null, modes: null, marks: null, styles: null, fire: null,
        buys: [], markBtns: [], styleBtns: [], secretBtns: [],
        base: 0, total: 0, lastSync: 0, viewNow: 0, recvAt: Date.now(), offlineMs: 8 * 3600 * 1000,
        clickBase: 1, baseSecond: 0, fairUntil: 0, fairMult: 7, inspireUntil: 0, inspireMult: 25,
        rateOf: 100, canSell: 0, mine: false, wear: null,
        golden: null, goldenGone: 0, lookedFor: 0,
        stamps: 0, stampsFree: 0, stampBonus: 0.02, fireArmed: 0,
        ups: {}, markList: [], styleList: [], secretList: [],
        tab: storeGet('clk.tab', 'shop'), mode: storeGet('clk.mode', '1'),
        unsent: 0, inflight: 0, tokens: MAX_BATCH, tokensAt: Date.now(), shown: -1, slowAt: 0,
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
    let n = sure + (st.unsent + st.inflight) * clickNow(st);
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

    // Продаж — від підтвердженого числа, а не від намальованого: у st.unsent може лежати хвіст кліків,
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

    paintGolden(st);

    const now = Date.now();
    if (now - st.slowAt >= SLOW_MS) {
      st.slowAt = now;
      paintSlow(st, n);
    }
  }

  /// Те, що не мусить жити шістдесят разів на секунду: рядок швидкості, бонуси, суперник, прогрес клейм.
  function paintSlow(st, shown) {
    const sn = serverNow(st);
    const sec = secondNow(st);
    const rate = 'за клік +' + short(clickNow(st))
      + (sec > 0 ? ' · без тебе +' + short(sec) + ' за секунду' : ' · підмайстрів ще нема');
    if (st.rate.textContent !== rate) st.rate.textContent = rate;

    // Швидкість обертання — від пасиву: коло без підмайстрів стоїть, з піччю крутиться помітно.
    const spin = (sec > 0 ? Math.max(0.6, 9 / Math.log10(10 + sec)) : 0) + 's';
    if (st.wheel._spin !== spin) { st.wheel._spin = spin; st.wheel.style.setProperty('--clk-spin', spin); }

    let buffs = '';
    if (sn < st.fairUntil) buffs += '<span class="clk-buff fair">🎪 Ярмарок ×' + st.fairMult + ' · ' + Math.ceil((st.fairUntil - sn) / 1000) + ' с</span>';
    if (sn < st.inspireUntil) buffs += '<span class="clk-buff inspire">✨ Натхнення: клік ×' + st.inspireMult + ' · ' + Math.ceil((st.inspireUntil - sn) / 1000) + ' с</span>';
    if (st.buffs._html !== buffs) { st.buffs._html = buffs; st.buffs.innerHTML = buffs; st.buffs.hidden = !buffs; }

    const liveTotal = st.total + (shown - st.base);
    if (visible(st)) loadBoard(st);
    paintRival(st, liveTotal);
    if (st.tab === 'fire') paintFire(st, liveTotal);
  }

  /// Розписний глек: стоїть у своєму вікні; утік — один раз питаємо сервер про наступний.
  function paintGolden(st) {
    const g = st.golden;
    const b = st.gold;
    if (!g || !st.mine) { if (!b.hidden) b.hidden = true; return; }
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

  /// «+12», що злітає над колом. Живе рівно доти, доки триває анімація.
  function pop(st, amount) {
    if (st.pops.childElementCount > 12) return;   // палець швидший за око: більше однаково не роздивитись
    const el = document.createElement('span');
    el.className = 'clk-pop';
    el.textContent = '+' + short(amount);
    el.style.left = (32 + Math.random() * 36) + '%';
    el.addEventListener('animationend', () => el.remove());
    // У фоновій вкладці анімації не крутяться, а отже й animationend не прилетить — прибираємо і за часом,
    // інакше «+N» назбирувались би там сотнями до самого повернення.
    setTimeout(() => el.remove(), 2000);
    st.pops.appendChild(el);
  }

  // ---------- дії ----------

  function flush(st) {
    if (!st.unsent || !st.ctx) return;
    const n = Math.min(st.unsent, MAX_BATCH);
    st.unsent -= n;
    st.inflight += n;
    const back = () => { st.inflight = Math.max(0, st.inflight - n); };
    // Кліки, що вже полетіли, знімає з рахунку сам вид (див. update): вид і відповідь приходять різними
    // кадрами вебсокета, і якби ми чекали відповіді, між ними лічильник встигав би показати їх двічі.
    // Лишається тільки невдача: тоді виду не буде взагалі, і порахувати назад мусимо ми.
    st.ctx.act('spin', { n }).then((r) => { if (!r || !r.ok) back(); }, back);
  }

  function spin(st) {
    if (!st.ctx || !st.mine) return;
    // Те саме відро дозволів, що й на сервері: понад дванадцять кліків за секунду він однаково не візьме,
    // тож і малювати їх не варто — інакше лічильник обіцяв би те, чого потім не дорахується.
    const now = Date.now();
    st.tokens = Math.min(MAX_BATCH, st.tokens + ((now - st.tokensAt) / 1000) * MAX_BATCH);
    st.tokensAt = now;
    if (st.tokens < 1) return;
    st.tokens -= 1;
    st.unsent++;
    pop(st, clickNow(st));
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

  function catchGolden(st) {
    if (!st.golden || !st.mine) return;
    st.goldenGone = st.golden.at;      // ховаємо одразу: другий клік по тому самому глеку — лише червоний тост
    st.gold.hidden = true;
    order(st, 'catch');
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
      return '<button type="button" class="clk-up' + (k === best ? ' best' : '') + '" data-buy="' + esc(k) + '" disabled>'
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

  /// Глек на колі: перемальовуємо лише тоді, коли гончар поставив інший розпис.
  function wheelJug(st) {
    if (st.jugBox._wear === st.wear) return;
    st.jugBox._wear = st.wear;
    st.jugBox.innerHTML = jug(st.wear || '', 'wheel');
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
      root.innerHTML = '<div class="clk">'
        + '<div class="clk-stage">'
        + '<div class="clk-head"><b class="clk-count">0</b><span class="muted small">глеків</span></div>'
        + '<div class="clk-rate muted small"></div>'
        + '<div class="clk-rival small" hidden></div>'
        + '<div class="clk-wheelbox"><div class="clk-pops"></div>'
        + '<button type="button" class="clk-wheel" aria-label="Крутити коло">'
        // Крутиться сам круг із борознами й цяткою (без неї обертання ідеального кола не видно),
        // а глек стоїть рівно: гончар його тримає.
        + '<svg viewBox="0 0 100 100" aria-hidden="true">'
        + '<g class="clk-turn"><circle class="clk-disc" cx="50" cy="50" r="46"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="35"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="24"/>'
        + '<circle class="clk-speck" cx="50" cy="12" r="2.6"/></g>'
        + '<g class="clk-jugbox"></g>'
        + '</svg></button></div>'
        + '<div class="clk-buffs" hidden></div>'
        + '<button type="button" class="clk-gold" hidden aria-label="Розписний глек — лови!" title="Розписний глек — лови!">'
        + '<svg class="clk-gold-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>'
        + jugSvg('golden', 'clk-gold-jug', 'gold') + '</button>'
        + '</div>'
        + '<div class="clk-sell"><button type="button" class="primary clk-one" disabled></button>'
        + '<button type="button" class="ghost clk-all" data-pots="0" disabled></button></div>'
        + '<div class="clk-left muted small"></div>'
        + '<div class="clk-tabs" role="tablist">'
        + '<button type="button" class="ghost" data-tab="shop">Майстерня</button>'
        + '<button type="button" class="ghost" data-tab="styles">Розписи</button>'
        + '<button type="button" class="ghost" data-tab="fire">Обпал</button></div>'
        + '<div class="clk-pane" data-pane="shop">'
        + '<div class="clk-modes"><span class="muted small">купувати</span>'
        + '<button type="button" class="ghost" data-mode="1">×1</button>'
        + '<button type="button" class="ghost" data-mode="10">×10</button>'
        + '<button type="button" class="ghost" data-mode="max">макс</button></div>'
        + '<div class="clk-markbox"></div><div class="clk-shop"></div></div>'
        + '<div class="clk-pane" data-pane="styles" hidden></div>'
        + '<div class="clk-pane" data-pane="fire" hidden>'
        + '<div class="clk-firebox"><div class="clk-bar"><i></i></div><div class="clk-next muted small"></div>'
        + '<button type="button" class="primary clk-fire" disabled></button><div class="clk-after small"></div></div>'
        + '<div class="clk-firestatic"></div></div>'
        + '</div>';
      const q = (s) => root.querySelector(s);
      st.el = q('.clk');
      st.count = q('.clk-count');
      st.rate = q('.clk-rate');
      st.rival = q('.clk-rival');
      st.wheel = q('.clk-wheel');
      st.jugBox = q('.clk-jugbox');
      st.jugBox._wear = null;
      st.pops = q('.clk-pops');
      st.buffs = q('.clk-buffs');
      st.gold = q('.clk-gold');
      st.one = q('.clk-one');
      st.all = q('.clk-all');
      st.left = q('.clk-left');
      st.tabs = q('.clk-tabs');
      st.modes = q('.clk-modes');
      st.marks = q('.clk-markbox');
      st.shop = q('.clk-shop');
      st.panes = { shop: q('[data-pane="shop"]'), styles: q('[data-pane="styles"]'), fire: q('[data-pane="fire"]') };
      st.styles = st.panes.styles;
      st.fire = q('.clk-firebox');
      st.fire._bar = q('.clk-bar i');
      st.fire._next = q('.clk-next');
      st.fire._btn = q('.clk-fire');
      st.fire._after = q('.clk-after');
      st.fire._static = q('.clk-firestatic');
      st.ctx = ctx;
      ctx.clk = st;                     // щоб onKey дістався до стану: там є лише ctx
      st.wheel.addEventListener('click', () => spin(st));
      st.gold.addEventListener('click', () => catchGolden(st));
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
        if (v.golden) {
          const at = Date.parse(v.golden.at);
          const until = Date.parse(v.golden.until);
          if (Number.isFinite(at) && Number.isFinite(until)) st.golden = { at, until, x: v.golden.x || 0, y: v.golden.y || 0 };
        }
      }
      const one = 'Продати ' + num(st.rateOf) + ' → 🏺1';
      if (st.one.textContent !== one) st.one.textContent = one;
      const left = st.canSell > 0
        ? 'сьогодні ще ' + num(st.canSell) + ' ' + shards(st.canSell) + ', по ' + num(st.rateOf) + ' глеків за черепок'
        : 'на сьогодні черепки скінчились, приходь завтра';
      if (st.left.textContent !== left) st.left.textContent = left;

      const owned = st.styleList.filter((s) => s.owned).length;
      const tabs = { shop: 'Майстерня', styles: 'Розписи ' + owned + '/' + (st.styleList.length || 8), fire: 'Обпал' + (st.stamps ? ' · 🔖' + st.stamps : '') };
      for (const b of st.tabs.querySelectorAll('[data-tab]')) {
        const t = tabs[b.dataset.tab];
        if (b.textContent !== t) b.textContent = t;
      }
      wheelJug(st);
      shop(st, ctx);
      styles(st, ctx);
      firePane(st, ctx);
      st.slowAt = 0;
      paint(st);
    },

    onKey(e, ctx) {
      if (e.code !== 'Space' || !ctx.mine || !ctx.clk) return false;
      // Фокус на будь-якій кнопці картки — пробіл належить їй: на колі він і так порахується (інакше клік
      // пішов би двічі), а на верстаті чи прилавку ми б крутили коло замість покупки й продажу.
      const on = document.activeElement;
      if (on && on.tagName === 'BUTTON' && ctx.clk.el && ctx.clk.el.contains(on)) return false;
      spin(ctx.clk);
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.total == null) return '';
      return 'усього наліплено ' + short(v.total) + ' · розписних спіймано ' + num(v.caught || 0)
        + ' · обміняно сьогодні ' + num(v.soldToday || 0);
    },

    unmount(root) {
      const st = root._clk;
      if (!st) return;
      clearInterval(st.timer);
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      st.el = null;
      root._clk = null;
    },
  });
})();
