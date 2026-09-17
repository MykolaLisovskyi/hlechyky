/*
  Позивні. Головне правило модуля те саме, що й у Мафії: нічого не додумувати. Розклад приходить у виді
  лише капітанам (поле key), і якщо його нема — його нема й на екрані, а не «є, але сховане стилями».

  Вид із сервера (Impl/Pozyvni.cs):
    { phase: 'setup'|'clue'|'guess'|'done', turn, side, board: [{w, open}], key: []|null,
      clue: {word, count, left}|null, teams: { red:{seats,boss}, blue:{seats,boss} },
      me: {side, boss}|null, left: {red, blue}, endsAt, phaseMs, zero, log: [], result }

  Кадрів у грі нема (TickMs потрібен лише годиннику й фазі складу), тож усе малюється з виду.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.5" y="2.5" width="5.5" height="4" rx="1" fill="var(--accent)"/>'
    + '<rect x="9" y="2.5" width="5.5" height="4" rx="1" fill="var(--muted)"/>'
    + '<rect x="1.5" y="9.5" width="5.5" height="4" rx="1" fill="var(--muted)"/>'
    + '<rect x="9" y="9.5" width="5.5" height="4" rx="1" fill="var(--ok)"/>'
    + '</svg>';

  const TEAM = { red: 'червоні', blue: 'сині' };
  const OF = { red: 'червоних', blue: 'синіх' };
  const COUNTS = [1, 2, 3, 4, 5, 6, 7, 8, 9];

  const side = (v) => v.side || 'red';
  const foe = (s) => (s === 'red' ? 'blue' : 'red');
  const mine = (v) => (v.me ? v.me.side : null);
  const myTurn = (v) => !!v.me && v.me.side === side(v);
  const iAmBoss = (v) => !!v.me && v.me.boss;

  /// Дуга-таймер: заводимо на фазу, яка справді має кінець (склад завжди, решта — лише з годинником).
  function arcTo(host, v) {
    if (!host) return;
    if (v && v.endsAt && v.phaseMs) { HGames.ui.timerArc(host, v.endsAt, v.phaseMs); return; }
    const el = host.querySelector(':scope > .garc');
    if (el && el._arc) el._arc.stop();
  }

  function build(root, ctx) {
    const el = document.createElement('div');
    el.className = 'pz';
    el.innerHTML = '<div class="pz-top">'
      + '<div class="pz-arc"></div>'
      + '<div class="pz-head"><b class="pz-turn"></b><span class="pz-hint muted small"></span></div>'
      + '</div>'
      + '<div class="pz-teams"></div>'
      + '<div class="pz-clue"></div>'
      + '<div class="pz-grid"></div>'
      + '<div class="pz-panel">'
      + '<form class="pz-give" hidden>'
      + '<input class="pz-word" type="text" maxlength="24" placeholder="слово і число, як «море 2»…" autocomplete="off" spellcheck="false">'
      + '<div class="pz-counts"></div>'
      + '<button class="primary" type="submit">Сказати</button>'
      + '</form>'
      + '<div class="pz-do"></div>'
      + '</div>'
      + '<details class="pz-logbox"><summary class="muted small">Як ішло</summary><div class="pz-log"></div></details>';
    root.appendChild(el);

    // Слухачі вішаємо раз, свіжий ctx тримаємо на елементі: інакше клік назавжди пішов би в перший.
    el.querySelector('.pz-grid').addEventListener('click', (e) => {
      const b = e.target.closest('.pz-card');
      if (!b || b.disabled || !el._ctx) return;
      el._ctx.act('pick', { i: +b.dataset.i });
    });
    el.querySelector('.pz-counts').addEventListener('click', (e) => {
      const b = e.target.closest('.pz-n');
      if (!b) return;
      el._count = +b.dataset.n;
      paintCounts(el);
    });
    el.querySelector('.pz-give').addEventListener('submit', (e) => { e.preventDefault(); say(el); });
    // Enter у полі: сторінка ловить клавіші раніше за форму (це її право — у неї свої гарячі клавіші),
    // тож підказку відправляємо руками, а не чекаємо на submit від браузера.
    el.querySelector('.pz-word').addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      e.stopPropagation();
      say(el);
    });
    el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-act]');
      if (!b || !el._ctx) return;
      const act = b.dataset.act;
      if (act === 'team') el._ctx.act('team', { side: b.dataset.side });
      else if (act === 'boss') el._ctx.act('boss');
      else if (act === 'go') el._ctx.act('go');
      else if (act === 'pass') el._ctx.act('pass');
    });
    el._count = 1;
    el._ctx = ctx;
    return el;
  }

  /// Сказати підказку: «море 2» одним рядком теж годиться — так її й вимовляють уголос.
  function say(el) {
    const input = el.querySelector('.pz-word');
    const raw = input.value.trim();
    if (!raw || !el._ctx) return;
    const tail = raw.match(/^(.+?)[\s,]+(\d)$/);
    const word = tail ? tail[1].trim() : raw;
    const count = tail ? +tail[2] : (el._count == null ? 1 : el._count);
    if (tail) { el._count = count; paintCounts(el); }
    el._ctx.act('clue', { word, count }).then((r) => {
      if (r && r.ok) input.value = '';
    });
  }

  /// Чіпи з числом підказки. «Нуль» показуємо лише там, де стіл його дозволив.
  function paintCounts(el, zero) {
    const host = el.querySelector('.pz-counts');
    if (zero != null) host.dataset.zero = zero ? '1' : '';
    const nums = host.dataset.zero ? [0].concat(COUNTS) : COUNTS;
    const sig = nums.join(',') + ':' + el._count;
    if (host.dataset.sig === sig) return;
    host.dataset.sig = sig;
    host.innerHTML = nums.map((n) => '<button type="button" class="pz-n' + (el._count === n ? ' on' : '')
      + '" data-n="' + n + '">' + (n === 0 ? '∞' : n) + '</button>').join('');
  }

  function paint(root, ctx) {
    const el = root.querySelector(':scope > .pz') || build(root, ctx);
    el._ctx = ctx;
    const v = ctx.view || {};
    if (!v.phase) { el.querySelector('.pz-grid').innerHTML = '<div class="gwait">розкладаю слова…</div>'; return; }

    const s = side(v), over = v.phase === 'done';
    // Поки стіл у лобі, гра ще не почалась, а вид у неї вже є (порожній стіл і порожні команди).
    // Малювати його — брехати: показуємо тільки те, чого чекаємо.
    const lobby = !!ctx.room && ctx.room.status === 'lobby';
    el.classList.toggle('pz-lobby', lobby);

    // ---- шапка ----
    const arc = el.querySelector('.pz-arc');
    const ticking = !lobby && !over && (v.phase === 'setup' || !!v.endsAt);
    arc.hidden = !ticking;
    arcTo(arc, ticking ? v : null);
    el.querySelector('.pz-turn').textContent = head(v, lobby);
    el.querySelector('.pz-hint').textContent = lobby ? 'на двох тут не грають' : hint(v, ctx);
    const teams = el.querySelector('.pz-teams');
    teams.hidden = lobby;
    if (!lobby) {
      const rows = lineUp(v, ctx, over);
      if (teams.dataset.sig !== rows) { teams.dataset.sig = rows; teams.innerHTML = rows; }
    }

    // ---- підказка ----
    const clue = el.querySelector('.pz-clue');
    const clueText = lobby ? '' : v.clue
      ? '<b>' + ctx.esc(v.clue.word.toUpperCase()) + '</b> · ' + (v.clue.count === 0 ? '∞' : v.clue.count)
        + '<span class="muted small"> лишилось ' + (v.clue.count === 0 ? '—' : v.clue.left) + '</span>'
      : '';
    if (clue.dataset.sig !== clueText) { clue.dataset.sig = clueText; clue.innerHTML = clueText; }
    clue.hidden = !clueText;

    // ---- стіл ----
    const key = v.key || null;
    const canPick = !lobby && v.phase === 'guess' && myTurn(v) && !iAmBoss(v);
    const fingers = v.fingers || {};
    const cards = lobby ? '' : (v.board || []).map((c, i) => {
      const open = c.open || null;
      const cls = ['pz-card'];
      // Шрифт на столі один на всіх — різні розміри в сусідніх плитках очі помічають одразу. Дрібнішає
      // лише те, що інакше не влізе: «сани діда мороза» в плитку 60 px не вміщаються жодним чином.
      const longest = (c.w || '').split(' ').reduce((n, part) => Math.max(n, part.length), 0);
      if ((c.w || '').length > 14 || longest > 11) cls.push('xlong');
      if (open) cls.push('open', 'pz-' + open);
      else if (key) cls.push('key', 'pz-k-' + key[i]);
      const at = fingers[i] || [];
      if (at.indexOf(ctx.seat) >= 0) cls.push('pointed');
      const hands = at.length ? '<span class="pz-hands">' + at.map((seat) => {
        const nick = ctx.nickOf(seat) || ('гравець ' + (seat + 1));
        return '<i title="' + ctx.esc(nick) + '">' + ctx.esc(nick.slice(0, 2)) + '</i>';
      }).join('') + '</span>' : '';
      return '<button class="' + cls.join(' ') + '" data-i="' + i + '"'
        + (canPick && !open ? '' : ' disabled') + '><span>' + ctx.esc(c.w || '') + '</span>' + hands + '</button>';
    }).join('');
    const sig = cards + '|' + canPick;
    const grid = el.querySelector('.pz-grid');
    if (grid.dataset.sig !== sig) { grid.dataset.sig = sig; grid.innerHTML = cards; }

    // ---- керування ----
    const give = el.querySelector('.pz-give');
    const canClue = !lobby && v.phase === 'clue' && myTurn(v) && iAmBoss(v);
    give.hidden = !canClue;
    if (canClue) paintCounts(el, !!v.zero);

    const doHtml = lobby ? lobbyLine(v, ctx) : advice(v, ctx);
    const doBox = el.querySelector('.pz-do');
    if (doBox.dataset.sig !== doHtml) { doBox.dataset.sig = doHtml; doBox.innerHTML = doHtml; }

    // ---- хроніка ----
    el.querySelector('.pz-logbox').hidden = lobby;
    const log = (v.log || []).map((l) => '<div>' + ctx.esc(l) + '</div>').join('')
      || '<div class="muted small">Ще нічого не сталось.</div>';
    const logBox = el.querySelector('.pz-log');
    if (logBox.dataset.sig !== log) { logBox.dataset.sig = log; logBox.innerHTML = log; logBox.scrollTop = logBox.scrollHeight; }
  }

  /// Склад столу: дві колонки, капітан із рупором, команда, що ходить, підсвічена. У фазі складу
  /// сюди ж лягають кнопки — той самий блок і показує, і дає помінятись, щоб не було двох різних списків.
  function lineUp(v, ctx, over) {
    const setup = v.phase === 'setup';
    const cols = ['red', 'blue'].map((t) => {
      const team = (v.teams && v.teams[t]) || { seats: [], boss: null };
      const seats = (team.seats || []).slice().sort((a, b) => (a === team.boss ? -1 : b === team.boss ? 1 : a - b));
      const mates = seats.map((seat) => {
        const boss = team.boss === seat;
        return '<span class="pz-mate' + (boss ? ' boss' : '') + (seat === ctx.seat ? ' me' : '') + '">'
          + (boss ? '<i class="pz-mic" aria-hidden="true">🎙</i>' : '')
          + ctx.esc(ctx.nickOf(seat) || ('гравець ' + (seat + 1)))
          + (boss ? '<i class="pz-role">капітан</i>' : '') + '</span>';
      }).join('') || '<span class="muted small">поки нікого</span>';
      const acts = !setup || !ctx.mine ? ''
        : '<div class="pz-acts">'
          + (mine(v) === t ? '' : '<button class="ghost" data-act="team" data-side="' + t + '">До ' + OF[t] + '</button>')
          + (mine(v) === t && team.boss !== ctx.seat ? '<button class="ghost" data-act="boss">Я капітан</button>' : '')
          + '</div>';
      return '<div class="pz-col ' + t + (!over && !setup && t === side(v) ? ' now' : '') + '">'
        + '<div class="pz-colhead"><b>' + TEAM[t] + '</b><span class="pz-left">'
        + ((v.left && v.left[t]) || 0) + ' ' + words((v.left && v.left[t]) || 0) + '</span></div>'
        + '<div class="pz-mates">' + mates + '</div>' + acts + '</div>';
    }).join('');
    return cols + (setup && ctx.mine ? '<button class="primary pz-go" data-act="go">Почати</button>' : '');
  }

  /// «9 слів», «2 слова», «1 слово» — рахунок у колонці команди читається реченням, а не цифрою.
  const words = (n) => (n % 10 === 1 && n % 100 !== 11 ? 'слово' : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'слова' : 'слів');

  function head(v, lobby) {
    if (lobby) return 'Збираємо стіл';
    if (v.phase === 'setup') return 'Розбираємось, хто з ким';
    if (v.phase === 'done') {
      if (!v.result || !v.result.side) return 'Партії не вийшло';
      return 'Перемогли ' + TEAM[v.result.side] + (v.result.black ? ' — чорне слово' : '');
    }
    return 'Ходять ' + TEAM[side(v)];
  }

  /// Скільки в команди, що ходить, польових гравців: один — клік одразу відкриває, кілька — це палець.
  function mates(v) {
    const team = (v.teams && v.teams[side(v)]) || { seats: [] };
    return (team.seats || []).filter((s) => s !== team.boss).length;
  }

  /// Хто зараз ведучий — капітан команди, що ходить. Це головне, чого не видно з самої сітки.
  function bossNick(v, ctx) {
    const team = (v.teams && v.teams[side(v)]) || {};
    return team.boss == null ? '' : (ctx.nickOf(team.boss) || ('гравець ' + (team.boss + 1)));
  }

  function hint(v, ctx) {
    if (v.phase === 'setup') return 'Можна помінятись командами й капітанами';
    if (v.phase === 'clue') {
      const nick = bossNick(v, ctx);
      return nick ? 'підказку дає ' + nick : 'чекаємо на підказку капітана';
    }
    if (v.phase === 'guess' && v.clue) {
      const nick = bossNick(v, ctx);
      return (myTurn(v) && !iAmBoss(v) ? 'ваша черга тикати' : 'вгадують ' + TEAM[side(v)])
        + (nick ? ' · підказка від ' + nick : '');
    }
    return ctx.seat == null ? 'дивишся збоку' : '';
  }

  /// Рядок «що зараз робити» — картка має пояснювати гру тому, хто сів уперше.
  function advice(v, ctx) {
    if (v.phase === 'setup') {
      return '<span class="muted small">У кожній команді має бути щонайменше двоє і один капітан. '
        + 'Не встигнете — стіл розбере склад сам.</span>';
    }
    if (v.phase === 'done') return '';
    if (!v.me) return '<span class="muted small">Дивишся збоку: розкладу тобі не покажуть.</span>';
    if (!myTurn(v)) return '<span class="muted small">Зараз не ваш хід. Слухайте, що скажуть ' + TEAM[side(v)] + '.</span>';
    if (iAmBoss(v)) {
      return v.phase === 'clue'
        ? '<span class="muted small">Одне слово і число: скільки ваших слів воно накриває. Число можна дописати в те саме поле («море 2»). Слова зі столу казати не можна.</span>'
        : '<span class="muted small">Тепер мовчи. Ні слова, ні брови.</span>';
    }
    if (v.phase === 'clue') return '<span class="muted small">Капітан думає.</span>';
    return '<span class="muted small">' + (mates(v) > 1
      ? 'Тисни слово — це «показую пальцем». Відкриється, коли покажуть усі; передумав — тисни ще раз.'
      : 'Тисніть слова, поки впевнені.') + '</span>'
      + '<button class="ghost" data-act="pass">Досить</button>';
  }

  HGames.register({
    id: 'pozyvni',
    icon: ICON,
    seatNames: (i, room) => (room && room.seatNames && room.seatNames[i]) || (i % 2 === 0 ? 'червоні' : 'сині'),
    seatClass: ['x', 'o'],
    mount(root, ctx) { build(root, ctx); paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    unmount(root) { arcTo(root.querySelector('.pz-arc'), null); },

    status(ctx) {
      const v = ctx.view || {};
      if (!v.phase || v.phase === 'done') return '';
      if (ctx.room && ctx.room.status === 'lobby') return '';
      if (v.phase === 'setup') return 'Збираємо команди';
      if (!myTurn(v)) return 'Ходять ' + TEAM[side(v)];
      if (iAmBoss(v)) return v.phase === 'clue' ? 'Твій хід: дай підказку' : 'Мовчи, вони вгадують';
      return v.phase === 'clue' ? 'Чекаємо на капітана'
        : mates(v) > 1 ? 'Ваш хід: показуйте пальцем' : 'Ваш хід: тисніть слова';
    },
  });
})();
