/*
  Цех Гончарного кола (docs/games/specs/clicker-v7.md §4 B5; як зроблено — clicker-v7-guild.md). Частина ядра clicker.js.

  Вкладка «Цех»:
  1) віз тижня — великий SVG-віз, що наповнюється виробами, смуга з порогами бронза/срібло/золото, підцілі, внески
     друзів (за абеткою, без місць), «Покласти на віз» (вибір виробів із комори) і «Забрати нагороду»;
  2) дарунки — скільки ще сьогодні, вибір друга й виробу, полиця дарунків «від Миколи»;
  3) ранг — шлях учень → челядник → майстер → цехмістр, що бракує, майстерштук, перки й вимикач автогорна;
  4) гончарі цеху (GET /api/games/clicker/guild) і «Зазирнути в хату» (GET /api/games/clicker/house) — хата в overlay;
  5) «Похвалитись» виробом у Журнал.
  Правила рахує сервер (Clicker.ActGuild / ClickerGuildService); тут — лише малюнок і дії guild { op, … }.
  Звуки: api.sfx('wagon' | 'gift' | 'rank-up' | 'brag').
*/
(() => {
  const ROSTER_MS = 60000;
  const RANKS = ['Учень', 'Челядник', 'Майстер', 'Цехмістр'];
  const RANK_ICON = ['🧑‍🎓', '🛠️', '🏅', '🎖️'];
  const TIER = ['', 'бронза', 'срібло', 'золото'];
  const TIER_ICON = ['', '🥉', '🥈', '🥇'];
  const TIER_COLOR = ['#8a6a4a', '#c07a3a', '#c9ced6', '#f4c542'];
  const QUALITY = ['', 'звичайний', 'добрий', 'дзвінкий'];
  const STARS = ['', '★', '★★', '★★★'];
  const TOOL_ICON = { paddle: '🪵', string: '🧵', sponge: '🧽', ribs: '🦴', lantern: '🏮', apron: '🥼', bucket: '🪣', whistle: '🎶', scales: '⚖️', iron: '🔖' };
  const TIER_EMBLEM = { workshop: '🏠', fair: '🎪', artel: '🤝', chumaks: '🐂', pit: '⛏️', school: '📜', chaika: '⛵', museum: '🏛️', tsar: '👑' };

  // ---------- дрібниці ----------

  const cat = (st) => (st.catalog && st.catalog.guild) || null;
  const rankName = (st, r) => {
    const c = cat(st);
    return (c && c.ranks && c.ranks[r] && c.ranks[r].name) || RANKS[r] || '';
  };
  const wareName = (st, key) => {
    const list = (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
    const w = list.find((x) => x.key === key);
    return w ? w.name : key;
  };
  const styleName = (st, key) => {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key) || ((st.catalog && st.catalog.styles) || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
  const headers = (st) => ({ 'X-Nick': encodeURIComponent(myNick(st)) });
  const itemsOf = (st) => (st.craft && st.craft.items) || [];

  /// «3 дн 4 год», «5 год 12 хв», «12:04».
  function left(api, ms) {
    if (ms <= 0) return 'ось-ось';
    const m = Math.floor(ms / 60000);
    const d = Math.floor(m / 1440);
    const h = Math.floor((m % 1440) / 60);
    if (d > 0) return d + ' дн ' + h + ' год';
    if (h > 0) return h + ' год ' + (m % 60) + ' хв';
    return api.mmss(ms);
  }

  function act(st, api, payload, sound) {
    return api.act(st, 'guild', payload).then((r) => {
      if (r && r.ok && sound) api.sfx(sound);
      return r;
    });
  }

  // ---------- віз ----------

  /// Віз: віл у ярмі, дерев'яна платформа з бортами, два колеса, купа виробів (до 28), прапорець рівня. viewBox 360×200.
  function wagonSvg(st, api, w) {
    const goal = Math.max(1, w.goal);
    const fill = Math.min(1, w.total / (goal * 2));
    const count = Math.round(fill * 28);
    // Що лежить на возі: підцілі у своїх пропорціях, решта — горщики.
    const kinds = [];
    for (const s of w.subs) for (let i = 0; i < Math.min(s.have, s.need); i++) kinds.push(s.ware);
    const rest = Math.max(0, w.total - kinds.length);
    for (let i = 0; i < rest; i++) kinds.splice(Math.floor((i * (kinds.length + 1)) / (rest + 1)) + i % 2, 0, 'pot');
    while (kinds.length < Math.max(count, 1)) kinds.push('pot');
    const step = kinds.length / Math.max(1, count);
    const K = 0.5;
    let pile = '';
    for (let i = 0; i < count; i++) {
      const ware = kinds[Math.floor(i * step)] || 'pot';
      const row = Math.floor(i / 7);
      const col = i % 7;
      const x = 124 + col * 34 + (row % 2) * 17 - 50 * K;
      // Низ виробу — трохи за переднім бортом (y 104): і миску видно, і лежить «у возі».
      const y = 106 - row * 20 - 86 * K;
      // Анімація «впав на віз» — лише для щойно покладених (внутрішня <g>: CSS-transform перебив би атрибут зовнішньої).
      const fresh = st.guildPile != null && i >= st.guildPile;
      pile += '<g transform="translate(' + x.toFixed(1) + ' ' + y.toFixed(1) + ') scale(' + K + ')"><g' + (fresh ? ' class="clkg-ware" style="--d:' + ((i - st.guildPile) * 60) + 'ms"' : '') + '>'
        + api.wareSvg(ware, { quality: 1, slot: 'wg-' + i, wrap: false }) + '</g></g>';
    }
    st.guildPile = count;
    const wheel = (cx) => '<g class="clkg-wheel" style="transform-origin:' + cx + 'px 152px">'
      + '<circle cx="' + cx + '" cy="152" r="28" fill="none" stroke="#5c3b1e" stroke-width="7"/>'
      + '<circle cx="' + cx + '" cy="152" r="6" fill="#3a2412"/>'
      + [0, 45, 90, 135].map((a) => '<path d="M' + cx + ' 126v52" stroke="#6b4423" stroke-width="3" transform="rotate(' + a + ' ' + cx + ' 152)"/>').join('')
      + '</g>';
    const ox = '<g class="clkg-ox">'
      + '<path d="M30 150v28M40 152v26M66 152v26M76 150v28" stroke="#4a3426" stroke-width="6" stroke-linecap="round"/>'
      + '<ellipse cx="54" cy="134" rx="34" ry="22" fill="#6d5140"/>'
      + '<path d="M86 128q10-2 12 10" stroke="#4a3426" stroke-width="3" fill="none"/>'
      + '<ellipse cx="18" cy="124" rx="13" ry="11" fill="#5d4435"/>'
      + '<path d="M10 115q-8-10-2-16M26 115q8-10 2-16" stroke="#e8dcc0" stroke-width="3" fill="none" stroke-linecap="round"/>'
      + '<circle cx="13" cy="122" r="1.8" fill="#111"/><ellipse cx="10" cy="131" rx="5" ry="3.4" fill="#8a6a58"/>'
      + '<path d="M34 112h8v16h-8z" fill="#8b5a2b"/>'
      + '</g>';
    const flag = w.tier > 0
      ? '<g class="clkg-flag"><path d="M344 56v48" stroke="#5c3b1e" stroke-width="3"/><path d="M342 56h-40l10 12-10 12h40z" fill="' + TIER_COLOR[w.tier] + '"/>'
        + '<text x="324" y="73" font-size="12" text-anchor="middle">' + TIER_ICON[w.tier] + '</text></g>'
      : '';
    // Поки виробів на два ряди — небо над возом обрізаємо, щоб порожній віз не був високою порожньою картинкою.
    const top = count > 14 ? 0 : 44;
    return '<svg class="clkg-wagon" viewBox="0 ' + top + ' 360 ' + (200 - top) + '" role="img" aria-label="Віз цеху, повний на ' + Math.floor(w.pct) + ' %">'
      + '<path d="M0 182h360" stroke="rgba(255,255,255,.12)" stroke-width="2"/>'
      + ox + flag
      + '<path d="M104 118L40 114" stroke="#6b4423" stroke-width="5" stroke-linecap="round"/>'
      + pile
      + '<path d="M100 104h250l-8 30H108z" fill="#8f6d4b" stroke="#5c3b1e" stroke-width="2"/>'
      + '<path d="M104 114h242M106 124h238" stroke="#6b4a2c" stroke-width="1.4"/>'
      + '<path d="M100 104v-24M350 104v-24M225 104v-14" stroke="#6b4423" stroke-width="4" stroke-linecap="round"/>'
      + wheel(150) + wheel(300)
      + '</svg>';
  }

  /// Смуга до 200 %: пороги бронзи, срібла й золота, власний внесок — окремим відтінком.
  function barHtml(w) {
    const goal = Math.max(1, w.goal);
    const pct = Math.min(100, (w.total / (goal * 2)) * 100);
    const mine = Math.min(pct, (w.mine / (goal * 2)) * 100);
    const marks = [1, 2, 3].map((t) => '<span class="clkg-mark t' + t + (w.tier >= t ? ' on' : '') + '" style="left:' + ([0, 50, 75, 100][t]) + '%">'
      + '<b>' + TIER_ICON[t] + '</b><i>' + [0, 100, 150, 200][t] + '%</i></span>').join('');
    return '<div class="clkg-bar"><div class="clkg-fill" style="width:' + pct.toFixed(1) + '%"></div>'
      + '<div class="clkg-mine" style="width:' + mine.toFixed(1) + '%"></div>' + marks + '</div>';
  }

  function wagonHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const w = v.week;
    const c = cat(st);
    const minGive = (c && c.minGive) || 5;
    const subs = w.subs.map((s) => '<div class="clkg-sub' + (s.have >= s.need ? ' done' : '') + '">'
      + api.wareSvg(s.ware, { quality: 1, cls: 'clkg-ico', slot: 'wsub-' + s.ware })
      + '<span>' + esc(wareName(st, s.ware)) + '</span><b>' + Math.min(s.have, s.need) + '/' + s.need + '</b></div>').join('');
    const givers = w.givers.length
      ? '<div class="clkg-givers">' + w.givers.map((g) => '<span class="clkg-chip' + (g.nick === myNick(st) ? ' me' : '') + '">'
        + esc(g.nick) + ' · ' + g.n + '</span>').join('') + '</div>'
      : '<div class="muted small">Віз ще порожній — хтось має покласти перший горщик.</div>';
    const claims = (v.claims || []).map((cl) => '<button type="button" class="primary clkg-claim" data-claim="' + esc(cl.week) + '">'
      + '🛒 Забрати: ' + TIER_ICON[cl.tier] + ' ' + TIER[cl.tier] + (cl.week !== w.id ? ' (минулий тиждень)' : '')
      + ' · +' + api.potsShort(cl.pots) + '</button>').join('');
    const mineNote = w.mine >= minGive
      ? 'ти поклав(ла) ' + w.mine + ' — нагорода твоя, щойно віз дійде до порогу'
      : 'поклади хоч ' + minGive + ' (' + w.mine + ' є) — і нагорода віза буде й твоя';
    const prev = v.prev
      ? '<div class="muted small">Минулого тижня: ' + (v.prev.tier ? TIER_ICON[v.prev.tier] + ' ' + TIER[v.prev.tier] : 'до бронзи не дотягли — не біда') + ', разом ' + v.prev.total + '</div>'
      : '';
    return '<section class="clkg-card clkg-week">'
      + '<div class="clk-sub">🐴 Віз цеху на ярмарок · <span class="muted small">від\'їде за <span class="clkg-cd" data-at="' + Date.parse(w.endsAt) + '"></span></span></div>'
      + wagonSvg(st, api, w)
      + '<div class="clkg-line"><b>' + w.total + '</b> з ' + w.goal + ' <span class="muted small">· ' + Math.floor(w.pct) + ' % · '
      + (w.tier ? TIER_ICON[w.tier] + ' ' + TIER[w.tier] : 'ще до бронзи') + '</span></div>'
      + barHtml(w)
      + '<div class="clkg-subs">' + subs + '</div>'
      + '<div class="muted small">Бронза — ціль і всі підцілі; срібло — 150 %; золото — 200 %. Ціль росте з кількістю гончарів минулого тижня ('
      + w.potters + ').</div>'
      + givers
      + '<div class="clkg-btns"><button type="button" class="ghost clkg-give"' + (st.mine && itemsOf(st).length ? '' : ' disabled') + '>🧺 Покласти на віз</button>'
      + claims + '</div>'
      + '<div class="muted small">' + mineNote + '</div>'
      + prev
      + '</section>';
  }

  // ---------- дарунки ----------

  function giftsHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const per = (c && c.giftsPerDay) || 3;
    const shelf = v.shelf.length
      ? '<div class="clkg-shelf">' + v.shelf.map((g, i) => '<span class="clkg-gift q' + g.q + '" title="' + esc(QUALITY[g.q] + ' ' + wareName(st, g.ware).toLowerCase()
        + ' · ' + styleName(st, g.style) + ' · від ' + g.from) + '">'
        + api.wareSvg(g.ware, { style: g.style, quality: g.q, cls: 'clkg-mid', slot: 'gsh-' + i })
        + '<i>від ' + esc(g.from) + '</i></span>').join('') + '</div>'
      : '<div class="muted small">Полиця дарунків порожня. Дарунок від друга стане тут із підписом — продати його не можна, це пам\'ять.</div>';
    return '<section class="clkg-card">'
      + '<div class="clk-sub">🎁 Дарунки <span class="muted small">· сьогодні ще ' + v.gifts.left + ' з ' + per + ' · подаровано ' + v.gifts.sent
      + ' · отримано ' + v.gifts.got + '</span></div>'
      + '<div class="clkg-btns"><button type="button" class="ghost clkg-gifting"' + (st.mine && v.gifts.left > 0 && itemsOf(st).length ? '' : ' disabled') + '>🎁 Подарувати виріб другові</button>'
      + '<button type="button" class="ghost clkg-bragging"' + (st.mine && itemsOf(st).length ? '' : ' disabled') + '>🏺 Похвалитись</button>'
      + '<span class="muted small clkg-bragcd" data-at="' + (Date.parse(v.bragAt) || 0) + '"></span></div>'
      + '<div class="clk-sub small">Полиця дарунків</div>' + shelf
      + '</section>';
  }

  // ---------- ранг ----------

  function rankHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const path = RANKS.map((_, r) => '<span class="clkg-step' + (v.rank === r ? ' on' : v.rank > r ? ' done' : '') + '">'
      + '<b>' + RANK_ICON[r] + '</b><i>' + esc(rankName(st, r)) + '</i></span>').join('<span class="clkg-dash"></span>');
    let next = '<div class="clkg-top">🎖️ Ти цехмістр — вище в цеху лише небо. Нагорода воза ×1,5, титул у похвалах і в хаті.</div>';
    if (v.next) {
      const n = v.next;
      const row = (label, have, need) => '<div class="clkg-req' + (have >= need ? ' ok' : '') + '"><span>' + label + '</span>'
        + '<div class="clkg-rbar"><i style="width:' + (need > 0 ? Math.min(100, (have / need) * 100) : 100).toFixed(1) + '%"></i></div>'
        + '<b>' + api.short(Math.min(have, need)) + '/' + api.short(need) + '</b></div>';
      const p = n.piece;
      const pieceText = 'дзвінк' + (['bowl', 'makitra', 'tile'].includes(p.ware) ? 'а' : p.ware === 'barrel' ? 'е' : 'ий') + ' '
        + wareName(st, p.ware).toLowerCase() + (p.style ? ' — ' + ((c && c.styleWords && c.styleWords[p.style]) || styleName(st, p.style).toLowerCase()) : ' у будь-якому розписі');
      const perk = c && c.ranks && c.ranks[n.rank] ? c.ranks[n.rank].perk : '';
      next = '<div class="clk-sub small">До рангу «' + esc(rankName(st, n.rank)) + '»</div>'
        + row('обпалено виробів', n.fired.have, n.fired.need)
        + row('покладено на вози', n.given.have, n.given.need)
        + (n.styles.need ? row('розписів у колекції', n.styles.have, n.styles.need) : '')
        + '<div class="clkg-piece' + (p.have ? ' have' : '') + '">'
        + api.wareSvg(p.ware, { style: p.style || 'kosiv', quality: 3, cls: 'clkg-big', slot: 'piece' })
        + '<div><b>Майстерштук</b><span class="small">' + esc(pieceText) + '</span>'
        + '<span class="muted small">' + (p.have ? '✓ лежить у коморі' : 'виліпи, обпали дзвінким — і принеси цехові') + '</span></div></div>'
        + (perk ? '<div class="muted small">Перк: ' + esc(perk) + '</div>' : '')
        + '<button type="button" class="primary clkg-master"' + (st.mine && n.ready && p.have ? '' : ' disabled') + '>🎓 Здати майстерштук</button>';
    }
    const perks = [];
    if (v.rank >= 1) {
      perks.push('<label class="clkg-auto"><input type="checkbox" class="clkg-autobox"' + (v.autoOff ? '' : ' checked') + (st.mine ? '' : ' disabled') + '> '
        + '🔥 Автогорно: підмайстри самі палять повну сушарню (якість звичайна)</label>');
    }
    if (v.rank >= 2) perks.push('<div class="small">🏺 +' + v.kilnSlots + ' місця в горні · наступний виріб відкривається на щабель раніше</div>');
    if (v.rank >= 3) perks.push('<div class="small">🐴 Нагорода воза ×1,5 · титул «Цехмістр» у похвалах і в хаті</div>');
    return '<section class="clkg-card">'
      + '<div class="clk-sub">' + RANK_ICON[v.rank] + ' Ранг у цеху: ' + esc(rankName(st, v.rank)) + '</div>'
      + '<div class="clkg-path">' + path + '</div>'
      + (perks.length ? '<div class="clkg-perks">' + perks.join('') + '</div>' : '')
      + next
      + '</section>';
  }

  // ---------- гончарі й хата ----------

  function rosterHtml(st, api) {
    const esc = (x) => api.esc(st, x);
    const r = st.guildRoster;
    if (!r) return '<section class="clkg-card"><div class="clk-sub">👥 Гончарі цеху</div><div class="muted small">Кличемо гончарів…</div></section>';
    const rows = r.potters.length
      ? r.potters.map((p) => '<div class="clkg-potter' + (p.me ? ' me' : '') + '">'
        + '<span class="clkg-pico">' + RANK_ICON[p.rank] + '</span>'
        + '<div class="clkg-pname"><b>' + esc(p.nick) + (p.me ? ' <span class="muted small">(ти)</span>' : '') + '</b>'
        + '<span class="muted small">' + esc(rankName(st, p.rank)) + (p.gave ? ' · на возі ' + p.gave : '') + '</span></div>'
        + '<button type="button" class="ghost small" data-house="' + esc(p.nick) + '">🏠 Зазирнути в хату</button></div>').join('')
      : '<div class="muted small">Поки нікого — зайди пізніше.</div>';
    return '<section class="clkg-card"><div class="clk-sub">👥 Гончарі цеху <span class="muted small">· ' + r.potters.length + '</span></div>' + rows + '</section>';
  }

  function loadRoster(st, api, force) {
    if (!force && Date.now() - (st.guildRosterAt || 0) < ROSTER_MS) return;
    st.guildRosterAt = Date.now();
    fetch('/api/games/clicker/guild', { headers: headers(st) })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d || !Array.isArray(d.potters) || !st.guildPane) return;
        st.guildRoster = d;
        paint(st, api);
        if (st.guildOv === 'gift') renderPicker(st, api);
      })
      .catch(() => { /* без списку просто не буде кому дарувати — спробуємо за хвилину */ });
  }

  /// Хата друга: стіни, піч, полиця з найкращими виробами, полиця дарунків, прикраси, знаряддя, емблеми драбини.
  function friendHouseSvg(st, api, h) {
    const esc = (x) => api.esc(st, x);
    const ware = (w, style, q, x, y, s, slot) => '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')">'
      + api.wareSvg(w, { style, quality: q, slot, wrap: false }) + '</g>';
    let s = '<rect width="360" height="300" fill="#2a1d14"/>'
      + '<rect y="236" width="360" height="64" fill="#3a2a1c"/><path d="M0 236h360" stroke="#5c4530" stroke-width="3"/>'
      + '<path d="M0 0h360v10H0z" fill="#1d140e"/>';
    // Вікно з калиною, якщо є; інакше — просто вікно.
    s += '<rect x="138" y="26" width="84" height="62" rx="4" fill="#0c1a2b" stroke="#6b4423" stroke-width="4"/><path d="M180 26v62M138 57h84" stroke="#6b4423" stroke-width="3"/>';
    if (h.decor.includes('window')) s += '<g fill="#d7372b"><circle cx="150" cy="80" r="3"/><circle cx="156" cy="84" r="3"/><circle cx="153" cy="76" r="2.6"/></g>';
    if (h.decor.includes('icon')) s += '<rect x="20" y="24" width="34" height="42" rx="3" fill="#5a3a1a" stroke="#d9a92f" stroke-width="1.5"/><circle cx="37" cy="40" r="7" fill="#f4c542" opacity=".9"/>';
    if (h.decor.includes('towel')) s += '<path d="M232 20h40v46l-20 8-20-8z" fill="#f4efe3"/><path d="M236 46h32M236 52h32" stroke="#d7372b" stroke-width="2"/>';
    // Полиця найкращих виробів.
    s += '<path d="M16 150h150" stroke="#6b4423" stroke-width="5"/>';
    (h.best || []).forEach((b, i) => { s += ware(b.ware, b.style, b.q, 20 + i * 48, 150 - 86 * 0.5, 0.5, 'fhb-' + i); });
    if (!(h.best || []).length) s += '<text x="90" y="140" font-size="10" fill="#b8a38a" text-anchor="middle">комора порожня</text>';
    // Полиця дарунків.
    s += '<path d="M190 150h156" stroke="#6b4423" stroke-width="5"/>';
    (h.gifts || []).slice(0, 6).forEach((g, i) => { s += ware(g.ware, g.style, g.q, 192 + i * 26, 150 - 86 * 0.3, 0.3, 'fhg-' + i); });
    s += '<text x="268" y="164" font-size="9" fill="#b8a38a" text-anchor="middle">полиця дарунків' + ((h.gifts || []).length ? ' · ' + h.gifts.length : '') + '</text>';
    // Піч і коло.
    s += '<path d="M280 236v-50q0-18 30-18t30 18v50z" fill="#8f6d4b" stroke="#5c4530" stroke-width="1.5"/><rect x="298" y="200" width="24" height="26" rx="4" fill="#2a1508"/>'
      + '<path class="clkg-fire" d="M310 224c-7-6-5-14 0-19 2 5 5 5 3 11 3-3 4-6 3-11 6 6 5 15-6 19z" fill="#ff8a3d"/>';
    s += '<ellipse cx="180" cy="232" rx="42" ry="8" fill="#1b1310"/><rect x="176" y="200" width="8" height="30" fill="#3a2a1c"/>';
    s += ware('jug', h.wear || '', 1, 180 - 50 * 0.55, 200 - 86 * 0.55 + 6, 0.55, 'fhw');
    if (h.decor.includes('rooster')) s += '<text x="40" y="228" font-size="22">🐓</text>';
    if (h.decor.includes('dog')) s += '<text x="226" y="262" font-size="22">🐕</text>';
    if (h.decor.includes('chest')) s += '<rect x="16" y="244" width="54" height="34" rx="5" fill="#8b3a22" stroke="#4a1e10"/><circle cx="43" cy="262" r="4" fill="#f2c230"/>';
    // Знаряддя на стіні.
    (h.tools || []).forEach((t, i) => { s += '<text x="' + (18 + (i % 5) * 24) + '" y="' + (96 + Math.floor(i / 5) * 24) + '" font-size="16">' + (TOOL_ICON[t] || '🔧') + '</text>'; });
    // Емблеми драбини праворуч.
    (h.ladder || []).filter((l) => TIER_EMBLEM[l.key]).forEach((l, i) => {
      s += '<text x="' + (244 + (i % 5) * 22) + '" y="' + (100 + Math.floor(i / 5) * 24) + '" font-size="15"><title>' + esc(l.name + ' · ' + l.level) + '</title>' + TIER_EMBLEM[l.key] + '</text>';
    });
    // Табличка з рангом.
    const title = (h.rank >= 3 ? 'Цехмістр ' : '') + h.nick;
    s += '<rect x="96" y="276" width="168" height="20" rx="4" fill="#6b4423"/><text x="180" y="290" font-size="11" fill="#f4efe3" text-anchor="middle">'
      + RANK_ICON[h.rank] + ' ' + esc(title) + '</text>';
    return '<svg class="clkg-house" viewBox="0 0 360 300" role="img" aria-label="Хата гончаря ' + esc(h.nick) + '">' + s + '</svg>';
  }

  function openHouse(st, api, nick) {
    const body = api.overlay(st, '<div class="clk-sub">🏠 Хата: ' + api.esc(st, nick) + '</div><div class="muted small">Відчиняємо двері…</div>', { cls: 'clkg-ov' });
    fetch('/api/games/clicker/house?nick=' + encodeURIComponent(nick), { headers: headers(st) })
      .then((r) => r.json().catch(() => null).then((d) => ({ ok: r.ok, d })))
      .then(({ ok, d }) => {
        if (!body.isConnected) return;
        if (!ok || !d) { body.innerHTML = '<div class="clk-sub">🏠 ' + api.esc(st, nick) + '</div><div class="muted">' + api.esc(st, (d && d.message) || 'Хата зачинена') + '</div>'; return; }
        const esc = (x) => api.esc(st, x);
        const stat = (label, val) => '<span class="clkg-stat"><b>' + val + '</b><i>' + label + '</i></span>';
        body.innerHTML = '<div class="clk-sub">🏠 Хата: ' + esc(d.nick) + ' <span class="muted small">· ' + esc(rankName(st, d.rank)) + '</span></div>'
          + friendHouseSvg(st, api, d)
          + '<div class="clkg-stats">'
          + stat('глеків за весь час', api.potsShort(d.total))
          + stat('виліплено', api.short(d.formed))
          + stat('обпалено', api.short(d.fired))
          + stat('розписів', d.styles.length)
          + (d.album != null ? stat('клітинок альбому', d.album) : '')
          + (d.tiles != null ? stat('кахлів у печі', d.tiles) : '')
          + stat('клейм', d.stamps)
          + '</div>'
          + (d.gifts.length ? '<div class="muted small">Дарунки від: ' + esc([...new Set(d.gifts.map((g) => g.from))].join(', ')) + '</div>' : '')
          + (d.nick.toLowerCase() !== myNick(st).toLowerCase()
            ? '<div class="muted small">Хата — зі збереження; живе коло друга може бути трохи новішим.</div>' : '');
      })
      .catch(() => { if (body.isConnected) body.innerHTML = '<div class="muted">Не вийшло зазирнути — спробуй ще</div>'; });
  }

  // ---------- вибір виробу (віз, дарунок, похвала) ----------

  function itemRow(st, api, it, i, buttons) {
    const esc = (x) => api.esc(st, x);
    return '<div class="clkw-item q' + it.q + '">'
      + api.wareSvg(it.ware, { style: it.style, quality: it.q, cls: 'clkw-mid', slot: 'gp-' + i })
      + '<div class="clkw-itxt"><b>' + esc(wareName(st, it.ware)) + ' <span class="clkw-n">×' + it.n + '</span></b>'
      + '<span class="muted small">' + esc(styleName(st, it.style)) + ' · <span class="clkw-q">' + STARS[it.q] + ' ' + QUALITY[it.q] + '</span></span></div>'
      + '<div class="clkw-btns">' + buttons + '</div></div>';
  }

  function openPicker(st, api, mode) {
    st.guildOv = mode;
    renderPicker(st, api);
  }

  function renderPicker(st, api) {
    const mode = st.guildOv;
    const v = st.guild;
    if (!mode || !v) return;
    const esc = (x) => api.esc(st, x);
    const items = itemsOf(st);
    let head;
    let rows;
    if (mode === 'give') {
      const w = v.week;
      head = '<div class="clk-sub">🧺 Покласти на віз</div><p class="muted small clk-note">Потрібні: '
        + w.subs.map((s) => esc(wareName(st, s.ware)) + ' ' + Math.min(s.have, s.need) + '/' + s.need).join(' · ')
        + '. Будь-який розпис і якість рахуються; на воза краще класти простіші — дзвінкі згодяться для майстерштука.</p>';
      rows = items.map((it, i) => itemRow(st, api, it, i,
        '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="1">+1</button>'
        + (it.n >= 5 ? '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="5">+5</button>' : '')
        + (it.n > 1 ? '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="' + it.n + '">усі ' + it.n + '</button>' : ''))).join('');
    } else if (mode === 'brag') {
      const wait = (Date.parse(v.bragAt) || 0) - api.serverNow(st);
      head = '<div class="clk-sub">🏺 Похвалитись у Журнал</div><p class="muted small clk-note">Усе село прочитає, чим ти пишаєшся. '
        + 'Раз на 15 хвилин; виріб лишається в тебе.' + (wait > 0 ? ' Наступна похвала за ' + api.mmss(wait) + '.' : '') + '</p>';
      const best = items.slice().sort((a, b) => b.q - a.q || (b.style ? 1 : 0) - (a.style ? 1 : 0));
      rows = best.map((it, i) => itemRow(st, api, it, i, '<button type="button" class="ghost small" data-brag="' + esc(it.key) + '"' + (wait > 0 ? ' disabled' : '') + '>Похвалитись</button>')).join('');
    } else {
      const roster = (st.guildRoster && st.guildRoster.potters) || [];
      const friends = roster.filter((p) => !p.me);
      if (!st.guildGiftTo || !friends.some((f) => f.nick === st.guildGiftTo)) st.guildGiftTo = friends.length ? friends[0].nick : '';
      head = '<div class="clk-sub">🎁 Дарунок другові</div><p class="muted small clk-note">Виріб поїде на полицю дарунків у хаті друга з підписом від тебе. '
        + 'Сьогодні ще ' + v.gifts.left + '.</p>'
        + (friends.length
          ? '<div class="clkg-friends">' + friends.map((f) => '<button type="button" class="ghost small' + (f.nick === st.guildGiftTo ? ' active' : '') + '" data-to="' + esc(f.nick) + '">'
            + RANK_ICON[f.rank] + ' ' + esc(f.nick) + '</button>').join('') + '</div>'
          : '<div class="muted">У цеху поки нікого, крім тебе, — дарувати нікому.</div>');
      rows = friends.length
        ? items.map((it, i) => itemRow(st, api, it, i, '<button type="button" class="ghost small" data-gift="' + esc(it.key) + '"' + (v.gifts.left > 0 ? '' : ' disabled') + '>Подарувати</button>')).join('')
        : '';
    }
    const html = head + (items.length ? '<div class="clkw-items clkg-pick">' + rows + '</div>' : '<div class="clk-teaser muted small">Комора порожня — спершу обпали щось у горні.</div>');
    let body;
    if (api.overlayOpen(st) && st.guildOvBody && st.guildOvBody.isConnected) {
      body = st.guildOvBody;
      if (body._sig === html) return;
      body._sig = html;
      body.innerHTML = html;
    } else {
      body = api.overlay(st, html, { cls: 'clkg-ov', onClose: () => { st.guildOv = null; st.guildOvBody = null; } });
      body._sig = html;
      st.guildOvBody = body;
    }
    for (const b of body.querySelectorAll('[data-give]')) {
      b.onclick = (e) => {
        if (!e.isTrusted) return;
        act(st, api, { op: 'give', key: b.dataset.give, n: +b.dataset.n }, 'wagon').then((r) => { if (r && r.ok) st.guildRoll = Date.now(); });
      };
    }
    for (const b of body.querySelectorAll('[data-brag]')) {
      b.onclick = (e) => { if (e.isTrusted) act(st, api, { op: 'brag', key: b.dataset.brag }, 'brag').then((r) => { if (r && r.ok) api.closeOverlay(st); }); };
    }
    for (const b of body.querySelectorAll('[data-to]')) b.onclick = () => { st.guildGiftTo = b.dataset.to; renderPicker(st, api); };
    for (const b of body.querySelectorAll('[data-gift]')) {
      b.onclick = (e) => {
        if (!e.isTrusted || !st.guildGiftTo) return;
        act(st, api, { op: 'gift', nick: st.guildGiftTo, key: b.dataset.gift }, 'gift');
      };
    }
  }

  // ---------- вкладка ----------

  function paint(st, api) {
    const v = st.guild;
    if (!st.guildBody || !v) return;
    if (!v.enabled) {
      api.swap(st.guildBody, '<div class="clk-teaser muted">🔒 Цех зараз зачинений. Твій ранг — ' + api.esc(st, rankName(st, v.rank || 0)) + '.</div>');
      return;
    }
    const html = '<div class="muted small clkg-intro">Цех — це всі гончарі сайту разом: один віз на тиждень, дарунки одне одному, хати в гості. '
      + 'Тут ніхто не перший і не останній — пропущений тиждень нічого не забирає.</div>'
      + wagonHtml(st, api, v) + giftsHtml(st, api, v) + rankHtml(st, api, v) + rosterHtml(st, api);
    if (!api.swap(st.guildBody, html)) return;
    const q = (sel) => st.guildBody.querySelector(sel);
    const give = q('.clkg-give');
    if (give) give.onclick = () => openPicker(st, api, 'give');
    for (const b of st.guildBody.querySelectorAll('[data-claim]')) {
      b.onclick = (e) => {
        if (!e.isTrusted) return;
        b.disabled = true;
        act(st, api, { op: 'claim', week: b.dataset.claim }, 'wagon').then((r) => {
          if (r && r.ok) api.sparks(st, null, 16, true, 50, 40);
          else b.disabled = false;
        });
      };
    }
    const gifting = q('.clkg-gifting');
    if (gifting) gifting.onclick = () => { loadRoster(st, api, true); openPicker(st, api, 'gift'); };
    const bragging = q('.clkg-bragging');
    if (bragging) bragging.onclick = () => openPicker(st, api, 'brag');
    const master = q('.clkg-master');
    if (master) master.onclick = (e) => { if (e.isTrusted) act(st, api, { op: 'masterpiece' }, null); };
    const auto = q('.clkg-autobox');
    if (auto) auto.onchange = () => act(st, api, { op: 'auto', on: auto.checked }, null);
    for (const b of st.guildBody.querySelectorAll('[data-house]')) b.onclick = () => openHouse(st, api, b.dataset.house);
    st.guildCds = [...st.guildBody.querySelectorAll('.clkg-cd, .clkg-bragcd')];
    // Щойно поклали — колеса віза крутнуться.
    if (Date.now() - (st.guildRoll || 0) < 4000) {
      const svg = q('.clkg-wagon');
      if (svg) svg.classList.add('roll');
    }
    countdowns(st, api);
  }

  function countdowns(st, api) {
    const now = api.serverNow(st);
    for (const el of st.guildCds || []) {
      const at = +el.dataset.at;
      const t = el.classList.contains('clkg-bragcd') ? (at > now ? 'похвала за ' + api.mmss(at - now) : '') : left(api, at - now);
      if (el.textContent !== t) el.textContent = t;
    }
  }

  /// Нові дарунки на полиці й новий ранг — тост, звук, іскри (раз: пам'ятаємо в localStorage).
  function celebrate(st, api, v) {
    const seen = +api.storeGet('clk.guild.giftAt', '0') || 0;
    const fresh = v.shelf.filter((g) => (Date.parse(g.at) || 0) > seen);
    if (fresh.length) {
      api.storeSet('clk.guild.giftAt', String(Math.max(...fresh.map((g) => Date.parse(g.at) || 0))));
      // Перший вид після встановлення — не тостимо всю полицю, лише запам'ятовуємо.
      if (seen > 0) {
        const g = fresh[0];
        api.toast(st, '🎁 Дарунок від ' + g.from + ': ' + QUALITY[g.q] + ' ' + wareName(st, g.ware).toLowerCase()
          + (fresh.length > 1 ? ' і ще ' + (fresh.length - 1) : ''), 'ok');
        api.sfx('gift');
      }
    }
    if (st.guildRank != null && v.rank > st.guildRank) {
      api.sfx('rank-up');
      api.sparks(st, null, 24, true, 50, 45);
      api.popAt(st, RANK_ICON[v.rank] + ' ' + rankName(st, v.rank) + '!', 'big', 50, 30);
    }
    st.guildRank = v.rank;
  }

  HClicker.part({
    id: 'guild',
    order: 60,

    mount(st, api) {
      st.guildPane = api.tab(st, 'guild', 'Цех', 60);
      st.guildBody = document.createElement('div');
      st.guildBody.className = 'clkg';
      st.guildPane.appendChild(st.guildBody);
      st.guild = null;
      st.guildRank = null;
      st.guildRoster = null;
      st.guildRosterAt = 0;
      st.guildCds = [];
      st.guildOv = null;
    },

    update(st, v, api) {
      const g = v.guild;
      if (!g) return;
      st.guild = {
        enabled: !!g.enabled, rank: g.rank || 0, week: g.week || null, prev: g.prev || null, claims: g.claims || [],
        gifts: g.gifts || { left: 0, sent: 0, got: 0 }, shelf: g.shelf || [], given: g.given || 0, next: g.next || null,
        autoKiln: !!g.autoKiln, autoOff: !!g.autoOff, kilnSlots: g.kilnSlots || 0, bragAt: g.bragAt || null,
      };
      if (st.guild.enabled && !st.guild.week) st.guild.enabled = false;
      if (st.guild.enabled) celebrate(st, api, st.guild);
      const claim = st.guild.claims.length;
      api.tabLabel(st, 'guild', claim ? 'Цех · 🛒' : 'Цех');
      paint(st, api);
      if (st.guildOv) renderPicker(st, api);
    },

    slow(st, api) {
      if (st.tab !== 'guild' || !st.guild || !st.guild.enabled) return;
      loadRoster(st, api, false);
      countdowns(st, api);
    },

    unmount(st) {
      st.guildPane = null;
      st.guildBody = null;
      st.guildOv = null;
      st.guildOvBody = null;
    },
  });
})();
