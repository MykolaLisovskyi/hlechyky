/*
  Турнір на вечір — панель «Турнір» у розділі «Ігри». Правила й столи — на сервері (Games/Tournament.cs); тут лише
  показ і кнопки.

  Стан приходить подією 'tournament' (app.js віддає його сюди через HTournament.update):
    { active, id, host, stage: 'gathering'|'playing'|'between'|'done', index,
      games: [{ id, title }], players: [nick], online: [nick],
      standings: [{ nick, points }], results: [{ game, title, skipped, places: [{ nick, place, points, score }] }],
      room: null | { id, status }, champions: [nick], crown: [nick] }
  Хаб: TournamentCreate(games[]), TournamentJoin(), TournamentLeave(), TournamentNext(), TournamentSkip(),
       TournamentCancel() — кожен повертає текст відмови або null.
*/
(() => {
  const SUGGESTED = ['pictionary', 'melody', 'telephone'];
  const MEDALS = ['🥇', '🥈', '🥉'];

  let state = null;
  let picked = null;           // вибір ігор у формі «зібрати турнір» — живе між перемальовуваннями
  let composing = false;       // після дограного турніру показуємо підсумок, поки не натиснуть «Новий турнір»
  let invoke = () => Promise.reject(new Error('нема зв\'язку'));

  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();

  /// Каталог ігор. Панель може відкритись раніше, ніж «Ігри» його завантажили (пряме посилання, F5) — тоді беремо самі.
  let ownCatalog = null;
  let fetching = null;
  function eligible(catalog, host, ctx) {
    const games = (catalog && catalog.games && catalog.games.length ? catalog : ownCatalog || {}).games || [];
    if (!games.length && !fetching) {
      fetching = fetch('/api/games/catalog').then((r) => r.json()).then((c) => {
        ownCatalog = c;
        if (picked && !picked.length) picked = null;   // підказаний набір — щойно стало з чого підказувати
        if (host && host.isConnected) form(host, ctx);
      }).catch(() => { fetching = null; });
    }
    return games.filter((g) => g.maxPlayers >= 2 && !g.private);
  }

  function run(ctx, method, ...args) {
    return invoke(method, ...args)
      .then((err) => { if (err) ctx.toast(err, 'err'); return !err; })
      .catch((e) => { ctx.toast('Не вийшло: ' + e.message, 'err'); return false; });
  }

  function standings(ctx, s) {
    if (!s.standings || !s.standings.length) return '';
    return '<div class="trtable">' + s.standings.map((r, i) => {
      const place = 1 + s.standings.filter((x) => x.points > r.points).length;
      return '<div class="trrow' + (same(r.nick, ctx.me.nick) ? ' me' : '') + '">'
        + '<span class="trplace">' + (s.stage === 'done' && place <= 3 ? MEDALS[place - 1] : place + '.') + '</span>'
        + '<span class="trnick">' + ctx.esc(r.nick) + ((s.online || []).some((n) => same(n, r.nick)) ? '' : ' <i class="muted small">не на сайті</i>') + '</span>'
        + '<b>' + r.points + '</b></div>';
    }).join('') + '</div>';
  }

  function results(ctx, s) {
    if (!s.results || !s.results.length) return '';
    return '<div class="trresults">' + s.results.slice().reverse().map((r) => '<div class="trres"><b>' + ctx.esc(r.title) + '</b> '
      + (r.skipped ? '<span class="muted small">не дограли</span>'
        : r.places.map((p) => '<span class="chip">' + p.place + '. ' + ctx.esc(p.nick) + ' <b>+' + p.points + '</b></span>').join(' '))
      + '</div>').join('') + '</div>';
  }

  function gamesLine(ctx, s) {
    return '<ol class="trgames">' + s.games.map((g, i) => '<li class="'
      + (i < s.index ? 'past' : i === s.index && s.stage !== 'done' ? 'now' : '') + '">' + ctx.esc(g.title) + '</li>').join('') + '</ol>';
  }

  function crownLine(ctx, s) {
    const crown = (s && s.crown) || [];
    return crown.length ? '<div class="trcrown">👑 Чинний чемпіон: <b>' + crown.map(ctx.esc).join(', ') + '</b></div>' : '';
  }

  function form(host, ctx) {
    const games = eligible(ctx.catalog, host, ctx);
    if (!picked && games.length) picked = SUGGESTED.filter((id) => games.some((g) => g.id === id));
    if (!picked) picked = [];
    host.innerHTML = '<section class="trbox">'
      + '<h3>🏆 Турнір на вечір</h3>'
      + '<p class="muted">Кілька ігор поспіль: за кожну — очки за місце (утрьох це 3, 2 і 1), хто набрав більше — чемпіон і 👑 біля ніка до наступного турніру. Столи ставить сайт сам — лишається грати.</p>'
      + crownLine(ctx, state)
      + '<div class="muted small">Обери 2–6 ігор у тому порядку, у якому гратимете:</div>'
      + '<div class="trpick">' + games.map((g) => {
        const n = picked.indexOf(g.id);
        return '<button type="button" class="gpick' + (n >= 0 ? ' on' : '') + '" data-g="' + ctx.esc(g.id) + '">'
          + (n >= 0 ? '<b>' + (n + 1) + '</b> ' : '') + ctx.esc(g.title) + ' <span class="muted small">' + g.minPlayers + '–' + g.maxPlayers + '</span></button>';
      }).join('') + '</div>'
      + '<div class="tract"><button type="button" class="primary" data-do="create"' + (picked.length < 2 ? ' disabled' : '') + '>Зібрати турнір</button>'
      + (state && state.stage === 'done' ? '<button type="button" class="ghost" data-do="back">Назад до підсумків</button>' : '') + '</div>'
      + '</section>';
    host.querySelectorAll('[data-g]').forEach((b) => b.onclick = () => {
      const id = b.dataset.g;
      const i = picked.indexOf(id);
      if (i >= 0) picked.splice(i, 1); else if (picked.length < 6) picked.push(id);
      form(host, ctx);
    });
    const create = host.querySelector('[data-do="create"]');
    create.onclick = () => ctx.busy(create, 'збираю…', async () => { if (await run(ctx, 'TournamentCreate', picked)) composing = false; });
    const back = host.querySelector('[data-do="back"]');
    if (back) back.onclick = () => { composing = false; paint(host, ctx); };
  }

  function paint(host, ctx) {
    const s = state;
    if (!s || !s.id || (s.stage === 'done' && composing)) { form(host, ctx); return; }
    const mine = (s.players || []).some((p) => same(p, ctx.me.nick));
    const lead = same(s.host, ctx.me.nick) || (mine && !(s.online || []).some((n) => same(n, s.host)));
    const next = s.games[s.index];
    let html = '<section class="trbox">';
    if (s.stage === 'gathering') {
      html += '<h3>🏆 Турнір збирається</h3>'
        + '<div class="muted small">Збирає ' + ctx.esc(s.host) + '. Ігри:</div>' + gamesLine(ctx, s)
        + '<div class="trplayers">' + s.players.map((p) => '<span class="chip' + ((s.online || []).some((n) => same(n, p)) ? ' on' : '') + '">' + ctx.esc(p) + '</span>').join('') + '</div>'
        + '<div class="tract">'
        + (mine ? '' : '<button type="button" class="primary" data-do="TournamentJoin">Я в турнірі</button>')
        + (lead ? '<button type="button" class="primary" data-do="TournamentNext">Почати: ' + ctx.esc(next.title) + ' ▸</button>' : '')
        + (mine ? '<button type="button" class="ghost" data-do="TournamentLeave">Вийти</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentCancel">Скасувати</button>' : '')
        + '</div>'
        + (lead ? '<div class="muted small">Коли всі зайшли — тисни «Почати»: сайт сам поставить стіл і посадить учасників, які зараз на сайті.</div>' : '');
    } else if (s.stage === 'playing') {
      html += '<h3>🏆 Гра ' + (s.index + 1) + ' з ' + s.games.length + ': ' + ctx.esc(next.title) + '</h3>'
        + gamesLine(ctx, s)
        + '<div class="tract">'
        + (s.room ? '<button type="button" class="primary" data-room="' + ctx.esc(s.room.id) + '">До столу ▸</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentSkip" title="Якщо гра зависла чи всі розбіглись">Пропустити гру</button>' : '')
        + (mine ? '' : '<button type="button" class="ghost" data-do="TournamentJoin">Я теж (з наступної гри)</button>')
        + '</div>'
        + standings(ctx, s) + results(ctx, s);
    } else if (s.stage === 'between') {
      html += '<h3>🏆 Після гри ' + s.index + ' з ' + s.games.length + '</h3>'
        + gamesLine(ctx, s)
        + '<div class="tract">'
        + (lead ? '<button type="button" class="primary" data-do="TournamentNext">Далі: ' + ctx.esc(next.title) + ' ▸</button>' : '<span class="muted">Чекаємо, поки ' + ctx.esc(s.host) + ' запустить наступну гру</span>')
        + (mine ? '' : '<button type="button" class="ghost" data-do="TournamentJoin">Я теж</button>')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentCancel">Завершити зараз</button>' : '')
        + '</div>'
        + standings(ctx, s) + results(ctx, s);
    } else {
      const champ = s.champions || [];
      html += '<h3>🏆 Турнір закінчено</h3>'
        + (champ.length ? '<div class="trchamp">👑 ' + champ.map(ctx.esc).join(' і ') + '</div>' : '<div class="muted">Без чемпіона</div>')
        + standings(ctx, s) + results(ctx, s)
        + '<div class="tract"><button type="button" class="primary" data-do="new">Новий турнір</button></div>';
    }
    html += '</section>';
    host.innerHTML = html;
    host.querySelectorAll('[data-do]').forEach((b) => b.onclick = () => {
      if (b.dataset.do === 'new') { composing = true; picked = null; paint(host, ctx); return; }
      if (b.dataset.do === 'TournamentCancel' && !confirm(s.results && s.results.length ? 'Завершити турнір зараз? Чемпіон — хто попереду.' : 'Скасувати турнір?')) return;
      ctx.busy(b, '…', () => run(ctx, b.dataset.do));
    });
    host.querySelectorAll('[data-room]').forEach((b) => b.onclick = () => { location.hash = '#games/room/' + encodeURIComponent(b.dataset.room); });
  }

  /// Відкрита зараз панель. core.js не перемальовує вже відкриту панель на повторний registerPanel, тож свіжий стан
  /// малюємо самі — поки host у документі.
  let mounted = null;

  const panel = {
    id: 'tournament',
    title: 'Турнір',
    icon: '👑',
    mount(host, ctx) { mounted = { host, ctx }; paint(host, ctx); },
  };

  window.HTournament = {
    /// app.js дає свій conn.invoke — панель сама до хаба не ходить.
    connect(fn) { invoke = fn; },
    get state() { return state; },
    update(s) {
      state = s || null;
      if (state && state.stage !== 'done') composing = false;
      if (mounted && mounted.host.isConnected) paint(mounted.host, mounted.ctx);
    },
    /// Чи носить нік корону.
    crowned(nick) { return !!state && (state.crown || []).some((n) => same(n, nick)); },
  };

  if (window.HGames) HGames.registerPanel(panel);
})();
