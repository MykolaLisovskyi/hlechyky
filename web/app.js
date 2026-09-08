(() => {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = (sec) => { sec = Math.max(0, Math.floor(sec || 0)); const m = Math.floor(sec / 60), s = sec % 60; return `${m}:${String(s).padStart(2, '0')}`; };
  const tm = (iso) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
  const isUrl = (s) => /^https?:\/\/\S+$/i.test(s.trim());
  const isMobile = () => window.matchMedia('(max-width: 900px)').matches;
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const EMOJIS = ['🔥', '❤️', '😂', '🕺', '🤘', '😴', '🤮', '🫠'];

  let me = { nick: localStorage.getItem('nick') || '', role: 'member' };
  let state = null;
  let conn = null;
  let searchTimer = null;
  let lastQuery = '';
  let lastPlayId = null;
  let unread = 0;
  let chatTab = 'chat';
  let libTab = 'history';
  let queueDur = [];

  const dj = () => state?.djName || 'Дядько Глек';
  const djGen = () => state?.djNameGen || 'Дядька Глека';

  // ---------- toasts / busy buttons ----------
  function toast(text, kind) {
    const el = document.createElement('div');
    el.className = 'toast ' + (kind || '');
    el.innerHTML = (kind === 'wait' ? '<span class="spin"></span>' : '') + esc(text);
    $('toasts').appendChild(el);
    const ttl = kind === 'err' ? 5000 : 3200;
    setTimeout(() => el.remove(), ttl);
    return el;
  }
  const ok = (r) => toast(r.message, 'ok');
  const fail = (e) => toast(e.message, 'err');

  // Shows the click landed: spinner in the button until the server answers.
  async function busy(btn, label, fn) {
    if (!btn || btn.disabled) return;
    const html = btn.innerHTML;
    btn.disabled = true;
    btn.classList.add('busy');
    btn.innerHTML = `<span class="spin"></span> ${esc(label)}`;
    try { return await fn(); }
    finally { if (btn.isConnected) { btn.disabled = false; btn.classList.remove('busy'); btn.innerHTML = html; } }
  }

  // ---------- api ----------
  async function api(method, path, body) {
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(me.nick) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* no body */ }
    if (!r.ok) throw new Error((data && data.message) || `HTTP ${r.status}`);
    return data;
  }
  const queueTrack = (id) => api('POST', `/api/queue/track/${id}`).then(ok).catch(fail);

  // ---------- nick ----------
  function askNick(force) {
    if (me.nick && !force) return;
    $('nickInput').value = me.nick;
    $('nickModal').hidden = false;
    setTimeout(() => $('nickInput').focus(), 50);
  }
  function saveNick() {
    const n = $('nickInput').value.trim().slice(0, 24);
    if (!n) return;
    const changed = n !== me.nick;
    me.nick = n;
    localStorage.setItem('nick', n);
    $('nickModal').hidden = true;
    $('nickBtn').textContent = n;
    if (changed && conn && conn.state === 'Connected') conn.invoke('SetNick', n).catch(() => {});
    if (!conn) connect();
    else render();
  }
  $('nickForm').onsubmit = (e) => { e.preventDefault(); saveNick(); };
  $('nickBtn').onclick = () => askNick(true);

  // ---------- player ----------
  const audio = $('audio');
  const vol = $('volume');
  vol.value = localStorage.getItem('volume') ?? '0.8';
  audio.volume = parseFloat(vol.value);
  vol.oninput = () => { audio.volume = parseFloat(vol.value); localStorage.setItem('volume', vol.value); };
  let playState = 'idle'; // idle | connecting | live
  function setPlayUi() {
    const b = $('playBtn');
    if (playState === 'idle') { b.className = 'primary'; b.textContent = '▶ Врубити'; b.title = 'Слухати ефір прямо тут'; }
    else if (playState === 'connecting') { b.className = 'primary busy'; b.innerHTML = '<span class="spin"></span> Підключаю…'; }
    else { b.className = 'live'; b.innerHTML = '<span class="dot"></span> В ефірі · Стоп'; b.title = 'Вимкнути'; }
  }
  function stopAudio() {
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    playState = 'idle';
    setPlayUi();
  }
  $('playBtn').onclick = async () => {
    if (playState !== 'idle') { stopAudio(); return; }
    const url = state?.streamUrl || '';
    if (!url) { toast('Адреса потоку не налаштована', 'err'); return; }
    playState = 'connecting';
    setPlayUi();
    audio.src = url + (url.includes('?') ? '&' : '?') + '_=' + Date.now();
    try { await audio.play(); }
    catch (e) { playState = 'idle'; setPlayUi(); toast('Не вдалося запустити потік: ' + e.message, 'err'); }
  };
  audio.addEventListener('playing', () => { playState = 'live'; setPlayUi(); updateMediaSession(); });
  audio.addEventListener('waiting', () => { if (playState === 'live') { playState = 'connecting'; setPlayUi(); } });
  audio.addEventListener('error', () => { if (playState !== 'idle') { stopAudio(); toast('Потік обірвався. Натисни «Врубити» ще раз', 'err'); } });
  audio.addEventListener('ended', () => { if (playState !== 'idle') { stopAudio(); toast('Потік закінчився', 'err'); } });
  function updateMediaSession() {
    if (!('mediaSession' in navigator) || !state) return;
    const n = state.now, t = n.track;
    const playingTrack = t && (n.source === 'user' || n.source === 'autodj');
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: playingTrack ? t.title : (n.spotifyLive ? 'Spotify-резерв' : 'Тиша'),
        artist: playingTrack ? t.artist : state.siteName,
        album: state.siteName,
        artwork: playingTrack && t.thumbUrl ? [{ src: t.thumbUrl, sizes: '512x512', type: 'image/jpeg' }] : [],
      });
      navigator.mediaSession.setActionHandler('pause', () => stopAudio());
      navigator.mediaSession.setActionHandler('stop', () => stopAudio());
    } catch { /* unsupported */ }
  }

  // ---------- now playing ----------
  function nowRemaining() {
    if (!state) return 0;
    const n = state.now;
    if (!(n.source === 'user' || n.source === 'autodj')) return 0;
    const elapsed = (Date.now() - new Date(n.startedAt).getTime()) / 1000 - (state.streamDelaySeconds || 0);
    return Math.max(0, (n.durationSec || 0) - elapsed);
  }
  function etaText(sec) {
    if (sec < 25) return 'ось-ось';
    if (sec < 75) return 'десь за хвилину';
    return `за ~${Math.round(sec / 60)} хв`;
  }

  let nowSig = '', queueSig = '', sugSig = '';
  let dragging = null, pendingQueueRender = false; // queue drag-to-reorder state
    function renderNow() {
    const n = state.now;
    const box = $('now');
    const sig = JSON.stringify([n.playId, n.itemId, n.source, n.track?.id, n.likers, n.skipPending, n.requestedBy, n.via, n.reason,
      n.durationSec, n.startedAt, n.spotifyLive, n.spotifyTitle, state.liquidsoapOk, state.listeners, me.role, me.nick, state.siteName, state.djName]);
    if (sig === nowSig) return;
    nowSig = sig;
    const banner = $('banner');
    banner.hidden = state.liquidsoapOk;
    banner.textContent = 'Ефір не відповідає (liquidsoap). Черга збережеться, треки підуть, щойно він оживе.';
    $('liqStatus').className = 'chip ' + (state.liquidsoapOk ? 'ok' : 'err');
    $('liqStatus').textContent = state.liquidsoapOk ? 'ефір' : 'ефір ↓';
    $('listeners').textContent = '🎧 ' + state.listeners;

    if (n.source === 'user' || n.source === 'autodj') {
      const t = n.track || {};
      const liked = n.likers.some((x) => sameNick(x, me.nick));
      const by = n.source === 'user'
        ? `закинув <b>${esc(n.requestedBy)}</b>${n.via === 'suggestion' ? ` <span class="chip dj">порада ${esc(djGen())}</span>` : ''}`
        : `<b>${esc(dj())}</b> <span class="chip dj">авто</span>`;
      const why = n.reason ? `<div class="why">${esc(n.reason)}</div>` : '';
      const pending = n.skipPending;
      box.innerHTML = `
        <div class="coverwrap">${t.thumbUrl ? `<img class="cover" src="${esc(t.thumbUrl)}" alt="">` : '<div class="cover placeholder">♪</div>'}</div>
        <div style="min-width:0">
          <div class="title">${esc(t.title)}</div>
          <div class="artist">${esc(t.artist)}</div>
          <div class="by">${by}</div>
          ${why}
          <div class="progress ${pending ? 'pending' : ''}"><div id="bar"></div></div>
          <div class="times"><span id="tElapsed">0:00</span><span>${fmt(n.durationSec)}</span></div>
          ${pending ? `<div class="pending-note"><span class="spin"></span> Перемикаю, в ефірі зміниться за кілька секунд</div>` : ''}
          <div class="actions">
            <button id="likeBtn" class="${liked ? 'active' : ''}" title="${esc(n.likers.join(', ') || 'Лайкнути')}">❤ ${n.likers.length}</button>
            <button id="skipBtn" ${pending ? 'disabled' : ''} title="Перемкнути на наступний трек">⏭ Скіп</button>
            <button id="plBtn" title="Зберегти в плейлист">＋ плейлист</button>
            ${t.sourceUrl ? `<a class="chip" href="${esc(t.sourceUrl)}" target="_blank" rel="noopener">джерело ↗</a>` : ''}
            ${me.role === 'admin' ? `<button id="banBtn" class="danger ghost" title="Забанити трек і скіпнути">бан</button>` : ''}
          </div>
          <div class="reacts" title="Реакція, яку побачать усі">${EMOJIS.map((e) => `<button data-e="${e}">${e}</button>`).join('')}</div>
        </div>`;
      $('likeBtn').onclick = (e) => busy(e.currentTarget, '', () => api('POST', `/api/like/${t.id}`).catch(fail));
      $('skipBtn').onclick = (e) => busy(e.currentTarget, 'скіп…', () => api('POST', '/api/skip').then(ok).catch(fail));
      $('plBtn').onclick = () => openPlaylistPicker(t.id, t.title);
      const ban = $('banBtn');
      if (ban) ban.onclick = () => confirm('Забанити цей трек назавжди?') && api('POST', `/api/ban/${t.id}`).then(ok).catch(fail);
      box.querySelectorAll('.reacts button').forEach((b) => b.onclick = () => {
        if (!conn) return;
        conn.invoke('React', b.dataset.e).catch(() => {});
      });
      tick();
    } else {
      const spot = n.spotifyLive;
      box.innerHTML = `
        <div class="coverwrap"><img class="cover dj" src="/static/glek.svg" alt=""></div>
        <div>
          <div class="title">${spot ? 'Spotify-резерв' : 'Тиша'}</div>
          <div class="artist">${spot ? esc(n.spotifyTitle || '') : `${esc(dj())} шукає щось на полиці…`}</div>
          <div class="by">${spot ? 'грає резервний потік, поки в черзі порожньо' : 'закинь щось або зачекай'}</div>
          <div class="reacts">${EMOJIS.map((e) => `<button data-e="${e}">${e}</button>`).join('')}</div>
        </div>`;
      box.querySelectorAll('.reacts button').forEach((b) => b.onclick = () => conn && conn.invoke('React', b.dataset.e).catch(() => {}));
    }
    const playingTrack = n.track && (n.source === 'user' || n.source === 'autodj');
    document.title = playingTrack ? `${n.track.title} — ${n.track.artist} · ${state.siteName}` : state.siteName;
    if (playState !== 'idle') updateMediaSession();
  }

  function tick() {
    if (!state) return;
    const n = state.now;
    if (n.source === 'user' || n.source === 'autodj') {
      const bar = $('bar'), el = $('tElapsed');
      if (bar) {
        const elapsed = (Date.now() - new Date(n.startedAt).getTime()) / 1000 - (state.streamDelaySeconds || 0);
        const d = n.durationSec || 0;
        const e = Math.max(0, Math.min(elapsed, d || elapsed));
        el.textContent = fmt(e);
        bar.style.width = d ? Math.min(100, (e / d) * 100) + '%' : '0%';
      }
    }
    // ETAs in the queue: what is left of the current track plus everything queued before the item
    let acc = nowRemaining();
    document.querySelectorAll('[data-eta]').forEach((s) => {
      const i = +s.dataset.eta;
      let sum = acc;
      for (let j = 0; j < i; j++) sum += queueDur[j] || 0;
      s.textContent = etaText(sum);
    });
    const an = document.querySelector('[data-eta-after]');
    if (an) an.textContent = etaText(acc + queueDur.reduce((a, b) => a + b, 0));
  }
  setInterval(tick, 1000);

  function flyEmoji(emoji, nick) {
    const layer = $('flyLayer');
    const cover = document.querySelector('#now .cover');
    const pr = layer.parentElement.getBoundingClientRect();
    const cr = cover ? cover.getBoundingClientRect() : pr;
    const el = document.createElement('div');
    el.className = 'fly';
    el.style.left = (cr.left - pr.left + cr.width * (0.3 + Math.random() * 0.4)) + 'px';
    el.style.top = (cr.top - pr.top + cr.height * 0.75) + 'px';
    el.innerHTML = `${esc(emoji)}<small>${esc(nick)}</small>`;
    layer.appendChild(el);
    setTimeout(() => el.remove(), 2500);
  }

  // ---------- queue ----------
  function statusChip(it) {
    switch (it.status) {
      case 'queued': return '<span class="chip">чекає</span>';
      case 'downloading': return '<span class="chip warn"><span class="spin"></span> качається</span>';
      case 'ready': return '<span class="chip ok">готово</span>';
      case 'dispatched': return '<span class="chip ok">наступний</span>';
      case 'failed': return `<span class="chip err">${esc(it.error || 'помилка')}</span>`;
    }
    return '';
  }

  function renderQueue() {
    if (dragging?.active) { pendingQueueRender = true; return; } // finish the drag first, then redraw from the newest state
    const ul = $('queue');
    const q = state.queue;
    queueDur = q.map((it) => it.track.durationSec || 0);
    const sig = JSON.stringify([q.map((it) => [it.itemId, it.status, it.error, it.requestedBy, it.via]), me.role, me.nick, state.djName]);
    if (sig === queueSig) { tick(); return; }
    queueSig = sig;
    $('queueCount').textContent = q.length ? `· ${q.length} · ${fmt(queueDur.reduce((a, b) => a + b, 0))}` : '';
    if (!q.length) {
      ul.innerHTML = `<li class="empty">Порожньо. Закинь щось, або хай ${esc(dj())} крутить своє.</li>`;
    } else {
      const now = Date.now();
      ul.innerHTML = q.map((it, i) => {
        const mine = sameNick(it.requestedBy, me.nick) || me.role === 'admin';
        const canMove = mine && it.status !== 'dispatched' && q.length > 1;
        const fresh = now - new Date(it.addedAt).getTime() < 4000;
        return `<li class="qitem ${it.status} ${fresh ? 'fresh' : ''} ${canMove ? 'movable' : ''}" data-id="${it.itemId}">
          <div class="n">${i + 1}</div>
          ${it.track.thumbUrl ? `<img src="${esc(it.track.thumbUrl)}" alt="" draggable="false">` : '<div class="noimg"></div>'}
          <div style="min-width:0">
            <div class="t">${esc(it.track.title)}</div>
            <div class="a">${esc(it.track.artist)} · ${fmt(it.track.durationSec)}</div>
            <div class="meta"><span>${esc(it.requestedBy)}</span>${it.via === 'suggestion' ? `<span class="chip dj">порада ${esc(djGen())}</span>` : ''}${statusChip(it)}<span class="eta" data-eta="${i}"></span></div>
          </div>
          <div class="btns">
            ${canMove ? '<span class="grip" title="Тягни, щоб пересунути">⠿</span>' : ''}
            ${mine ? `<button class="icon danger rm" title="Прибрати">✕</button>` : ''}
          </div>
        </li>`;
      }).join('');
      ul.querySelectorAll('li').forEach((li) => {
        const id = li.dataset.id;
        li.querySelector('.rm')?.addEventListener('click', (e) => busy(e.currentTarget, '', () => api('DELETE', `/api/queue/${id}`).catch(fail)));
        if (li.classList.contains('movable')) li.addEventListener('pointerdown', (e) => startDrag(e, li));
      });
    }
    tick();
  }

  // ---------- drag to reorder: grab a row (mouse) or its ⠿ grip (touch) and pull it where it should play ----------
  function startDrag(e, li) {
    if (e.button !== 0 || dragging) return;
    if (e.target.closest('button, a, input')) return;
    if (e.pointerType === 'touch' && !e.target.closest('.grip')) return; // a finger on the row scrolls the page; only the grip drags
    const lis = [...$('queue').querySelectorAll('li.qitem')];
    const from = lis.indexOf(li);
    const min = lis[0]?.classList.contains('dispatched') ? 1 : 0; // the next track already sits in liquidsoap, nothing goes before it
    if (from < min) return;
    const gs = getComputedStyle($('queue'));
    dragging = {
      li, id: li.dataset.id, lis, from, to: from, min, max: lis.length - 1, active: false,
      rects: lis.map((el) => { const r = el.getBoundingClientRect(); return { top: r.top + window.scrollY, h: r.height }; }),
      gap: parseFloat(gs.rowGap || gs.gap) || 0,
      startY: e.clientY + window.scrollY, lastY: e.clientY, scrollV: 0, raf: 0,
    };
    try { li.setPointerCapture(e.pointerId); } catch { /* synthetic or already released pointer */ }
    window.addEventListener('pointermove', onDragMove);
    window.addEventListener('pointerup', endDrag);
    window.addEventListener('pointercancel', endDrag);
  }
  function onDragMove(e) {
    const d = dragging;
    if (!d) return;
    d.lastY = e.clientY;
    const dy = e.clientY + window.scrollY - d.startY;
    if (!d.active) {
      if (Math.abs(dy) < 6) return; // a click is not a drag
      d.active = true;
      d.li.classList.add('dragging');
      document.body.classList.add('is-dragging');
      d.lis.forEach((el) => { if (el !== d.li) el.classList.add('shift'); });
    }
    e.preventDefault();
    updateDrag(dy);
    // near the top or bottom of the window the page scrolls by itself
    const edge = 70, vh = window.innerHeight;
    d.scrollV = e.clientY < edge ? -(edge - e.clientY) / 5 : e.clientY > vh - edge ? (e.clientY - (vh - edge)) / 5 : 0;
    if (d.scrollV && !d.raf) d.raf = requestAnimationFrame(scrollStep);
  }
  function scrollStep() {
    const d = dragging;
    if (!d) return;
    d.raf = 0;
    if (!d.scrollV) return;
    window.scrollBy(0, d.scrollV);
    updateDrag(d.lastY + window.scrollY - d.startY);
    d.raf = requestAnimationFrame(scrollStep);
  }
  function updateDrag(dy) {
    const d = dragging;
    d.li.style.transform = `translateY(${dy}px) scale(1.02)`;
    // the row lands where its centre is: count the others whose centre is above it
    const centre = d.rects[d.from].top + d.rects[d.from].h / 2 + dy;
    let to = d.min;
    for (let i = d.min; i <= d.max; i++) if (i !== d.from && d.rects[i].top + d.rects[i].h / 2 < centre) to++;
    d.to = to;
    // the others slide out of the way by exactly one row
    const shift = d.rects[d.from].h + d.gap;
    d.lis.forEach((el, i) => {
      if (i === d.from) return;
      const t = i < d.from && i >= to ? shift : i > d.from && i <= to ? -shift : 0;
      el.style.transform = t ? `translateY(${t}px)` : '';
    });
  }
  async function endDrag() {
    const d = dragging;
    if (!d) return;
    dragging = null;
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', endDrag);
    window.removeEventListener('pointercancel', endDrag);
    if (d.raf) cancelAnimationFrame(d.raf);
    document.body.classList.remove('is-dragging');
    d.lis.forEach((el) => { el.style.transform = ''; el.classList.remove('shift'); });
    d.li.classList.remove('dragging');
    if (!d.active) return;
    const from = state.queue.findIndex((x) => x.itemId === d.id);
    if (from >= 0 && d.to !== from) {
      // show it in place at once; the server's next broadcast confirms (or corrects) it
      const [item] = state.queue.splice(from, 1);
      state.queue.splice(d.to, 0, item);
      queueSig = '';
      renderQueue();
      $('queue').querySelector(`li[data-id="${d.id}"]`)?.classList.add('moved');
      try { await api('POST', `/api/queue/${d.id}/move`, { toIndex: d.to }); }
      catch (err) { fail(err); }
    } else if (pendingQueueRender) {
      queueSig = '';
      renderQueue();
    }
    pendingQueueRender = false;
  }

  // ---------- DJ suggestions: four fixed slots built from the track on air; the first card is what really plays next ----------
  const SLOTS = 4;
  let sugSeen = new Set(); // cards already on screen: only newcomers get the fade-in, so the block does not blink
  function renderSuggestions() {
    const box = $('sugCards');
    $('djName').textContent = dj();
    const list = state.suggestions || [];
    const a = state.autoNext;
    const seed = state.suggestSeed;
    const sig = JSON.stringify([list.map((s) => s.itemId), a && [a.itemId, a.status, a.error], seed && seed.id, state.now.track && state.now.track.id, state.queue.length, state.djName]);
    if (sig === sugSig) { tick(); return; }
    sugSig = sig;
    const label = (t) => (t.artist ? `${t.artist} — ${t.title}` : t.title);
    const onAir = seed && state.now.track && state.now.track.id === seed.id;
    $('djSub').textContent = seed
      ? `Підбирає під ${onAir ? 'те, що зараз грає' : 'останнє, що грало'}: ${label(seed)}`
      : 'Підбирає під те, що зараз грає. Зміниться трек — зміняться й поради';
    const card = (s, next) => `<div class="sug ${next ? 'next' : ''} ${sugSeen.has(s.itemId) ? '' : 'fade'}" data-id="${s.itemId}">
        ${s.track.thumbUrl ? `<img src="${esc(s.track.thumbUrl)}" alt="">` : '<div class="noimg"></div>'}
        <div style="min-width:0">
          <div class="t">${esc(s.track.title)}</div>
          <div class="a">${esc(s.track.artist)} · ${fmt(s.track.durationSec)}</div>
          ${next
            ? `<div class="r">${state.queue.length ? 'Після черги' : 'Наступний'} · <span data-eta-after></span> · якщо ніхто нічого не закине${s.reason && !onAir ? ' · ' + esc(s.reason) : ''}</div>`
            : (s.reason && !onAir ? `<div class="r">${esc(s.reason)}</div>` : '')}
        </div>
        <div class="btns">
          ${next ? statusChip(s) : '<button class="primary add" title="Закинути в чергу">👍 Беру!</button>'}
          <button class="skip" title="${next ? 'Хай поставить щось інше' : 'Прибрати, хай запропонує інше'}">👎 Не те</button>
        </div>
      </div>`;
    const cards = (a ? [card(a, true)] : []).concat(list.map((s) => card(s, false)));
    const slot = (i) => `<div class="sug empty"><span class="spin"></span> ${cards.length ? `${esc(dj())} шукає ще…` : (i === 0 ? `${esc(dj())} порпається на полицях…` : '')}</div>`;
    while (cards.length < SLOTS) cards.push(slot(cards.length));
    box.innerHTML = cards.slice(0, SLOTS).join('');
    sugSeen = new Set((a ? [a.itemId] : []).concat(list.map((s) => s.itemId)));
    box.querySelectorAll('.sug[data-id]').forEach((el) => {
      const id = el.dataset.id;
      el.querySelector('.add')?.addEventListener('click', (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/suggest/${id}/add`).then(ok).catch(fail)));
      el.querySelector('.skip')?.addEventListener('click', (e) => busy(e.currentTarget, 'шукаю…', () => api('POST', `/api/suggest/${id}/skip`).catch(fail)));
    });
    tick();
  }

  function renderOnline() {
    $('online').innerHTML = state.online.map((n) => `<span class="chip">${esc(n)}</span>`).join('') || '<span class="muted small">нікого</span>';
  }

  function render() {
    if (!state) return;
    $('siteName').textContent = state.siteName;
    renderNow();
    renderQueue();
    renderOnline();
    renderSuggestions();
    if (state.now.playId !== lastPlayId) {
      lastPlayId = state.now.playId;
      if (libTab === 'history') loadLib();
    }
  }

  // ---------- chat + log ----------
  const linkify = (s) => esc(s).replace(/(https?:\/\/[^\s<]+)/g, (m) => `<a href="${m}" target="_blank" rel="noopener">${m}</a>`);
  function chatVisible() { return chatTab === 'chat' && (!isMobile() || document.body.classList.contains('view-chat')) && !document.hidden; }
  function setUnread(n) {
    unread = n;
    for (const id of ['chatBadge', 'mChatBadge']) { const b = $(id); b.hidden = !n; b.textContent = n; }
  }
  function addMessage(m, scroll = true) {
    const isLog = m.kind === 'system';
    const box = isLog ? $('log') : $('messages');
    const el = document.createElement('div');
    const mine = sameNick(m.nick, me.nick);
    el.className = 'msg ' + (isLog ? 'system' : m.kind === 'dj' ? 'dj' : mine ? 'mine' : '');
    if (m.kind === 'dj') {
      el.innerHTML = `<img src="/static/glek.svg" alt=""><div><span class="n">${esc(m.nick)}</span>${linkify(m.text)}<span class="time">${tm(m.at)}</span></div>`;
    } else if (isLog) {
      el.innerHTML = `<span class="time">${tm(m.at)}</span>${linkify(m.text)}`;
    } else {
      el.innerHTML = `<span class="n">${esc(m.nick)}</span>${linkify(m.text)}<span class="time">${tm(m.at)}</span>`;
    }
    box.appendChild(el);
    while (box.children.length > 300) box.firstChild.remove();
    if (scroll) box.scrollTop = box.scrollHeight;
    if (!isLog && scroll && !mine && !chatVisible()) setUnread(unread + 1);
  }
  $('chatForm').onsubmit = (e) => {
    e.preventDefault();
    const text = $('chatInput').value.trim();
    if (!text || !conn) return;
    if (chatTab !== 'chat') setChatTab('chat');
    conn.invoke('SendChat', text).then(() => { $('chatInput').value = ''; }).catch((err) => toast('Не відправилось: ' + err.message, 'err'));
  };
  function setChatTab(tab) {
    chatTab = tab;
    $('chatTabs').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.tab === tab));
    $('messages').hidden = tab !== 'chat';
    $('log').hidden = tab !== 'log';
    const box = tab === 'chat' ? $('messages') : $('log');
    box.scrollTop = box.scrollHeight;
    if (chatVisible()) setUnread(0);
  }
  $('chatTabs').querySelectorAll('button').forEach((b) => b.onclick = () => setChatTab(b.dataset.tab));

  // mobile: one column at a time
  function setView(v) {
    document.body.classList.toggle('view-main', v === 'main');
    document.body.classList.toggle('view-chat', v === 'chat');
    $('mtabMain').classList.toggle('on', v === 'main');
    $('mtabChat').classList.toggle('on', v === 'chat');
    if (v === 'chat') { const box = $('messages'); box.scrollTop = box.scrollHeight; }
    if (chatVisible()) setUnread(0);
  }
  $('mtabMain').onclick = () => setView('main');
  $('mtabChat').onclick = () => setView('chat');
  document.addEventListener('visibilitychange', () => { if (chatVisible()) setUnread(0); });

  // ---------- search / add ----------
  const q = $('q'), results = $('results');
  let lastResults = [];
  let sel = -1;
  function showResults(list, hint, wait) {
    sel = -1;
    if (hint) { results.innerHTML = `<div class="hint">${wait ? '<span class="spin"></span>' : ''}${esc(hint)}</div>`; return; }
    results.innerHTML = list.map((r, i) => `<div class="result" data-i="${i}">
        ${r.thumbUrl ? `<img src="${esc(r.thumbUrl)}" alt="">` : '<div></div>'}
        <div style="min-width:0"><div class="t">${esc(r.title)}</div><div class="a">${esc(r.artist)}${r.album ? ' · ' + esc(r.album) : ''}</div></div>
        <div class="d">${fmt(r.durationSec)}</div>
      </div>`).join('');
    results.querySelectorAll('.result').forEach((el) => el.onclick = () => addPick(list[+el.dataset.i]));
  }
  async function search(text) {
    if (text === lastQuery) return;
    lastQuery = text;
    if (!text || isUrl(text)) { results.innerHTML = ''; lastResults = []; return; }
    showResults([], 'шукаю…', true);
    try {
      const list = await api('GET', `/api/search?q=${encodeURIComponent(text)}`);
      if (lastQuery !== text) return;
      lastResults = list;
      showResults(list, list.length ? null : 'нічого не знайшов, спробуй інакше або кинь посилання');
    } catch (e) { showResults([], 'пошук впав: ' + e.message); }
  }
  q.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => search(q.value.trim()), 350); });
  q.addEventListener('keydown', (e) => {
    const items = results.querySelectorAll('.result');
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (!items.length) return;
      e.preventDefault();
      sel = (sel + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items.forEach((el, i) => el.classList.toggle('sel', i === sel));
      items[sel].scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (sel >= 0 && lastResults[sel]) addPick(lastResults[sel]); else addFromInput();
    } else if (e.key === 'Escape') { results.innerHTML = ''; q.blur(); }
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('.add')) results.innerHTML = ''; });
  q.addEventListener('focus', () => { if (lastResults.length && q.value.trim() === lastQuery) showResults(lastResults); });
  $('addBtn').onclick = (e) => busy(e.currentTarget, 'закидаю…', addFromInput);
  document.addEventListener('keydown', (e) => {
    if (e.key === '/' && !['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName)) { e.preventDefault(); q.focus(); }
  });

  async function addPick(r) {
    results.innerHTML = '';
    q.value = '';
    lastQuery = '';
    lastResults = [];
    const t = toast(`Закидаю ${r.artist} — ${r.title}…`, 'wait');
    try { const res = await api('POST', '/api/queue', { pick: r }); t.remove(); ok(res); }
    catch (e) { t.remove(); fail(e); }
  }
  async function addFromInput() {
    const text = q.value.trim();
    if (!text) { q.focus(); return; }
    if (!isUrl(text) && lastResults.length && lastQuery === text) return addPick(lastResults[0]);
    results.innerHTML = '';
    const t = toast(isUrl(text) ? 'Розбираю посилання, це може зайняти кілька секунд…' : 'Шукаю…', 'wait');
    try { const res = await api('POST', '/api/queue', { input: text }); t.remove(); ok(res); q.value = ''; lastQuery = ''; lastResults = []; }
    catch (e) { t.remove(); fail(e); }
  }

  // ---------- drop (or paste) a link anywhere on the page ----------
  const drop = $('drop');
  const dropTitle = drop.querySelector('.drop-title');
  const dropSub = drop.querySelector('.drop-sub');
  const dropUrl = drop.querySelector('.drop-url');
  let dragDepth = 0, dropBusy = false, dropTimer = null;
  const isLink = (s) => isUrl(s) || /^spotify:track:/i.test(s);
  const hasText = (dt) => !!dt && [...(dt.types || [])].some((t) => t === 'text/uri-list' || t === 'text/plain' || t === 'text' || t === 'Text');

  function showDrop(kind, title, sub, url) {
    clearTimeout(dropTimer);
    drop.className = 'drop ' + kind;
    dropTitle.textContent = title;
    dropSub.textContent = sub || '';
    dropUrl.textContent = url || '';
    dropUrl.hidden = !url;
    drop.hidden = false;
  }
  function hideDrop(delay) {
    clearTimeout(dropTimer);
    dropTimer = setTimeout(() => {
      drop.classList.add('leaving');
      setTimeout(() => { drop.hidden = true; drop.className = 'drop'; }, 250);
    }, delay || 0);
  }
  document.addEventListener('dragenter', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    if (dragDepth++ === 0 && !dropBusy) showDrop('over', 'Кидай сюди', 'YouTube, YT Music, Spotify — закину в чергу');
  });
  document.addEventListener('dragover', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', (e) => {
    if (!hasText(e.dataTransfer)) return;
    if (--dragDepth <= 0) { dragDepth = 0; if (!dropBusy) hideDrop(); }
  });
  document.addEventListener('drop', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    dragDepth = 0;
    const dt = e.dataTransfer;
    const uri = (dt.getData('text/uri-list') || '').split('\n').map((s) => s.trim()).find((s) => s && !s.startsWith('#'));
    dropText((uri || dt.getData('text/plain') || dt.getData('text') || '').trim());
  });
  document.addEventListener('paste', (e) => {
    const tag = (e.target.tagName || '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || e.target.isContentEditable) return;
    const text = (e.clipboardData?.getData('text/plain') || '').trim();
    if (isLink(text)) { e.preventDefault(); dropText(text); }
  });
  async function dropText(text) {
    if (!text) { hideDrop(); return; }
    if (!isLink(text)) {
      // plain words are a search, not a link
      hideDrop();
      q.value = text.slice(0, 120);
      q.focus();
      search(q.value);
      return;
    }
    if (dropBusy) return;
    dropBusy = true;
    showDrop('busy', 'Закидаю…', 'розбираю посилання, це може зайняти кілька секунд', text);
    try {
      const res = await api('POST', '/api/queue', { input: text });
      showDrop('done', 'Закинуто!', String(res.message || '').replace(/^Закинуто:\s*/, ''), '');
      hideDrop(1600);
    } catch (err) {
      showDrop('err', 'Не вийшло', err.message, text);
      hideDrop(3500);
    } finally {
      dropBusy = false;
    }
  }

  // ---------- library: history / likes / playlists / stats ----------
  $('libTabs').querySelectorAll('button').forEach((b) => b.onclick = () => {
    libTab = b.dataset.tab;
    $('libTabs').querySelectorAll('button').forEach((x) => x.classList.toggle('on', x === b));
    loadLib();
  });
  const trackRow = (t, right, extra) => `<li>
      ${t.thumbUrl ? `<img src="${esc(t.thumbUrl)}" alt="">` : '<div class="noimg"></div>'}
      <div style="min-width:0"><div class="t ${extra?.skipped ? 'skipped' : ''}">${esc(t.title)} <span class="muted">· ${esc(t.artist)}</span></div><div class="r">${right}</div></div>
      <div class="btns"><button class="q" data-id="${esc(t.id)}" title="Закинути в чергу">в чергу</button><button class="ghost pl" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="У плейлист">＋</button></div>
    </li>`;
  function wireRows(root) {
    root.querySelectorAll('button.q').forEach((b) => b.onclick = (e) => busy(e.currentTarget, '…', () => queueTrack(b.dataset.id)));
    root.querySelectorAll('button.pl').forEach((b) => b.onclick = () => openPlaylistPicker(b.dataset.id, b.dataset.title));
  }
  async function loadLib() {
    const box = $('lib');
    try {
      if (libTab === 'history') {
        const list = await api('GET', '/api/history?n=80');
        box.innerHTML = `<ul class="list">${list.map((h) => trackRow(h.track,
          `${h.source === 'autodj' ? esc(dj()) : esc(h.requestedBy || '')}${h.via === 'suggestion' ? ' · порада' : ''}${h.likes ? ' · ❤' + h.likes : ''}${h.skipped ? ' · скіп' : ''} · ${tm(h.startedAt)}`,
          { skipped: h.skipped })).join('') || '<li class="empty">ще нічого не грало</li>'}</ul>`;
      } else if (libTab === 'likes') {
        const list = await api('GET', '/api/likes');
        box.innerHTML = `<ul class="list">${list.map((l) => trackRow(l.track, `❤ ${esc(l.likers.join(', '))}`)).join('') || '<li class="empty">Ще ніхто нічого не лайкнув. Сердечко під треком в ефірі.</li>'}</ul>`;
      } else if (libTab === 'top') {
        const r = await api('GET', '/api/top?days=7');
        const max = Math.max(1, ...r.requesters.map((x) => x.count));
        box.innerHTML = `<div class="muted small" style="margin-bottom:8px">Хто скільки закинув за останній тиждень</div>` +
          (r.requesters.map((x) => `<div class="stat"><span>${esc(x.nick)}</span><b>${x.count}</b><div class="bar" style="width:${Math.round(x.count / max * 100)}%"></div></div>`).join('') || '<div class="empty">поки тиша</div>');
      } else if (libTab === 'playlists') {
        await renderPlaylists();
        return;
      }
      wireRows(box);
    } catch (e) { box.innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
  }
  const openPl = new Set();
  async function renderPlaylists() {
    const box = $('lib');
    const list = await api('GET', '/api/playlists');
    box.innerHTML = `<form class="pl-new" id="plCreate"><input type="text" maxlength="40" placeholder="Новий плейлист: назва…" autocomplete="off"><button class="primary" type="submit">Створити</button></form>` +
      (list.map((p) => `<div class="pl" data-id="${p.id}">
          ${p.thumbUrl ? `<img src="${esc(p.thumbUrl)}" alt="">` : '<div class="noimg">🎵</div>'}
          <div style="min-width:0"><div class="name" title="Показати треки">${esc(p.name)}</div><div class="r muted small">${p.count} трек${p.count % 10 === 1 && p.count % 100 !== 11 ? '' : (p.count % 10 >= 2 && p.count % 10 <= 4 && (p.count % 100 < 10 || p.count % 100 >= 20)) ? 'и' : 'ів'} · ${esc(p.createdBy)}</div></div>
          <div class="btns"><button class="primary go" title="Закинути весь плейлист у чергу впереміш">▶ у чергу</button>${(me.role === 'admin' || sameNick(p.createdBy, me.nick)) ? `<button class="ghost danger del" title="Видалити плейлист">✕</button>` : ''}</div>
        </div><div class="pl-tracks" data-for="${p.id}" hidden></div>`).join('') || '<div class="empty">Плейлистів ще нема. Створи перший: назва вище, а треки додаються кнопкою «＋ плейлист» під тим, що грає, або «＋» в історії та улюбленому.</div>');
    box.querySelector('#plCreate').onsubmit = async (e) => {
      e.preventDefault();
      const inp = e.target.querySelector('input');
      try { ok(await api('POST', '/api/playlists', { name: inp.value })); inp.value = ''; renderPlaylists(); } catch (err) { fail(err); }
    };
    box.querySelectorAll('.pl').forEach((el) => {
      const id = el.dataset.id;
      el.querySelector('.go').onclick = (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/playlists/${id}/queue`, { shuffle: true }).then(ok).catch(fail));
      el.querySelector('.del')?.addEventListener('click', async () => {
        if (!confirm('Видалити плейлист?')) return;
        try { ok(await api('DELETE', `/api/playlists/${id}`)); renderPlaylists(); } catch (err) { fail(err); }
      });
      el.querySelector('.name').onclick = () => { if (openPl.has(id)) openPl.delete(id); else openPl.add(id); showPlTracks(id); };
      if (openPl.has(id)) showPlTracks(id);
    });
  }
  async function showPlTracks(id) {
    const box = document.querySelector(`.pl-tracks[data-for="${id}"]`);
    if (!box) return;
    if (!openPl.has(id)) { box.hidden = true; return; }
    box.hidden = false;
    box.innerHTML = '<div class="muted small"><span class="spin"></span></div>';
    try {
      const r = await api('GET', `/api/playlists/${id}`);
      box.innerHTML = `<ul class="list">${r.tracks.map((x) => `<li>
          ${x.track.thumbUrl ? `<img src="${esc(x.track.thumbUrl)}" alt="">` : '<div class="noimg"></div>'}
          <div style="min-width:0"><div class="t">${esc(x.track.title)} <span class="muted">· ${esc(x.track.artist)}</span></div><div class="r">${fmt(x.track.durationSec)} · додав ${esc(x.addedBy)}</div></div>
          <div class="btns"><button class="q" data-id="${esc(x.track.id)}">в чергу</button><button class="ghost danger rmt" data-id="${esc(x.track.id)}" title="Прибрати з плейлиста">✕</button></div>
        </li>`).join('') || '<li class="empty">порожньо</li>'}</ul>`;
      wireRows(box);
      box.querySelectorAll('.rmt').forEach((b) => b.onclick = async () => {
        try { ok(await api('DELETE', `/api/playlists/${id}/tracks/${b.dataset.id}`)); renderPlaylists(); } catch (err) { fail(err); }
      });
    } catch (e) { box.innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
  }

  // ---------- playlist picker modal ----------
  let plTarget = null;
  async function openPlaylistPicker(trackId, title) {
    plTarget = trackId;
    $('plModal').hidden = false;
    $('plModal').querySelector('h3').textContent = `«${title}» — у який плейлист?`;
    const pick = $('plPick');
    pick.innerHTML = '<span class="spin"></span>';
    try {
      const list = await api('GET', '/api/playlists');
      pick.innerHTML = list.map((p) => `<button data-id="${p.id}"><span>${esc(p.name)}</span><span class="muted small">${p.count}</span></button>`).join('') || '<div class="muted small">Плейлистів ще нема, створи перший нижче.</div>';
      pick.querySelectorAll('button').forEach((b) => b.onclick = () => addToPlaylist(b.dataset.id));
    } catch (e) { pick.innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
    setTimeout(() => $('plNewName').focus(), 50);
  }
  async function addToPlaylist(id) {
    try { ok(await api('POST', `/api/playlists/${id}/tracks`, { trackId: plTarget })); $('plModal').hidden = true; if (libTab === 'playlists') renderPlaylists(); }
    catch (e) { fail(e); }
  }
  $('plNewForm').onsubmit = async (e) => {
    e.preventDefault();
    const name = $('plNewName').value.trim();
    if (!name) return;
    try { const r = await api('POST', '/api/playlists', { name }); $('plNewName').value = ''; await addToPlaylist(r.id); }
    catch (err) { fail(err); }
  };
  $('plClose').onclick = () => { $('plModal').hidden = true; };
  $('plModal').addEventListener('click', (e) => { if (e.target === $('plModal')) $('plModal').hidden = true; });

  // ---------- realtime ----------
  function connect() {
    conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub?nick=' + encodeURIComponent(me.nick))
      .withAutomaticReconnect()
      .build();
    conn.on('state', (s) => { state = s; render(); });
    conn.on('chat', (m) => addMessage(m));
    conn.on('reaction', (r) => flyEmoji(r.emoji, r.nick));
    conn.on('chatHistory', (list) => {
      $('messages').innerHTML = '';
      $('log').innerHTML = '';
      list.forEach((m) => addMessage(m, false));
      $('messages').scrollTop = $('messages').scrollHeight;
      $('log').scrollTop = $('log').scrollHeight;
    });
    conn.onreconnected(() => { conn.invoke('SetNick', me.nick).catch(() => {}); toast('Знову на зв\'язку', 'ok'); });
    conn.onreconnecting(() => toast('Зв\'язок зник, підключаюсь…', 'wait'));
    conn.onclose(() => toast('Зв\'язок із сервером втрачено, онови сторінку', 'err'));
    conn.start().then(() => loadLib()).catch((e) => { toast('Не підключився: ' + e.message, 'err'); setTimeout(connect, 4000); });
  }

  // ---------- boot ----------
  setPlayUi();
  api('GET', '/api/me').then((m) => { me.role = m.role; $('nickBtn').classList.toggle('admin', me.role === 'admin'); if (state) render(); }).catch(() => {});
  if (me.nick) { $('nickBtn').textContent = me.nick; connect(); } else { askNick(); }
})();
