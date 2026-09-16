/*
  Альбом майстра Гончарного кола (docs/games/specs/clicker-v7.md, пакет B3; як зроблено — clicker-v7-album.md).

  Що тут:
  1) вкладка «Альбом · 43/108»: зведення з кільцями прогресу й сумою бонусу;
  2) «Розписи з осередків» — вузол вкладки «Розписи» з ядра переїжджає сюди (купівля paint і «на колі» wear лишаються
     ядрові), а сама вкладка ховається;
  3) сітка «виріб × розпис» 12 × 9 з тінями невідкритих, зірками дзвінких і майстерністю в заголовку рядка; тап —
     картка з фактом про виріб і осередок; нова клітинка спалахує;
  4) кахляна піч (SVG): 12 гнізд, кахлі в їхніх розписах, вставити кахлю з комори (album { op: "tile" });
     та сама піч маленька в хаті (шар back);
  5) трипільський музей: вісім вітрин, уламки, «склеїти» (album { op: "glue" }); знахідка — «🏺 знахідка!» над сценою.
  Правила рахує сервер (view.album); тексти фактів — у каталозі (catalog.album).
*/
(() => {
  const COLS = 9;
  const FULL_ROW = (1 << COLS) - 1;
  const MASTERY_AT = [5, 15, 40, 100, 250, 600, 1500, 4000, 10000, 25000];
  const RING_R = 16;
  const RING_LEN = 2 * Math.PI * RING_R;
  const QUALITY = ['', 'звичайний', 'добрий', 'дзвінкий'];
  const pc = (x) => { let n = 0; for (x >>>= 0; x; x &= x - 1) n++; return n; };
  const pct = (api, x) => api.dec(x * 100) + ' %';

  // ---------- трипільські речі (SVG 60×60) ----------

  const FIND_SVG = {
    spiral: '<path d="M8 22l14-12 20 2 12 14-4 22-18 6-20-8z" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<path d="M31 32c0-3 4-3 4 0 0 5-8 5-8 0 0-7 12-7 12 0 0 9-16 9-16 0 0-11 20-11 20 0" stroke="#2a1a12" stroke-width="2" fill="none"/>'
      + '<path d="M12 20l8-6M44 46l6-6" stroke="#f1e4cc" stroke-width="1.2"/>',
    binocular: '<path d="M10 14h14l-2 36h-10zM36 14h14l-2 36h-10z" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<rect x="22" y="26" width="16" height="7" fill="#c97a40" stroke="#7a3f1c" stroke-width="1"/>'
      + '<path d="M12 22h10M38 22h10M13 32q4-4 8 0M39 32q4-4 8 0M13 42h8M39 42h8" stroke="#2a1a12" stroke-width="1.6" fill="none"/>',
    figurine: '<circle cx="30" cy="11" r="5" fill="#d9884a" stroke="#7a3f1c"/>'
      + '<path d="M26 16h8l3 10 9 10-6 16H20l-6-16 9-10z" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<path d="M22 38q8 6 16 0M25 44l5 4 5-4" stroke="#2a1a12" stroke-width="1.4" fill="none"/><circle cx="30" cy="30" r="1.5" fill="#2a1a12"/>',
    house: '<path d="M8 52h44l-3-6H11z" fill="#8b5a33"/><path d="M12 46V28l18-14 18 14v18z" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<path d="M8 30l22-18 22 18" stroke="#7a3f1c" stroke-width="3" fill="none" stroke-linecap="round"/>'
      + '<circle cx="30" cy="36" r="5" fill="#3a2014"/><path d="M16 30h6M38 30h6M16 42h6M38 42h6" stroke="#2a1a12" stroke-width="1.4"/>',
    grain: '<ellipse cx="30" cy="30" rx="24" ry="20" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<ellipse cx="30" cy="30" rx="18" ry="14" fill="#e9b27e"/>'
      + '<path d="M30 44V18M30 24l-5-4M30 24l5-4M30 30l-6-4M30 30l6-4M30 36l-6-4M30 36l6-4" stroke="#2a1a12" stroke-width="1.8" fill="none" stroke-linecap="round"/>',
    krater: '<path d="M8 8h44l-8 18c6 6 6 18-2 24l-4 4H22l-4-4c-8-6-8-18-2-24z" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<path d="M12 12h36M17 30h26M16 40h28" stroke="#2a1a12" stroke-width="1.6"/>'
      + '<path d="M20 35c0-3 4-3 4 0M36 35c0-3 4-3 4 0" stroke="#f1e4cc" stroke-width="1.3" fill="none"/>',
    ladle: '<ellipse cx="22" cy="38" rx="15" ry="11" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<ellipse cx="22" cy="37" rx="10" ry="6" fill="#8b4a25"/>'
      + '<path d="M34 31l20-20" stroke="#d9884a" stroke-width="6" stroke-linecap="round"/><path d="M34 31l20-20" stroke="#7a3f1c" stroke-width="1" />',
    whorl: '<ellipse cx="30" cy="34" rx="22" ry="12" fill="#b86b36"/><ellipse cx="30" cy="30" rx="22" ry="12" fill="#d9884a" stroke="#7a3f1c" stroke-width="1.2"/>'
      + '<ellipse cx="30" cy="30" rx="4" ry="2.4" fill="#2a1a12"/>'
      + '<path d="M13 27l6 3M47 27l-6 3M30 19v5M22 38l3-4M38 38l-3-4" stroke="#2a1a12" stroke-width="1.5"/>',
  };

  const findSvg = (key, cls) => '<svg class="' + (cls || '') + '" viewBox="0 0 60 60" aria-hidden="true">' + (FIND_SVG[key] || '') + '</svg>';

  // ---------- стан ----------

  const cat = (st) => (st.catalog && st.catalog.album) || null;
  const wareList = (st) => (st.catalog && st.catalog.wares) || (st.craft && st.craft.wares) || [];
  const styleKeys = (st) => [''].concat(((st.catalog && st.catalog.styles) || st.styleList || []).map((s) => s.key));
  const styleName = (st, key) => {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const wareName = (st, key) => {
    const w = wareList(st).find((x) => x.key === key);
    return w ? w.name : key;
  };
  const firedOf = (st, key) => {
    const w = st.craft && st.craft.wares.find((x) => x.key === key);
    return (w && w.fired) || 0;
  };

  /// Частка до наступного рівня майстерності: від попереднього порога до наступного.
  function masteryProgress(st, key, level) {
    const at = (cat(st) && cat(st).masteryAt) || MASTERY_AT;
    if (level >= at.length) return 1;
    const from = level ? at[level - 1] : 0;
    return Math.max(0, Math.min(1, (firedOf(st, key) - from) / (at[level] - from)));
  }

  const ring = (p, cls) => '<svg class="clka-ring ' + (cls || '') + '" viewBox="0 0 40 40" aria-hidden="true">'
    + '<circle class="bg" cx="20" cy="20" r="' + RING_R + '"/>'
    + '<circle class="fg" cx="20" cy="20" r="' + RING_R + '" stroke-dasharray="' + RING_LEN.toFixed(1) + '" stroke-dashoffset="'
    + (RING_LEN * (1 - Math.max(0, Math.min(1, p)))).toFixed(1) + '"/></svg>';

  // ---------- кахлі й піч ----------

  /// Кахля в розписі: квадрат тіла розпису з орнаментом глечика (пояс 34…66 × 33…65), перенесений у (x, y) розміром size.
  function tileSvg(api, style, q, x, y, size, clip) {
    const S = api.STYLE[style] || api.STYLE[''];
    const k = size / 32;
    return '<g transform="translate(' + x + ' ' + y + ') scale(' + k.toFixed(4) + ') translate(-34 -33)">'
      + '<rect x="34" y="33" width="32" height="32" rx="1.5" fill="' + S.body + '"/>'
      + '<g clip-path="url(#' + clip + ')">' + S.decor + '</g>'
      + '<rect x="35" y="34" width="30" height="30" rx="1" fill="none" stroke="rgba(0,0,0,.28)" stroke-width="1.2"/>'
      + '<path d="M36 36h28" stroke="rgba(255,255,255,.22)" stroke-width="1"/>'
      + (q >= 3 ? '<rect x="34.4" y="33.4" width="31.2" height="31.2" rx="1.5" fill="none" stroke="#f4c542" stroke-width=".9"/>' : '')
      + '</g>';
  }

  /// Кахляна піч-груба: цоколь, 4 × 3 гнізда, карниз, комин. id — щоб у хаті й у вкладці clipPath не збігались.
  function stoveSvg(api, stove, id, cls) {
    const clip = 'clka-tclip-' + id;
    let s = '<svg class="' + (cls || '') + '" viewBox="0 0 220 300" aria-hidden="true">'
      + '<defs><clipPath id="' + clip + '"><rect x="34" y="33" width="32" height="32"/></clipPath>'
      + '<linearGradient id="clka-glow-' + id + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffb35c"/><stop offset="1" stop-color="#c2451c"/></linearGradient></defs>'
      // комин до стелі
      + '<path d="M84 0h52v38H84z" fill="#b9a58c"/><path d="M84 12h52M84 26h52" stroke="rgba(0,0,0,.15)"/>'
      // карниз
      + '<path d="M14 38h192l-8 16H22z" fill="#8a5a3a"/><path d="M18 46h184" stroke="rgba(255,255,255,.18)" stroke-width="2"/>'
      // тіло
      + '<rect x="24" y="54" width="172" height="196" fill="#6b4630"/>';
    for (let i = 0; i < 12; i++) {
      const cx = 30 + (i % 4) * 41;
      const cy = 60 + Math.floor(i / 4) * 41;
      const t = stove[i];
      s += t
        ? '<g class="clka-tile" data-i="' + i + '">' + tileSvg(api, t.style, t.q, cx, cy, 38, clip) + '</g>'
        : '<rect x="' + (cx + 1) + '" y="' + (cy + 1) + '" width="36" height="36" rx="2" fill="#3b2619" stroke="rgba(255,255,255,.18)" stroke-dasharray="3 3"/>';
    }
    const full = stove.length >= 12;
    // челюсті печі: жар, коли піч повна
    s += '<rect x="24" y="186" width="172" height="64" fill="#5a3a26"/>'
      + '<path d="M86 246v-30q0-18 24-18t24 18v30z" fill="' + (full ? 'url(#clka-glow-' + id + ')' : '#21140c') + '"/>'
      + (full ? '<path class="clka-fire" d="M110 244c-12-6-10-20 0-28 1 8 7 8 5 16 5-4 7-8 5-14 9 9 8 20-10 26z" fill="#ffe28a"/>' : '')
      + '<path d="M40 200h36M40 214h36M40 228h36M144 200h36M144 214h36M144 228h36" stroke="rgba(0,0,0,.25)" stroke-width="2"/>'
      // цоколь
      + '<path d="M16 250h188v18H16z" fill="#8a5a3a"/><path d="M8 268h204v14H8z" fill="#6b4630"/>'
      + '</svg>';
    return s;
  }

  // ---------- малювання вкладки ----------

  function paintHead(st, api) {
    const a = st.album;
    const moveTiles = a.stove.length;
    const found = pc(a.finds);
    const lv = a.mastery.reduce((x, y) => x + y, 0);
    // На першому екрані — одне число, яке справді щось каже; чотири кільця — за ▾ для тих, кому цікаво.
    const html = '<div class="clka-bonus"><b>+' + pct(api, a.bonus) + '</b><span class="muted small">до всього від альбому · клейма його не спалюють</span></div>'
      + '<details class="clka-ringsec"><summary>з чого це складається</summary>'
      + '<div class="clka-rings">'
      + '<div class="clka-rg">' + ring(a.open / a.size) + '<span><b>' + a.open + '/' + a.size + '</b><i class="muted">клітинок</i></span></div>'
      + '<div class="clka-rg">' + ring(moveTiles / 12, 'tile') + '<span><b>' + moveTiles + '/12</b><i class="muted">кахлів</i></span></div>'
      + '<div class="clka-rg">' + ring(found / 8, 'museum') + '<span><b>' + found + '/8</b><i class="muted">знахідок</i></span></div>'
      + '<div class="clka-rg">' + ring(lv / 120, 'mastery') + '<span><b>' + lv + '/120</b><i class="muted">майстерність</i></span></div>'
      + '</div></details>';
    api.swap(st.albumUi.head, html);
  }

  function paintGrid(st, api) {
    const a = st.album;
    const esc = (x) => api.esc(st, x);
    const wares = wareList(st);
    const keys = styleKeys(st);
    if (!wares.length) return;
    const sig = a.cells.join(',') + '|' + a.stars.join(',') + '|' + a.mastery.join(',') + '|'
      + wares.map((w) => Math.floor(masteryProgress(st, w.key, a.mastery[wares.indexOf(w)] || 0) * 20)).join(',') + '|' + keys.length
      + '|' + [...st.albumFlash].join(',');
    if (st.albumUi.grid._sig === sig) return;
    st.albumUi.grid._sig = sig;
    let h = '<div class="clka-scroll"><table class="clka-grid"><thead><tr><th class="clka-corner"><span class="small muted">виріб \\ розпис</span></th>';
    keys.forEach((k, c) => {
      const full = a.cells.every((m) => (m >> c) & 1);
      h += '<th class="clka-colh' + (full ? ' full' : '') + '" title="' + esc(styleName(st, k)) + (full ? ' — стовпчик зібрано, +3 %' : '') + '">'
        + api.jugSvg(k, 'clka-colj', 'alb-h-' + (k || 'plain'), '') + '</th>';
    });
    h += '</tr></thead><tbody>';
    wares.forEach((w, r) => {
      const mask = a.cells[r] || 0;
      const lvl = a.mastery[r] || 0;
      const p = masteryProgress(st, w.key, lvl);
      const full = (mask & FULL_ROW) === FULL_ROW;
      h += '<tr class="' + (full ? 'full' : '') + '"><th class="clka-rowh" title="Майстерність ' + lvl + ' з 10 · обпалено ' + api.num(firedOf(st, w.key)) + '"><div class="clka-rh">'
        + '<span class="clka-wn">' + esc(w.name) + '</span>'
        + '<span class="clka-lv' + (lvl >= 10 ? ' max' : '') + '">' + (lvl >= 10 ? '★10' : 'м' + lvl) + '</span>'
        + '<i class="clka-mbar"><i style="width:' + Math.round(p * 100) + '%"></i></i></div></th>';
      keys.forEach((k, c) => {
        const open = (mask >> c) & 1;
        const star = (a.stars[r] >> c) & 1;
        const flash = st.albumFlash.has(r * COLS + c);
        h += '<td><button type="button" class="clka-cell' + (open ? ' open' : '') + (star ? ' star' : '') + (flash ? ' new' : '') + '" data-r="' + r + '" data-c="' + c
          + '" aria-label="' + esc(w.name + ', ' + styleName(st, k) + (open ? '' : ' — ще не обпалено')) + '">'
          + (open
            ? api.wareSvg(w.key, { style: k, quality: star ? 3 : 1, cls: 'clka-w', slot: 'alb-' + r + '-' + c })
            : api.wareSvg(w.key, { style: '', quality: 1, cls: 'clka-w shadow', slot: 'alb-s-' + r }))
          + (star ? '<i class="clka-star">★</i>' : '') + '</button></td>';
      });
      h += '</tr>';
    });
    h += '</tbody></table></div>';
    st.albumUi.grid.innerHTML = h;
    for (const b of st.albumUi.grid.querySelectorAll('.clka-cell')) b.onclick = () => openCell(st, api, +b.dataset.r, +b.dataset.c);
  }

  function paintStove(st, api) {
    const a = st.album;
    const esc = (x) => api.esc(st, x);
    const tiles = ((st.craft && st.craft.items) || []).filter((it) => it.ware === 'tile' && it.q >= 2);
    const left = 12 - a.stove.length;
    const sig = JSON.stringify(a.stove) + '|' + tiles.map((t) => t.key + t.n).join(',') + '|' + st.mine;
    if (st.albumUi.stove._sig === sig) return;
    st.albumUi.stove._sig = sig;
    const pick = left <= 0
      ? '<div class="clka-done small">🔥 Піч склала вся — +17 % до всього і тепло на всю хату.</div>'
      : tiles.length
        ? '<div class="clka-tiles">' + tiles.map((t) => '<button type="button" class="ghost clka-tilebtn" data-key="' + esc(t.key) + '"' + (st.mine ? '' : ' disabled') + '>'
          + '<svg class="clka-tmini" viewBox="0 0 40 40" aria-hidden="true"><defs><clipPath id="clka-tclip-b-' + esc(t.key.replace(/\|/g, '-')) + '"><rect x="34" y="33" width="32" height="32"/></clipPath></defs>'
          + tileSvg(api, t.style, t.q, 1, 1, 38, 'clka-tclip-b-' + t.key.replace(/\|/g, '-')) + '</svg>'
          + '<span><b>' + esc(styleName(st, t.style)) + '</b><i class="muted small">' + QUALITY[t.q] + ' · ×' + t.n + '</i></span><span class="clk-price done">у піч</span></button>').join('') + '</div>'
        : '<div class="clk-teaser muted small">Добрих кахлів у коморі нема. Кахля відкривається на 500 млн глеків за весь час; обпали її якості '
          + '«добра» чи «дзвінка» — і вставляй сюди. Звичайну піч не приймає: гості ж дивляться.</div>';
    const html = '<div class="clk-sub">Кахляна піч · ' + a.stove.length + ' з 12<span class="muted small"> · кожна кахля +1 %, уся піч ще +5 %</span></div>'
      + '<div class="clka-stovebox">' + stoveSvg(api, a.stove, 'tab', 'clka-stove') + '<div class="clka-stoveside">'
      + '<p class="muted small clk-note">Косів славиться кахлями — ними облицьовували печі. Тут кожне гніздо чекає кахлю в будь-якому розписі; '
      + 'різнобарвна піч — теж піч.</p></div></div>' + pick;
    st.albumUi.stove.innerHTML = html;
    for (const b of st.albumUi.stove.querySelectorAll('[data-key]')) {
      b.onclick = () => api.act(st, 'album', { op: 'tile', key: b.dataset.key }).then((r) => {
        if (r && r.ok) { api.sfx('stove'); if (st.albumUi) st.albumUi.stove.classList.add('pulse'); setTimeout(() => st.albumUi && st.albumUi.stove.classList.remove('pulse'), 900); }
      });
    }
  }

  function paintMuseum(st, api) {
    const a = st.album;
    const c = cat(st);
    const finds = (c && c.finds) || Object.keys(FIND_SVG).map((key) => ({ key, name: key, desc: '' }));
    const glue = (c && c.glue) || 5;
    const found = pc(a.finds);
    const sig = a.finds + '|' + a.shards + '|' + (c ? 1 : 0) + '|' + st.mine + '|' + (a.find ? a.find.key : '');
    if (st.albumUi.museum._sig === sig) return;
    st.albumUi.museum._sig = sig;
    const esc = (x) => api.esc(st, x);
    const html = '<div class="clk-sub">Трипільський музей · ' + found + ' з 8<span class="muted small"> · знахідка +1 %, усі вісім ще +5 %</span></div>'
      + '<p class="muted small clk-note">Кожен виліплений виріб — це копаний у глинищі пласт: інколи там трапляється щось трипільське. '
      + 'Друга така сама річ — уламок; ' + glue + ' уламків склеюються в те, чого бракує.</p>'
      + '<div class="clka-museum">' + finds.map((f, i) => {
        const have = (a.finds >> i) & 1;
        return '<button type="button" class="clka-find' + (have ? ' have' : '') + '" data-i="' + i + '">'
          + findSvg(f.key, 'clka-fsvg') + (have ? '' : '<i class="clka-q">?</i>')
          + '<span class="small">' + (have ? esc(f.name) : 'ще в землі') + '</span></button>';
      }).join('') + '</div>'
      + '<div class="clka-shards"><span>🧩 уламків: <b>' + a.shards + '</b>' + (found < 8 ? ' з ' + glue : '') + '</span>'
      + (found < 8 ? '<button type="button" class="ghost small clka-glue"' + (st.mine && a.shards >= glue ? '' : ' disabled') + '>Склеїти</button>' : '<span class="muted small">музей повний 🎉</span>')
      + '</div>';
    st.albumUi.museum.innerHTML = html;
    for (const b of st.albumUi.museum.querySelectorAll('.clka-find')) b.onclick = () => openFind(st, api, +b.dataset.i);
    const g = st.albumUi.museum.querySelector('.clka-glue');
    if (g) g.onclick = () => (st.albumGlueAt = Date.now(), api.act(st, 'album', { op: 'glue' })).then((r) => { if (r && r.ok) api.sfx('glue'); });
  }

  // ---------- картки ----------

  function openCell(st, api, r, c) {
    const a = st.album;
    const w = wareList(st)[r];
    const k = styleKeys(st)[c];
    if (!w) return;
    const esc = (x) => api.esc(st, x);
    const cc = cat(st);
    const open = (a.cells[r] >> c) & 1;
    const star = (a.stars[r] >> c) & 1;
    const wf = cc && cc.wares ? cc.wares[w.key] : '';
    const sf = cc && cc.styles ? cc.styles[k] : null;
    const lvl = a.mastery[r] || 0;
    const at = (cc && cc.masteryAt) || MASTERY_AT;
    const fired = firedOf(st, w.key);
    const body = api.overlay(st, '<div class="clka-card' + (open ? ' open' : '') + '">'
      + '<div class="clka-cardpic">' + api.wareSvg(w.key, { style: open ? k : '', quality: star ? 3 : 1, cls: 'clka-big' + (open ? '' : ' shadow'), slot: 'alb-card' }) + '</div>'
      + '<div class="clka-cardtxt"><div class="clk-sub">' + esc(w.name) + ' · ' + esc(styleName(st, k)) + (star ? ' <span class="clkw-q">★ дзвінкий</span>' : '') + '</div>'
      + '<div class="small ' + (open ? 'clka-ok' : 'muted') + '">' + (open
        ? (star ? 'Є в альбомі, ще й дзвінкий.' : 'Є в альбомі. Зірка — за дзвінкий виріб цієї пари.')
        : 'Ще не обпалено: виліпи ' + esc(w.name.toLowerCase()) + ' і обпали в горні ' + (k ? 'з розписом «' + esc(styleName(st, k)) + '»' : 'без розпису') + '.') + '</div>'
      + (wf ? '<p class="clka-fact">' + esc(wf) + '</p>' : '')
      + (sf ? '<p class="clka-fact"><b>' + esc(sf.place) + '.</b> ' + esc(sf.text) + '</p>' : '')
      + '<div class="clka-mast small"><span>Майстерність ' + lvl + ' з 10 · обпалено ' + api.num(fired)
      + (lvl < at.length ? ' · до рівня ' + (lvl + 1) + ' ще ' + api.num(Math.max(0, at[lvl] - fired)) : '') + '</span>'
      + '<i class="clka-mbar"><i style="width:' + Math.round(masteryProgress(st, w.key, lvl) * 100) + '%"></i></i>'
      + '<span class="muted">−' + lvl * 4 + ' % роботи · +' + lvl * 5 + ' % ціни цього виробу</span></div>'
      + '</div></div>', { cls: 'clka-ov' });
    return body;
  }

  function openFind(st, api, i) {
    const a = st.album;
    const cc = cat(st);
    const f = cc && cc.finds && cc.finds[i];
    if (!f) return;
    const have = (a.finds >> i) & 1;
    const esc = (x) => api.esc(st, x);
    api.overlay(st, '<div class="clka-card' + (have ? ' open' : '') + '"><div class="clka-cardpic">' + findSvg(f.key, 'clka-big' + (have ? '' : ' shadow')) + '</div>'
      + '<div class="clka-cardtxt"><div class="clk-sub">' + (have ? esc(f.name) : 'Ще в землі') + '</div>'
      + '<p class="clka-fact">' + (have ? esc(f.desc) : 'Копай далі: кожен виліплений виріб — шанс знайти.') + '</p>'
      + '<p class="muted small">Трипільська культура — мальована кераміка зі спіралями; посуд ліпили без гончарного кола.</p></div></div>', { cls: 'clka-ov' });
  }

  // ---------- події: нова клітинка, знахідка ----------

  function noticeCells(st, api, prev) {
    const a = st.album;
    if (!prev) return;
    const fresh = [];
    for (let r = 0; r < a.cells.length; r++) {
      const added = (a.cells[r] || 0) & ~(prev[r] || 0);
      for (let c = 0; c < COLS; c++) if ((added >> c) & 1) fresh.push(r * COLS + c);
    }
    if (!fresh.length) return;
    for (const x of fresh) st.albumFlash.add(x);
    clearTimeout(st.albumFlashT);
    st.albumFlashT = setTimeout(() => { st.albumFlash.clear(); if (st.albumUi && st.album) paintGrid(st, api); }, 2600);
    api.sfx('album');
    const r = Math.floor(fresh[0] / COLS);
    const w = wareList(st)[r];
    const k = styleKeys(st)[fresh[0] % COLS];
    api.popAt(st, '📒 ' + (fresh.length > 1 ? fresh.length + ' нові клітинки альбому' : 'в альбомі: ' + (w ? w.name.toLowerCase() : '') + ', ' + styleName(st, k)), 'big', 50, 18);
    api.sparks(st, st.fx, 12, true, 50, 22);
  }

  function noticeFind(st, api) {
    const f = st.album.find;
    if (!f) return;
    const at = Date.parse(f.at) || 0;
    const seen = +api.storeGet('clka.find', '0');
    if (at <= seen) return;
    api.storeSet('clka.find', String(at));
    // Перший вид після входу: знахідку, старшу за годину, лише запам'ятати — «поки тебе не було» вже про неї сказав.
    if (!st.albumSeen && api.serverNow(st) - at > 3600 * 1000) return;
    const cc = cat(st);
    const item = cc && cc.finds && cc.finds.find((x) => x.key === f.key);
    const name = item ? item.name : 'трипільська річ';
    const host = api.layer(st, 'front', 'album');
    const el = document.createElement('div');
    el.className = 'clka-found' + (f.dup ? ' dup' : '');
    el.innerHTML = findSvg(f.key, 'clka-foundsvg') + '<b>🏺 знахідка!</b><span>' + api.esc(st, name) + (f.dup ? ' — ще одна, в уламки' : '') + '</span>';
    api.fleeting(host, el, 4200);
    api.sfx('find');
    // Склеєне з уламків сервер уже назвав своїм тостом — другий такий самий ні до чого.
    if (Date.now() - (st.albumGlueAt || 0) > 4000) api.toast(st, f.dup ? '🧩 Знову ' + name.toLowerCase() + ' — уламок у скарбничку' : '🏺 У глинищі знахідка: ' + name.toLowerCase() + ' — +1 % до всього', 'ok');
  }

  // ---------- піч у хаті ----------

  function paintHouseStove(st, api) {
    const g = st.albumHouse;
    if (!g) return;
    const a = st.album;
    const sig = JSON.stringify(a.stove);
    if (g._sig === sig) return;
    g._sig = sig;
    if (!a.stove.length) { g.innerHTML = ''; return; }
    // Жива хата (360×450): праворуч, під іконою (y 188…230) і над горном (y від 272) — піч ~35×48.
    g.innerHTML = '<g class="clka-hstove" transform="translate(300 226) scale(.16)"><title>Кахляна піч · ' + a.stove.length + ' з 12</title>'
      + stoveSvg(api, a.stove, 'house', '').replace('<svg class="" viewBox="0 0 220 300" aria-hidden="true">', '').replace(/<\/svg>$/, '') + '</g>';
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'album',
    order: 50,

    mount(st, api) {
      st.album = null;
      st.albumPrev = null;
      st.albumSeen = false;
      st.albumFlash = new Set();
      const pane = api.tab(st, 'album', '📒 Альбом', 30);
      // Альбом з'являється з першим обпаленим виробом: до того в ньому самі тіні.
      api.showWhen(st, 'album', (st2) => !!st2.craft && st2.craft.fired > 0);
      pane.classList.add('clka-pane');
      pane.innerHTML = '<div class="clka">'
        + '<div class="clka-head"></div>'
        + '<section class="clka-sec"><div class="clk-sub">Альбом виробів<span class="muted small"> · клітинка +0,2 %, повний рядок +2 %, повний стовпчик +3 % · '
        + 'тап — картка</span></div><div class="clka-gridbox"></div></section>'
        + '<section class="clka-sec clka-stovesec"></section>'
        + '<section class="clka-sec clka-museumsec"></section>'
        + '</div>';
      st.albumUi = {
        pane,
        head: pane.querySelector('.clka-head'),
        grid: pane.querySelector('.clka-gridbox'),
        stove: pane.querySelector('.clka-stovesec'),
        museum: pane.querySelector('.clka-museumsec'),
      };
      // Розписи купуються за глеки, тож живуть у Майстерні поруч із рештою покупок (їх туди кладе ядро).
      st.albumHouse = api.layer(st, 'back', 'album');
    },

    update(st, v, api) {
      const a = v.album;
      if (!a || !Array.isArray(a.cells)) return;
      st.album = {
        cells: a.cells, stars: a.stars || [], open: a.open || 0, size: a.size || 108, mastery: a.mastery || [],
        stove: a.stove || [], finds: a.finds || 0, shards: a.shards || 0, find: a.find || null, bonus: a.bonus || 0,
      };
      noticeCells(st, api, st.albumPrev);
      st.albumPrev = a.cells.slice();
      noticeFind(st, api);
      st.albumSeen = true;
      paintHead(st, api);
      paintGrid(st, api);
      paintStove(st, api);
      paintMuseum(st, api);
      paintHouseStove(st, api);
    },

    unmount(st) {
      clearTimeout(st.albumFlashT);
      st.albumUi = null;
      st.albumHouse = null;
    },
  });
})();
