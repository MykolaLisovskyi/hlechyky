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
  let loading = null;           // проміс завантаження каталогу з модулями (ensureCatalog)
  let names = null;             // проміс самого лише каталогу — назви для балачок (ensureNames)

  let rooms = [];               // останній 'rooms'
  const views = {};             // id кімнати → { room, seat, view }
  const cards = {};             // id кімнати → картка на екрані
  const watched = new Set();    // на що зараз підписані WatchRoom
  const pinned = new Set();     // щойно відкриті соло/приватні кімнати: їх нема в лобі, дивимось за roomId
  let wallet = null;            // баланс черепків, null — ще не питали

  // Групи більше не вкладки, а чипи-фільтри каталогу: столи видно з будь-якого фільтра.
  const GROUPS = [
    { id: 'all', title: 'Усі', icon: '' },
    { id: 'board', title: 'Настільні', icon: '♟' },
    { id: 'live', title: 'Швидкі', icon: '⚡' },
    { id: 'party', title: 'Компанія', icon: '🎉' },
    { id: 'solo', title: 'Соло', icon: '🏺' },
  ];
  const NAV = [
    { id: 'profile', title: 'Профіль', icon: '👤' },
    { id: 'leaders', title: 'Таблиця', icon: '🏆' },
    { id: 'daily', title: 'Щоденний глек', icon: '🫙' },
  ];
  const PERIODS = [['day', 'за день'], ['week', 'за тиждень'], ['all', 'за весь час']];

  // Що зараз на екрані. Адресу дає app.js через HGames.show(tail): '' — лобі,
  // 'room/<id>' — сторінка столу, решта — підрозділ (профіль, таблиця, щоденне, панель гри).
  let view = { kind: 'lobby', id: '' };
  let full = false;                                               // ⛶ «на весь екран»
  let go = (hash) => { location.hash = hash; };                   // app.js підміняє своїм у init()
  let filter = localStorage.getItem('gamesFilter') || 'all';
  let find = '';
  let lbGame = localStorage.getItem('gamesLbGame') || 'shards';
  let lbPeriod = localStorage.getItem('gamesLbPeriod') || 'week';
  try { localStorage.removeItem('gamesPanel'); } catch { /* вкладка переїхала в адресу */ }

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
      if (method !== 'CreateRoom') go('#games/room/' + encodeURIComponent(id));
      // Кімнати може й не бути (сервер її вже прибрав, WatchRoom відмовив): щоб не тримати
      // підписку на мертвий id вічно, знімаємо шпильку, якщо 'room' так і не прийшла.
      setTimeout(() => {
        if (pinned.has(id) && !cards[id] && !views[id]) { pinned.delete(id); syncWatch(); }
      }, PIN_TTL);
    }
    return r;
  }

  /// Кадри просимо лише для відкритого столу, щойно відкритих приватних кімнат і тих, де сидимо:
  /// за останніми треба стежити й з лобі («твій хід» на резюме), решта — десятки повідомлень на секунду дарма.
  function syncWatch() {
    const want = new Set();
    if (shown) {
      if (view.kind === 'room' && view.id) want.add(view.id);
      for (const id of pinned) want.add(id);
      for (const id in views) if (views[id].seat != null || views[id].loose) want.add(id);
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

  const loadedFiles = new Map();   // файл модуля → проміс завантаження: двічі той самий не тягнемо

  const loadFile = (f) => {
    if (!loadedFiles.has(f)) loadedFiles.set(f, loadScript('/games/' + f + '.js'));
    return loadedFiles.get(f);
  };

  function addCss(g, f) {
    if (!g.hasCss || document.querySelector('link[data-game="' + f + '"]')) return;
    const l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = '/games/' + f + '.css';
    l.dataset.game = f;
    document.head.appendChild(l);
  }

  /// Іконку гри знає лише її модуль, а балачки згадують стіл ще до того, як людина зайшла в «Ігри».
  /// Тягнемо рівно один файл (разом із його css, інакше пізній loadModules його проґавить) і кличемо
  /// ready(), коли модуль зареєструвався. Поки він летить, на кнопці стоїть 🎲 — і це не помилка.
  function ensureIcon(gameId, ready) {
    if (modules[gameId]) return;
    // Спершу каталог: без нього ми не знаємо навіть, у якому файлі ця гра живе.
    ensureNames().then(() => {
      const g = byId[gameId];
      if (!g || modules[gameId]) return;
      addCss(g, moduleOf(g));
      return loadFile(moduleOf(g));
    }).then(() => { if (modules[gameId] && ready) ready(); })
      .catch(() => { /* каталог не прочитався — лишається 🎲, і це не привід шуміти */ });
  }

  /// Вантажимо всі модулі одразу: у каталозі їх буде два десятки, а послідовні await —
  /// це два десятки round-trip-ів поспіль. Один файл вантажимо рівно раз, скільки б ігор у ньому
  /// не реєструвалось. Вердикт «не завантажився» ставимо лише коли все відстрілялось.
  async function loadModules() {
    const want = catalog.games.filter((g) => !modules[g.id]);
    const files = [...new Set(want.map(moduleOf))];
    for (const g of want) addCss(g, moduleOf(g));
    const res = await Promise.all(files.map(async (f) => [f, await loadFile(f)]));
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

  /// Самі назви ігор, без двох десятків модулів: стільки треба балачкам, щоб написати «Мафія», а не
  /// «mafia». Запит той самий і кешується разом із повним ensureCatalog().
  function ensureNames() {
    if (names) return names;
    names = api('GET', '/api/games/catalog').then((c) => {
      catalog = { games: (c && c.games) || [], stakes: (c && c.stakes) || [0] };
      for (const g of catalog.games) byId[g.id] = g;
      return catalog;
    }).catch((e) => {
      names = null;
      console.warn('[games] каталог не прочитався', e);
      throw e;
    });
    return names;
  }

  function ensureCatalog() {
    if (loading) return loading;
    loading = ensureNames().then(() => {
      renderShell();
      renderView();
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
      // .gtables — склад змонтованих карток. Картка кімнати створюється один раз і далі лише
      // переїжджає складу ↔ сторінка столу: перестворити її означало б відібрати в модуля
      // канвас, таймери й половину стану (ARCHITECTURE §10).
      root.innerHTML = '<div class="gbar"><div class="gnav"></div></div>'
        + '<div class="groom" hidden><div class="grhead"></div><div class="grbox"></div></div>'
        + '<div class="gview"></div><div class="gtables" hidden></div>';
    }
    const items = [{ id: '', title: 'Лобі', icon: '🎲' }]
      .concat(NAV, extraPanels.map((p) => ({ id: 'x:' + p.id, title: p.title, icon: p.icon || '📋' })));
    const cur = view.kind === 'panel' ? view.id : '';
    root.querySelector('.gnav').innerHTML = items.map((p) => '<button data-go="' + esc(p.id) + '"'
      + (p.id === cur ? ' class="on"' : '') + '>'
      + (p.icon ? '<span class="gemo">' + p.icon + '</span>' : '') + esc(p.title) + '</button>').join('');
    root.querySelectorAll('.gnav [data-go]').forEach((b) => b.onclick = () =>
      go('#games' + (b.dataset.go ? '/' + b.dataset.go : '')));
    paintWallet();
    paintRoomCount();
  }

  /// Скільки живих столів — видно ще з шапки сайту, не заходячи в «Ігри».
  function paintRoomCount() {
    const el = document.getElementById('navGames');
    if (!el) return;
    el.textContent = rooms.length ? String(rooms.length) : '';
    el.hidden = !rooms.length;
  }

  /// Адреса всередині розділу: '' — лобі, 'room/<id>' — стіл, решта — підрозділ.
  function route(tail) {
    const t = String(tail || '');
    const next = t.startsWith('room/') ? { kind: 'room', id: decodeURIComponent(t.slice(5)) }
      : (t && (NAV.some((n) => n.id === t) || t.startsWith('x:'))) ? { kind: 'panel', id: t }
        : { kind: 'lobby', id: '' };
    const same = next.kind === view.kind && next.id === view.id;
    view = next;
    if (view.kind !== 'room') setFull(false);
    if (!same) chromeFor = null;          // повернулись у підрозділ — перечитуємо профіль/таблицю
    renderShell();
    renderView();
  }

  function setFull(on) {
    full = !!on && view.kind === 'room';
    document.body.classList.toggle('gfull', full);
  }

  /// Картки лише переносяться між складом і сторінкою столу й ховаються через hidden.
  function placeCards(openId) {
    const box = root.querySelector('.grbox');
    const store = root.querySelector('.gtables');
    if (openId) ensureCard(openId, box);
    for (const id in cards) {
      const c = cards[id];
      const here = id === openId;
      const host = here ? box : store;
      if (c.el.parentElement !== host) host.appendChild(c.el);
      c.el.hidden = !here;
    }
  }

  function renderView() {
    const v = root && root.querySelector('.gview');
    if (!v) return;
    const room = view.kind === 'room' ? view.id : null;
    placeCards(room);
    root.querySelector('.gbar').hidden = !!room;
    root.querySelector('.groom').hidden = !room;
    v.hidden = !!room;
    // Лобі малює свої секції-панелі саме, а профіль, таблиця й щоденне — просто вміст,
    // тож панель під них дає сам контейнер.
    v.classList.toggle('boxed', view.kind === 'panel');
    syncWatch();
    if (room) {
      // Кімнати ще не знаємо (зайшли за посиланням): підписка принесе її сама, а як не принесе —
      // не тримаємо людину на порожньому столі.
      if (!views[room] && !pinned.has(room)) {
        pinned.add(room);
        syncWatch();
        setTimeout(() => {
          if (!views[room] && view.kind === 'room' && view.id === room) {
            pinned.delete(room);
            toast('Цього столу вже нема', 'err');
            go('#games');
          }
        }, PIN_TTL);
      }
      renderRoomHead(room);
      return;
    }
    // Лобі малюємо щоразу (столи живі), а підрозділи з HTTP — лише коли справді перемкнулись:
    // інакше кожна зміна в лобі смикала б /api/games/profile.
    const key = view.kind === 'lobby' ? null : view.id;
    if (view.kind === 'lobby' || chromeFor !== key) {
      chromeFor = key;
      const token = ++renderToken;
      // Лобі перемальовується від кожної новини про столи — поле «знайти гру» не має від цього
      // губити ні фокус, ні курсор.
      const fi = v.querySelector('.gfind');
      const keep = fi && document.activeElement === fi ? fi.selectionStart : null;
      v.innerHTML = '';
      if (view.kind === 'lobby') renderLobby(v);
      else if (view.id === 'profile') renderProfile(v, token);
      else if (view.id === 'leaders') renderLeaders(v, token);
      else if (view.id === 'daily') renderDaily(v, token);
      else if (view.id.startsWith('x:')) renderExtra(v, view.id.slice(2));
      else renderLobby(v);
      if (keep != null) { const n = v.querySelector('.gfind'); if (n) { n.focus(); n.setSelectionRange(keep, keep); } }
    }
  }
  let renderToken = 0;
  let chromeFor = null;
  const stale = (t) => t !== renderToken;

  // ---------------------------------------------------------------------------------------------
  // Сторінка столу
  // ---------------------------------------------------------------------------------------------

  /// Кутики «на весь екран» малюємо самі: юнікодний ⛶ є не в кожному шрифті і подекуди
  /// падає в порожній квадрат.
  const FULL_ICON = (on) => '<svg class="gfico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="' + (on ? 'M6 2v4H2M10 2v4h4M6 14v-4H2M10 14v-4h4' : 'M2 6V2h4M14 6V2h-4M2 10v4h4M14 10v4h-4') + '"/></svg>';

  function renderRoomHead(id) {
    const head = root.querySelector('.grhead');
    const rv = views[id];
    const r = rv && rv.room;
    // Інші мої столи — чипами поруч: не треба вертатись у лобі, щоб перескочити на свій хід.
    const others = Object.keys(views).filter((x) => x !== id && views[x].seat != null)
      .map((x) => {
        const turn = views[x].room.status === 'playing' && turnOf(views[x]) === views[x].seat;
        return '<button class="chip grother' + (turn ? ' turn' : '') + '" data-room="' + esc(x) + '">'
          + iconOf(views[x].room.game) + esc(titleOf(views[x].room.game)) + (turn ? ' · твій хід' : '') + '</button>';
      }).join('');
    head.innerHTML = '<button class="ghost grback" title="Назад у лобі — Esc">← Лобі</button>'
      + '<span class="grtitle">' + (r ? iconOf(r.game) + esc(titleOf(r.game)) : 'Стіл') + '</span>'
      + (r && r.watchers ? '<span class="gwatchers" title="Скільки дивиться">👁 ' + r.watchers + '</span>' : '')
      + '<span class="grsp"></span>' + others
      + '<button class="ghost grfull" title="' + (full ? 'Повернути балачки й підрозділи' : 'На весь екран') + '"'
      + ' aria-label="' + (full ? 'Повернути балачки й підрозділи' : 'На весь екран') + '">' + FULL_ICON(full) + '</button>';
    head.querySelector('.grback').onclick = () => go('#games');
    head.querySelector('.grfull').onclick = () => { setFull(!full); renderRoomHead(id); };
    head.querySelectorAll('[data-room]').forEach((b) => b.onclick = () => go('#games/room/' + encodeURIComponent(b.dataset.room)));
  }

  // ---------------------------------------------------------------------------------------------
  // Лобі: живі столи з усіх груп + каталог
  // ---------------------------------------------------------------------------------------------

  const playersLabel = (g) => (g.maxPlayers === 1 ? 'соло'
    : (g.minPlayers === g.maxPlayers ? g.maxPlayers : g.minPlayers + '–' + g.maxPlayers) + ' 👤');

  /// Резюме столу в лобі — окремий елемент, а НЕ копія картки: картка з модулем гри
  /// живе лише на сторінці столу.
  function roomSummaryHtml(r) {
    const rv = views[r.id];
    const seat = seatOfMe(r);
    const mine = seat != null;
    const took = takenSeats(r), all = seatCount(r);
    const nicks = [];
    for (let i = 0; i < all; i++) { const n = nickAt(r, i); if (n) nicks.push(n); }
    const free = all - took;
    const myTurn = mine && r.status === 'playing' && rv && turnOf(rv) === seat;
    const status = r.status === 'playing' ? (myTurn ? '<b class="turn">твій хід</b>' : 'іде партія')
      : r.status === 'finished' ? 'дограли'
        : free ? 'чекає гравців' : 'ось-ось почнуть';
    const btns = mine
      ? '<button class="primary" data-open="' + esc(r.id) + '">Відкрити</button>'
      : (free > 0 && !seatedElsewhere(r.id) ? '<button class="primary" data-sit="' + esc(r.id) + '">Сісти</button>' : '')
        + '<button data-open="' + esc(r.id) + '">Дивитись</button>';
    return '<div class="gsum' + (mine ? ' mine' : '') + '">'
      + '<div class="gs-head"><span class="gtitle">' + iconOf(r.game) + esc(titleOf(r.game)) + '</span>'
      + (r.stake ? '<span class="gmode stake">🏺' + r.stake + '</span>' : '')
      + '<span class="chip">' + (all > 1 ? took + '/' + all : 'соло') + '</span></div>'
      + '<div class="gs-who">' + (nicks.length ? esc(nicks.join(', ')) : '<span class="muted">поки нікого</span>')
      + (free > 0 && all > 1 ? ' <span class="muted">· вільно ' + free + '</span>' : '')
      + ' · ' + status + (r.watchers ? ' <span class="muted">· 👁 ' + r.watchers + '</span>' : '') + '</div>'
      + '<div class="gs-btns">' + btns + '</div></div>';
  }

  function renderLobby(box) {
    const mineFirst = rooms.slice().sort((a, b) => (seatOfMe(b) != null ? 1 : 0) - (seatOfMe(a) != null ? 1 : 0));
    const want = find.trim().toLowerCase();
    const list = catalog.games.filter((g) => (filter === 'all' || g.group === filter)
      && (!want || (g.title + ' ' + (g.hint || '')).toLowerCase().includes(want)));
    const tiles = list.map((g) => {
      const solo = g.maxPlayers === 1;
      return '<div class="gtile"><div class="gt-head">' + iconOf(g.id) + '<b>' + esc(g.title) + '</b></div>'
        + '<div class="gt-hint muted small">' + esc(g.hint || '') + '</div>'
        + '<div class="gt-btns"><span class="gt-pl muted small">' + playersLabel(g) + '</span>'
        + (solo ? '<button class="primary" data-solo="' + esc(g.id) + '">Грати</button>'
          : '<button data-new="' + esc(g.id) + '">+ Стіл</button>') + '</div></div>';
    }).join('');

    box.innerHTML = '<section class="gpanel"><h3>🔥 Живі столи'
      + (rooms.length ? ' <span class="muted small">· ' + rooms.length + '</span>' : '') + '</h3>'
      + (mineFirst.length
        ? '<div class="gsums">' + mineFirst.map(roomSummaryHtml).join('') + '</div>'
        : '<div class="gempty glek">Столів нема. Постав перший із каталогу нижче і клич когось у балачках.</div>')
      + '</section>'
      + '<section class="gpanel"><h3>Каталог <span class="muted small">· ' + catalog.games.length + ' ігор</span></h3>'
      + '<div class="gfilters">'
      + GROUPS.filter((g) => g.id === 'all' || catalog.games.some((x) => x.group === g.id))
        .map((g) => '<button class="chip gchip' + (filter === g.id ? ' on' : '') + '" data-filter="' + g.id + '">'
          + (g.icon ? g.icon + ' ' : '') + esc(g.title) + '</button>').join('')
      + '<input class="gfind" type="search" placeholder="знайти гру" value="' + esc(find) + '" autocomplete="off">'
      + '</div>'
      + (tiles ? '<div class="gtiles">' + tiles + '</div>'
        : '<div class="gempty">Нічого схожого не знайшлось. Спробуй інакше або зніми фільтр.</div>')
      + '</section>';

    box.querySelectorAll('[data-filter]').forEach((b) => b.onclick = () => {
      filter = b.dataset.filter;
      try { localStorage.setItem('gamesFilter', filter); } catch { /* приватне вікно */ }
      renderView();
    });
    box.querySelector('.gfind').oninput = (e) => { find = e.target.value; renderView(); };
    box.querySelectorAll('[data-new]').forEach((b) => b.onclick = () => openCreate(gameOf(b.dataset.new)));
    box.querySelectorAll('[data-solo]').forEach((b) => b.onclick = (e) =>
      busy(e.currentTarget, 'відкриваю…', () => openRoom('OpenSolo', b.dataset.solo, null)));
    box.querySelectorAll('[data-open]').forEach((b) => b.onclick = () => go('#games/room/' + encodeURIComponent(b.dataset.open)));
    box.querySelectorAll('[data-sit]').forEach((b) => b.onclick = (e) => busy(e.currentTarget, 'сідаю…', async () => {
      const r = await call('JoinRoom', b.dataset.sit);
      if (r.ok) go('#games/room/' + encodeURIComponent(b.dataset.sit));
    }));
  }

  function renderExtra(box, id) {
    const p = extraPanels.find((x) => x.id === id);
    box.innerHTML = '';
    if (!p) { box.innerHTML = '<div class="gempty">Панель зникла.</div>'; return; }
    const host = document.createElement('div');
    host.className = 'gxpanel';
    box.appendChild(host);
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
      if (r.ok) { close(); if (r.roomId) go('#games/room/' + encodeURIComponent(r.roomId)); }
    });
  }

  // =============================================================================================
  // Картка кімнати
  // =============================================================================================

  function ensureCard(id, host) {
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
    if (host) host.appendChild(el);   // у DOM до першого mount: модуль, що міряє ширину, має що міряти
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
          if (r.ok && b.dataset.do === 'LeaveRoom') {
            // приватну соло-кімнату сервер із лобі не прибере — прибираємо картку самі
            if (views[id] && views[id].loose) { dropCard(id); delete views[id]; pinned.delete(id); }
            if (view.kind === 'room' && view.id === id) go('#games'); else renderView();
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
        : '<div class="gempty glek">Ачівок ще нема. Вони приходять самі — за перемоги, серії й дрібні дурниці.</div>')
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

  /// «за 1 спробу», «за 3 спроби», «за 6 спроб».
  function tries(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return n + ' спроб';
    if (o === 1) return n + ' спробу';
    if (o >= 2 && o <= 4) return n + ' спроби';
    return n + ' спроб';
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
            ? 'розв\'язано за ' + (p.me.attempts ? tries(p.me.attempts) : '? спроб') + (p.me.ms ? ' · ' + secs(p.me.ms) : '')
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

  /// Активна кімната — та, що зараз на екрані. Решта карток лежать змонтовані на складі
  /// під hidden, і клавіші до них іти не мають.
  function activeCard() {
    const list = (root ? [...root.querySelectorAll('.grbox > .gtable')] : [])
      .map((el) => cards[el.dataset.room]).filter((c) => c && c.ctx && !c.el.hidden);
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
  // Esc — назад зі столу. Слухач другий, тож гра, яка Esc уже з'їла (скрабл, шашки),
  // позначила подію preventDefault, і ми в неї не лізимо.
  document.addEventListener('keydown', (e) => {
    if (!shown || e.defaultPrevented || e.key !== 'Escape' || view.kind !== 'room') return;
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    if (document.querySelector('.modal:not([hidden])')) return;   // спершу попап, потім стіл
    e.preventDefault();
    if (full) { setFull(false); renderRoomHead(view.id); } else go('#games');
  });

  // =============================================================================================
  // Столи в балачках (PLAN.md §7.4): кнопка в рядку Журналу, заклик тостом, відповідь на /столи
  // =============================================================================================

  /// Стіл так, як він виглядає в рядку балачок: іконка з назвою, склад, стан людською мовою і те, що з
  /// ним можна зробити зараз. null — такого столу вже нема: дограли й прибрали, або він приватний.
  /// Джерело — та сама подія 'rooms', що малює лобі, тож кнопка не бреше про вчорашній склад.
  function roomLink(id) {
    const r = rooms.find((x) => x.id === id) || (views[id] && views[id].room);
    if (!r) return null;
    const all = seatCount(r), took = takenSeats(r), free = all - took;
    const mine = seatOfMe(r) != null;
    const canSit = !mine && r.status === 'lobby' && free > 0 && !seatedElsewhere(id);
    return {
      id: r.id,
      game: r.game,
      icon: iconOf(r.game),                                    // готовий HTML: 🎲, поки модуль гри не прийшов
      title: iconOf(r.game) + esc(titleOf(r.game)),            // іконка з назвою — для рядка, який гри не називає
      who: (all > 1 ? took + '/' + all : 'соло') + ' · '
        + (r.status === 'playing' ? 'іде партія' : r.status === 'finished' ? 'дограли'
          : free ? 'чекає гравців' : 'ось-ось почнуть'),
      canSit,
      label: mine ? 'До столу' : canSit ? 'Сісти' : 'Дивитись',
    };
  }

  async function sitAt(id) {
    const r = await call('JoinRoom', id);
    if (r.ok) go('#games/room/' + encodeURIComponent(id));
    return r;
  }

  const openAt = (id) => go('#games/room/' + encodeURIComponent(id));

  /// «Влад кличе в Мафію» — десять секунд і кнопка «Сісти». Мовчимо, коли кличемо самі себе, коли за
  /// тим столом уже нема куди сідати і коли тост закрив би пів партії: на весь екран або на вузькому
  /// екрані просто під час гри. Другий заклик за той самий стіл замінює перший, а не громадиться.
  function inviteToast(inv) {
    if (!inv || !inv.roomId || sameNick(inv.by, me.nick)) return;
    const link = roomLink(inv.roomId);
    if (!link || !link.canSit) return;
    if (full || (view.kind === 'room' && window.matchMedia('(max-width: 900px)').matches)) return;
    const box = document.getElementById('toasts');
    if (!box) { toast(inv.text, 'ok'); return; }
    const was = box.querySelector('.ginvite[data-room="' + CSS.escape(inv.roomId) + '"]');
    if (was) was.remove();
    const el = document.createElement('div');
    el.className = 'toast ok ginvite';
    el.dataset.room = inv.roomId;
    el.innerHTML = '<span class="gi-what">' + link.icon + '</span>'
      + '<span class="gi-text">' + esc(inv.text) + '<br><span class="muted small">' + esc(link.who) + '</span></span>'
      + '<button class="primary gi-sit">Сісти</button>'
      + '<button class="ghost gi-no" title="Не зараз" aria-label="Не зараз">✕</button>';
    el.querySelector('.gi-sit').onclick = (e) => busy(e.currentTarget, 'сідаю…', async () => {
      const r = await sitAt(inv.roomId);
      if (r.ok) el.remove();
      return r;
    });
    el.querySelector('.gi-no').onclick = () => el.remove();
    box.appendChild(el);
    ensureIcon(link.game, () => { const w = el.querySelector('.gi-what'); if (w) w.innerHTML = iconOf(link.game); });
    setTimeout(() => el.remove(), INVITE_MS);
  }

  /// Десять секунд: досить, щоб прочитати й натиснути, і не досить, щоб набриднути.
  const INVITE_MS = 10000;

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
      if (shown && root && root.querySelector('.gtiles')) renderView();
      if (shown && view.kind === 'room') renderRoomHead(view.id);
    },

    registerPanel(p) {
      if (!p || !p.id || !p.mount) { console.warn('[games] registerPanel без id/mount'); return; }
      const i = extraPanels.findIndex((x) => x.id === p.id);
      if (i >= 0) extraPanels[i] = p; else extraPanels.push(p);
      renderShell();
      if (view.kind === 'panel' && view.id === 'x:' + p.id) renderView();
    },

    has: (id) => !!modules[id],

    init(o) {
      o = o || {};
      if (o.esc) esc = o.esc;
      if (o.toast) toast = o.toast;
      if (o.busy) busy = o.busy;
      if (o.api) api = o.api;
      if (o.me) me = o.me;
      if (o.go) go = o.go;
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
        // Щойно столи взагалі є — беремо назви ігор: без них балачки писали б «mafia» замість «Мафія».
        // Модулі при цьому не тягнемо: слухачеві, який у «Ігри» не заходить, вони ні до чого.
        if (rooms.length) ensureNames().catch(() => { /* напишемо id, це не привід шуміти */ });
        for (const r of rooms) {
          const rv = views[r.id];
          if (rv) { rv.room = r; rv.seat = seatOfMe(r); rv.loose = false; }
          // Відкритий стіл (зайшли за посиланням або після F5): назву й місця беремо з лобі
          // одразу, а справжній вид домалює 'room' після WatchRoom.
          else if (r.id === view.id || cards[r.id]) views[r.id] = { room: r, seat: seatOfMe(r), view: undefined, loose: false };
        }
        // кімнати з лобі, яких уже нема, забираємо разом із видом; приватні соло тут не рахуються
        for (const id in views) if (!views[id].loose && !rooms.some((r) => r.id === id)) { dropCard(id); delete views[id]; }
        // стіл, на сторінці якого ми стоїмо, закрився — вертаємось у лобі, а не дивимось у порожнечу
        if (view.kind === 'room' && view.id && !views[view.id] && !pinned.has(view.id)) { go('#games'); return; }
        renderShell();
        renderView();
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
        pinned.delete(id);
        if (view.kind === 'room' && view.id === id && !cards[id]) renderView();
        else if (cards[id]) refreshCard(id);
        else if (view.kind === 'lobby') renderView();      // «твій хід» на резюме в лобі
        syncWatch();
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
      c.on('invite', inviteToast);
      loadWallet();          // черепки видно в шапці з будь-якого розділу, тож питаємо їх одразу
    },

    /// Після реконекту підписки на сервері вже нема — просимо заново для видимих кімнат.
    reconnected() {
      watched.clear();
      syncWatch();
      loadWallet();
    },

    /// tail — те, що в адресі після #games/: '' (лобі), 'room/<id>', 'profile', 'x:<id>'…
    show(tail) {
      const first = !shown;
      shown = true;
      if (!booted) return;
      ensureCatalog();
      if (first) chromeFor = null;   // повернувся в розділ — профіль і таблиця перечитуються
      route(tail);
      if (first) loadWallet();
    },

    hide() {
      shown = false;
      setFull(false);
      syncWatch();
    },

    /// Стіл для рядка балачок: { id, game, icon, title, who, canSit, label } або null, якщо столу вже нема.
    roomLink,
    /// Підтягнути модуль однієї гри заради її іконки; ready() — коли вона вже справжня.
    ensureIcon,
    /// Сісти за стіл прямо з балачок і піти до нього.
    sitAt,
    /// Просто відкрити сторінку столу.
    openAt,

    /// Для модулів і панелей, яким треба смикнути хаб самим (конкурс реклами тощо).
    call,
    send,
    get catalog() { return catalog; },
  };

  window.HGames = HGames;
})();
