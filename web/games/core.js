/*
  Каркас ігор у браузері. Один глобал — window.HGames.

  app.js про ігри більше нічого не знає: він кличе init() на старті, attach(conn) у connect(),
  reconnected() після реконекту і show()/hide() при перемиканні вкладок. Усе інше — тут:
  каталог із сервера, завантаження модулів, лобі, спільна картка кімнати, гаманець, профіль,
  таблиці, щоденний глек.

  Правила, за якими це живе:
  - Картка кімнати створюється рівно один раз (mount) і далі тільки оновлюється (update),
    інакше модуль щоразу губив би свій канвас, таймери й половину стану.
  - Шапка, статус і кнопки перемальовуються окремо від .gbody — тіло належить модулю.
  - Подія 'frame' (до 25 на секунду) не чіпає DOM каркаса взагалі: тільки module.frame().
  Контракт — docs/games/PROTOCOL.md §2–§3, задум — docs/games/ARCHITECTURE.md §10.
*/
(() => {
  'use strict';

  // ---------- те, що дає app.js ----------
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let toast = (t) => console.log('[games]', t);
  let busy = async (btn, label, fn) => fn();
  let api = async () => { throw new Error('api ще не підключено'); };
  let me = { nick: '', role: 'member' };
  let root = null;

  // ---------- стан ----------
  let conn = null;
  let shown = false;
  let booted = false;
  let catalog = { games: [], stakes: [0] };
  const byId = {};              // id гри → запис каталогу
  const modules = {};           // id гри → модуль (HGames.register)
  const extraPanels = [];       // registerPanel
  const failed = new Set();     // модулі, які не завантажились
  let loading = null;           // проміс завантаження каталогу

  let rooms = [];               // останній 'rooms'
  const views = {};             // id кімнати → { room, seat, view }
  const cards = {};             // id кімнати → картка на екрані
  const watched = new Set();    // на що зараз підписані WatchRoom
  const pinned = new Set();     // щойно відкриті соло/приватні кімнати: їх нема в лобі, дивимось за roomId
  let wallet = null;            // баланс черепків, null — ще не питали

  const GROUPS = [
    { id: 'board', title: 'Настільні' },
    { id: 'live', title: 'Швидкі' },
    { id: 'party', title: 'Компанія' },
    { id: 'solo', title: 'Соло' },
  ];
  const NAV = [
    { id: 'profile', title: 'Профіль', icon: '👤' },
    { id: 'leaders', title: 'Таблиця', icon: '🏆' },
    { id: 'daily', title: 'Щоденний глек', icon: '🫙' },
  ];
  const PERIODS = [['day', 'за день'], ['week', 'за тиждень'], ['all', 'за весь час']];

  let panel = localStorage.getItem('gamesPanel') || 'g:board';
  let lbGame = localStorage.getItem('gamesLbGame') || 'shards';
  let lbPeriod = localStorage.getItem('gamesLbPeriod') || 'week';

  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const cssVar = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  const coarse = () => window.matchMedia('(pointer: coarse)').matches;

  /// Місця в RoomSummary приходять як [{ i, nick }]; терпимо і простий масив ніків.
  function nickAt(room, i) {
    const s = room && room.seats && room.seats[i];
    if (s == null) return null;
    return typeof s === 'string' ? (s || null) : (s.nick || null);
  }
  const seatCount = (room) => (room.seats ? room.seats.length : room.maxPlayers || 0);
  const takenSeats = (room) => { let n = 0; for (let i = 0; i < seatCount(room); i++) if (nickAt(room, i)) n++; return n; };
  const freeSeat = (room) => { for (let i = 0; i < seatCount(room); i++) if (!nickAt(room, i)) return i; return -1; };
  function seatOfMe(room) {
    for (let i = 0; i < seatCount(room); i++) if (sameNick(nickAt(room, i), me.nick)) return i;
    return null;
  }
  /// Сидиш за одним мультиплеєрним столом — за інший не сядеш (соло не рахується).
  const seatedElsewhere = (exceptId) => rooms.some((r) => r.id !== exceptId && (r.maxPlayers > 1) && seatOfMe(r) !== null);

  const gameOf = (id) => byId[id] || null;
  const titleOf = (id) => (byId[id] && byId[id].title) || id;
  const iconOf = (id) => (modules[id] && modules[id].icon) || '<span class="gemo">🎲</span>';
  const groupOf = (id) => (byId[id] && byId[id].group) || 'board';

  // =============================================================================================
  // Хелпери для модулів — HGames.ui (PROTOCOL §3)
  // =============================================================================================

  /// Кнопкова сітка як у хрестиків. Геометрія та сама — оновлюємо клітинки, а не перебудовуємо
  /// сітку: інакше кожен хід гасив би :hover і ламав фокус.
  function grid(host, o) {
    o = o || {};
    const cols = o.cols || 3, rows = o.rows || o.cols || 3, n = cols * rows;
    const cls = 'board' + (o.cls ? ' ' + o.cls : '');
    let el = host.querySelector(':scope > .board');
    if (!el || el.className !== cls || +el.dataset.n !== n) {
      if (el) el.remove();
      el = document.createElement('div');
      el.className = cls;
      el.dataset.n = String(n);
      let html = '';
      for (let i = 0; i < n; i++) html += '<button class="cell" data-i="' + i + '"></button>';
      el.innerHTML = html;
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.cell');
        const opt = el._o || {};
        if (b && !b.disabled && opt.onCell) opt.onCell(+b.dataset.i, b);
      });
      host.appendChild(el);
    }
    // Слухач вішається один раз, а колбек модуль дає новий на кожен update — тримаємо
    // свіжі опції на елементі, інакше кліки б назавжди пішли в onCell із першого виклику.
    el._o = o;
    el.style.setProperty('--cols', cols);
    if (o.cell) {
      const kids = el.children;
      for (let i = 0; i < n; i++) {
        const c = o.cell(i);
        const v = (c && typeof c === 'object') ? c : { html: c == null ? '' : String(c) };
        const b = kids[i];
        const want = 'cell' + (v.cls ? ' ' + v.cls : '');
        if (b.className !== want) b.className = want;
        if (b.innerHTML !== (v.html || '')) b.innerHTML = v.html || '';
        const dis = !!v.disabled;
        if (b.disabled !== dis) b.disabled = dis;
      }
    }
    return el;
  }

  /// Канвас із логічними координатами w×h: модуль малює в них, а DPR і розтяг по ширині — наша справа.
  function canvas(host, o) {
    o = o || {};
    const w = o.w || 320, h = o.h || 200;
    const cls = 'gcanvas' + (o.cls ? ' ' + o.cls : '');
    let el = host.querySelector(':scope > canvas.gcanvas');
    if (!el) {
      el = document.createElement('canvas');
      host.appendChild(el);
    }
    if (el.className !== cls) el.className = cls;
    el.style.aspectRatio = w + ' / ' + h;
    const c = { el, ctx: el.getContext('2d'), w, h, resize };
    function resize() {
      const dpr = Math.min(3, window.devicePixelRatio || 1);
      const pw = Math.round(w * dpr), ph = Math.round(h * dpr);
      if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
      c.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    resize();
    return c;
  }

  /// Хрестовина під палець. На миші її нема взагалі — там стрілки зручніші.
  function dpad(host, onDir, dirs) {
    let el = host.querySelector(':scope > .dpad');
    if (!coarse()) { if (el) el.remove(); return null; }
    const want = (dirs || [3, 2, 1, 0]).join(',');
    if (el && el.dataset.dirs === want) { el._onDir = onDir; return el; }
    if (el) el.remove();
    el = document.createElement('div');
    el.className = 'dpad';
    el.dataset.dirs = want;
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
    el.innerHTML = (dirs || [3, 2, 1, 0]).map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('');
    el.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (b && el._onDir) el._onDir(+b.dataset.dir);
    });
    el._onDir = onDir;                 // колбек — завжди з останнього виклику, не з першого
    host.appendChild(el);
    return el;
  }

  const KB_ROWS = ['йцукенгшщзхї', 'фівапролджєґ', 'ячсмитьбю'];

  /// Екранна українська клавіатура. state — { 'а': 'G'|'Y'|'B' }, як у Глек-слова.
  function keyboardUa(host, onKey, state) {
    let el = host.querySelector(':scope > .gkbd');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gkbd';
      el.innerHTML = KB_ROWS.map((r, ri) => {
        let row = r.split('').map((ch) => '<button type="button" class="gkey" data-k="' + ch + '">' + ch + '</button>').join('');
        if (ri === 2) row = '<button type="button" class="gkey wide" data-k="Enter">Enter</button>' + row + '<button type="button" class="gkey wide" data-k="Backspace">⌫</button>';
        return '<div class="gkrow">' + row + '</div>';
      }).join('');
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.gkey');
        if (b && el._onKey) el._onKey(b.dataset.k);
      });
      host.appendChild(el);
    }
    el._onKey = onKey;                 // так само, як у grid: живий колбек, а не той, що був на створенні
    const st = state || {};
    el.querySelectorAll('.gkey').forEach((b) => {
      const k = b.dataset.k;
      const s = k.length === 1 ? (st[k] || '') : '';
      const want = 'gkey' + (k.length > 1 ? ' wide' : '') + (s ? ' ' + s.toLowerCase() : '');
      if (b.className !== want) b.className = want;
    });
    return el;
  }

  const lerp = (a, b, t) => a + (b - a) * t;

  /// Інтерполятор для ігор на 25 Гц: тримає два останні кадри й каже, де ми між ними
  /// на «зараз мінус один інтервал» — так рух не смикається на кожному повідомленні.
  function Interp() {
    let prev = null, last = null, prevAt = 0, lastAt = 0;
    return {
      push(f) {
        const t = performance.now();
        prev = last; prevAt = lastAt;
        last = f; lastAt = t;
        if (!prev) { prev = f; prevAt = t; }
      },
      reset() { prev = last = null; prevAt = lastAt = 0; },
      /// { a: старіший кадр, b: новіший, t: 0..1 }
      at() {
        if (!last) return null;
        const span = Math.max(1, lastAt - prevAt);
        const target = performance.now() - span;      // навмисно відстаємо на один інтервал
        const t = Math.max(0, Math.min(1, (target - prevAt) / span));
        return { a: prev, b: last, t };
      },
    };
  }

  /// Дуга-таймер фази: сама крутиться на rAF і сама вмирає, коли картку прибрали.
  /// Кликати можна з кожного update(): другий виклик лише переставляє час, а не заводить
  /// ще один rAF-цикл на тому самому елементі.
  function timerArc(host, untilIso, totalMs) {
    let el = host.querySelector(':scope > .garc');
    if (el && el._arc) { el._arc.set(untilIso, totalMs); return el._arc; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'garc';
      el.innerHTML = '<svg viewBox="0 0 40 40"><circle class="bg" cx="20" cy="20" r="17"></circle>'
        + '<circle class="fg" cx="20" cy="20" r="17" transform="rotate(-90 20 20)"></circle></svg><b>0</b>';
      host.appendChild(el);
    }
    const fg = el.querySelector('.fg'), num = el.querySelector('b');
    const LEN = 2 * Math.PI * 17;
    fg.style.strokeDasharray = LEN;
    const st = { until: Date.parse(untilIso) || Date.now(), total: totalMs || 1000, raf: 0 };
    function step() {
      if (!el.isConnected) { st.raf = 0; return; }
      const left = Math.max(0, st.until - Date.now());
      const k = Math.max(0, Math.min(1, left / st.total));
      fg.style.strokeDashoffset = LEN * (1 - k);
      const s = String(Math.ceil(left / 1000));
      if (num.textContent !== s) num.textContent = s;
      st.raf = requestAnimationFrame(step);
    }
    st.raf = requestAnimationFrame(step);
    const handle = {
      el,
      set(u, total) { st.until = Date.parse(u) || Date.now(); st.total = total || st.total; if (!st.raf) st.raf = requestAnimationFrame(step); },
      stop() { cancelAnimationFrame(st.raf); st.raf = 0; if (el._arc === handle) el._arc = null; },
    };
    el._arc = handle;
    return handle;
  }

  /// Віяло карт (дурень) або кісток (доміно). Рука міняється щохода, тож актуальні картки
  /// й колбек живуть на елементі: інакше клік віддавав би модулю карту з першого виклику.
  function hand(host, items, o) {
    o = o || {};
    items = items || [];
    let el = host.querySelector(':scope > .ghand');
    if (!el) {
      el = document.createElement('div');
      el.className = 'ghand';
      el.addEventListener('click', (e) => {
        const c = e.target.closest('.gcard');
        if (!c || c.classList.contains('off')) return;
        const list = el._items || [], opt = el._o || {};
        const cb = opt.onItem || opt.onCard;      // onCard — старе ім'я з PROTOCOL §3
        const i = +c.dataset.i;
        if (opt.selectable) {
          const on = c.classList.toggle('sel');
          if (!opt.multi) el.querySelectorAll('.gcard.sel').forEach((x) => { if (x !== c) x.classList.remove('sel'); });
          if (cb) cb(list[i], i, on);
        } else if (cb) cb(list[i], i, true);
      });
      host.appendChild(el);
    }
    el._items = items;
    el._o = o;
    const render = o.render || ((it) => esc(typeof it === 'object' ? (it.label || it.text || '') : it));
    const html = items.map((it, i) => {
      const off = it && typeof it === 'object' && it.disabled ? ' off' : '';
      const cls = it && typeof it === 'object' && it.cls ? ' ' + it.cls : '';
      return '<div class="gcard' + off + cls + '" data-i="' + i + '">' + render(it, i) + '</div>';
    }).join('');
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
    return {
      el,
      selected: () => [...el.querySelectorAll('.gcard.sel')].map((c) => (el._items || [])[+c.dataset.i]),
      clear: () => el.querySelectorAll('.gcard.sel').forEach((c) => c.classList.remove('sel')),
    };
  }

  const ui = { grid, canvas, dpad, keyboardUa, lerp, Interp, timerArc, hand, css: cssVar, coarse };

  // =============================================================================================
  // Хаб
  // =============================================================================================

  /// Виклик хаба, що повертає RoomReply: помилку показуємо тостом, успіх — лише якщо є що сказати.
  async function call(method, ...args) {
    if (!conn || conn.state !== 'Connected') { toast('Зв\'язку з сервером нема', 'err'); return { ok: false, message: '' }; }
    try {
      const r = await conn.invoke(method, ...args);
      if (!r) return { ok: true, message: '' };
      if (!r.ok) toast(r.message || 'Не вийшло', 'err');
      else if (r.message) toast(r.message, 'ok');
      return r;
    } catch (e) {
      toast('Не вийшло: ' + e.message, 'err');
      return { ok: false, message: e.message };
    }
  }
  const send = (method, ...args) => { if (conn && conn.state === 'Connected') conn.invoke(method, ...args).catch(() => {}); };

  const PIN_TTL = 5000;

  /// Соло і щоденні кімнати приватні: у списку лобі їх нема, тож єдиний спосіб їх побачити —
  /// підписатись за roomId із відповіді. Тримаємо його, поки не з'явиться картка.
  async function openRoom(method, ...args) {
    const r = await call(method, ...args);
    if (r.ok && r.roomId) {
      const id = r.roomId;
      pinned.add(id);
      syncWatch();
      // Кімнати може й не бути (сервер її вже прибрав, WatchRoom відмовив): щоб не тримати
      // підписку на мертвий id вічно, знімаємо шпильку, якщо 'room' так і не прийшла.
      setTimeout(() => {
        if (pinned.has(id) && !cards[id] && !views[id]) { pinned.delete(id); syncWatch(); }
      }, PIN_TTL);
    }
    return r;
  }

  /// Кадри просимо лише для кімнат, які зараз на екрані: інакше сервер сипле десятки повідомлень на секунду дарма.
  function syncWatch() {
    const want = new Set();
    if (shown) {
      for (const id in cards) if (cards[id].el.isConnected) want.add(id);
      for (const id of pinned) want.add(id);
    }
    for (const id of [...watched]) if (!want.has(id)) { watched.delete(id); send('UnwatchRoom', id); }
    for (const id of want) if (!watched.has(id)) { watched.add(id); send('WatchRoom', id); }
  }

  // =============================================================================================
  // Завантажувач модулів
  // =============================================================================================

  function loadScript(src) {
    return new Promise((resolve) => {
      const s = document.createElement('script');
      s.src = src;
      s.onload = () => resolve(true);
      s.onerror = () => resolve(false);
      document.head.appendChild(s);
    });
  }

  /// Файл модуля гри: типово <id>.js, але родина ігор може жити в одному файлі
  /// (зникаючі хрестики — у ttt.js), і тоді сервер каже це полем module каталогу.
  const moduleOf = (g) => g.module || g.id;

  /// Вантажимо всі модулі одразу: у каталозі їх буде два десятки, а послідовні await —
  /// це два десятки round-trip-ів поспіль. Один файл вантажимо рівно раз, скільки б ігор у ньому
  /// не реєструвалось. Вердикт «не завантажився» ставимо лише коли все відстрілялось.
  async function loadModules() {
    const want = catalog.games.filter((g) => !modules[g.id]);
    const files = [...new Set(want.map(moduleOf))];
    for (const g of want) {
      const f = moduleOf(g);
      if (g.hasCss && !document.querySelector('link[data-game="' + f + '"]')) {
        const l = document.createElement('link');
        l.rel = 'stylesheet';
        l.href = '/games/' + f + '.css';
        l.dataset.game = f;
        document.head.appendChild(l);
      }
    }
    const res = await Promise.all(files.map(async (f) => [f, await loadScript('/games/' + f + '.js')]));
    const loaded = Object.fromEntries(res);
    for (const g of want) {
      if (modules[g.id]) continue;
      failed.add(g.id);
      const f = moduleOf(g);
      console.warn('[games] модуль ' + g.id + ' не завантажився'
        + (loaded[f] ? ' (є ' + f + '.js, але register(' + g.id + ') не викликано)' : ' (нема ' + f + '.js)'));
    }
    refreshAll();
  }

  function ensureCatalog() {
    if (loading) return loading;
    loading = api('GET', '/api/games/catalog').then((c) => {
      catalog = { games: (c && c.games) || [], stakes: (c && c.stakes) || [0] };
      for (const g of catalog.games) byId[g.id] = g;
      renderShell();
      renderPanel();
      return loadModules();
    }).catch((e) => {
      loading = null;
      console.warn('[games] каталог не прочитався', e);
      // Помилку пишемо лише в тіло: знести шапку разом із кнопками означало б «повертайся через F5».
      renderShell();
      const v = root && root.querySelector('.gview');
      if (!v) return;
      v.innerHTML = '<div class="gempty">Каталог ігор не прочитався: ' + esc(e.message)
        + ' <button class="ghost" data-retry>Спробувати ще</button></div>';
      const b = v.querySelector('[data-retry]');
      if (b) b.onclick = (ev) => busy(ev.currentTarget, 'читаю…', () => ensureCatalog());
    });
    return loading;
  }

  async function loadWallet() {
    try {
      const w = await api('GET', '/api/games/wallet');
      wallet = typeof w === 'number' ? w : (w && (w.balance != null ? w.balance : w.shards));
      paintWallet();
    } catch { /* економіки ще нема — рядок гаманця просто мовчить */ }
  }
  /// Гаманець живе в шапці сайту (#hdrWallet), а не в лобі: черепки витрачають і поза іграми.
  /// Поки балансу нема — малюємо «—», а не ховаємо рядок: інакше шапка стрибала б на кожному вході.
  function paintWallet() {
    const el = document.querySelector('#hdrWallet b');
    if (el) el.textContent = wallet == null ? '—' : String(wallet);
  }

  // =============================================================================================
  // Каркас сторінки
  // =============================================================================================

  function renderShell() {
    if (!root) return;
    if (!root.querySelector('.gbar')) {
      root.innerHTML = '<div class="gbar"><div class="gtabs tabs"></div><div class="gnav"></div></div><div class="gview"></div>';
    }
    const tabs = root.querySelector('.gtabs');
    const counts = {};
    // Групу беремо лише в гри, яку вже знаємо з каталогу: до його приходу groupOf() віддає 'board'
    // геть на все, і кімната змійки рахувалась би настільною (а вкладка «Швидкі» зникала).
    for (const r of rooms) { const g = gameOf(r.game); if (g) counts[g.group] = (counts[g.group] || 0) + 1; }
    // private — ознака КІМНАТИ (соло і щоденні не потрапляють у лобі, ARCHITECTURE §4.1/§4.4),
    // а не гри: плитку такої гри показуємо, інакше вкладка «Соло» не з'явилась би ніколи.
    const alive = (id) => catalog.games.some((x) => x.group === id) || !!counts[id];
    // збережена в localStorage вкладка може вказувати на групу, якої в цій збірці ще нема.
    // Але тільки коли каталог уже прийшов: подія 'rooms' випереджає його, і без цієї умови
    // запам'ятана вкладка губилась би на кожному F5 (усі групи здавались би порожніми).
    if (catalog.games.length && panel.startsWith('g:') && !alive(panel.slice(2))) {
      const first = GROUPS.find((g) => alive(g.id));
      if (first) panel = 'g:' + first.id;
    }
    tabs.innerHTML = GROUPS.map((g) => {
      if (!alive(g.id)) return '';
      const on = panel === 'g:' + g.id ? ' class="on"' : '';
      return '<button data-panel="g:' + g.id + '"' + on + '>' + esc(g.title)
        + (counts[g.id] ? ' <span class="count">' + counts[g.id] + '</span>' : '') + '</button>';
    }).join('');
    const nav = root.querySelector('.gnav');
    nav.innerHTML = NAV.concat(extraPanels.map((p) => ({ id: 'x:' + p.id, title: p.title, icon: p.icon || '📋' })))
      .map((p) => '<button data-panel="' + esc(p.id) + '"' + (panel === p.id ? ' class="on"' : '') + '>'
        + (p.icon ? '<span class="gemo">' + p.icon + '</span>' : '') + esc(p.title) + '</button>').join('');
    root.querySelectorAll('[data-panel]').forEach((b) => b.onclick = () => setPanel(b.dataset.panel));
    paintWallet();
  }

  function setPanel(id) {
    panel = id;
    localStorage.setItem('gamesPanel', id);
    renderShell();
    renderPanel();
  }

  /// Які кімнати належать поточній панелі. Соло й приватні (їх нема в 'rooms') показуємо
  /// там, де стоїть людина — інакше «Щоденний глек» відкривав би кімнату в нікуди.
  function roomsForPanel() {
    const loose = Object.keys(views).filter((id) => views[id].loose);
    if (panel.startsWith('g:')) {
      const g = panel.slice(2);
      return loose.concat(rooms.filter((r) => groupOf(r.game) === g).map((r) => r.id));
    }
    return loose;
  }

  /// Панель складається з двох частин: .gchrome (плитки, профіль, таблиця — можна сміливо
  /// перемальовувати) і .gtables (картки кімнат — їх лише переносять, ніколи не перестворюють).
  function renderPanel() {
    const view = root && root.querySelector('.gview');
    if (!view) return;
    const want = roomsForPanel();
    for (const id in cards) if (!want.includes(id)) dropCard(id);

    let chrome = view.querySelector(':scope > .gchrome');
    let box = view.querySelector(':scope > .gtables');
    if (!chrome) { chrome = document.createElement('div'); chrome.className = 'gchrome'; }
    if (!box) { box = document.createElement('div'); box.className = 'gtables'; }
    const lobby = panel.startsWith('g:');
    view.innerHTML = '';
    // у лобі спершу плитки, потім столи; у решті панелей відкрита кімната має бути одразу видно
    view.append(...(lobby ? [chrome, box] : [box, chrome]));

    // Лобі малюємо щоразу (столи й лічильники живі), а панелі з HTTP — лише коли справді
    // перемкнулись: інакше кожна зміна в лобі смикала б /api/games/profile.
    if (lobby || chromeFor !== panel) {
      chromeFor = panel;
      const token = ++renderToken;
      chrome.innerHTML = '';
      if (lobby) renderLobby(chrome, panel.slice(2));
      else if (panel === 'profile') renderProfile(chrome, token);
      else if (panel === 'leaders') renderLeaders(chrome, token);
      else if (panel === 'daily') renderDaily(chrome, token);
      else if (panel.startsWith('x:')) renderExtra(chrome, panel.slice(2));
      else renderLobby(chrome, 'board');
    }

    for (const id of want) box.appendChild(ensureCard(id).el);
    syncWatch();
  }
  let renderToken = 0;
  let chromeFor = null;
  const stale = (t) => t !== renderToken;

  function renderLobby(view, group) {
    const list = catalog.games.filter((g) => g.group === group);
    const tiles = list.map((g) => {
      const solo = group === 'solo' || g.maxPlayers === 1;
      const btn = solo
        ? '<button class="primary" data-solo="' + esc(g.id) + '">Грати</button>'
        : '<button class="primary" data-new="' + esc(g.id) + '">+ Стіл</button>';
      return '<div class="gtile"><div class="gt-head">' + iconOf(g.id) + '<b>' + esc(g.title) + '</b></div>'
        + '<div class="gt-hint muted small">' + esc(g.hint || '') + '</div>'
        + '<div class="gt-btns">' + btn + '</div></div>';
    }).join('');
    const mine = rooms.filter((r) => groupOf(r.game) === group);
    view.innerHTML = (tiles ? '<div class="gtiles">' + tiles + '</div>' : '<div class="gempty">Тут поки жодної гри не завезли.</div>')
      + (mine.length ? '' : '<div class="gempty">Столів поки нема. Постав перший і клич когось у балачках.</div>');
    view.querySelectorAll('[data-new]').forEach((b) => b.onclick = () => openCreate(gameOf(b.dataset.new)));
    view.querySelectorAll('[data-solo]').forEach((b) => b.onclick = (e) => busy(e.currentTarget, 'відкриваю…', () => openRoom('OpenSolo', b.dataset.solo, null)));
  }

  function renderExtra(view, id) {
    const p = extraPanels.find((x) => x.id === id);
    view.innerHTML = '';
    if (!p) { view.innerHTML = '<div class="gempty">Панель зникла.</div>'; return; }
    const host = document.createElement('div');
    host.className = 'gxpanel';
    view.appendChild(host);
    const c = panelCtx();
    try { p.mount(host, c); if (p.update) p.update(host, c); }
    catch (e) { console.warn('[games] панель ' + id, e); host.innerHTML = '<div class="gempty">Панель зламалась.</div>'; }
  }

  const panelCtx = () => ({ me, esc, toast, busy, api, call, ui, css: cssVar, catalog });

  // =============================================================================================
  // Попап створення столу
  // =============================================================================================

  /// Ставки є лише там, де є що ділити: рівно двоє і партія рейтингова (ARCHITECTURE §4.4).
  /// Те саме правило на сервері (Rooms.ReadStake), тому змійка й дуель теж зі ставками.
  const stakeable = (g) => !!g && g.maxPlayers === 2 && !!g.rated;

  /// Пари [значення, підпис] опції — з каталогу вони приходять масивами, але терпимо й {value, label}.
  const optPairs = (o) => (o.values || []).map((v) => Array.isArray(v) ? v : [v.value, v.label || v.value]);

  /// Опція в попапі: звичайна — випадайка, multi — чипи, де можна ввімкнути кілька.
  function optHtml(o) {
    if (o.multi) {
      const on = String(o.default || '').split(',');
      return '<div class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
        + '<div class="gpicks" data-key="' + esc(o.key) + '" data-any="' + esc(o.default || '') + '">'
        + optPairs(o).map(([val, lab]) => '<button type="button" class="gpick' + (on.includes(val) ? ' on' : '')
          + '" data-val="' + esc(val) + '">' + esc(lab) + '</button>').join('')
        + '</div></div>';
    }
    return '<label class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
      + '<select data-key="' + esc(o.key) + '">'
      + optPairs(o).map(([val, lab]) => '<option value="' + esc(val) + '"' + (val === o.default ? ' selected' : '') + '>' + esc(lab) + '</option>').join('')
      + '</select></label>';
  }

  /// Клік по чипу multi-опції. Типове значення — «усе»: воно гасить решту, а будь-який інший чип гасить
  /// його; вимкнули останній — «усе» повертається само (сервер робить із порожнім вибором те саме).
  function togglePick(box, chip) {
    const chips = [...box.querySelectorAll('.gpick')];
    const any = chips.find((x) => x.dataset.val === box.dataset.any);
    if (chip === any) { chips.forEach((x) => x.classList.toggle('on', x === any)); return; }
    chip.classList.toggle('on');
    if (any) any.classList.toggle('on', !chips.some((x) => x !== any && x.classList.contains('on')));
  }

  function openCreate(g) {
    if (!g) return;
    const opts = g.options || [];
    const stakes = stakeable(g) ? (catalog.stakes || []) : [];
    const wrap = document.createElement('div');
    wrap.className = 'modal gmodal';
    wrap.innerHTML = '<div class="card">'
      + '<h3>' + iconOf(g.id) + esc(g.title) + '</h3>'
      + (g.hint ? '<div class="muted small">' + esc(g.hint) + '</div>' : '')
      + opts.map(optHtml).join('')
      + (stakes.length > 1 ? '<div class="gopt"><span class="muted small">Ставка з кожного</span><div class="gstakes">'
        + stakes.map((s, i) => '<button type="button" class="gstake' + (i === 0 ? ' on' : '') + '" data-stake="' + s + '">🏺' + s + '</button>').join('')
        + '</div></div>' : '')
      + '<div class="grow"><button class="primary" data-go>Поставити стіл</button><button class="ghost" data-close>Скасувати</button></div>'
      + '</div>';
    document.body.appendChild(wrap);
    const close = () => wrap.remove();
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-close]').onclick = close;
    wrap.querySelectorAll('.gstake').forEach((b) => b.onclick = () => {
      wrap.querySelectorAll('.gstake').forEach((x) => x.classList.toggle('on', x === b));
    });
    wrap.querySelectorAll('.gpicks').forEach((box) => box.querySelectorAll('.gpick').forEach((b) => b.onclick = () => togglePick(box, b)));
    wrap.querySelector('[data-go]').onclick = (e) => busy(e.currentTarget, 'ставлю…', async () => {
      const payload = {};
      wrap.querySelectorAll('select[data-key]').forEach((s) => payload[s.dataset.key] = s.value);
      wrap.querySelectorAll('.gpicks').forEach((box) => payload[box.dataset.key] =
        [...box.querySelectorAll('.gpick.on')].map((x) => x.dataset.val).join(','));
      const st = wrap.querySelector('.gstake.on');
      if (st) payload.stake = +st.dataset.stake;
      const r = await openRoom('CreateRoom', g.id, payload);
      if (r.ok) { close(); setPanel('g:' + (g.group || 'board')); }
    });
  }

  // =============================================================================================
  // Картка кімнати
  // =============================================================================================

  function ensureCard(id) {
    if (cards[id]) return cards[id];
    const el = document.createElement('div');
    el.className = 'gtable';
    el.dataset.room = id;
    el.innerHTML = '<div class="gseats"></div><div class="gbody"></div><div class="gstatus"></div><div class="gbtns"></div>';
    const card = {
      id, el,
      head: el.querySelector('.gseats'),
      body: el.querySelector('.gbody'),
      statusEl: el.querySelector('.gstatus'),
      btns: el.querySelector('.gbtns'),
      mod: null, mounted: false, ctx: null,
      sig: '',
    };
    cards[id] = card;
    pinned.delete(id);           // картка вже є — далі підписку тримає вона
    // Вид прийде з першою подією 'room' після WatchRoom; поки що вистачить того, що є в лобі.
    if (!views[id]) {
      const r = rooms.find((x) => x.id === id);
      if (r) views[id] = { room: r, seat: seatOfMe(r), view: undefined };
    }
    if (views[id]) refreshCard(id);
    else card.body.innerHTML = '<div class="gwait"><span class="spin"></span> завантажую…</div>';
    return card;
  }

  function dropCard(id) {
    const c = cards[id];
    if (!c) return;
    if (c.mounted && c.mod && c.mod.unmount) { try { c.mod.unmount(c.body, c.ctx); } catch (e) { console.warn('[games] unmount', e); } }
    c.el.remove();
    delete cards[id];
  }

  function seatNameOf(rv, i) {
    const fromServer = rv.room.seatNames && rv.room.seatNames[i];
    if (fromServer) return fromServer;
    const m = cards[rv.room.id] && cards[rv.room.id].mod;
    const sn = m && m.seatNames;
    if (typeof sn === 'function') return sn(i, rv.room);
    if (Array.isArray(sn) && sn[i]) return sn[i];
    return i === 0 ? 'перший' : i === 1 ? 'другий' : 'гравець ' + (i + 1);
  }
  function seatClassOf(rv, i) {
    const m = cards[rv.room.id] && cards[rv.room.id].mod;
    const sc = m && m.seatClass;
    if (Array.isArray(sc) && sc[i]) return sc[i];
    return ['x', 'o', 'c', 'd'][i % 4];
  }
  const turnOf = (rv) => (rv.view && typeof rv.view.turn === 'number' ? rv.view.turn : null);

  function makeCtx(card, rv) {
    const room = rv.room;
    const ctx = card.ctx || {};
    ctx.room = room;
    ctx.seat = rv.seat;
    ctx.view = rv.view;
    if (ctx.frame === undefined) ctx.frame = null;   // останній 'frame'; кадри не скидають вид і навпаки
    ctx.me = me;
    ctx.playing = room.status === 'playing';
    ctx.mine = rv.seat != null;
    ctx.myTurn = ctx.mine && ctx.playing && turnOf(rv) === rv.seat;
    ctx.esc = esc;
    ctx.toast = toast;
    ctx.ui = ui;
    ctx.css = cssVar;
    ctx.seatName = (i) => seatNameOf(rv, i);
    ctx.nickOf = (i) => nickAt(room, i);
    ctx.act = (action, payload) => call('Act', room.id, action, payload === undefined ? null : payload);
    ctx.input = (action, payload) => send('Input', room.id, action, payload === undefined ? null : payload);
    card.ctx = ctx;
    return ctx;
  }

  function headHtml(rv) {
    const room = rv.room;
    const g = gameOf(room.game);
    const solo = room.maxPlayers === 1;
    const chips = [];
    if (!solo) {
      for (let i = 0; i < seatCount(room); i++) {
        const nick = nickAt(room, i);
        const turn = room.status === 'playing' && turnOf(rv) === i;
        chips.push('<span class="gseat ' + seatClassOf(rv, i) + (nick ? '' : ' free') + (turn ? ' turn' : '')
          + '"><i>' + esc(seatNameOf(rv, i)) + '</i>' + esc(nick || 'вільно') + '</span>');
      }
    }
    // Варіант, обраний при створенні (зникаючі хрестики, розмір поля) — підписуємо, якщо він не типовий.
    const modes = [];
    for (const o of (g && g.options) || []) {
      const v = room.options && room.options[o.key];
      if (v == null || v === o.default) continue;
      // multi-опція: «ukraine,science» → «Україна · Наука, природа й тіло» (кома вже буває в самих підписах)
      const label = (o.multi ? v.split(',') : [v])
        .map((x) => { const pair = optPairs(o).find((p) => p[0] === x); return pair ? pair[1] : x; }).join(' · ');
      modes.push('<span class="gmode">' + esc(label) + '</span>');
    }
    return '<span class="gtitle">' + iconOf(room.game) + esc(titleOf(room.game)) + '</span>'
      + modes.join('')
      + (room.stake ? '<span class="gmode stake">🏺' + room.stake + '</span>' : '')
      + chips.join('')
      + (room.watchers ? '<span class="gwatchers" title="Скільки дивиться">👁 ' + room.watchers + '</span>' : '');
  }

  function defaultStatus(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    if (r.status === 'finished') {
      const res = r.result;
      if (!res) return 'Партію зіграно';
      // соло: «перемога над собою» звучить дивно, тому беремо те, що написала гра
      if (solo) return res.text || (res.draw ? 'Не вийшло' : 'Готово');
      if (res.draw || !(res.winners || []).length) return 'Нічия';
      return 'Перемога: ' + res.winners.map((i) => nickAt(r, i) || seatNameOf(rv, i)).join(', ');
    }
    if (r.status === 'lobby') return solo ? '' : 'Чекаємо на гравців';
    const t = turnOf(rv);
    if (t != null) return t === rv.seat ? 'Твій хід' : 'Ходить ' + (nickAt(r, t) || seatNameOf(rv, t));
    return rv.seat == null ? 'Дивишся збоку' : '';
  }

  function btnsHtml(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    const out = [];
    // Дограний стіл із вільним місцем сервер віддає новому гравцеві (Rooms.Join, гілка reopen),
    // тож статус тут не питаємо — інакше стіл висів би в лобі до прибиральника, і сісти нікому.
    const canSit = !solo && rv.seat == null && freeSeat(r) >= 0 && !seatedElsewhere(r.id);
    if (canSit) out.push('<button class="primary" data-do="JoinRoom">Сісти</button>');
    // «Ще раз» пропонуємо лише коли є з ким: інакше кнопка є, а сервер відповідає «Замало гравців»
    if (!solo && rv.seat != null && r.status === 'finished' && takenSeats(r) >= r.minPlayers)
      out.push('<button class="primary" data-do="Rematch">Ще раз</button>');
    // щоденна головоломка одна на день — «Ще раз» там не пропонуємо
    if (solo && r.status === 'finished' && !(gameOf(r.game) || {}).daily) out.push('<button class="primary" data-do="Rematch">Ще раз</button>');
    if (rv.seat != null && r.status === 'lobby' && sameNick(r.host, me.nick) && (gameOf(r.game) || {}).start === 'byHost'
      && takenSeats(r) >= r.minPlayers)
      out.push('<button class="primary" data-do="StartRoom">Почати</button>');
    if (rv.seat != null) out.push('<button class="ghost" data-do="LeaveRoom">' + (solo ? 'Закрити' : 'Встати') + '</button>');
    // сісти нема куди (або сидиш за іншим столом) — хоч скажемо, чому кнопок нема
    else if (!solo && !canSit) out.push('<span class="muted small">Дивлюсь збоку</span>');
    return out.join('');
  }

  /// Шапка/статус/кнопки — окремо від .gbody: тіло чіпає лише модуль.
  function refreshCard(id) {
    const card = cards[id], rv = views[id];
    if (!card || !rv) return;
    const ctx = makeCtx(card, rv);

    const mod = modules[rv.room.game] || null;
    if (mod && card.mod !== mod) card.mod = mod;

    // seatedElsewhere — стан ЧУЖОГО столу, але від нього залежить кнопка «Сісти» тут: без нього
    // людина, яка щойно встала з іншого столу, лишалась би без кнопки, поки в цій кімнаті щось не зміниться
    const sig = JSON.stringify([rv.room.status, rv.room.seats, rv.room.seatNames, rv.room.watchers, rv.room.stake,
      rv.room.options, rv.room.result, rv.seat, turnOf(rv), rv.room.host, me.nick, !!card.mod,
      rv.seat == null && seatedElsewhere(rv.room.id)]);
    if (sig !== card.sig) {
      card.sig = sig;
      card.head.innerHTML = headHtml(rv);
      card.btns.innerHTML = btnsHtml(rv);
      card.btns.querySelectorAll('[data-do]').forEach((b) => b.onclick = (e) =>
        busy(e.currentTarget, '…', async () => {
          const r = await call(b.dataset.do, id);
          // приватну соло-кімнату сервер із лобі не прибере — прибираємо картку самі
          if (r.ok && b.dataset.do === 'LeaveRoom' && views[id] && views[id].loose) {
            dropCard(id);
            delete views[id];
            pinned.delete(id);
            renderPanel();
          }
        }));
      card.el.classList.toggle('mine', rv.seat != null);
    }

    if (!card.mod) {
      card.body.innerHTML = failed.has(rv.room.game)
        ? '<div class="gwait err">модуль гри не завантажився</div>'
        : '<div class="gwait"><span class="spin"></span> завантажую…</div>';
    } else if (rv.view !== undefined) {
      if (!card.mounted) {
        card.body.innerHTML = '';
        card.mounted = true;
        try { if (card.mod.mount) card.mod.mount(card.body, ctx); }
        catch (e) { console.warn('[games] mount ' + rv.room.game, e); }
      }
      try { if (card.mod.update) card.mod.update(card.body, ctx); }
      catch (e) { console.warn('[games] update ' + rv.room.game, e); }
    }

    paintStatus(card, rv);
  }

  /// Рядок статусу — тільки textContent, тому його не шкода перерахувати і на кожен кадр:
  /// у реалтайм-іграх фаза й відлік живуть у кадрах, а не у видах.
  function paintStatus(card, rv) {
    let text = '';
    if (card.mod && card.mod.status) { try { text = card.mod.status(card.ctx) || ''; } catch { text = ''; } }
    if (!text) text = defaultStatus(rv);
    if (card.statusEl.textContent !== text) card.statusEl.textContent = text;
    const cls = 'gstatus' + (rv.room.status === 'finished' ? ' done' : '') + (card.ctx && card.ctx.myTurn ? ' my' : '');
    if (card.statusEl.className !== cls) card.statusEl.className = cls;
  }

  const refreshAll = () => { for (const id in cards) refreshCard(id); };

  // =============================================================================================
  // Панелі: профіль, таблиця, щоденний глек
  // =============================================================================================

  const asList = (r) => (Array.isArray(r) ? r : (r && (r.rows || r.top || r.items || r.list)) || []);
  const secs = (ms) => (ms == null ? '' : (ms / 1000).toFixed(ms < 10000 ? 1 : 0) + ' с');

  async function renderProfile(view, token) {
    view.innerHTML = '<div class="gwait"><span class="spin"></span> дивлюсь у профіль…</div>';
    let p;
    try { p = await api('GET', '/api/games/profile?nick=' + encodeURIComponent(me.nick)); }
    catch (e) { if (!stale(token)) view.innerHTML = '<div class="gempty">Профіль не прочитався: ' + esc(e.message) + '</div>'; return; }
    if (stale(token)) return;
    p = p || {};                     // api() віддає null, якщо тіла нема — панель від цього не має вмирати
    const bal = (p.wallet && p.wallet.balance != null) ? p.wallet.balance : (p.balance != null ? p.balance : wallet);
    const earned = (p.wallet && p.wallet.earned != null) ? p.wallet.earned : p.earned;
    const ratings = p.ratings || [];
    const achs = p.achievements || [];
    const recent = p.recent || p.games || [];
    view.innerHTML = '<div class="gprofile">'
      + '<div class="gp-head"><b>' + esc(p.nick || me.nick) + '</b>'
      + '<span class="chip">🏺 ' + (bal == null ? '—' : bal) + '</span>'
      + (earned != null ? '<span class="chip">зароблено ' + earned + '</span>' : '') + '</div>'
      + '<h4>Рейтинги</h4>'
      + (ratings.length
        ? '<div class="glb">' + ratings.map((r) => '<div class="glbrow"><span>' + esc(titleOf(r.game)) + '</span>'
          + '<b>' + (r.elo != null ? r.elo : '—') + '</b>'
          + '<span class="muted small">' + (r.wins || 0) + '/' + (r.losses || 0) + '/' + (r.draws || 0)
          + ' · ' + (r.games || 0) + ' парт.</span></div>').join('') + '</div>'
        : '<div class="gempty">Ще нічого не зіграно.</div>')
      + '<h4>Ачівки</h4>'
      + (achs.length
        ? '<div class="gachs">' + achs.map((a) => {
          // сервер віддає лише здобуті, з датою в at (Leaderboards.Profile)
          const on = a.unlocked != null ? a.unlocked : !!(a.unlockedAt || a.at);
          return '<div class="gach' + (on ? '' : ' locked') + '" title="' + esc(a.text || '') + '">'
            + '<span class="gicon">' + esc(a.icon || '🏅') + '</span><b>' + esc(a.title || a.key) + '</b>'
            + '<span class="muted small">' + esc(a.text || '') + '</span>'
            + (a.reward ? '<span class="chip">🏺 ' + a.reward + '</span>' : '') + '</div>';
        }).join('') + '</div>'
        : '<div class="gempty">Ачівок ще нема.</div>')
      + '<h4>Останні партії</h4>'
      + (recent.length
        ? '<div class="glb">' + recent.slice(0, 15).map((x) => '<div class="glbrow"><span>' + esc(titleOf(x.game)) + '</span>'
          + '<b class="o-' + esc(x.outcome || '') + '">' + esc({ win: 'перемога', loss: 'поразка', draw: 'нічия', solo: 'соло' }[x.outcome] || x.outcome || '') + '</b>'
          + '<span class="muted small">' + esc(x.opponents || '') + (x.score != null ? ' · ' + x.score : '') + '</span></div>').join('') + '</div>'
        : '<div class="gempty">Порожньо.</div>')
      + '</div>';
  }

  // ключі — як їх називає Leaderboards.cs: rated → elo/wins/losses/draws/games/streak,
  // solo → best/tries, daily → attempts/ms, shards → balance/earned
  const LB_COLS = [['elo', 'Ело'], ['wins', 'В'], ['losses', 'П'], ['draws', 'Н'], ['games', 'партій'],
    ['streak', 'серія'], ['score', 'результат'], ['best', 'рекорд'], ['attempts', 'спроб'],
    ['tries', 'спроб'], ['ms', 'час'], ['balance', '🏺'], ['earned', 'зароблено'], ['count', 'разів']];

  async function renderLeaders(view, token) {
    // соло й щоденні теж мають таблиці — фільтрувати їх за private не можна (див. renderShell)
    const games = [{ id: 'shards', title: 'Черепки' }].concat(catalog.games.map((g) => ({ id: g.id, title: g.title })));
    if (!games.some((g) => g.id === lbGame)) lbGame = 'shards';
    view.innerHTML = '<div class="glbbar">'
      + '<select class="glbgame">' + games.map((g) => '<option value="' + esc(g.id) + '"' + (g.id === lbGame ? ' selected' : '') + '>' + esc(g.title) + '</option>').join('') + '</select>'
      + '<select class="glbperiod">' + PERIODS.map((p) => '<option value="' + p[0] + '"' + (p[0] === lbPeriod ? ' selected' : '') + '>' + p[1] + '</option>').join('') + '</select>'
      + '</div><div class="glbbox"><div class="gwait"><span class="spin"></span> рахую…</div></div>';
    view.querySelector('.glbgame').onchange = (e) => { lbGame = e.target.value; localStorage.setItem('gamesLbGame', lbGame); renderLeaders(view, token); };
    view.querySelector('.glbperiod').onchange = (e) => { lbPeriod = e.target.value; localStorage.setItem('gamesLbPeriod', lbPeriod); renderLeaders(view, token); };
    const box = view.querySelector('.glbbox');
    let r;
    try { r = await api('GET', '/api/games/leaderboard?game=' + encodeURIComponent(lbGame) + '&period=' + encodeURIComponent(lbPeriod)); }
    catch (e) { if (!stale(token)) box.innerHTML = '<div class="gempty">Таблиця не прочиталась: ' + esc(e.message) + '</div>'; return; }
    if (stale(token) || !box.isConnected) return;
    const rows = asList(r);
    if (!rows.length) { box.innerHTML = '<div class="gempty">За цей час ще ніхто не відзначився.</div>'; return; }
    const cols = LB_COLS.filter(([k]) => rows.some((x) => x[k] != null));
    box.innerHTML = '<div class="glb wide"><div class="glbrow head"><span>#</span><span>хто</span>'
      + cols.map(([, l]) => '<span>' + esc(l) + '</span>').join('') + '</div>'
      + rows.map((x, i) => '<div class="glbrow' + (sameNick(x.nick, me.nick) ? ' me' : '') + '"><span class="n">' + (i + 1) + '</span>'
        + '<span>' + esc(x.nick || '') + '</span>'
        + cols.map(([k]) => '<span>' + esc(k === 'ms' ? secs(x[k]) : (x[k] == null ? '—' : x[k])) + '</span>').join('')
        + '</div>').join('') + '</div>';
  }

  async function renderDaily(view, token) {
    view.innerHTML = '<div class="gwait"><span class="spin"></span> дивлюсь, що там сьогодні…</div>';
    let d;
    try { d = await api('GET', '/api/games/daily'); }
    catch (e) { if (!stale(token)) view.innerHTML = '<div class="gempty">Щоденне не прочиталось: ' + esc(e.message) + '</div>'; return; }
    if (stale(token)) return;
    d = d || {};
    const list = d.puzzles || [];
    view.innerHTML = '<div class="gdhead"><b>Щоденний глек</b>'
      + (d.no ? '<span class="chip">день №' + d.no + '</span>' : '')
      + (d.day ? '<span class="muted small">' + esc(d.day) + '</span>' : '') + '</div>'
      + (list.length ? '<div class="gdaily">' + list.map((p) => {
        const solved = p.me && p.me.solved;
        return '<div class="gdcard' + (solved ? ' done' : '') + '">'
          + '<div class="gt-head">' + iconOf(p.game) + '<b>' + esc(p.title || titleOf(p.game)) + '</b></div>'
          + '<div class="muted small">' + (solved
            ? 'розв\'язано за ' + (p.me.attempts || '?') + ' спроб' + (p.me.ms ? ' · ' + secs(p.me.ms) : '')
            : 'ще не розв\'язано') + '</div>'
          + '<div class="gdmeta">' + (p.streak ? '<span class="chip">🔥 ' + p.streak + '</span>' : '')
          + (p.solvedCount != null ? '<span class="chip">' + p.solvedCount + ' вже розв\'язали</span>' : '') + '</div>'
          + (p.top && p.top.length ? '<div class="glb small">' + p.top.slice(0, 10).map((t, i) =>
            '<div class="glbrow' + (sameNick(t.nick, me.nick) ? ' me' : '') + '"><span class="n">' + (i + 1) + '</span><span>' + esc(t.nick) + '</span>'
            + '<span class="muted small">' + (t.attempts != null ? t.attempts + ' спр.' : '') + (t.ms ? ' · ' + secs(t.ms) : '') + '</span></div>').join('') + '</div>' : '')
          + '<div class="gt-btns"><button class="primary" data-solo="' + esc(p.game) + '">Грати</button></div></div>';
      }).join('') + '</div>' : '<div class="gempty">Сьогодні головоломок нема.</div>');
    view.querySelectorAll('[data-solo]').forEach((b) => b.onclick = (e) =>
      busy(e.currentTarget, 'відкриваю…', () => openRoom('OpenSolo', b.dataset.solo, null)));
  }

  // =============================================================================================
  // Клавіатура
  // =============================================================================================

  /// Активна кімната: та, де я сиджу і йде партія; інакше перша видима.
  function activeCard() {
    // Порядок беремо з DOM, а не з ключів об'єкта: «перша видима» — це перша на екрані.
    const list = (root ? [...root.querySelectorAll('.gtable')] : [])
      .map((el) => cards[el.dataset.room]).filter((c) => c && c.ctx);
    return list.find((c) => c.ctx.mine && c.ctx.playing) || list[0] || null;
  }
  document.addEventListener('keydown', (e) => {
    if (!shown || e.metaKey || e.ctrlKey || e.altKey) return;
    // target може бути й самим document (подія, яку хтось згенерував сам) — у нього нема matches()
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    const c = activeCard();
    if (!c || !c.mod || !c.mod.onKey) return;
    let handled = false;
    try { handled = c.mod.onKey(e, c.ctx); } catch (err) { console.warn('[games] onKey', err); }
    if (handled) e.preventDefault();
  });

  // =============================================================================================
  // Тости гаманця й ачівок
  // =============================================================================================

  /// Ачівка заслуговує довшого тоста, ніж «трек закинуто»: 6 секунд, щоб устигли прочитати.
  function longToast(html, ms) {
    const box = document.getElementById('toasts');
    if (!box) { toast(String(html).replace(/<[^>]*>/g, ''), 'ok'); return; }
    const el = document.createElement('div');
    el.className = 'toast ok gbig';
    el.innerHTML = html;
    box.appendChild(el);
    setTimeout(() => el.remove(), ms || 6000);
  }

  // =============================================================================================
  // Публічний API
  // =============================================================================================

  const HGames = {
    ui,

    register(mod) {
      if (!mod || !mod.id) { console.warn('[games] register без id'); return; }
      modules[mod.id] = mod;
      failed.delete(mod.id);
      for (const id in cards) if (views[id] && views[id].room.game === mod.id) refreshCard(id);
      if (shown && root && root.querySelector('.gtiles')) renderPanel();
    },

    registerPanel(p) {
      if (!p || !p.id || !p.mount) { console.warn('[games] registerPanel без id/mount'); return; }
      const i = extraPanels.findIndex((x) => x.id === p.id);
      if (i >= 0) extraPanels[i] = p; else extraPanels.push(p);
      renderShell();
      if (panel === 'x:' + p.id) renderPanel();
    },

    has: (id) => !!modules[id],

    init(o) {
      o = o || {};
      if (o.esc) esc = o.esc;
      if (o.toast) toast = o.toast;
      if (o.busy) busy = o.busy;
      if (o.api) api = o.api;
      if (o.me) me = o.me;
      root = o.root || (o.$ ? o.$('games') : document.getElementById('games'));
      booted = true;
      renderShell();
      // Каталог і модуль кожної гри тягнемо в show(): слухачеві, який у «Ігри» не заходить,
      // ці два десятки запитів ні до чого.
    },

    attach(c) {
      conn = c;
      watched.clear();
      c.on('rooms', (list) => {
        rooms = list || [];
        for (const r of rooms) {
          const rv = views[r.id];
          if (rv) { rv.room = r; rv.seat = seatOfMe(r); rv.loose = false; }
        }
        // кімнати з лобі, яких уже нема, забираємо разом із видом; приватні соло тут не рахуються
        for (const id in views) if (!views[id].loose && !rooms.some((r) => r.id === id)) { dropCard(id); delete views[id]; }
        renderShell();
        renderPanel();
        refreshAll();
      });
      c.on('room', (rv) => {
        if (!rv || !rv.room) return;
        const id = rv.room.id;
        const old = views[id];
        views[id] = {
          room: rv.room,
          seat: rv.seat != null ? rv.seat : seatOfMe(rv.room),
          view: rv.view,
          // приватна соло-кімната в 'rooms' не приходить — тягнемо її за собою по панелях
          loose: old ? old.loose : !rooms.some((r) => r.id === id),
        };
        if (!cards[id]) renderPanel();
        else refreshCard(id);
      });
      c.on('frame', (f) => {
        if (!f || !f.id) return;
        const card = cards[f.id];
        if (!card || !card.mounted) return;
        // Останній кадр кладемо в ctx: фаза й відлік реалтайм-ігор живуть саме тут,
        // і без цього status() бачив би лише застарілий вид із рідкої події 'room'.
        if (card.ctx) card.ctx.frame = f.f;
        if (card.mod && card.mod.frame) {
          try { card.mod.frame(card.body, card.ctx, f.f); } catch (e) { console.warn('[games] frame', e); }
        }
        if (views[f.id]) paintStatus(card, views[f.id]);
      });
      c.on('wallet', (w) => {
        if (!w) return;
        wallet = w.balance;
        paintWallet();
        // сервер уже присилає готовий рядок «+5 черепків: перемога — Хрестики-нолики»;
        // своє число ліпимо лише тоді, коли тексту нема, інакше виходило «+5 🏺 +5 черепків: …»
        if (w.delta) toast('🏺 ' + (w.text || (w.delta > 0 ? '+' : '') + w.delta), w.delta > 0 ? 'ok' : '');
      });
      c.on('achievement', (a) => {
        if (!a) return;
        longToast('<span class="gemo">' + esc(a.icon || '🏅') + '</span> <b>' + esc(a.title || a.key) + '</b>'
          + (a.reward ? ' — +' + a.reward + ' 🏺' : '') + (a.text ? '<br><span class="muted small">' + esc(a.text) + '</span>' : ''), 6000);
      });
      c.on('toast', (t) => { if (t && t.text) toast(t.text, t.kind || ''); });
      loadWallet();          // черепки видно в шапці з будь-якого розділу, тож питаємо їх одразу
    },

    /// Після реконекту підписки на сервері вже нема — просимо заново для видимих кімнат.
    reconnected() {
      watched.clear();
      syncWatch();
      loadWallet();
    },

    show() {
      shown = true;
      if (!booted) return;
      ensureCatalog();
      chromeFor = null;          // відкрив вкладку — профіль і таблиця перечитуються
      renderShell();
      renderPanel();
      loadWallet();
    },

    hide() {
      shown = false;
      syncWatch();
    },

    /// Для модулів і панелей, яким треба смикнути хаб самим (конкурс реклами тощо).
    call,
    send,
    get catalog() { return catalog; },
  };

  window.HGames = HGames;
})();
