(() => {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = (sec) => { sec = Math.max(0, Math.floor(sec || 0)); const m = Math.floor(sec / 60), s = sec % 60; return `${m}:${String(s).padStart(2, '0')}`; };
  const tm = (iso) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
  const isUrl = (s) => /^https?:\/\/\S+$/i.test(s.trim());
  const isMobile = () => window.matchMedia('(max-width: 900px)').matches;
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  // Голосове — такий самий трек у черзі, тільки з нашим id і без обкладинки: замість неї мікрофон.
  const isVoice = (t) => !!t && String(t.id || '').startsWith('voice-');
  const cover = (t, attrs) => (t && t.thumbUrl
    ? `<img src="${esc(t.thumbUrl)}" alt=""${attrs ? ' ' + attrs : ''}>`
    : `<div class="noimg${isVoice(t) ? ' voice' : ''}">${isVoice(t) ? '🎙' : ''}</div>`);
  const voiceBtn = (t) => (isVoice(t) ? `<button class="ghost vplay" data-id="${esc(t.id)}" title="Послухати">▶</button>` : '');
  const EMOJIS = ['🔥', '❤️', '😂', '🕺', '🤘', '😴', '🤮', '🫠'];
  // Смайли для чату. Перша купка — ті самі, що літають над обкладинкою, далі просто по темах.
  const EMOJI_GROUPS = [
    { name: 'Ті, що літають', list: EMOJIS },
    { name: 'Пики', list: ['😀', '😁', '😂', '🤣', '😊', '😉', '😍', '😘', '😜', '🤪', '🤨', '🧐', '😎', '🥳', '😏', '🤤', '😢', '😭', '😤', '😡', '🤯', '😱', '🥶', '🤢', '🤒', '🤠', '🥴', '🤔', '🤫', '🙄', '😬', '🫡', '🤗', '🥺', '😇', '🤡', '💀', '👻'] },
    { name: 'Руки', list: ['👍', '👎', '👌', '🤙', '✌️', '🤝', '👏', '🙌', '🙏', '💪', '🫶', '👋', '🤌', '🖖', '☝️', '🤞'] },
    { name: 'Музика', list: ['🎵', '🎶', '🎧', '🎤', '🎸', '🥁', '🎹', '🎺', '🎻', '📻', '💃', '🔊', '⚡', '✨', '🎉', '🎊'] },
    { name: 'Всяке', list: ['🇺🇦', '🧡', '💛', '💚', '💙', '💜', '🖤', '💔', '⭐', '🌟', '🍺', '🍻', '☕', '🍕', '🌻', '🌚', '🌞', '🐈', '🐕', '⚽', '🏆', '🚀', '💩', '🥔'] },
  ];
  // «Тільки смайли» — таке повідомлення показуємо великим. Крім самих значків пускаємо пробіли,
  // селектор емодзі (FE0F), склейку (200D), відтінки шкіри і пари літер прапора.
  const EMOJI_TEXT = /^(?:\p{Extended_Pictographic}|\p{Regional_Indicator}|[\u{1F3FB}-\u{1F3FF}\uFE0F\u200D\s])+$/u;
  const EMOJI_SEQ = /\p{Extended_Pictographic}(?:[\u{1F3FB}-\u{1F3FF}]|\uFE0F)*(?:\u200D\p{Extended_Pictographic}(?:[\u{1F3FB}-\u{1F3FF}]|\uFE0F)*)*|\p{Regional_Indicator}{2}/gu;
  /// Скільки смайлів у рядку, якщо в ньому взагалі нема нічого іншого; інакше 0.
  function emojiCount(text) {
    const t = String(text || '').trim();
    if (!t || !EMOJI_TEXT.test(t)) return 0;
    return (t.match(EMOJI_SEQ) || []).length;
  }

  // account — чи це акаунт із паролем; інакше нік гостьовий, з приставкою «гість », і його дає сервер.
  let me = { nick: localStorage.getItem('nick') || '', role: 'member', account: false };
  let state = null;
  let conn = null;
  let searchTimer = null;
  let lastQuery = '';
  let lastPlayId = null;
  let unread = 0;
  let chatTab = 'chat';
  let libTab = 'history';
  let queueDur = [];
  let route = 'efir';                                        // efir | lib | games | chat (телефон)
  let chatOpen = localStorage.getItem('chatOpen') !== '0';   // балачки типово відкриті
  let baseTitle = 'Глечики';

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
  // Одна картка на всі випадки: зайти (паролем чи через Google), зареєструвати нік, піти гостем; після
  // Google для новенького — «як тебе кликати?» (gnick); а для того, хто вже в акаунті — «Ти — Влад»:
  // вийти, змінити пароль (password), прив'язати Google. Нік каже сервер: акаунт — із куки, гість —
  // «гість » + те, що набрав. Після входу чи виходу сторінка перезавантажується: хаб тримає одне
  // з'єднання на вкладку і чужу куку на льоту не підхопить.
  const NICK_HINTS = {
    login: 'Нік і пароль. Забув пароль — зайди через Google, якщо прив\'язував, або попроси адміна (/пароль у балачках).',
    register: 'Нік стане твоїм: під ним ніхто інший не напише, а глеки й ачівки під цим іменем — твої.',
    guest: 'Без пароля. Будеш «гість Вася»: усе, що заробиш, лежатиме під цим іменем.',
    gnick: 'Google тебе підтвердив. Обери нік — під ним тебе тут знатимуть, пароль не потрібен.',
    me: 'Вийти — і ти знову гість. Глеки й ачівки лишаються за ніком, зайдеш — усе на місці.',
    password: 'Новий пароль — хоча б 6 символів. Інші вкладки з цим акаунтом доведеться перезайти.',
  };
  const NICK_BUTTONS = {
    login: 'Зайти', register: 'Зареєструватись', guest: 'Заходжу як гість', gnick: 'Заходжу',
    me: 'Вийти з акаунта', password: 'Зберегти пароль',
  };
  let nickMode = 'register';
  let pendingGoogle = null;   // ID-токен від Google, поки новенький обирає нік
  const guestBody = (n) => String(n || '').replace(/^гість\s*/i, '').trim();
  function setNickMode(mode) {
    nickMode = mode;
    const isMe = mode === 'me' || mode === 'password';
    $('nickTabs').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.mode === mode));
    $('nickTabs').hidden = isMe || mode === 'gnick';
    $('nickInput').hidden = isMe;
    $('passCurrent').hidden = mode !== 'password' || !me.hasPassword;
    $('passInput').hidden = mode === 'guest' || mode === 'gnick' || mode === 'me';
    $('passInput').autocomplete = mode === 'login' ? 'current-password' : 'new-password';
    $('passInput').placeholder = mode === 'password' ? 'новий пароль' : 'пароль';
    $('nickTitle').textContent = isMe ? `Ти — ${me.nick}` : 'Хто прийшов?';
    $('nickHint').textContent = NICK_HINTS[mode];
    $('nickSave').textContent = NICK_BUTTONS[mode];
    $('nickSave').classList.toggle('primary', mode !== 'me');
    $('nickPass').hidden = mode !== 'me';
    $('nickPass').textContent = me.hasPassword ? 'Змінити пароль' : 'Поставити пароль';
    $('nickErr').hidden = true;
    paintMeInfo();
    paintGoogle();
  }
  function paintMeInfo() {
    const box = $('meInfo');
    box.hidden = nickMode !== 'me';
    if (box.hidden) return;
    const lines = [];
    if (me.google) lines.push(`Google прив'язано${me.email ? ' · ' + esc(me.email) : ''}.`);
    else lines.push('Прив\'яжи Google — заходитимеш без пароля, і нік не пропаде, якщо його забудеш.');
    if (!me.hasPassword) lines.push('Пароля нема: заходиш через Google. Хочеш — постав.');
    box.innerHTML = lines.map((l) => `<div class="muted">${l}</div>`).join('');
  }
  function askNick(force, mode, prefill) {
    if (me.nick && !force) return;
    setNickMode(mode || (me.account ? 'me' : me.nick ? 'login' : 'register'));
    $('nickInput').value = prefill !== undefined ? prefill : guestBody(me.nick);
    $('passInput').value = '';
    $('passCurrent').value = '';
    $('nickLater').hidden = !me.nick;   // без ніка на сайті робити нічого — картку не закрити
    $('nickModal').hidden = false;
    if (!$('nickInput').hidden) setTimeout(() => $('nickInput').focus(), 50);
    else if (nickMode === 'password') setTimeout(() => ($('passCurrent').hidden ? $('passInput') : $('passCurrent')).focus(), 50);
  }
  function paintNick() {
    const b = $('nickBtn');
    b.textContent = me.nick;
    b.classList.toggle('admin', me.role === 'admin');
    b.classList.toggle('guest', !me.account);
    b.title = me.account ? 'Твій акаунт' : 'Зайти в акаунт, зареєструвати нік або змінити гостьовий';
  }
  function showNickError(text) { $('nickErr').textContent = text; $('nickErr').hidden = false; }
  async function saveNick() {
    const n = $('nickInput').value.trim().slice(0, 24);
    const password = $('passInput').value;
    $('nickErr').hidden = true;
    try {
      if (nickMode === 'me') {
        await api('POST', '/api/account/logout');
        localStorage.removeItem('nick');
        location.reload();
        return;
      }
      if (nickMode === 'password') {
        if (password.length < 6) { showNickError('Пароль — хоча б 6 символів'); return; }
        await busy($('nickSave'), 'Зберігаю…', () => api('POST', '/api/account/password', { current: $('passCurrent').value, password }));
        me.hasPassword = true;
        $('nickModal').hidden = true;
        toast('Пароль збережено', 'ok');
        return;
      }
      if (!n) return;
      if (nickMode === 'guest') { await becomeGuest(n); return; }
      if (nickMode === 'gnick') {
        const r = await busy($('nickSave'), 'Заходжу…', () => api('POST', '/api/account/google', { credential: pendingGoogle, nick: n }));
        if (!r.ok) { showNickError(r.message || 'Не вийшло'); return; }
        localStorage.setItem('nick', r.nick);
        location.reload();
        return;
      }
      if (password.length < 6) { showNickError('Пароль — хоча б 6 символів'); return; }
      const r = await busy($('nickSave'), nickMode === 'login' ? 'Заходжу…' : 'Реєструю…',
        () => api('POST', `/api/account/${nickMode}`, { nick: n, password }));
      localStorage.setItem('nick', r.nick);
      location.reload();
    } catch (e) { showNickError(e.message); }
  }
  // Гостьовий нік остаточно складає сервер (приставка, довжина): питаємо /api/me з новим X-Nick.
  async function becomeGuest(n) {
    const was = me.nick;
    me.nick = n;
    let m;
    try { m = await api('GET', '/api/me'); }
    catch (e) { me.nick = was; throw e; }
    me.nick = m.nick;
    localStorage.setItem('nick', m.nick);
    $('nickModal').hidden = true;
    paintNick();
    if (m.nick !== was && conn && conn.state === 'Connected') conn.invoke('SetNick', m.nick).catch(() => {});
    if (!conn) connect();
    else render();
  }
  $('nickForm').onsubmit = (e) => { e.preventDefault(); saveNick(); };
  $('nickTabs').querySelectorAll('button').forEach((b) => b.onclick = () => { setNickMode(b.dataset.mode); $('nickInput').focus(); });
  $('nickPass').onclick = () => askNick(true, 'password');
  $('nickLater').onclick = () => { $('nickModal').hidden = true; };
  $('nickBtn').onclick = () => askNick(true);

  // ---------- вхід через Google ----------
  // Бібліотеку Google тягнемо лише коли сервер дав Client ID; вона сама малює кнопку в наш контейнер
  // і віддає підписаний ID-токен — його перевіряє сервер. Та сама кнопка в картці «Ти — …» — прив'язка.
  let googleReady = false;
  function loadGoogle(clientId) {
    if (!clientId || googleReady || document.getElementById('gsi')) return;
    const s = document.createElement('script');
    s.id = 'gsi';
    s.src = 'https://accounts.google.com/gsi/client';
    s.async = true;
    s.onload = () => {
      try {
        google.accounts.id.initialize({ client_id: clientId, callback: onGoogle, ux_mode: 'popup', itp_support: true });
        googleReady = true;
        if (!$('nickModal').hidden) paintGoogle();
      } catch (e) { console.warn('[google]', e); }
    };
    document.head.appendChild(s);
  }
  function paintGoogle() {
    const box = $('googleBox');
    const want = googleReady && (nickMode === 'login' || nickMode === 'register' || (nickMode === 'me' && !me.google));
    box.hidden = !want;
    if (!want) return;
    const el = $('googleBtn');
    el.innerHTML = '';
    const width = Math.max(200, Math.min(400, el.parentElement.clientWidth || 300));
    google.accounts.id.renderButton(el, {
      theme: 'filled_black', size: 'large', shape: 'pill', locale: 'uk', width,
      text: nickMode === 'me' ? 'continue_with' : 'signin_with',
    });
  }
  async function onGoogle(resp) {
    const credential = resp && resp.credential;
    if (!credential) return;
    $('nickErr').hidden = true;
    try {
      if (me.account) {
        await api('POST', '/api/account/google/link', { credential });
        toast('Google прив\'язано', 'ok');
        location.reload();
        return;
      }
      const r = await api('POST', '/api/account/google', { credential });
      if (r.needNick) { pendingGoogle = credential; askNick(true, 'gnick', r.suggest || ''); return; }
      if (!r.ok) { showNickError(r.message || 'Не вийшло'); return; }
      localStorage.setItem('nick', r.nick);
      location.reload();
    } catch (e) { showNickError(e.message); }
  }

  // ---------- player ----------
  const audio = $('audio');
  const vol = $('volume');
  // повзунок 0..100 іде по децибелах, а не лінійно: крок = 0.5 дБ, 1 → −50 дБ, 100 → 0 дБ.
  // Так тихі рівні мають десятки кроків замість двох-трьох. У localStorage лежить сама гучність 0..1.
  const VOL_DB = 50;
  const posToVol = (p) => p <= 0 ? 0 : Math.pow(10, -VOL_DB * (1 - p / 100) / 20);
  const volToPos = (v) => v <= 0 ? 0 : Math.min(100, Math.max(1, Math.round(100 * (1 + 20 * Math.log10(v) / VOL_DB))));
  function setVolPos(p, save) {
    p = Math.min(100, Math.max(0, p));
    vol.value = p;
    const v = posToVol(p);
    audio.volume = v;
    vol.title = p ? `Гучність ${p} (${(20 * Math.log10(v)).toFixed(1)} дБ) · колесо миші — по кроку` : 'Гучність: тиша';
    if (save) localStorage.setItem('volume', String(v));
  }
  const savedVol = parseFloat(localStorage.getItem('volume') ?? '');
  setVolPos(Number.isFinite(savedVol) ? volToPos(savedVol) : 90, false);
  vol.oninput = () => setVolPos(parseInt(vol.value, 10), true);
  // клац колеса = 1 крок; тачпад шле дрібні дельти, тож накопичуємо їх, щоб гучність не злітала
  let wheelAcc = 0;
  vol.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (e.deltaMode !== 0) wheelAcc = -Math.sign(e.deltaY) * 100;
    else wheelAcc -= e.deltaY;
    const steps = Math.trunc(wheelAcc / 100);
    if (!steps) return;
    wheelAcc -= steps * 100;
    setVolPos(parseInt(vol.value, 10) + steps, true);
  }, { passive: false });
  let playState = 'idle'; // idle | connecting | live
  function setPlayUi() {
    const b = $('playBtn');
    if (playState === 'idle') { b.className = 'primary'; b.textContent = '▶ Врубити'; b.title = 'Слухати ефір прямо тут'; }
    else if (playState === 'connecting') { b.className = 'primary busy'; b.innerHTML = '<span class="spin"></span> Підключаю…'; }
    else { b.className = 'live'; b.innerHTML = '<span class="dot"></span> В ефірі · Стоп'; b.title = 'Вимкнути'; }
  }
  // сервер записує, хто слухав кожен трек (вкладка «Рейтинг»); ETS2 і VLC він бачить лише числом у потоці
  let listening = false;
  function tellListening(on) {
    if (listening === on) return;
    listening = on;
    if (conn && conn.state === 'Connected') conn.invoke('SetListening', on).catch(() => {});
  }
  function stopAudio() {
    tellListening(false);
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
  audio.addEventListener('playing', () => { playState = 'live'; setPlayUi(); updateMediaSession(); tellListening(true); });
  audio.addEventListener('pause', () => tellListening(false));
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
      // «наступний трек» на навушниках і клавіатурі — це наш скіп: ефір один на всіх
      navigator.mediaSession.setActionHandler('nexttrack', () => skipNow());
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

  const reactsHtml = () => EMOJIS.map((e) => `<button data-e="${e}">${e}</button>`).join('');
  function wireReacts(box) {
    box.querySelectorAll('.reacts button').forEach((b) => b.onclick = () => {
      if (conn) conn.invoke('React', b.dataset.e).catch(() => {});
    });
  }
  const skipNow = () => api('POST', '/api/skip').then(ok).catch(fail);

  /// Лайк, скіп, плейлист і бан — один набір обробників на обидві копії трека (панель і шапка),
  /// щоб не тримати дві однакові гілки, які розійдуться від першої ж правки.
  function wireNow(box, t, o) {
    const at = (act) => box.querySelector(`[data-act="${act}"]`);
    at('like')?.addEventListener('click', (e) => busy(e.currentTarget, '', () => api('POST', `/api/like/${t.id}`).catch(fail)));
    at('skip')?.addEventListener('click', (e) => busy(e.currentTarget, o.mini ? '' : 'скіп…', skipNow));
    at('pl')?.addEventListener('click', () => openPlaylistPicker(t.id, t.title));
    at('ban')?.addEventListener('click', (e) => {
      const banPrice = me.role === 'admin' ? 0 : (me.banPrice || 0);
      const ask = banPrice
        ? `Забанити «${t.title}» назавжди за ${banPrice} 🏺?
Трек скіпнеться і більше не заграє. Викупити його з бану теж коштуватиме черепки.`
        : 'Забанити цей трек назавжди?';
      if (!confirm(ask)) return;
      busy(e.currentTarget, 'баню…', () => api('POST', `/api/ban/${t.id}`).then((r) => { ok(r); if (route === 'lib' && libTab === 'bans') loadLib(); }).catch(fail));
    });
  }

  /// Те, що в ефірі, малюється двічі з одного джерела: велика панель «В ефірі» (o.mini не задано)
  /// і міні-плеєр у шапці (o.mini). Шапка бере коротку версію — обкладинка, назва, ❤ і ⏭.
  function paintNowInto(box, o) {
    const n = state.now;
    const live = n.source === 'user' || n.source === 'autodj';
    const mini = !!o.mini;
    if (!live) {
      const spot = n.spotifyLive;
      const title = spot ? 'Spotify-резерв' : 'Тиша';
      const sub = spot ? (n.spotifyTitle || '') : `${dj()} шукає щось на полиці…`;
      box.innerHTML = mini
        ? `<a class="mini-cv" href="#efir" title="Перейти в Ефір"><img src="/static/glek.svg" alt=""></a>
           <a class="mini-tt" href="#efir" title="${esc(title)}"><b>${esc(title)}</b><small>${esc(sub)}</small></a>`
        : `<div class="coverwrap"><img class="cover dj" src="/static/glek.svg" alt=""></div>
        <div>
          <div class="title">${esc(title)}</div>
          <div class="artist">${esc(sub)}</div>
          <div class="by">${spot ? 'грає резервний потік, поки в черзі порожньо' : 'закинь щось або зачекай'}</div>
          <div class="reacts">${reactsHtml()}</div>
        </div>`;
      if (!mini) wireReacts(box);
      return;
    }
    const t = n.track || {};
    const liked = n.likers.some((x) => sameNick(x, me.nick));
    const pending = n.skipPending;
    const like = `<button data-act="like" class="${liked ? 'active' : ''}" title="${esc(n.likers.join(', ') || 'Лайкнути')}">❤ ${n.likers.length}</button>`;
    const skip = `<button data-act="skip" ${pending ? 'disabled' : ''} title="Перемкнути на наступний трек">⏭${mini ? '' : ' Скіп'}</button>`;
    if (mini) {
      box.innerHTML = `<a class="mini-cv" href="#efir" title="Перейти в Ефір">${cover(t)}</a>
        <a class="mini-tt" href="#efir" title="${esc(`${t.title} — ${t.artist}`)}"><b>${esc(t.title)}</b><small>${esc(t.artist)}</small></a>
        <span class="mini-acts">${like}${skip}</span>`;
      wireNow(box, t, o);
      return;
    }
    const by = n.source === 'user'
      ? `закинув <b>${esc(n.requestedBy)}</b>${n.via === 'suggestion' ? ` <span class="chip dj">порада ${esc(djGen())}</span>` : ''}`
      : `<b>${esc(dj())}</b> <span class="chip dj">авто</span>`;
    // адмін банить безкоштовно; решта — за черепки, і голосові не банять
    const banPrice = me.role === 'admin' ? 0 : (me.banPrice || 0);
    const canBan = me.role === 'admin' || (banPrice > 0 && !isVoice(t));
    box.innerHTML = `
      <div class="coverwrap">${t.thumbUrl ? `<img class="cover" src="${esc(t.thumbUrl)}" alt="">` : `<div class="cover placeholder">${isVoice(t) ? '🎙' : '♪'}</div>`}</div>
      <div style="min-width:0">
        <div class="title">${esc(t.title)}</div>
        <div class="artist">${esc(t.artist)}</div>
        <div class="by">${by}</div>
        ${n.reason ? `<div class="why">${esc(n.reason)}</div>` : ''}
        <div class="progress ${pending ? 'pending' : ''}"><div id="bar"></div></div>
        <div class="times"><span id="tElapsed">0:00</span><span>${fmt(n.durationSec)}</span></div>
        ${pending ? `<div class="pending-note"><span class="spin"></span> Перемикаю, в ефірі зміниться за кілька секунд</div>` : ''}
        <div class="actions">
          ${like}${skip}
          <button data-act="pl" title="Зберегти в плейлист">＋ плейлист</button>
          ${t.sourceUrl ? `<a class="chip" href="${esc(t.sourceUrl)}" target="_blank" rel="noopener">${isVoice(t) ? 'послухати ↗' : 'джерело ↗'}</a>` : ''}
          ${canBan ? `<button data-act="ban" class="danger ghost" title="${banPrice ? `Забанити назавжди за ${banPrice} черепків: трек скіпнеться і більше не заграє` : 'Забанити трек і скіпнути'}">🚫 бан${banPrice ? ` · ${banPrice} 🏺` : ''}</button>` : ''}
        </div>
        <div class="reacts" title="Реакція, яку побачать усі">${reactsHtml()}</div>
      </div>`;
    wireNow(box, t, o);
    wireReacts(box);
  }

  function renderNow() {
    const n = state.now;
    const sig = JSON.stringify([n.playId, n.itemId, n.source, n.track?.id, n.likers, n.skipPending, n.requestedBy, n.via, n.reason,
      n.durationSec, n.startedAt, n.spotifyLive, n.spotifyTitle, state.liquidsoapOk, state.listeners, me.role, me.nick, me.banPrice, state.siteName, state.djName]);
    if (sig === nowSig) return;
    nowSig = sig;
    const banner = $('banner');
    banner.hidden = state.liquidsoapOk;
    banner.textContent = 'Ефір не відповідає (liquidsoap). Черга збережеться, треки підуть, щойно він оживе.';
    // Зелений чіп «ефір» у шапці — шум: показуємо лише тоді, коли з ефіром щось не так.
    $('liqStatus').hidden = !!state.liquidsoapOk;
    $('liqStatus').className = 'chip err';
    $('liqStatus').textContent = 'ефір ↓';

    paintNowInto($('now'), {});
    paintNowInto($('nowMini'), { mini: true });

    const playingTrack = n.track && (n.source === 'user' || n.source === 'autodj');
    baseTitle = playingTrack ? `${n.track.title} — ${n.track.artist} · ${state.siteName}` : state.siteName;
    paintTitle();
    if (playState !== 'idle') updateMediaSession();
    tick();
  }

  function tick() {
    if (!state) return;
    const n = state.now;
    const live = n.source === 'user' || n.source === 'autodj';
    const d = live ? (n.durationSec || 0) : 0;
    let elapsed = 0, pct = 0;
    if (live) {
      const raw = (Date.now() - new Date(n.startedAt).getTime()) / 1000 - (state.streamDelaySeconds || 0);
      elapsed = Math.max(0, Math.min(raw, d || raw));
      pct = d ? Math.min(100, (elapsed / d) * 100) : 0;
    }
    const bar = $('bar'), el = $('tElapsed');
    if (bar && el) { el.textContent = fmt(elapsed); bar.style.width = d ? pct + '%' : '0%'; }
    // Смужка під шапкою: скільки лишилось треку видно з будь-якого розділу.
    $('hdrBar').style.width = d ? pct + '%' : '0%';
    $('hdrProg').classList.toggle('pending', !!n.skipPending);
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
    const sug0 = (state.suggestions || [])[0];
    const sig = JSON.stringify([q.map((it) => [it.itemId, it.status, it.error, it.requestedBy, it.via]), me.role, me.nick, state.djName, !q.length && sug0 && sug0.itemId]);
    if (sig === queueSig) { tick(); return; }
    queueSig = sig;
    $('queueCount').textContent = q.length ? `· ${q.length} · ${fmt(queueDur.reduce((a, b) => a + b, 0))}` : '';
    if (!q.length) {
      // Порожня черга — привід не виправдовуватись, а запропонувати перше, що Глек уже підібрав.
      ul.innerHTML = `<li class="empty queue-empty">
        <img src="/static/glek.svg" alt="">
        <div>Порожньо. Закинь щось, або хай ${esc(dj())} крутить своє.
        ${sug0 ? `<div class="qe-btn"><button id="qTakeSug" class="primary" title="${esc(`${sug0.track.artist} — ${sug0.track.title}`)}">👍 Закинути перше з порад ${esc(djGen())}</button></div>` : ''}</div>
      </li>`;
      if (sug0) $('qTakeSug').onclick = (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/suggest/${sug0.itemId}/add`).then(ok).catch(fail));
    } else {
      const now = Date.now();
      ul.innerHTML = q.map((it, i) => {
        const mine = sameNick(it.requestedBy, me.nick) || me.role === 'admin';
        const canMove = mine && it.status !== 'dispatched' && q.length > 1;
        const fresh = now - new Date(it.addedAt).getTime() < 4000;
        return `<li class="qitem ${it.status} ${fresh ? 'fresh' : ''} ${canMove ? 'movable' : ''}" data-id="${it.itemId}">
          <div class="n">${i + 1}</div>
          ${cover(it.track, 'draggable="false"')}
          <div style="min-width:0">
            <div class="t">${esc(it.track.title)}</div>
            <div class="a">${esc(it.track.artist)} · ${fmt(it.track.durationSec)}</div>
            <div class="meta"><span>${esc(it.requestedBy)}</span>${it.via === 'suggestion' ? `<span class="chip dj">порада ${esc(djGen())}</span>` : ''}${statusChip(it)}<span class="eta" data-eta="${i}"></span></div>
          </div>
          <div class="btns">
            ${voiceBtn(it.track)}
            ${canMove ? '<span class="grip" title="Тягни, щоб пересунути">⠿</span>' : ''}
            ${mine ? `<button class="icon danger rm" title="Прибрати">✕</button>` : ''}
          </div>
        </li>`;
      }).join('');
      ul.querySelectorAll('li').forEach((li) => {
        const id = li.dataset.id;
        li.querySelector('.rm')?.addEventListener('click', (e) => busy(e.currentTarget, '', () => api('DELETE', `/api/queue/${id}`).catch(fail)));
        wireVoiceButtons(li);
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
    const sig = JSON.stringify([list.map((s) => s.itemId), a && [a.itemId, a.status, a.error], seed && seed.id, state.suggestSeedNote, state.now.track && state.now.track.id, state.queue.length, state.djName]);
    if (sig === sugSig) { tick(); return; }
    sugSig = sig;
    const label = (t) => (t.artist ? `${t.artist} — ${t.title}` : t.title);
    const onAir = seed && state.now.track && state.now.track.id === seed.id;
    $('djSub').textContent = seed
      ? `Підбирає під ${state.suggestSeedNote || (onAir ? 'те, що зараз грає' : 'останнє, що грало')}: ${label(seed)}`
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

  // Хто в навушниках: ніки з плеєром на сайті плюс решта підключень до потоку (ETS2, VLC), яких по імені не видно.
  function listenersText() {
    const nicks = state.listeningNicks || [];
    const others = Math.max(0, (state.listeners || 0) - (state.listeningTabs || 0));
    const parts = [];
    if (nicks.length) parts.push('Слухають: ' + nicks.join(', '));
    if (others && state.listeningTabs !== undefined) parts.push(`${nicks.length ? 'ще ' : 'Слухають '}${others} через ETS2/VLC/інший плеєр`);
    else if (others) parts.push(`Слухають ${others}`);
    return parts.join('; ') || 'Зараз ніхто не слухає';
  }

  function renderOnline() {
    const nicks = state.listeningNicks || [];
    const listens = (n) => nicks.some((x) => sameNick(x, n));
    const shown = Math.max(state.listeners || 0, nicks.length);
    const chip = $('listeners');
    chip.textContent = '🎧 ' + shown + (nicks.length ? ' · ' + nicks.join(', ') : '');
    chip.title = listenersText();
    chip.classList.toggle('on', shown > 0);
    state.online.forEach(learnNick);
    const people = state.online.slice().sort((a, b) => listens(b) - listens(a));
    $('online').innerHTML = people.map((n) => listens(n)
      ? `<span class="chip listening" title="${esc(n)} зараз слухає ефір">🎧 ${esc(n)}${crownOf(n)}</span>`
      : `<span class="chip" title="на сайті, але плеєр вимкнений">${esc(n)}${crownOf(n)}</span>`).join('') || '<span class="muted small">нікого</span>';
  }
  $('listeners').onclick = () => { if (state) toast(listenersText()); };

  function render() {
    if (!state) return;
    $('siteName').textContent = state.siteName;
    $('micBtn').hidden = !state.voiceMaxSeconds || !canRecord();   // без https мікрофона браузер не дасть, нема чого й дражнити
    renderNow();
    renderQueue();
    renderOnline();
    renderSuggestions();
    if (state.now.playId !== lastPlayId) {
      lastPlayId = state.now.playId;
      if (route === 'lib' && (libTab === 'history' || libTab === 'bans' || libTab === 'ads')) loadLib();
    }
  }

  // ---------- кубик і команди чату ----------
  // Нова команда: рядок сюди і гілка в ChatCommands.Run на сервері.
  const COMMANDS = [
    { cmd: '/roll', args: '[N | A-B]', help: 'кинути кубик: /roll — 1–6, /roll 100 — 1–100, /roll 2-12 — свої межі' },
    { cmd: '/coin', args: '', help: 'монетка: орел чи решка. Аліас — /монетка' },
    { cmd: '/choose', args: 'а | б | в', help: 'обрати за тебе: /choose чай | кава | компот. Аліаси — /обери, /вибери' },
    { cmd: '/8ball', args: 'питання', help: 'спитати Дядька Глека: /8ball чи буде дощ? Аліаси — /куля, /глек' },
    { cmd: '/столи', args: '', help: 'які столи зараз живі — з кнопками. Бачиш лише ти. Аліас — /tables' },
    { cmd: '/пароль', args: 'нік новий_пароль', help: 'поставити людині новий пароль, коли вона свій забула. Лише адмін', admin: true },
  ];
  // Грані малюємо крапками самі: юнікодні ⚀⚁⚂ у кожному шрифті сидять у своєму квадраті по-своєму
  // і в плитці стоять криво. Індекси — клітинки сітки 3×3 зліва направо.
  const PIPS = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
  const isFace = (min, max) => min === 1 && max === 6;
  const pipsHtml = (v) => Array.from({ length: 9 }, (_, i) => `<i${PIPS[v].includes(i) ? ' class="on"' : ''}></i>`).join('');
  function paintDie(el, v, min, max) {
    if (isFace(min, max)) el.innerHTML = pipsHtml(v); else el.textContent = String(v);
  }

  /// Кубик падає згори і крутиться, поки не вляжеться на своє число.
  function rollDie(el, min, max, value) {
    el.classList.add('rolling');
    const until = performance.now() + 850;
    const tick = () => {
      if (performance.now() >= until) {
        paintDie(el, value, min, max);
        el.classList.remove('rolling');
        el.classList.add('landed');
        return;
      }
      paintDie(el, min + Math.floor(Math.random() * (max - min + 1)), min, max);
      setTimeout(tick, 70);
    };
    tick();
  }

  /// Монетка крутиться 0.8 с, і лише тоді видно, чим вона впала: результат приходить із сервера
  /// одразу, тож інтрига — це єдине, що фронт тут може додати. Анімація — Web Animations API,
  /// щоб не чіпати спільний style.css заради однієї команди.
  const COIN_MS = 800;
  function flipCoin(el, label) {
    label.style.visibility = 'hidden';
    if (el.animate) {
      el.animate([
        { transform: 'rotateX(0) scale(.8)' },
        { transform: 'rotateX(900deg) scale(1.2)', offset: .55 },
        { transform: 'rotateX(1800deg) scale(1)' },
      ], { duration: COIN_MS, easing: 'cubic-bezier(.3, 1.1, .5, 1)' });
    }
    setTimeout(() => { label.style.visibility = ''; }, COIN_MS);
  }

  function showCmdHint(typed) {
    const box = $('cmdHint');
    const q = (typed || '/').toLowerCase();
    const list = COMMANDS.filter((c) => (!c.admin || me.role === 'admin') && (c.cmd.startsWith(q.split(' ')[0]) || q === '/'));
    if (!list.length) { box.hidden = true; return; }
    box.innerHTML = list.map((c) => `<div class="cmd" data-cmd="${c.cmd}">
        <b>${esc(c.cmd)}</b> <span class="muted small">${esc(c.args)}</span>
        <div class="muted small">${esc(c.help)}</div>
      </div>`).join('');
    box.querySelectorAll('.cmd').forEach((el) => el.onclick = () => {
      $('chatInput').value = el.dataset.cmd + ' ';
      $('chatInput').focus();
      showCmdHint(el.dataset.cmd);
    });
    box.hidden = false;
  }
  const hideCmdHint = () => { $('cmdHint').hidden = true; };
  $('cmdBtn').onclick = () => { hideEmoji(); $('cmdHint').hidden ? showCmdHint($('chatInput').value) : hideCmdHint(); };
  $('chatInput').addEventListener('input', () => {
    const v = $('chatInput').value;
    if (v.startsWith('/')) showCmdHint(v); else hideCmdHint();
  });
  $('chatInput').addEventListener('keydown', (e) => { if (e.key === 'Escape') { hideCmdHint(); hideEmoji(); } });

  // ---------- смайли ----------
  // Панель відкривається над рядком вводу; клік ставить смайл туди, де стоїть курсор,
  // і панель лишається відкритою — щоб можна було накидати кілька підряд.
  $('emojiPick').innerHTML = EMOJI_GROUPS.map((g) => `<div class="egroup">
      <div class="muted small">${esc(g.name)}</div>
      <div class="erow">${g.list.map((e) => `<button type="button" data-e="${esc(e)}">${e}</button>`).join('')}</div>
    </div>`).join('');
  $('emojiPick').querySelectorAll('button').forEach((b) => b.onclick = () => putEmoji(b.dataset.e));
  const hideEmoji = () => { $('emojiPick').hidden = true; $('emojiBtn').classList.remove('on'); };
  function showEmoji() {
    hideCmdHint();
    $('emojiPick').hidden = false;
    $('emojiBtn').classList.add('on');
    $('chatInput').focus();
  }
  function putEmoji(e) {
    const inp = $('chatInput');
    const [from, to] = [inp.selectionStart ?? inp.value.length, inp.selectionEnd ?? inp.value.length];
    const text = inp.value.slice(0, from) + e + inp.value.slice(to);
    if (text.length > inp.maxLength) { toast('Задовге повідомлення', 'err'); return; }
    inp.value = text;
    inp.focus();
    inp.setSelectionRange(from + e.length, from + e.length);
  }
  $('emojiBtn').onclick = () => ($('emojiPick').hidden ? showEmoji() : hideEmoji());
  document.addEventListener('click', (e) => {
    // Поки клацаєш у самому чаті (вводиш, шлеш) — панель не зачиняється; клік по решті сторінки її прибирає.
    if (!$('emojiPick').hidden && !e.target.closest('#emojiPick, #chatForm')) hideEmoji();
  });

  // ---------- chat + log ----------
  const linkify = (s) => esc(s).replace(/(https?:\/\/[^\s<]+)/g, (m) => `<a href="${m}" target="_blank" rel="noopener">${m}</a>`);
  // Повідомлення справді видно, коли відкрита вкладка «Балачки», сама панель не згорнута
  // (на телефоні — коли стоїмо на вкладці балачок) і вкладка браузера на передньому плані.
  function chatVisible() {
    if (chatTab !== 'chat' || document.hidden) return false;
    return isMobile() ? route === 'chat' : chatOpen;
  }
  function setUnread(n) {
    unread = n;
    for (const id of ['chatBadge', 'mChatBadge', 'hdrChatBadge']) { const b = $(id); b.hidden = !n; b.textContent = n; }
    paintTitle();
  }
  /// Заголовок вкладки: трек плюс «(3)», поки непрочитане нікуди не поділось.
  function paintTitle() { document.title = (unread ? `(${unread}) ` : '') + baseTitle; }
  /// Кличуть на ім'я — навіть коли балачки згорнуті, це має долетіти.
  const mentionsMe = (text) => !!me.nick && me.nick.length > 1 && String(text || '').toLowerCase().includes(me.nick.toLowerCase());
  /// Тегнули саме через @ — це вже не просто згадка в розмові, а поклик: на нього й звук.
  const taggedMe = (text) => !!me.nick && String(text || '').toLowerCase().includes('@' + me.nick.toLowerCase());
  /// 👑 біля ніка чинного чемпіона турніру.
  const crownOf = (nick) => (window.HTournament && HTournament.crowned(nick) ? '<span class="crown" title="Чемпіон турніру">👑</span>' : '');

  /// Ніки, які сайт знає: хто зараз онлайн і хто писав у балачках. З них — підказка після @ і підсвітка.
  const knownNicks = new Map();          // нижній регістр → як пишеться
  function learnNick(n) { if (n && n.length > 1) knownNicks.set(n.toLowerCase(), n); }

  /// @Нік у тексті — підсвітити. Працює по вже екранованому HTML: ніки теж екрануємо, довші спершу («Оля Петрівна» раніше «Оля»).
  function highlightMentions(html) {
    const nicks = [...knownNicks.values()].sort((a, b) => b.length - a.length);
    if (!nicks.length || html.indexOf('@') < 0) return html;
    const escRe = (x) => x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const re = new RegExp('@(' + nicks.map((n) => escRe(esc(n))).join('|') + ')(?![\\p{L}\\p{N}_])', 'giu');
    return html.replace(re, (m, n) => `<span class="mention${sameNick(n, esc(me.nick)) ? ' me' : ''}">${m}</span>`);
  }

  // ---------- звук на поклик ----------
  // Коли тебе тегнули через @ або відповіли — коротке «дзінь» (два тони через WebAudio, без файлів). Вимикається 🔔.
  let pingOn = localStorage.getItem('pingSound') !== '0';
  let audioCtx = null;
  function ping() {
    if (!pingOn) return;
    try {
      audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
      if (audioCtx.state === 'suspended') audioCtx.resume();
      const t0 = audioCtx.currentTime;
      [[880, 0], [1320, 0.12]].forEach(([freq, at]) => {
        const o = audioCtx.createOscillator();
        const g = audioCtx.createGain();
        o.type = 'sine';
        o.frequency.value = freq;
        g.gain.setValueAtTime(0.0001, t0 + at);
        g.gain.exponentialRampToValueAtTime(0.18, t0 + at + 0.015);
        g.gain.exponentialRampToValueAtTime(0.0001, t0 + at + 0.35);
        o.connect(g).connect(audioCtx.destination);
        o.start(t0 + at);
        o.stop(t0 + at + 0.4);
      });
    } catch { /* браузер без WebAudio — мовчки */ }
  }
  // Браузер дає звук лише після першого жесту на сторінці — будимо контекст на першому ж кліку/клавіші.
  const wakeAudio = () => {
    try { audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)(); if (audioCtx.state === 'suspended') audioCtx.resume(); } catch { /* нема — то й нема */ }
  };
  document.addEventListener('pointerdown', wakeAudio, { once: true });
  document.addEventListener('keydown', wakeAudio, { once: true });
  function paintPing() {
    const b = $('pingBtn');
    b.textContent = pingOn ? '🔔' : '🔕';
    b.title = pingOn ? 'Звук, коли тебе тегнули чи відповіли — увімкнено' : 'Звук на @ і відповіді вимкнено';
  }
  $('pingBtn').onclick = () => {
    pingOn = !pingOn;
    try { localStorage.setItem('pingSound', pingOn ? '1' : '0'); } catch { /* приватне вікно */ }
    paintPing();
    if (pingOn) ping();
  };
  paintPing();
  /// Кнопка до столу біля рядка балачок: «Сісти», поки є куди, інакше «Дивитись». Столу вже нема —
  /// кнопки теж нема: мертве посилання гірше, ніж його відсутність. Що там за стіл, знає HGames.
  /// <c>named</c> — чи назвати гру на самій кнопці; у рядку Журналу вона вже названа в тексті.
  function roomBtn(id, named) {
    const link = window.HGames && HGames.roomLink && HGames.roomLink(id);
    if (!link) return null;
    const b = document.createElement('button');
    b.className = 'roomlink' + (link.canSit ? ' primary' : '');
    b.title = link.who;
    const paint = () => {
      const now = HGames.roomLink(id) || link;
      b.innerHTML = (named ? now.title : now.icon) + '<span class="rl-go">' + esc(now.label) + '</span>';
    };
    paint();
    // Іконка гри приходить із її модулем, а він міг ще не завантажитись: домалюємо, щойно прилетить.
    if (HGames.ensureIcon) HGames.ensureIcon(link.game, paint);
    b.onclick = () => ((HGames.roomLink(id) || link).canSit
      ? busy(b, 'сідаю…', () => HGames.sitAt(id))
      : HGames.openAt(id));
    return b;
  }
  /// Кнопки до столів у вже намальованих рядках. Слот у рядку стоїть завжди, кнопка в ньому —
  /// поки стіл живий: стіл заповнився чи його прибрали — рядок міняється разом із ним, а не бреше.
  function paintRoomSlots(root) {
    for (const slot of (root || document).querySelectorAll('.roomslot')) {
      const b = roomBtn(slot.dataset.room, slot.dataset.named === '1');
      slot.textContent = '';
      if (b) slot.appendChild(b);
    }
  }
  function addMessage(m, scroll = true, live = false) {
    const isLog = m.kind === 'system';
    const box = isLog ? $('log') : $('messages');
    const el = document.createElement('div');
    const mine = sameNick(m.nick, me.nick);
    // Монетка живе в тій самій розкладці, що й кубик (.msg.dice — рядок у флексі); /choose і /8ball
    // це звичайні рядки з іконкою в самому тексті, тож їм окрема гілка ні до чого.
    el.className = 'msg ' + (isLog ? 'system' : m.kind === 'dj' ? 'dj'
      : m.kind === 'dice' ? 'dice' : m.kind === 'coin' ? 'dice coin'
        : m.kind === 'tables' ? 'tables' : mine ? 'mine' : '');
    if (m.kind === 'tables') {
      // Відповідь на /столи бачить лише той, хто спитав: це погляд у лобі, не виходячи з балачок,
      // а не репліка. Тому вона й у базу не лягає — після F5 її не буде, і це правильно.
      el.innerHTML = '<span class="n">🎲 ' + esc(m.text) + '</span><span class="time">лише тобі</span>'
        + '<div class="tlist">'
        + (m.rooms || []).map((id) => '<span class="roomslot" data-named="1" data-room="' + esc(id) + '"></span>').join('')
        + '</div>';
      paintRoomSlots(el);
      if (!el.querySelector('.roomlink')) el.querySelector('.tlist').innerHTML = '<span class="muted small">Столи щойно розібрали.</span>';
    } else if (m.kind === 'coin') {
      // Бік читаємо хвостом рядка, а не першим словом: боків колись може стати більше («Стало ребром»),
      // і двослівний не має лишити порожню плитку. Формат не впізнали — малюємо текст як є.
      const hit = /🪙\s+(.+)$/.exec(m.text || '');
      const side = hit ? hit[1].trim() : String(m.text || '').replace('🪙', '').trim();
      el.classList.toggle('mine', mine);
      el.innerHTML = `<span class="n">${esc(m.nick)}</span><span class="die"></span>`
        + `<span class="t">${esc(side || '')}</span><span class="time">${tm(m.at)}</span>`;
      const coin = el.querySelector('.die');
      coin.textContent = '🪙';
      if (live) flipCoin(coin, el.querySelector('.t'));
    } else if (m.kind === 'dice') {
      const [, value, min, max] = /🎲 (\d+) \((\d+)–(\d+)\)/.exec(m.text) || [];
      const [v, lo, hi] = [+value, +min, +max];
      el.classList.toggle('mine', mine);
      el.innerHTML = `<span class="n">${esc(m.nick)}</span><span class="die${isFace(lo, hi) ? ' face' : ''}"></span>`
        + `<span class="muted small">з ${lo}–${hi}</span><span class="time">${tm(m.at)}</span>`;
      const die = el.querySelector('.die');
      paintDie(die, v, lo, hi);
      if (live) rollDie(die, lo, hi, v);
    } else if (m.kind === 'dj') {
      el.innerHTML = `<img src="/static/glek.svg" alt=""><div><span class="n">${esc(m.nick)}</span>${linkify(m.text)}<span class="time">${tm(m.at)}</span></div>`;
    } else if (isLog) {
      el.innerHTML = `<span class="time">${tm(m.at)}</span>${linkify(m.text)}`;
    } else {
      // Саме лише «🔥» — не рядок тексту, а жест: показуємо на весь зріст, поки їх не набралося багато.
      const big = emojiCount(m.text);
      if (big && big <= 3) el.classList.add('big');
      el.innerHTML = `<span class="n">${esc(m.nick)}${crownOf(m.nick)}</span><span class="t">${highlightMentions(linkify(m.text))}</span><span class="time">${tm(m.at)}</span>`;
    }
    if (!isLog && m.kind !== 'tables' && m.id > 0) decorateMessage(el, m);
    // Рядок про живий стіл («Новий стіл: Мафія», «Оля і Петро сіли грати») носить його id — лишаємо
    // слот під кнопку, щоб до столу можна було дійти прямо звідси (PLAN.md §7.4).
    if (m.roomId && m.kind !== 'tables') {
      const slot = document.createElement('span');
      slot.className = 'roomslot';
      slot.dataset.room = m.roomId;
      el.appendChild(slot);
      paintRoomSlots(el);
    }
    box.appendChild(el);
    while (box.children.length > 300) box.firstChild.remove();
    if (scroll) box.scrollTop = box.scrollHeight;
    if (!isLog) learnNick(m.nick);
    const repliedMe = !mine && !isLog && sameNick(m.replyNick, me.nick);
    const tagged = !mine && !isLog && m.kind === 'chat' && taggedMe(m.text);
    if (repliedMe || tagged) el.classList.add('tome');
    // звук — лише на живе повідомлення, не на історію після F5
    if (live && (repliedMe || tagged)) ping();
    if (!isLog && scroll && !mine && !chatVisible()) {
      setUnread(unread + 1);
      if (repliedMe) toast(`↩ ${m.nick} відповідає тобі: ${m.text}`.slice(0, 140));
      else if (tagged) toast(`@ ${m.nick} кличе тебе: ${m.text}`.slice(0, 140));
      else if (mentionsMe(m.text)) toast(`${m.nick}: ${m.text}`.slice(0, 140));
    }
  }

  // ---------- ❤ і відповіді в балачках ----------
  // Кожне повідомлення людини чи Глека (не рядок Журналу) можна лайкнути й на нього відповісти. Кнопки
  // з'являються при наведенні (на телефоні — після тапу по повідомленню); подвійний клік — теж ❤.
  let replyTo = null;               // { id, nick, text } — на що зараз відповідаємо

  function decorateMessage(el, m) {
    el.dataset.id = String(m.id);
    el.dataset.nick = m.nick;
    const host = el.querySelector(':scope > div') || el;     // у Глека текст живе у внутрішньому div
    if (m.replyTo) {
      const q = document.createElement('div');
      q.className = 'rq';
      q.dataset.to = String(m.replyTo);
      q.title = 'До цього повідомлення';
      q.innerHTML = `↪ <b>${esc(m.replyNick || '')}</b> ${esc(m.replyText || '')}`;
      host.prepend(q);
    }
    const acts = document.createElement('span');
    acts.className = 'macts';
    acts.innerHTML = '<button type="button" class="ghost" data-a="like" title="❤ (подвійний клік — теж)">❤</button>'
      + '<button type="button" class="ghost" data-a="reply" title="Відповісти">↩</button>';
    el.appendChild(acts);
    const likes = document.createElement('button');
    likes.type = 'button';
    likes.className = 'mlikes';
    host.appendChild(likes);
    paintLikes(el, m.likes || []);
  }

  function paintLikes(el, likes) {
    const chip = el.querySelector('.mlikes');
    if (!chip) return;
    chip.hidden = !likes.length;
    chip.textContent = '❤ ' + likes.length;
    chip.title = likes.join(', ');
    chip.classList.toggle('on', likes.some((n) => sameNick(n, me.nick)));
    el.classList.toggle('liked', likes.some((n) => sameNick(n, me.nick)));
  }

  /// Корона переїхала — перемалювати ніки у вже намальованих повідомленнях.
  function repaintCrowns() {
    $('messages').querySelectorAll('.msg .n').forEach((n) => {
      const msg = n.closest('.msg');
      const nick = msg && msg.dataset.nick;
      if (!nick) return;
      const old = n.querySelector('.crown');
      if (old) old.remove();
      if (window.HTournament && HTournament.crowned(nick)) n.insertAdjacentHTML('beforeend', crownOf(nick));
    });
  }

  function likeMessage(id) {
    if (!conn || !id) return;
    conn.invoke('LikeChat', id).then((err) => { if (err) toast(err, 'err'); }).catch(() => {});
  }

  function setReply(el) {
    const id = +el.dataset.id;
    if (!id) return;
    const t = el.querySelector('.t') || el.querySelector(':scope > div');
    // текст без цитати й без кнопок: беремо лише саму репліку
    let text = '';
    if (t) {
      const clone = t.cloneNode(true);
      clone.querySelectorAll('.rq, .n, .time, .mlikes, .macts').forEach((x) => x.remove());
      text = clone.textContent.trim();
    }
    replyTo = { id, nick: el.dataset.nick || '', text };
    $('replyBar').hidden = false;
    $('replyBar').querySelector('.rb-text').innerHTML = `↩ Відповідь <b>${esc(replyTo.nick)}</b>: ${esc(text.slice(0, 80))}`;
    if (chatTab !== 'chat') setChatTab('chat');
    $('chatInput').focus();
  }

  function clearReply() {
    replyTo = null;
    $('replyBar').hidden = true;
  }

  $('replyCancel').onclick = () => { clearReply(); $('chatInput').focus(); };

  // ---------- @: підказка ніків ----------
  // Набираєш «@о» — випадає список тих, кого сайт знає (спершу онлайн). ↑↓ — вибір, Enter/Tab — вставити, Esc — закрити.
  let mentionSel = 0;
  function mentionQuery() {
    const inp = $('chatInput');
    const upto = inp.value.slice(0, inp.selectionStart ?? inp.value.length);
    const hit = /(^|\s)@([^\s@]*)$/.exec(upto);
    return hit ? { q: hit[2].toLowerCase(), start: upto.length - hit[2].length - 1 } : null;
  }
  function mentionList(q) {
    const online = new Set(((state && state.online) || []).map((n) => n.toLowerCase()));
    return [...knownNicks.values()]
      .filter((n) => !sameNick(n, me.nick) && n.toLowerCase().startsWith(q))
      .sort((a, b) => (online.has(b.toLowerCase()) - online.has(a.toLowerCase())) || a.localeCompare(b, 'uk'))
      .slice(0, 6);
  }
  function hideMentions() { $('mentionPick').hidden = true; }
  function showMentions() {
    const m = mentionQuery();
    const box = $('mentionPick');
    const list = m ? mentionList(m.q) : [];
    if (!list.length) { hideMentions(); return; }
    mentionSel = Math.min(mentionSel, list.length - 1);
    const online = new Set(((state && state.online) || []).map((n) => n.toLowerCase()));
    box.innerHTML = list.map((n, i) => `<button type="button" class="${i === mentionSel ? 'on' : ''}" data-nick="${esc(n)}">`
      + `@${esc(n)}${crownOf(n)}${online.has(n.toLowerCase()) ? ' <span class="dot" title="на сайті"></span>' : ''}</button>`).join('');
    box.hidden = false;
    box.querySelectorAll('button').forEach((b) => b.onmousedown = (e) => { e.preventDefault(); insertMention(b.dataset.nick); });
  }
  function insertMention(nick) {
    const m = mentionQuery();
    const inp = $('chatInput');
    if (!m) return;
    const caret = inp.selectionStart ?? inp.value.length;
    inp.value = inp.value.slice(0, m.start) + '@' + nick + ' ' + inp.value.slice(caret);
    const pos = m.start + nick.length + 2;
    inp.setSelectionRange(pos, pos);
    hideMentions();
    inp.focus();
  }
  $('chatInput').addEventListener('input', () => { mentionSel = 0; showMentions(); });
  $('chatInput').addEventListener('click', showMentions);
  $('chatInput').addEventListener('blur', () => setTimeout(hideMentions, 150));
  $('chatInput').addEventListener('keydown', (e) => {
    if ($('mentionPick').hidden) return;
    const buttons = [...$('mentionPick').querySelectorAll('button')];
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      mentionSel = (mentionSel + (e.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
      showMentions();
    } else if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      e.stopImmediatePropagation();
      if (buttons[mentionSel]) insertMention(buttons[mentionSel].dataset.nick);
    } else if (e.key === 'Escape') {
      e.stopImmediatePropagation();
      hideMentions();
    }
  });
  $('chatInput').addEventListener('keydown', (e) => { if (e.key === 'Escape' && replyTo) clearReply(); });

  $('messages').addEventListener('click', (e) => {
    const el = e.target.closest('.msg[data-id]');
    if (!el) return;
    const btn = e.target.closest('[data-a], .mlikes');
    if (btn) {
      if (btn.classList.contains('mlikes') || btn.dataset.a === 'like') likeMessage(+el.dataset.id);
      else if (btn.dataset.a === 'reply') setReply(el);
      el.classList.remove('act');
      return;
    }
    const quote = e.target.closest('.rq');
    if (quote) {
      const orig = $('messages').querySelector(`.msg[data-id="${quote.dataset.to}"]`);
      if (orig) {
        orig.scrollIntoView({ block: 'center', behavior: 'smooth' });
        orig.classList.remove('flash');
        void orig.offsetWidth;
        orig.classList.add('flash');
      } else toast('Це повідомлення вже випало з історії');
      return;
    }
    if (e.target.closest('a, button')) return;
    // на телефоні наведення нема: тап по повідомленню показує кнопки, тап по іншому — ховає
    $('messages').querySelectorAll('.msg.act').forEach((x) => { if (x !== el) x.classList.remove('act'); });
    el.classList.toggle('act');
  });
  $('messages').addEventListener('dblclick', (e) => {
    const el = e.target.closest('.msg[data-id]');
    if (!el || e.target.closest('a, button, .rq')) return;
    window.getSelection()?.removeAllRanges();
    likeMessage(+el.dataset.id);
  });
  $('chatForm').onsubmit = (e) => {
    e.preventDefault();
    const text = $('chatInput').value.trim();
    if (!text || !conn) return;
    if (chatTab !== 'chat') setChatTab('chat');
    const call = replyTo ? conn.invoke('SendReply', text, replyTo.id) : conn.invoke('SendChat', text);
    call
      .then((err) => { if (err) { toast(err, 'err'); return; } $('chatInput').value = ''; hideCmdHint(); clearReply(); })
      .catch((err) => toast('Не відправилось: ' + err.message, 'err'));
  };
  function setChatTab(tab) {
    chatTab = tab;
    $('chatTabs').querySelectorAll('button[data-tab]').forEach((b) => b.classList.toggle('on', b.dataset.tab === tab));
    $('messages').hidden = tab !== 'chat';
    $('log').hidden = tab !== 'log';
    const box = tab === 'chat' ? $('messages') : $('log');
    box.scrollTop = box.scrollHeight;
    if (chatVisible()) setUnread(0);
  }
  // Лише вкладки: у тому ж рядку живуть 🔔 і ✕, у них свої обробники — інакше клік по дзвіночку «відкривав» порожню вкладку.
  $('chatTabs').querySelectorAll('button[data-tab]').forEach((b) => b.onclick = () => setChatTab(b.dataset.tab));

  // ---------- маршрути ----------
  // Кожен екран має адресу: #efir, #lib/<вкладка>, #games(/…), #chat (вкладка балачок на телефоні).
  // Хеш — єдине джерело істини: кнопки лише ставлять його, малює applyRoute(), F5 повертає на місце.
  const ROUTES = ['efir', 'lib', 'games', 'chat'];
  const LIB_TABS = ['history', 'likes', 'playlists', 'rating', 'top', 'bans', 'ads'];
  const ROUTE_TITLE = { efir: 'Ефір', lib: 'Бібліотека', games: 'Ігри', chat: 'Балачки' };
  const LIB_TITLE = { history: 'Що вже було', likes: 'Улюблене', playlists: 'Плейлисти', rating: 'Рейтинг', top: 'Хто скільки', bans: 'Бан-лист', ads: 'Реклама' };
  // Вкладки зі списком рядків уміють шукати по собі; у плейлистах і «Хто скільки» шукати нічого.
  const LIB_FIND = { history: 'знайти в історії', likes: 'знайти в улюбленому', rating: 'знайти трек', bans: 'знайти в бан-листі', ads: 'знайти рекламу' };
  let libShown = null;    // яку вкладку бібліотеки вже намалювали: щоб не смикати API на кожен маршрут

  function parseHash() {
    const raw = String(location.hash || '').replace(/^#/, '');
    const i = raw.indexOf('/');
    return i < 0 ? { head: raw, tail: '' } : { head: raw.slice(0, i), tail: raw.slice(i + 1) };
  }
  const hashFor = (r) => (r === 'lib' ? '#lib/' + libTab : '#' + r);
  function go(hash) {
    if (location.hash === hash) applyRoute(); else location.hash = hash;
  }

  let lastHash = null;
  function applyRoute() {
    const { head, tail } = parseHash();
    // Перейшов кудись — починаємо згори: інакше з довгого лобі потрапляєш на стіл уже прогорнутим.
    if (lastHash !== null && lastHash !== location.hash) window.scrollTo(0, 0);
    lastHash = location.hash;
    let r = ROUTES.includes(head) ? head : 'efir';
    // На широкому екрані балачки — панель збоку, а не розділ: #chat лише розгортає її.
    if (r === 'chat' && !isMobile()) {
      setChatOpen(true);
      history.replaceState(null, '', hashFor('efir'));
      r = 'efir';
    }
    if (r === 'lib') {
      const want = decodeURIComponent(tail);
      const next = LIB_TABS.includes(want) ? want : 'history';
      if (next !== libTab) { libTab = next; $('libFind').value = ''; }
      $('libTitle').textContent = LIB_TITLE[libTab];
      $('libFind').hidden = !LIB_FIND[libTab];
      $('libFind').placeholder = LIB_FIND[libTab] || '';
    }
    route = r;
    $('hdrTitle').textContent = ROUTE_TITLE[r];    // на телефоні в шапці лишається сама назва розділу
    for (const name of ROUTES) document.body.classList.toggle('route-' + name, r === name);
    document.querySelectorAll('#mainNav button, .mtabs button').forEach((b) => b.classList.toggle('on',
      b.dataset.route === r || (r === 'chat' && b.dataset.route === 'chat')));
    $('libTabs').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.tab === libTab));
    if (r === 'games') HGames.show(head === 'games' ? tail : ''); else HGames.hide();
    if (r === 'lib' && libShown !== libTab) { libShown = libTab; loadLib(); }
    if (r === 'chat') { const box = $('messages'); box.scrollTop = box.scrollHeight; }
    if (chatVisible()) setUnread(0);
  }
  window.addEventListener('hashchange', applyRoute);
  document.querySelectorAll('#mainNav button, .mtabs button').forEach((b) => b.onclick = () => go(hashFor(b.dataset.route)));
  document.addEventListener('visibilitychange', () => { if (chatVisible()) setUnread(0); });

  // ---------- балачки: згорнути / розгорнути ----------
  function setChatOpen(on) {
    chatOpen = on;
    try { localStorage.setItem('chatOpen', on ? '1' : '0'); } catch { /* приватне вікно */ }
    document.body.classList.toggle('chat-collapsed', !on);
    if (on) { const box = chatTab === 'chat' ? $('messages') : $('log'); box.scrollTop = box.scrollHeight; }
    if (chatVisible()) setUnread(0);
  }
  const toggleChat = () => (isMobile() ? go(route === 'chat' ? hashFor('efir') : '#chat') : setChatOpen(!chatOpen));
  $('chatToggle').onclick = toggleChat;
  $('chatClose').onclick = () => setChatOpen(false);
  setChatOpen(chatOpen);

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
  // Каркас ігор слухає document раніше за нас (core.js підключений вище за app.js), тож клавішу,
  // яку вже з'їла гра, він позначає preventDefault — і ми в неї не лізимо.
  document.addEventListener('keydown', (e) => {
    if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.altKey) return;
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    if (e.key === '/') { e.preventDefault(); go(hashFor('efir')); q.focus(); return; }
    if (e.key === ']') { e.preventDefault(); toggleChat(); return; }
    if (e.key === '1') { e.preventDefault(); go(hashFor('efir')); }
    else if (e.key === '2') { e.preventDefault(); go(hashFor('lib')); }
    else if (e.key === '3') { e.preventDefault(); go(hashFor('games')); }
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

  // ---------- голосові: записати і поставити в чергу ----------
  // MediaRecorder пише в тому форматі, який уміє браузер (webm/opus, у Safari mp4) — сервер сам
  // перегонить його в mp3 і кладе в кеш, далі запис іде чергою як звичайний трек.
  const recBox = $('rec');
  const voiceMax = () => (state && state.voiceMaxSeconds) || 0;
  const canRecord = () => !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
  const MIMES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
  let recorder = null, recStream = null, recChunks = [], recStartedAt = 0, recTimer = 0, recTossed = false;
  let recBlob = null, recUrl = null, recActx = null, recAnalyser = null, recRaf = 0;

  async function startRec() {
    if (recorder) return;
    if (!voiceMax()) { toast('Голосові вимкнені', 'err'); return; }
    if (!canRecord()) { toast('Цей браузер не вміє писати звук (потрібен https і свіжий Chrome, Firefox або Safari)', 'err'); return; }
    if (!me.nick) { askNick(); return; }
    let stream;
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
    catch (e) { toast(e.name === 'NotAllowedError' ? 'Мікрофон не дозволено — дозволь у браузері й спробуй ще' : 'Мікрофон не відкрився: ' + e.message, 'err'); return; }
    dropRecBlob();
    recStream = stream;
    recChunks = [];
    recTossed = false;
    const type = MIMES.find((m) => MediaRecorder.isTypeSupported(m));
    try { recorder = new MediaRecorder(stream, type ? { mimeType: type, audioBitsPerSecond: 96000 } : undefined); }
    catch { recorder = new MediaRecorder(stream); }
    recorder.ondataavailable = (e) => { if (e.data && e.data.size) recChunks.push(e.data); };
    recorder.onstop = finishRec;
    recorder.start();
    recStartedAt = Date.now();
    drawRecLive();
    recTimer = setInterval(() => {
      const sec = (Date.now() - recStartedAt) / 1000;
      const el = $('recTime');
      if (el) el.textContent = fmt(sec);
      if (sec >= voiceMax()) stopRec();   // довше сервер усе одно відріже
    }, 200);
    startMeter(stream);
  }

  function stopRec() {
    clearInterval(recTimer);
    recTimer = 0;
    if (recorder && recorder.state !== 'inactive') { try { recorder.stop(); } catch { /* уже стало */ } }
  }
  function cancelRec() { recTossed = true; stopRec(); }

  function finishRec() {
    const type = (recorder && recorder.mimeType) || 'audio/webm';
    const sec = Math.round((Date.now() - recStartedAt) / 1000);
    const blob = new Blob(recChunks, { type });
    recorder = null;
    recChunks = [];
    stopMeter();
    releaseMic();
    if (recTossed) { closeRec(); return; }
    if (blob.size < 1024) { closeRec(); toast('Нічого не записалось, спробуй ще раз', 'err'); return; }
    recBlob = blob;
    recUrl = URL.createObjectURL(blob);
    drawRecPreview(sec);
  }

  function drawRecLive() {
    recBox.hidden = false;
    recBox.className = 'rec live';
    recBox.innerHTML = `<span class="rec-dot"></span><span id="recTime" class="rec-time">0:00</span>
      <div class="rec-bars">${'<i></i>'.repeat(16)}</div>
      <span class="muted small">ліміт ${fmt(voiceMax())}</span>
      <button id="recStop" class="primary">Готово</button>
      <button id="recCancel" class="ghost icon" title="Викинути">✕</button>`;
    $('recStop').onclick = stopRec;
    $('recCancel').onclick = cancelRec;
  }

  function drawRecPreview(sec) {
    recBox.hidden = false;
    recBox.className = 'rec prev';
    recBox.innerHTML = `<span class="rec-mic">🎙</span><audio controls src="${recUrl}"></audio><span class="chip">${fmt(sec)}</span>
      <button id="recSend" class="primary">Закинути в чергу</button>
      <button id="recAgain" class="ghost">Ще раз</button>
      <button id="recDrop" class="ghost icon danger" title="Викинути">✕</button>`;
    $('recSend').onclick = (e) => busy(e.currentTarget, 'несу…', sendRec);
    $('recAgain').onclick = () => { closeRec(); startRec(); };
    $('recDrop').onclick = closeRec;
  }

  async function sendRec() {
    if (!recBlob) return;
    try {
      const r = await fetch('/api/voice', {
        method: 'POST',
        headers: { 'Content-Type': recBlob.type || 'application/octet-stream', 'X-Nick': encodeURIComponent(me.nick) },
        body: recBlob,
      });
      let data = null;
      try { data = await r.json(); } catch { /* без тіла */ }
      if (!r.ok) throw new Error((data && data.message) || `HTTP ${r.status}`);
      ok(data);
      closeRec();
    } catch (e) { fail(e); }
  }

  function closeRec() {
    clearInterval(recTimer);
    recTimer = 0;
    stopMeter();
    releaseMic();
    dropRecBlob();
    recorder = null;
    recBox.hidden = true;
    recBox.innerHTML = '';
  }
  function dropRecBlob() {
    if (recUrl) URL.revokeObjectURL(recUrl);
    recUrl = null;
    recBlob = null;
  }
  function releaseMic() {
    if (recStream) recStream.getTracks().forEach((t) => t.stop());   // гасне і червона крапка у вкладці
    recStream = null;
  }

  // Смужки рівня: видно, що мікрофон таки чує, а не пише тишу.
  function startMeter(stream) {
    try {
      recActx = new (window.AudioContext || window.webkitAudioContext)();
      recAnalyser = recActx.createAnalyser();
      recAnalyser.fftSize = 256;
      recActx.createMediaStreamSource(stream).connect(recAnalyser);
      const data = new Uint8Array(recAnalyser.frequencyBinCount);
      const step = () => {
        if (!recAnalyser) return;
        recAnalyser.getByteFrequencyData(data);
        recBox.querySelectorAll('.rec-bars i').forEach((b, i) => {
          b.style.transform = `scaleY(${Math.max(0.14, Math.min(1, (data[2 + i * 3] / 255) * 1.7))})`;
        });
        recRaf = requestAnimationFrame(step);
      };
      step();
    } catch { /* без смужок теж пишеться */ }
  }
  function stopMeter() {
    cancelAnimationFrame(recRaf);
    recRaf = 0;
    recAnalyser = null;
    try { if (recActx) recActx.close(); } catch { /* уже закритий */ }
    recActx = null;
  }

  $('micBtn').onclick = () => (recorder ? stopRec() : startRec());
  window.addEventListener('pagehide', closeRec);

  // ---------- послухати голосове до того, як воно піде в ефір ----------
  function playVoice(id) {
    const a = $('voiceAudio');
    if (a.dataset.id === id && !a.paused) { a.pause(); return; }
    a.dataset.id = id;
    a.src = `/api/voice/${encodeURIComponent(id)}.mp3`;
    a.play().catch((e) => toast('Не програлось: ' + e.message, 'err'));
  }
  function markVoiceButtons() {
    const a = $('voiceAudio');
    document.querySelectorAll('button.vplay').forEach((b) => {
      const on = b.dataset.id === a.dataset.id && !a.paused;
      b.textContent = on ? '⏸' : '▶';
      b.classList.toggle('active', on);
    });
  }
  function wireVoiceButtons(root) {
    root.querySelectorAll('button.vplay').forEach((b) => b.onclick = () => playVoice(b.dataset.id));
    markVoiceButtons();
  }
  ['play', 'pause', 'ended'].forEach((e) => $('voiceAudio').addEventListener(e, markVoiceButtons));

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
  $('libTabs').querySelectorAll('button').forEach((b) => b.onclick = () => go('#lib/' + b.dataset.tab));
  /// Шукаємо по тому, що вже на екрані: сервер тут ні до чого, і відповідь миттєва.
  function filterLib() {
    const box = $('lib');
    const want = $('libFind').value.trim().toLowerCase();
    const rows = box.querySelectorAll('.list > li');
    let shown = 0;
    rows.forEach((li) => {
      const hit = !want || li.textContent.toLowerCase().includes(want);
      li.hidden = !hit;
      if (hit) shown++;
    });
    let none = box.querySelector('.findnone');
    if (want && rows.length && !shown) {
      if (!none) { none = document.createElement('div'); none.className = 'empty findnone'; box.appendChild(none); }
      none.textContent = `Нічого схожого на «${$('libFind').value.trim()}» тут нема.`;
    } else if (none) none.remove();
  }
  $('libFind').addEventListener('input', filterLib);

  const trackRow = (t, right, extra) => `<li>
      ${cover(t)}
      <div style="min-width:0"><div class="t ${extra?.skipped ? 'skipped' : ''}">${esc(t.title)} <span class="muted">· ${esc(t.artist)}</span></div><div class="r">${right}</div></div>
      <div class="btns">${voiceBtn(t)}<button class="q" data-id="${esc(t.id)}" title="Закинути в чергу">в чергу</button><button class="ghost pl" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="У плейлист">＋</button></div>
    </li>`;
  function wireRows(root) {
    root.querySelectorAll('button.q').forEach((b) => b.onclick = (e) => busy(e.currentTarget, '…', () => queueTrack(b.dataset.id)));
    root.querySelectorAll('button.pl').forEach((b) => b.onclick = () => openPlaylistPicker(b.dataset.id, b.dataset.title));
    wireVoiceButtons(root);
  }
  async function loadLib() {
    await drawLib();
    filterLib();
  }
  async function drawLib() {
    const box = $('lib');
    try {
      if (libTab === 'history') {
        const list = await api('GET', '/api/history?n=80');
        box.innerHTML = `<ul class="list">${list.map((h) => trackRow(h.track,
          `${h.source === 'autodj' ? esc(dj()) : esc(h.requestedBy || '')}${h.via === 'suggestion' ? ' · порада' : ''}${h.likes ? ' · ❤' + h.likes : ''}${h.skipped ? ' · скіп' : ''} · ${tm(h.startedAt)}`,
          { skipped: h.skipped })).join('') || '<li class="empty glek">Ще нічого не грало. Закинь першу пісню — і тут почне збиратись історія.</li>'}</ul>`;
      } else if (libTab === 'likes') {
        const list = await api('GET', '/api/likes');
        box.innerHTML = `<ul class="list">${list.map((l) => trackRow(l.track, `❤ ${esc(l.likers.join(', '))}`)).join('') || '<li class="empty glek">Ще ніхто нічого не лайкнув. Сердечко під треком в ефірі — і пісня осяде тут.</li>'}</ul>`;
      } else if (libTab === 'rating') {
        await renderRating();
        return;
      } else if (libTab === 'top') {
        const r = await api('GET', '/api/top?days=7');
        const max = Math.max(1, ...r.requesters.map((x) => x.count));
        box.innerHTML = `<div class="muted small" style="margin-bottom:8px">Хто скільки закинув за останній тиждень</div>` +
          (r.requesters.map((x) => `<div class="stat"><span>${esc(x.nick)}</span><b>${x.count}</b><div class="bar" style="width:${Math.round(x.count / max * 100)}%"></div></div>`).join('') || '<div class="empty">поки тиша</div>');
      } else if (libTab === 'playlists') {
        await renderPlaylists();
        return;
      } else if (libTab === 'bans') {
        await renderBans();
        return;
      } else if (libTab === 'ads') {
        await renderAds();
        return;
      }
      wireRows(box);
    } catch (e) { box.innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
  }
  // рейтинг: скільки грало, скільки дослуховують, хто слухав; період і сортування пам'ятаємо
  const ratingOpt = { days: localStorage.getItem('ratingDays') || '7', sort: localStorage.getItem('ratingSort') || 'plays' };
  const gb = (b) => (b / 1024 ** 3).toLocaleString('uk-UA', { maximumFractionDigits: 1 });
  async function renderRating() {
    const box = $('lib');
    const r = await api('GET', `/api/rating?days=${ratingOpt.days}&sort=${ratingOpt.sort}`);
    const seg = (key, items) => `<div class="tabs seg" data-key="${key}">${items.map(([v, label]) =>
      `<button data-v="${v}" class="${ratingOpt[key] === v ? 'on' : ''}">${label}</button>`).join('')}</div>`;
    const c = r.cache;
    const cacheLine = `Кеш треків: ${gb(c.bytes)}${c.limitBytes ? ' з ' + gb(c.limitBytes) : ''} ГБ · ${c.files} файлів`;
    const row = (x) => {
      const bits = [`▶ ${x.plays}`];
      if (x.completion != null) bits.push(`<span title="у середньому дослуховують">до кінця ${x.completion}%</span>`);
      if (x.listeners) bits.push(`<span title="${esc(x.listenerNicks.join(', '))}">🎧 ${x.listeners}</span>`);
      if (x.streamPeak > x.listeners) bits.push(`<span title="найбільше підключень до потоку разом з ETS2 і VLC">потік ${x.streamPeak}</span>`);
      if (x.likes) bits.push(`❤ ${x.likes}`);
      if (x.skips) bits.push(`скіп ${x.skips}`);
      return trackRow(x.track, bits.join(' · '));
    };
    box.innerHTML = seg('days', [['7', '7 днів'], ['30', '30 днів'], ['3650', 'весь час']]) +
      seg('sort', [['plays', 'частіше грали'], ['completion', 'дослуховують'], ['listeners', 'більше слухачів'], ['likes', 'лайки']]) +
      `<div class="muted small" style="margin:2px 0 8px">${cacheLine}. 🎧 — скільки людей слухало на сайті (рахується з 13.09)</div>` +
      `<ul class="list">${r.tracks.map(row).join('') || '<li class="empty">за цей час нічого не грало</li>'}</ul>`;
    box.querySelectorAll('.seg').forEach((s) => s.querySelectorAll('button').forEach((b) => b.onclick = () => {
      ratingOpt[s.dataset.key] = b.dataset.v;
      try { localStorage.setItem(s.dataset.key === 'days' ? 'ratingDays' : 'ratingSort', b.dataset.v); } catch { /* приватне вікно */ }
      loadLib();
    }));
    wireRows(box);
  }

  // бан-лист: хто, коли й за скільки забанив; адмін розбанює безкоштовно, решта викуповує за черепки
  const dayTime = (iso) => new Date(iso).toLocaleString('uk-UA', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
  async function renderBans() {
    const box = $('lib');
    const r = await api('GET', '/api/bans');
    const admin = me.role === 'admin';
    const how = [
      'Забанене не грає, не стає в чергу й не потрапляє в поради.',
      r.banPrice ? `Забанити те, що зараз в ефірі, — ${r.banPrice} 🏺, кнопка «🚫 бан» під треком.` : '',
      admin ? 'Ти адмін: банити й розбанювати безкоштовно.'
        : r.unbanPrice ? `Викупити трек із бану — ${r.unbanPrice} 🏺. У тебе ${r.balance} 🏺.` : 'Розбанює лише адмін.',
    ].filter(Boolean).join(' ');
    const unbanBtn = (t) => admin
      ? `<button class="ghost unban" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="Зняти бан">розбанити</button>`
      : r.unbanPrice ? `<button class="ghost unban" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="Викупити з бану за ${r.unbanPrice} черепків">викупити · ${r.unbanPrice} 🏺</button>` : '';
    const row = (b) => `<li>
        ${cover(b.track)}
        <div style="min-width:0"><div class="t">${esc(b.track.title)}${b.track.artist ? ` <span class="muted">· ${esc(b.track.artist)}</span>` : ''}</div>
          <div class="r">забанив ${esc(b.by || '?')}${b.price ? ` за ${b.price} 🏺` : ''} · ${dayTime(b.createdAt)}</div></div>
        <div class="btns">${b.track.sourceUrl && !isVoice(b.track) ? `<a class="chip" href="${esc(b.track.sourceUrl)}" target="_blank" rel="noopener" title="Що це було">↗</a>` : ''}${unbanBtn(b.track)}</div>
      </li>`;
    box.innerHTML = `<div class="muted small" style="margin-bottom:8px">${how}</div>` +
      `<ul class="list">${r.items.map(row).join('') || '<li class="empty glek">Бан-лист порожній: усе, що грало, всіх влаштувало.</li>'}</ul>`;
    box.querySelectorAll('button.unban').forEach((b) => b.onclick = (e) => {
      const ask = admin ? `Розбанити «${b.dataset.title}»?` : `Викупити «${b.dataset.title}» з бану за ${r.unbanPrice} 🏺?`;
      if (!confirm(ask)) return;
      busy(e.currentTarget, '…', () => api('DELETE', `/api/ban/${encodeURIComponent(b.dataset.id)}`)
        .then((res) => { ok(res); return loadLib(); }).catch(fail));
    });
  }

  // ---------- реклама: бібліотека, ротація, частота (лише адмін) ----------
  const ago = (iso) => {
    if (!iso) return 'ще не грала';
    const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
    return m < 1 ? 'щойно' : m < 60 ? `${m} хв тому` : m < 1440 ? `${Math.round(m / 60)} год тому` : dayTime(iso);
  };
  const times = (n) => `${n} ${n % 100 >= 11 && n % 100 <= 14 ? 'разів' : n % 10 === 1 ? 'раз' : n % 10 >= 2 && n % 10 <= 4 ? 'рази' : 'разів'}`;
  async function renderAds() {
    const box = $('lib');
    if (me.role !== 'admin') { box.innerHTML = '<div class="empty">Це бачить лише адмін</div>'; return; }
    const r = await api('GET', '/api/ads/library');
    const on = r.items.filter((a) => a.enabled && !a.missing).length;
    const fb = r.fallback ? `крутиться ${r.fallback.house ? 'реклама господаря' : 'переможець конкурсу'} (${esc(r.fallback.nick)})` : 'реклами в ефірі не буде';
    const head = `<div class="ads-head">
        <div class="ads-row"><b>У ротації ${on} з ${r.items.length}</b>
          <span class="muted small">випадково без повторів · від останньої реклами ${r.since} тр.${r.jingle ? '' : ' · ⚠ джингл вимкнено в Ad:Jingle'}</span></div>
        <div class="ads-row">
          <span>Раз на</span><input id="adsEvery" type="number" min="1" max="100" value="${r.everyTracks}"><span>тр., не частіше ніж раз на</span>
          <input id="adsMins" type="number" min="0" max="600" value="${r.minMinutes}"><span>хв</span>
          <button class="ghost" id="adsSaveEvery">Зберегти</button>
        </div>
        <div class="ads-row">
          <button class="primary" id="adsNow" title="Наступна реклама з колоди стане в чергу">📻 Наступну в чергу</button>
          <button class="ghost" id="adsAllOn">Усі в ротацію</button>
          <button class="ghost" id="adsAllOff">Вимкнути ротацію</button>
        </div>
        <div class="muted small">За прослухану рекламу слухачам з увімкненим плеєром +${r.reward.amount} 🏺 (до ${r.reward.dailyCap} на день).
          Коли в ротації порожньо, ${fb}.</div>
        <form class="ads-row" id="adsUpload">
          <input type="file" accept="audio/*,video/webm,.mp3,.wav,.ogg,.m4a" required>
          <input type="text" maxlength="60" placeholder="назва реклами" autocomplete="off">
          <button class="primary" type="submit">Залити</button>
          <span class="muted small">до ${r.maxMb} МБ</span>
        </form>
      </div>`;
    const row = (a) => `<li class="${a.enabled ? '' : 'off'}">
        ${cover({ id: a.trackId })}
        <div style="min-width:0"><div class="t"><span class="ads-title" data-id="${a.id}" title="Перейменувати">${esc(a.title)}</span></div>
          <div class="r">${fmt(a.seconds)} · грала ${times(a.plays)} · ${ago(a.lastPlayedAt)}${a.missing ? ' · <span class="chip err">файл зник</span>' : ''}</div></div>
        <div class="btns">
          ${voiceBtn({ id: a.trackId })}
          <label class="ads-switch" title="${a.enabled ? 'У ротації' : 'Не в ротації'}"><input type="checkbox" data-id="${a.id}" ${a.enabled ? 'checked' : ''}> ротація</label>
          <button class="ghost" data-now="${a.id}" title="Саме цю — в чергу">📻</button>
          <button class="ghost danger" data-del="${a.id}" data-title="${esc(a.title)}" title="Видалити">✕</button>
        </div>
      </li>`;
    box.innerHTML = head + `<ul class="list ads-list">${r.items.slice().reverse().map(row).join('') || '<li class="empty">Бібліотека порожня. Залий перший файл вище.</li>'}</ul>`;

    const again = () => loadLib();
    $('adsSaveEvery').onclick = (e) => busy(e.currentTarget, '…', () => api('POST', '/api/ads/air/every',
      { everyTracks: +$('adsEvery').value, minMinutes: +$('adsMins').value }).then(ok).then(again).catch(fail));
    $('adsNow').onclick = (e) => busy(e.currentTarget, 'ставлю…', () => api('POST', '/api/ads/air/now').then(ok).catch(fail));
    $('adsAllOn').onclick = (e) => busy(e.currentTarget, '…', () => api('POST', '/api/ads/library/all', { enabled: true }).then(ok).then(again).catch(fail));
    $('adsAllOff').onclick = (e) => confirm('Вимкнути всі реклами з ротації?') && busy(e.currentTarget, '…',
      () => api('POST', '/api/ads/library/all', { enabled: false }).then(ok).then(again).catch(fail));
    $('adsUpload').onsubmit = (e) => {
      e.preventDefault();
      const [file, name] = e.target.querySelectorAll('input');
      const f = file.files[0];
      if (!f) return;
      const title = name.value.trim() || f.name.replace(/\.[^.]+$/, '');
      busy(e.target.querySelector('button'), 'заливаю…', async () => {
        const res = await fetch('/api/ads/library?title=' + encodeURIComponent(title), {
          method: 'POST', body: f,
          headers: { 'X-Nick': encodeURIComponent(me.nick), 'Content-Type': f.type || 'application/octet-stream' },
        });
        const j = await res.json().catch(() => ({ message: 'сервер відповів ' + res.status }));
        if (!res.ok) throw new Error(j.message);
        ok(j);
        await again();
      }).catch(fail);
    };
    box.querySelectorAll('.ads-switch input').forEach((c) => c.onchange = () =>
      api('PATCH', `/api/ads/library/${c.dataset.id}`, { enabled: c.checked }).then(again).catch((e) => { c.checked = !c.checked; fail(e); }));
    box.querySelectorAll('[data-now]').forEach((b) => b.onclick = (e) =>
      busy(e.currentTarget, '…', () => api('POST', `/api/ads/library/${b.dataset.now}/now`).then(ok).catch(fail)));
    box.querySelectorAll('[data-del]').forEach((b) => b.onclick = (e) => confirm(`Видалити «${b.dataset.title}» назавжди?`) &&
      busy(e.currentTarget, '…', () => api('DELETE', `/api/ads/library/${b.dataset.del}`).then(ok).then(again).catch(fail)));
    box.querySelectorAll('.ads-title').forEach((t) => t.onclick = () => {
      const name = prompt('Нова назва реклами', t.textContent);
      if (name == null || !name.trim() || name.trim() === t.textContent) return;
      api('PATCH', `/api/ads/library/${t.dataset.id}`, { title: name.trim() }).then(ok).then(again).catch(fail);
    });
    wireVoiceButtons(box);
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
        </div><div class="pl-tracks" data-for="${p.id}" hidden></div>`).join('') || '<div class="empty glek">Плейлистів ще нема. Створи перший: назва вище, а треки додаються кнопкою «＋ плейлист» під тим, що грає, або «＋» в історії та улюбленому.</div>');
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
          ${cover(x.track)}
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
    conn.on('chat', (m) => addMessage(m, true, true));
    let tourRoom = null;
    if (window.HTournament) HTournament.connect((...a) => conn.invoke(...a));
    conn.on('tournament', (t) => {
      if (!window.HTournament) return;
      const hadCrown = JSON.stringify((HTournament.state || {}).crown || []);
      HTournament.update(t);
      if (hadCrown !== JSON.stringify((t && t.crown) || [])) { if (state) renderOnline(); repaintCrowns(); }
      // Нова гра турніру, а я в ньому — одразу за стіл (якщо вже в «Іграх»), інакше — тост із підказкою.
      const room = t && t.stage === 'playing' && t.room ? t.room.id : null;
      const mineT = t && (t.players || []).some((p) => sameNick(p, me.nick));
      if (room && room !== tourRoom && mineT) {
        if (tourRoom !== null || route === 'games') {
          if (route === 'games') go('#games/room/' + encodeURIComponent(room));
          else toast('🏆 Турнір: наступна гра почалась — зазирни в «Ігри»', 'ok');
        }
      }
      tourRoom = room || (tourRoom === null ? '' : tourRoom);
    });
    conn.on('chatLikes', (x) => {
      const el = x && $('messages').querySelector(`.msg[data-id="${x.id}"]`);
      if (el) paintLikes(el, x.likes || []);
    });
    conn.on('reaction', (r) => flyEmoji(r.emoji, r.nick));
    HGames.attach(conn);           // усе про ігри — у web/games/core.js
    // Після HGames.attach: спершу хай каркас оновить свій список столів, а тоді вже перемальовуємо
    // кнопки в рядках. Історія балачок приходить раніше за перше лобі, тож без цього рядок про стіл
    // лишався б без кнопки аж до наступної новини з лобі.
    conn.on('rooms', () => paintRoomSlots());
    conn.on('chatHistory', (list) => {
      $('messages').innerHTML = '';
      $('log').innerHTML = '';
      list.forEach((m) => addMessage(m, false));
      $('messages').scrollTop = $('messages').scrollHeight;
      $('log').scrollTop = $('log').scrollHeight;
    });
    conn.onreconnected(() => {
      conn.invoke('SetNick', me.nick).catch(() => {});
      if (listening) conn.invoke('SetListening', true).catch(() => {});
      HGames.reconnected();
      toast('Знову на зв\'язку', 'ok');
    });
    conn.onreconnecting(() => toast('Зв\'язок зник, підключаюсь…', 'wait'));
    conn.onclose(() => toast('Зв\'язок із сервером втрачено, онови сторінку', 'err'));
    conn.start().then(() => {
      if (listening) conn.invoke('SetListening', true).catch(() => {});
      // Перші 'rooms' прилітають ще до того, як start() віддасть 'Connected', тож підписки на
      // кімнати треба попросити заново — як після реконекту.
      HGames.reconnected();
      if (route === 'lib') { libShown = libTab; loadLib(); }
    }).catch((e) => { toast('Не підключився: ' + e.message, 'err'); setTimeout(connect, 4000); });
  }

  // ---------- boot ----------
  setPlayUi();
  HGames.init({ $, esc, toast, busy, api, me, root: $('games'), go });
  applyRoute();
  // Хто я — каже сервер: акаунт із куки або гість із приставкою до того, що лежить у localStorage.
  // Тому підключаємось до хабу лише після /api/me: інакше me.nick розійшовся б із тим, як нас звуть за столами.
  api('GET', '/api/me').then((m) => {
    me.role = m.role;
    me.account = !!m.account;
    me.hasPassword = !!m.hasPassword;
    me.google = !!m.google;
    me.email = m.email || '';
    me.banPrice = m.banPrice || 0;
    loadGoogle(m.googleClientId);
    $('adsTab').hidden = me.role !== 'admin';
    // на #lib/ads зайшов не адмін — відкриваємо звичайну вкладку, а не порожню сторінку
    if (libTab === 'ads' && me.role !== 'admin') go('#lib/history');
    if (me.account || me.nick) {
      // Нік без приставки з часів до акаунтів: сервер уже зве нас «гість …» — запропонуємо закріпити його паролем.
      const plain = !me.account && me.nick && m.nick !== me.nick ? me.nick : null;
      me.nick = m.nick;
      localStorage.setItem('nick', m.nick);
      paintNick();
      connect();
      if (plain) askNick(true, 'register', plain);
    } else askNick();
    if (state) render();
  }).catch(() => {
    if (me.nick) { paintNick(); connect(); } else askNick();
  });
})();
