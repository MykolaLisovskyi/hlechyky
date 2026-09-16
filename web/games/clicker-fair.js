/*
  Ярмарок і люди Гончарного кола (пакет B4, docs/games/specs/clicker-v7-fair.md). Частина ядра clicker.js.

  Правила — на сервері (Impl/ClickerFair.cs), тут лише малюємо й передбачаємо:
  1) плашка над сценою: погода дня, пора року, свято, вихідні, тимчасові бафи (сіль чумака, пісня кобзаря, пригоди);
  2) пригода з вибором — банер над сценою (не overlay: той може бути зайнятий горном), дві кнопки й відлік;
  3) гості біля вікна/дверей — анімовані SVG-фігурки з кільцем-таймером у шарі front; ловляться справжнім натиском;
  4) комора: секція «Замовлення» (картки з силуетами, «є 3 з 5», відлік, торг) і «Шана сіл» над виробами, а дошка
     купців-інвесторів із вкладки «Купці» переїжджає вниз комори секцією «Купці в дорогу»;
  5) хроніка — рядок сільських новин під сценою, змінюється раз на 12 с (лише клієнт).

  Вид: v.market = { weather, season, holiday: {key,name}|null, weekend, dry, orders: [...], nextOrderAt, rep: [...],
    allMult, guest: {kind,at,until,x,y}|null, guests, eventAt, event: {id,key,until,sure}|null, foresight, buffs, delivered }.
  Дія: fair { op: 'deliver', id, bid: 'down'|'as'|'up' } · fair { op: 'guest' } · fair { op: 'choose', id, pick }.
  Звуки: guest, coin, deal, refuse, event, rep-up.
*/
(() => {
  const CHRON_MS = 12000;                 // як часто змінюється рядок хроніки
  const GRACE_MS = 2000;                  // той самий запас, що й на сервері (CatchGrace)
  const REACT_MS = 4500;                  // скільки висить реакція купця над замовленнями

  const SEASON = { winter: '❄️ Зима', spring: '🌸 Весна', summer: '🌻 Літо', autumn: '🍂 Осінь' };
  const WEATHER = {
    sun: { icon: '☀️', text: 'Сонце — сирці сохнуть швидше' },
    cloud: { icon: '☁️', text: 'Хмарно — сохне як завжди' },
    rain: { icon: '🌧️', text: 'Дощ — сохне повільніше' },
    frost: { icon: '❄️', text: 'Мороз — сохне вдвічі довше' },
  };
  const HOLIDAY = {
    christmas: '🎄 Святвечір і Різдво: макітри ×2',
    easter: '🥚 Великдень: миски й полумиски ×1,5',
    pokrova: '💐 Покрова й весілля: куманці ×2',
    sorochyntsi: '🎪 Сорочинський ярмарок: усе ×1,5',
  };
  const BUFF = {
    chumak: '🧂 Сіль чумака', kobzar: '🎶 Пісня кобзаря', event: '✨ Пригода',
  };
  const KIND = { work: 'ліплення', dry: 'сушіння', value: 'ціни' };
  const Q_REQ = ['', '', 'добрі й дзвінкі', 'лише дзвінкі'];
  const GUEST_NAME = { chumak: 'Чумак!', magpie: 'Сорока!', lord: 'Пан!', kobzar: 'Кобзар!', fortune: 'Ворожка!' };

  // ---------- фігурки гостей (viewBox 0 0 60 80) ----------

  const shadow = '<ellipse cx="30" cy="77" rx="15" ry="3" fill="rgba(0,0,0,.35)"/>';
  const face = (cx, cy, skin) => '<circle cx="' + cx + '" cy="' + cy + '" r="8" fill="' + skin + '"/>'
    + '<circle cx="' + (cx - 3) + '" cy="' + (cy - 1) + '" r="1" fill="#1b1310"/><circle cx="' + (cx + 3) + '" cy="' + (cy - 1) + '" r="1" fill="#1b1310"/>';

  const FIGURE = {
    chumak: shadow
      + '<path d="M16 76 19 45Q30 39 41 45L44 76Z" fill="#7a5230"/><rect x="18" y="58" width="24" height="4" fill="#b3342a"/>'
      + '<path d="M22 46l8 10 8-10" stroke="#f4efe3" stroke-width="1.4" fill="none"/>'
      + face(30, 32, '#e0b48a')
      + '<path d="M23 36q7 5 14 0" stroke="#3a2a1a" stroke-width="2.2" fill="none" stroke-linecap="round"/>'
      + '<ellipse cx="30" cy="25" rx="14" ry="3.4" fill="#d9b25a"/><path d="M22 25q8-13 16 0z" fill="#e6c36e"/>'
      + '<g class="clkf-wave"><path d="M40 48l9 9" stroke="#7a5230" stroke-width="5" stroke-linecap="round"/>'
      + '<path d="M46 55q7-3 9 4 0 8-8 8-7 0-6-7z" fill="#f4efe3" stroke="#b9b2a4"/><path d="M47 57h7" stroke="#b9b2a4"/></g>',
    magpie: '<ellipse cx="30" cy="74" rx="12" ry="2.6" fill="rgba(0,0,0,.3)"/>'
      + '<path d="M18 52 3 64l4 3 15-10z" fill="#1f3550"/>'
      + '<ellipse cx="30" cy="50" rx="13" ry="9" fill="#1c1c22"/><ellipse cx="33" cy="53" rx="7" ry="5" fill="#f4f4f4"/>'
      + '<circle cx="42" cy="42" r="6.5" fill="#1c1c22"/><circle cx="44" cy="41" r="1.3" fill="#fff"/>'
      + '<path d="M48 42l6 2-6 1.5z" fill="#3a3a3a"/>'
      + '<circle class="clkf-coin" cx="56" cy="46" r="4.2" fill="#f2c230" stroke="#a87b12" stroke-width="1"/>'
      + '<path class="clkf-flap" d="M22 47q8-16 19-3-9 7-19 3z" fill="#2a3f5f"/>'
      + '<path d="M28 59v12M33 59v12" stroke="#333" stroke-width="1.6"/>',
    lord: shadow
      + '<path d="M15 76 18 46Q30 40 42 46L45 76Z" fill="#7b1e2b"/><rect x="17" y="58" width="26" height="4" fill="#d9a92f"/>'
      + '<g fill="#d9a92f"><circle cx="30" cy="49" r="1"/><circle cx="30" cy="53" r="1"/><circle cx="30" cy="66" r="1"/></g>'
      + face(30, 33, '#e8c09a')
      + '<path d="M21 37q9 6 18 0" stroke="#5a3a1a" stroke-width="2.4" fill="none" stroke-linecap="round"/>'
      + '<path d="M21 28q9-15 18 0z" fill="#3b2a55"/><rect x="19.5" y="26" width="21" height="4" rx="2" fill="#6b4f2b"/>'
      + '<path class="clkf-feather" d="M37 25q9-9 6-17" stroke="#f4efe3" stroke-width="2" fill="none" stroke-linecap="round"/>'
      + '<path d="M47 50l3 28" stroke="#4a3020" stroke-width="2.5"/><circle cx="47" cy="49" r="2.5" fill="#d9a92f"/>',
    kobzar: shadow
      + '<path d="M16 76 19 45Q30 39 41 45L44 76Z" fill="#8a8170"/>'
      + face(30, 31, '#e0b48a')
      + '<path d="M22 28q8-10 16 0" stroke="#eeeae0" stroke-width="3" fill="none"/>'
      + '<path d="M22 34q8 22 16 0-8 6-16 0z" fill="#eeeae0"/>'
      + '<path d="M34 47 49 29" stroke="#6b3b1b" stroke-width="3" stroke-linecap="round"/>'
      + '<ellipse cx="33" cy="59" rx="9" ry="11" fill="#b87333" stroke="#6b3b1b"/><circle cx="33" cy="58" r="2.6" fill="#3a2010"/>'
      + '<path class="clkf-strum" d="M30 51 45 33M33 52 47 34M36 52 49 35" stroke="#f4efe3" stroke-width=".5"/>'
      + '<text class="clkf-note" x="47" y="22" font-size="11" fill="#f2c230">♪</text>'
      + '<text class="clkf-note n2" x="7" y="30" font-size="9" fill="#f2c230">♫</text>',
    fortune: shadow
      + '<path d="M14 76 20 46Q30 40 40 46L46 76Z" fill="#2f5fa8"/><path d="M22 57h16l3 19H19z" fill="#d7372b" opacity=".85"/>'
      + '<g fill="#f2c230"><circle cx="24" cy="66" r="1"/><circle cx="30" cy="70" r="1"/><circle cx="36" cy="66" r="1"/></g>'
      + face(30, 32, '#d9a77a')
      + '<path d="M19 33q11-21 22 0-3 4-5 1-6-11-12 0-2 3-5-1z" fill="#d7372b"/>'
      + '<g fill="#f2c230"><circle cx="21.5" cy="37" r="1.7"/><circle cx="38.5" cy="37" r="1.7"/></g>'
      + '<circle class="clkf-ball" cx="30" cy="60" r="7" fill="#9fd3ff" opacity=".9"/><circle cx="27.5" cy="57.5" r="2" fill="#fff" opacity=".7"/>',
  };
  const guestSvg = (kind) => '<svg class="clkf-fig" viewBox="0 0 60 80" aria-hidden="true">' + (FIGURE[kind] || FIGURE.magpie) + '</svg>';

  // ---------- хроніка ----------

  /// Плітки й новини. Функція отримує стан c і повертає рядок або null (коли до гравця не стосується).
  const CHRON = [
    'У Бубнівці баба Христя вчить онуків малювати «кривульки» — у малого вже рука набита',
    'Опішнянські гончарі третій день сперечаються, чия глина пластичніша',
    'Кум Цибуля знову загубив воза на ярмарку. Воза знайшли, кума — ні',
    'У Косові вівчар Юра виміняв вовну на миску й тепер їсть із неї навіть узвар',
    'Васильківська пекарка Люба каже, що паска в глиняній формі виходить пишніша',
    'Хівря заборонила Черевикові їхати на ярмарок без неї. Черевик поїхав. Чекаємо новин',
    'У Гавареччині кажуть: у чорному глеку молоко довше не кисне. Сусіди перевіряють',
    'Чумаки повернулися з сіллю й новинами: у Криму глеки дорожчі, ніж удома',
    'Коза діда Трохима знову залізла в чиюсь майстерню. Шкоди нема, сміху — повно',
    'Корчмар Лесь замовив сорок кухлів і клянеться, що це вже точно востаннє',
    'Писар Никифор пише книгу майстрів. Уже третій том, а про себе — ні слова',
    'На річці знову бачили козацьку чайку — чи то на Царград, чи то по раки',
    'Бабця Настя каже, що найкращий горщик — той, у якому борщ',
    'Дударик Тарас грає під вікнами так, що навіть кіт на полиці підспівує',
    'Параска вийшла заміж. Гуляли три дні, макітр побили — не злічити',
    'Мельник Семен каже, що гончарне коло — майже як млинове, тільки пахне глиною',
    'У Сорочинцях уже ставлять намети: ярмарок буде — хто кого перекричить',
    'Молодиця Оксана посадила калину під вікном майстерні — на щастя',
    'Пасічник Гнат приніс меду в глечику й божиться, що в глині мед солодший',
    'Різьбяр Дмитро з Косова вирізав ложку, якою можна їсти з будь-якої миски',
    'Ґаздиня Марічка вивісила рушники сушитись — усе село подумало, що свято',
    'Хтось пустив чутку, що в глинищі скарб. Копали всім селом, знайшли глину',
    'Швачка Ганна вишила рушник із глечиками — кажуть, дуже схожими на справжні',
    'Вчителька Ніна повела дітей дивитися, як ліплять глеки. Діти досі в глині',
    'Шинкар Мусій записав у борг ще одну макітру. Макітра не заперечує',
    'Гончар Юхим з Опішні жартує: хто рахує глеки, той їх не ліпить',
    'Дід Панас розказує, що замолоду виліпив глек на всю хату. Хата каже — неправда',
    'Кума Параска знає всі плітки на три села вперед. Ця — теж від неї',
    'Коваль Остап викував новий гачок для гончарного кола — задарма, по-сусідськи',
    'Бабця Олена з Косова малює на мисках оленів — як живі, аж рогами чіпляються',
    'Парубок Грицько сватається вдруге. Перший гарбуз стоїть у нього на полиці',
    'У шинку сперечались, що важче — виліпити макітру чи вмовити кума. Вирішили: кума',
    'Сорока знову винесла з чиєїсь хати ложку. Ложку шукають, сорока скрекоче',
    'Ґазда Василь продав вівцю й купив три глечики. Дружина рада, вівця — ні',
    'Мандрівний дяк читав на ярмарку вірші. Купили в нього тільки миску',
    'Тітка Христя кличе всіх на вареники: у макітрі вже тісто підходить',
    'Кобзар співав на площі до ночі. Кажуть, одна пісня була про гончаря',
    'Пан із маєтку роздивлявся глеки на базарі й хмурився. Значить, купить',
    'Ворожка нагадала кумові Цибулі, що він забуде воза. Він забув',
    'У Василькові кажуть: у доброго гончаря й кіт до глини охочий',
    'Під Опішнею знайшли пласт голубої глини. Дід Панас каже, що знав про нього завжди',
    'Дощ пройшов лише над Бубнівкою — там кажуть, це тому, що вони найкраще малюють',
    'У Гавареччині на тину глечиків більше, ніж кілків',
    'На базарі миски розходяться, як мед: хто встиг, той і купив',
    'Сусідський кіт виліпив лапою щось схоже на миску. Продали за гривню',
    'Хтось бачив, як гуси з річки несли в дзьобах черепки. Будують, кажуть',
    'Молоді з Сорочинців питають, чи правда, що гончарі найкращі женихи. Правда',
    'Отаман проїжджав селом і попросив води — тільки з глиняного кухля',
    'Діти грають у ярмарок: продають пиріжки з болота в мисках із листя',
    'Бабця Настя вгадує погоду за тим, як сохне глина. Поки не помилилась',
    // Про самого гончаря — з виду.
    (c) => c.total >= 100 ? 'У Сорочинцях кажуть, що ' + c.nick + ' наліпив уже ' + c.api.potsShort(c.total) : null,
    (c) => c.formed > 0 ? 'Кума Параска рахувала через тин: ' + c.nick + ' виліпив уже ' + c.api.num(c.formed) + ' ' + c.api.plural(c.formed, 'виріб', 'вироби', 'виробів') : null,
    (c) => c.fired > 0 ? 'Кажуть, із горна в ' + c.nick + ' вийшло вже ' + c.api.num(c.fired) + ' ' + c.api.plural(c.fired, 'виріб', 'вироби', 'виробів') : null,
    (c) => c.delivered > 0 ? c.nick + ' виконав уже ' + c.api.num(c.delivered) + ' ' + c.api.plural(c.delivered, 'замовлення', 'замовлення', 'замовлень') + ' — купці аж чергу займають' : null,
    (c) => c.guests > 0 ? 'До хати ' + c.nick + ' заходило вже ' + c.api.num(c.guests) + ' ' + c.api.plural(c.guests, 'гість', 'гості', 'гостей') + ' — і кожен щось приніс' : null,
    (c) => c.top ? c.top.in[0].toUpperCase() + c.top.in.slice(1) + ' про ' + c.nick + ' кажуть: «Наш майстер!»' : null,
    (c) => !c.top ? c.nick + ' поки що знають лише сусіди. Замовлення — найкращий спосіб прославитись' : null,
    (c) => c.weather === 'sun' ? 'Сонце таке, що сирці сохнуть просто на очах' : null,
    (c) => c.weather === 'rain' ? 'Дощ: сирці сохнуть повільніше, а гончарі розмовляють довше' : null,
    (c) => c.weather === 'frost' ? 'Мороз мальовничий: на вікнах узори, сирці сохнуть удвічі довше' : null,
    (c) => c.weather === 'cloud' ? 'Хмарно, але без дощу — саме те, щоб ліпити й не відволікатися' : null,
    (c) => c.holiday === 'christmas' ? 'Господині скуповують макітри для куті й маку — ціна подвоїлась' : null,
    (c) => c.holiday === 'easter' ? 'Перед Великоднем усі шукають миски для кошиків' : null,
    (c) => c.holiday === 'pokrova' ? 'Весільна пора: куманці розбирають, як гарячі пиріжки' : null,
    (c) => c.holiday === 'sorochyntsi' ? 'Сорочинський ярмарок гуде: усе дорожче в півтора раза, гості йдуть косяком' : null,
    (c) => c.weekend ? 'Вихідні: базар повний, ціни вищі на п\'яту частину' : null,
    (c) => c.season === 'winter' ? 'Зима: у хаті тепло від печі, а на вікнах — зорі з інею' : null,
    (c) => c.season === 'spring' ? 'Весна: у садках цвіте, глина після зими м\'яка й слухняна' : null,
    (c) => c.season === 'summer' ? 'Літо: соняхи за тином вищі за хату' : null,
    (c) => c.season === 'autumn' ? 'Осінь: калина червоніє під вікном, по селах — ярмарки й весілля' : null,
    (c) => c.ware ? 'Сусіди підглядають через тин — на колі в майстра ' + c.nick + ' знову ' + c.ware.toLowerCase() : null,
    (c) => c.items > 0 ? 'У коморі ' + c.nick + ' ' + c.api.num(c.items) + ' ' + c.api.plural(c.items, 'виріб', 'вироби', 'виробів') + ' — купці придивляються' : null,
    (c) => c.items >= 160 ? 'Комора ' + c.nick + ' ледь не тріщить — час на базар!' : null,
    (c) => c.orders > 0 ? 'На дошці замовлень чекають ' + c.orders + ' ' + c.api.plural(c.orders, 'замовник', 'замовники', 'замовників') : null,
    (c) => c.lord ? 'Пан із маєтку чекає свого замовлення й покручує вуса' : null,
    (c) => c.stamps > 0 ? c.nick + ' має ' + c.stamps + ' ' + c.api.plural(c.stamps, 'клеймо', 'клейма', 'клейм') + ' майстра — таке не в кожного' : null,
    (c) => c.styles > 0 ? 'У колекції ' + c.nick + ' уже ' + c.styles + ' з 8 розписів' : null,
    (c) => c.caught > 0 ? c.nick + ' спіймав уже ' + c.api.num(c.caught) + ' розписних глеків. Руки — золоті' : null,
    (c) => c.grabbed > 0 ? 'З полиці ' + c.nick + ' зловив ' + c.api.num(c.grabbed) + ' ' + c.api.plural(c.grabbed, 'глек', 'глеки', 'глеків') + ' — кіт ображений' : null,
    (c) => c.firings > 0 ? c.nick + ' обпалював майстерню ' + c.firings + ' ' + c.api.plural(c.firings, 'раз', 'рази', 'разів') + ' — і щоразу вертався сильнішим' : null,
  ];

  // ---------- дрібниці ----------

  const cat = (st) => (st.catalog && st.catalog.fair) || null;
  const villageOf = (st, key) => { const c = cat(st); return (c && c.villages.find((v) => v.key === key)) || null; };
  const wareName = (st, key) => {
    const list = (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
    const w = list.find((x) => x.key === key);
    return w ? w.name : key;
  };
  const styleName = (st, key) => {
    const s = (st.styleList || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const pct = (m) => Math.round(Math.abs(m - 1) * 100);
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const ms = (t) => { const x = Date.parse(t); return Number.isFinite(x) ? x : 0; };

  // ---------- плашка й бафи ----------

  function paintPlaque(st, api) {
    const k = st.fair;
    const m = k.m;
    if (!m) return;
    const w = WEATHER[m.weather] || WEATHER.cloud;
    let html = '<span class="clkf-chip season">' + (SEASON[m.season] || '') + '</span>'
      + '<span class="clkf-chip weather ' + api.esc(st, m.weather) + '" title="сушіння ×' + api.dec(m.dry) + '">' + w.icon + ' ' + w.text + '</span>';
    if (m.holiday) html += '<span class="clkf-chip holiday">' + api.esc(st, HOLIDAY[m.holiday.key] || m.holiday.name) + '</span>';
    if (m.weekend) html += '<span class="clkf-chip weekend">🛍️ Вихідні: ціни +20 %</span>';
    api.swap(k.plaque, html);
    // Бафи — під сценою, поруч із ярмарком і розгоном: над сценою їхня поява зсувала б коло під пальцем.
    const sn = api.serverNow(st);
    let buffs = '';
    for (const b of k.buffs) {
      if (b.until <= sn) continue;
      const sign = b.mult < 1 ? '−' : '+';
      buffs += '<span class="clkf-chip buff ' + (b.mult < 1 === (b.kind !== 'value') ? 'good' : 'bad') + '">' + (BUFF[b.src] || '✨')
        + ': ' + KIND[b.kind] + ' ' + sign + pct(b.mult) + ' % · <i class="clkf-cd" data-at="' + b.until + '"></i></span>';
    }
    if (k.buffsEl.hidden !== !buffs) k.buffsEl.hidden = !buffs;
    if (api.swap(k.buffsEl, buffs)) countdowns(st, api, k.buffsEl);
  }

  // ---------- пригода ----------

  function paintEvent(st, api) {
    const k = st.fair;
    const m = k.m;
    const el = k.eventEl;
    const sn = api.serverNow(st);
    const e = m && k.event;
    const c = cat(st);
    const def = e && c && c.events.find((x) => x.key === e.key);
    let html = '';
    if (k.result && Date.now() - k.result.at < 6000) {
      html = '<div class="clkf-ev-result">' + api.esc(st, k.result.text) + '</div>';
    } else if (def && st.mine && e.until > sn && !api.guardOn(st)) {
      const choice = (ch, i) => {
        const sure = e.sure ? ch.outcomes[Math.min(e.sure[i], ch.outcomes.length - 1)] : '';
        return '<button type="button" class="clkf-ev-btn' + (i ? '' : ' primary') + '" data-pick="' + i + '">'
          + '<b>' + api.esc(st, ch.label) + '</b>' + (sure ? '<span class="clkf-ev-sure">🔮 ' + api.esc(st, sure) + '</span>' : '') + '</button>';
      };
      html = '<div class="clkf-ev-head"><span class="clkf-ev-emoji">' + api.esc(st, def.emoji) + '</span><div><b>' + api.esc(st, def.title) + '</b>'
        + '<span class="muted small">' + api.esc(st, def.text) + '</span></div>'
        + '<span class="clkf-ev-left small muted">⏳ <i class="clkf-cd" data-at="' + e.until + '"></i></span></div>'
        + '<div class="clkf-ev-btns">' + choice(def.a, 0) + choice(def.b, 1) + '</div>'
        + (e.sure ? '' : '<div class="clkf-ev-hint small muted">Що з цього вийде — невідомо. Ворожка бачила б наперед…</div>');
    }
    const show = !!html;
    if (el.hidden === show) el.hidden = !show;
    // Банер живе під сценою (щоб не зсувати коло), тож на самій сцені — дзвіночок, що до нього гортає.
    const bell = !!(def && st.mine && e.until > sn && !api.guardOn(st) && !(k.result && Date.now() - k.result.at < 6000));
    if (k.bellEl.hidden === bell) k.bellEl.hidden = !bell;
    if (api.swap(el, html)) {
      countdowns(st, api, el);
      for (const b of el.querySelectorAll('[data-pick]')) {
        b.onclick = (ev) => {
          if (!ev.isTrusted || !e) return;
          for (const x of el.querySelectorAll('[data-pick]')) x.disabled = true;
          api.act(st, 'fair', { op: 'choose', id: e.id, pick: +b.dataset.pick }).then((r) => {
            if (r && r.ok) {
              k.result = { text: r.message || '', at: Date.now() };
              api.sfx(/\(\+|шана|⭐/.test(r.message || '') ? 'coin' : 'deal');
              api.sparks(st, st.fx, 10, true, 50, 30);
            }
            paintEvent(st, api);
          });
        };
      }
    }
    // Нова пригода — дзвіночок, раз на неї.
    if (e && k.eventSeen !== e.id && e.until > sn && st.mine) {
      if (k.eventSeen != null) api.sfx('event');
      k.eventSeen = e.id;
    }
  }

  // ---------- гості ----------

  function paintGuest(st, api) {
    const k = st.fair;
    const g = k.guest;
    const b = k.guestEl;
    if (!b) return;
    if (!g || !st.mine || api.guardOn(st)) { if (!b.hidden) b.hidden = true; return; }
    const now = api.serverNow(st);
    const show = now >= g.at && now <= g.until && k.guestGone !== g.at;
    if (b.hidden === show) {
      b.hidden = !show;
      if (show) {
        b.style.left = g.x + '%';
        b.style.top = g.y + '%';
        b.className = 'clkf-guest ' + g.kind;
        b.innerHTML = '<svg class="clkf-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>'
          + guestSvg(g.kind) + '<span class="clkf-gname">' + (GUEST_NAME[g.kind] || '') + '</span>';
        b.style.setProperty('--clkf-left', Math.max(0, (g.until - now) / 1000) + 's');
        const guest = (cat(st) && cat(st).guests.find((x) => x.key === g.kind)) || null;
        b.title = guest ? guest.name + ' — ' + guest.desc : 'Гість — лови!';
        b.setAttribute('aria-label', b.title);
        void b.offsetWidth;
        b.classList.add('run');
        if (k.guestSound !== g.at) { k.guestSound = g.at; api.sfx('guest'); }
      }
    }
    // Гість пішов — один раз питаємо наступний розклад (і лише коли картку видно, як для розписного глека).
    if (now > g.until + GRACE_MS + 5000 && k.guestLooked !== g.until && api.visible(st)) {
      k.guestLooked = g.until;
      api.act(st, 'look');
    }
  }

  function catchGuest(st, api, ev) {
    const k = st.fair;
    const g = k.guest;
    if (!ev.isTrusted || !g || !st.mine || api.guardOn(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    k.guestGone = g.at;
    k.guestEl.hidden = true;
    const x = g.x + 8;
    const y = g.y + 10;
    api.act(st, 'fair', { op: 'guest' }).then((r) => {
      if (!r || !r.ok) return;
      const text = r.message || '';
      if (text.startsWith('👁')) return;
      api.sparks(st, st.fx, 14, true, x, y);
      const info = cat(st) && cat(st).guests.find((q) => q.key === g.kind);
      api.popAt(st, ((info && info.emoji) || '✨') + ' ' + (GUEST_NAME[g.kind] || ''), 'big', Math.min(70, x), Math.max(6, y - 6));
      api.sfx(g.kind === 'magpie' || g.kind === 'lord' ? 'coin' : 'deal');
    });
  }

  // ---------- замовлення ----------

  /// Хто замовив: пан із маєтку або конкретна людина з села (тексти — у каталозі).
  function whoOf(st, o) {
    const v = villageOf(st, o.village);
    return o.lord ? 'Пан із маєтку' : (v && v.people[o.who]) || 'Замовник';
  }

  function orderCard(st, api, o, sn) {
    const esc = (x) => api.esc(st, x);
    const v = villageOf(st, o.village);
    const who = whoOf(st, o);
    const ready = o.have >= o.n;
    const req = [];
    if (o.style) req.push('розпис «' + esc(styleName(st, o.style)) + '»');
    if (Q_REQ[o.q]) req.push(Q_REQ[o.q]);
    const share = Math.min(100, Math.round((Math.min(o.have, o.n) / o.n) * 100));
    const chance = Math.round(o.chance * 100);
    const off = !st.mine || !ready;
    return '<div class="clkf-order' + (o.lord ? ' lord' : '') + (ready ? ' ready' : '') + (o.sour ? ' sour' : '') + '" data-order="' + o.id + '">'
      + '<div class="clkf-oart">' + api.wareSvg(o.ware, { style: o.style, quality: o.q, cls: 'clkf-oware', slot: 'ord-' + o.id })
      + '<span class="clkf-on">×' + o.n + '</span></div>'
      + '<div class="clkf-obody">'
      + '<div class="clkf-ohead"><b>' + (o.lord ? '🎩 ' : '') + esc(who) + '</b><span class="muted small">' + (v ? esc(v.emoji + ' ' + v.from) : '') + '</span>'
      + (o.sour ? '<span class="clkf-sour" title="Образився на «накинути»">😤</span>' : '') + '</div>'
      + '<div class="clkf-oreq">' + esc(wareName(st, o.ware)) + ' ×' + o.n + (req.length ? ' · <span class="clkf-oreq2">' + req.join(' · ') + '</span>' : '') + '</div>'
      + '<div class="clkf-obar"><i style="width:' + share + '%"></i><span>є ' + Math.min(o.have, 999) + ' з ' + o.n + '</span></div>'
      + '<div class="clkf-ometa small"><span>💰 ~' + api.potsShort(o.pay) + '</span><span class="muted">×' + api.dec(o.mult) + ' · шана +' + o.rep + '</span>'
      + '<span class="clkf-otime">⏳ <i class="clkf-cd" data-at="' + o.until + '" data-done="поїхав"></i></span></div>'
      + '<div class="clkf-obtns">'
      + '<button type="button" class="ghost small" data-bid="down" data-id="' + o.id + '"' + (off ? ' disabled' : '') + ' title="−15 % плати, зате шана вдвічі">🙇 Поступитись</button>'
      + '<button type="button" class="primary small" data-bid="as" data-id="' + o.id + '"' + (off ? ' disabled' : '') + '>🤝 Здати</button>'
      + '<button type="button" class="ghost small" data-bid="up" data-id="' + o.id + '"' + (off || o.sour ? ' disabled' : '') + ' title="+30 % плати, якщо погодиться">💰 Накинути · ' + chance + ' %</button>'
      + '</div>'
      + (ready ? '' : '<div class="clkf-ohint muted small">бракує ' + (o.n - o.have) + ' — виліпи й обпали</div>')
      + '</div></div>';
  }

  function paintOrders(st, api) {
    const k = st.fair;
    if (!k) return;                    // картку вже закрили (таймер угоди чи відповідь сервера прийшли пізніше)
    const m = k.m;
    if (!m || !k.ordersEl) return;
    const sn = api.serverNow(st);
    const live = k.orders.filter((o) => o.until > sn);
    const c = cat(st);
    const head = '<div class="clk-sub clkf-title">📜 Замовлення' + (live.length ? ' · ' + live.length : '')
      + '<span class="muted small"> · платять ×2–3 від ціни й дають шану селу</span></div>';
    const react = k.react && Date.now() - k.react.at < REACT_MS
      ? '<div class="clkf-react ' + k.react.cls + '"><span class="clkf-remoji">' + k.react.emoji + '</span><span>' + api.esc(st, k.react.text) + '</span></div>'
      : '';
    const cards = live.length
      ? '<div class="clkf-orders">' + live.map((o) => orderCard(st, api, o, sn)).join('') + '</div>'
      : '<div class="clk-teaser muted small">Замовників поки нема — новий прийде через <i class="clkf-cd" data-at="' + k.nextOrderAt + '" data-done="ось-ось"></i></div>';
    const more = live.length && live.length < 4 && k.nextOrderAt > sn
      ? '<div class="muted small clkf-next">наступний замовник — через <i class="clkf-cd" data-at="' + k.nextOrderAt + '" data-done="ось-ось"></i></div>'
      : '';
    // Шана сіл: зірки, смужка до наступного рівня, пільга.
    const levels = (c && c.levels) || [0, 8, 25, 60, 120, 220];
    const rep = c
      ? '<div class="clk-sub clkf-title">🤝 Шана сіл<span class="muted small"> · кожен рівень +1 % до всього'
        + (m.allMult > 1 ? ' (зараз +' + api.dec((m.allMult - 1) * 100) + ' %)' : '') + '</span></div>'
        + '<div class="clkf-reps">' + m.rep.map((r) => {
          const vv = villageOf(st, r.key);
          if (!vv) return '';
          const lvl = r.level;
          const from = levels[lvl];
          const to = levels[Math.min(levels.length - 1, lvl + 1)];
          const p = lvl >= levels.length - 1 ? 100 : Math.round(((r.pts - from) / Math.max(1, to - from)) * 100);
          return '<div class="clkf-rep l' + lvl + '" title="' + api.esc(st, vv.perk) + '"><div class="clkf-rtop"><span>' + vv.emoji + ' <b>' + api.esc(st, vv.name) + '</b></span>'
            + '<span class="clkf-stars">' + '★'.repeat(lvl) + '<i>' + '★'.repeat(levels.length - 1 - lvl) + '</i></span></div>'
            + '<div class="clkf-rbar"><i style="width:' + p + '%"></i></div>'
            + '<div class="muted small clkf-perk">' + (lvl >= levels.length - 1 ? '👑 шана найвища · ' : r.pts + ' / ' + to + ' · ') + api.esc(st, vv.perk) + '</div></div>';
        }).join('') + '</div>'
      : '';
    const html = head + react + cards + more + rep;
    if (api.swap(k.ordersEl, html)) {
      countdowns(st, api, k.ordersEl);
      for (const b of k.ordersEl.querySelectorAll('[data-bid]')) b.onclick = (ev) => deliver(st, api, ev, +b.dataset.id, b.dataset.bid);
    }
  }

  function deliver(st, api, ev, id, bid) {
    const k = st.fair;
    if (!ev || !ev.isTrusted || !st.mine) return;
    const card = k.ordersEl.querySelector('[data-order="' + id + '"]');
    for (const x of (card ? card.querySelectorAll('[data-bid]') : [])) x.disabled = true;
    api.act(st, 'fair', { op: 'deliver', id, bid }).then((r) => {
      if (!r || !r.ok) { paintOrders(st, api); return; }
      const text = r.message || '';
      const line = (text.match(/«([^»]+)»/) || [])[1] || '';
      const refused = text.startsWith('😤');
      const emoji = refused ? '😤' : text.startsWith('🥰') ? '🥰' : text.startsWith('💰') ? '😏' : '🤝';
      k.react = { at: Date.now(), emoji, text: line, cls: refused ? 'no' : 'yes' };
      if (refused) {
        api.sfx('refuse');
      } else {
        api.sfx('deal');
        setTimeout(() => api.sfx('coin'), 180);
        const pay = (text.match(/\+([^·]+?)\s+глек/) || [])[1];
        if (pay) api.popAt(st, '+' + pay.trim(), 'big', 50, 36);
        api.sparks(st, st.fx, 16, true, 50, 44);
      }
      paintOrders(st, api);
      setTimeout(() => paintOrders(st, api), REACT_MS + 50);
    });
  }

  /// Відліки частини: у плашці, пригоді, замовленнях — лише видимі, раз на slow.
  function countdowns(st, api, host) {
    const sn = api.serverNow(st);
    for (const el of host.querySelectorAll('.clkf-cd')) {
      const left = +el.dataset.at - sn;
      const t = left > 0 ? api.mmss(left) : (el.dataset.done || '0:00');
      if (el.textContent !== t) el.textContent = t;
    }
  }

  // ---------- комора: секції й дошка купців ----------

  /// Комору створює ремесло (clicker-craft.js) — воно могло завантажитись і пізніше за нас, тож секції
  /// ставимо щоразу, коли бачимо панель: замовлення зверху, купці в дорогу — в самому низу.
  function ensureStore(st, api) {
    const k = st.fair;
    const store = api.pane(st, 'store');
    if (!store) return;
    if (store.firstChild !== k.ordersEl) store.insertBefore(k.ordersEl, store.firstChild);
    const orders = k.merchantsPane || api.pane(st, 'orders');
    if (orders && !k.merchantsPane) {
      k.merchantsPane = orders;
      const wasOn = st.tab === 'orders';
      api.hideTab(st, 'orders');
      // Панель дошки лишається тим самим елементом (ядро малює її, як і малювало), але більше не вкладка:
      // інакше перемикання вкладок ховало б її всередині комори.
      delete st.panes.orders;
      orders.hidden = false;
      orders.classList.add('clkf-merchants');
      k.merchantsEl.appendChild(orders);
      if (wasOn) api.showTab(st, 'store');
    }
    if (store.lastChild !== k.merchantsEl) store.appendChild(k.merchantsEl);
  }

  // ---------- хроніка ----------

  function chronicle(st, api) {
    const k = st.fair;
    const v = st.lastView || {};
    const m = k.m || {};
    const rep = (m.rep || []).slice().sort((a, b) => b.level - a.level || b.pts - a.pts)[0];
    const topV = rep && rep.level > 0 ? villageOf(st, rep.key) : null;
    const craft = v.craft || {};
    const c = {
      api, nick: (st.ctx && st.ctx.me && st.ctx.me.nick) || 'гончар', total: v.total || 0, formed: craft.formed || 0, fired: craft.fired || 0,
      delivered: m.delivered || 0, guests: m.guests || 0, top: topV, weather: m.weather, holiday: m.holiday && m.holiday.key,
      weekend: !!m.weekend, season: m.season, ware: craft.ware ? wareName(st, craft.ware) : '',
      items: (craft.items || []).reduce((s, it) => s + it.n, 0), orders: (m.orders || []).length, lord: (m.orders || []).some((o) => o.lord),
      stamps: v.stamps || 0, styles: (v.styles || []).filter((s) => s.owned).length, caught: v.caught || 0, grabbed: v.grabbed || 0, firings: v.firings || 0,
    };
    // Про гравця — частіше (раз на два-три рядки), але не той самий рядок поспіль.
    for (let tries = 0; tries < 12; tries++) {
      const pool = Math.random() < 0.4 ? CHRON.filter((x) => typeof x === 'function') : CHRON.filter((x) => typeof x === 'string');
      const pick = pool[Math.floor(Math.random() * pool.length)];
      const text = typeof pick === 'function' ? pick(c) : pick;
      if (!text || text === k.chronText || k.chronRecent.includes(text)) continue;
      k.chronRecent.push(text);
      if (k.chronRecent.length > 20) k.chronRecent.shift();
      k.chronText = text;
      const el = k.chronEl;
      el.classList.remove('in');
      void el.offsetWidth;
      el.textContent = '📰 ' + text;
      el.classList.add('in');
      return;
    }
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'fair',
    order: 50,

    mount(st, api) {
      const k = st.fair = {
        m: null, orders: [], buffs: [], guest: null, nextOrderAt: 0, eventAt: 0,
        guestGone: 0, guestLooked: 0, guestSound: 0, eventSeen: null, eventLooked: 0, orderLooked: 0,
        react: null, result: null, levels: null, chronAt: 0, chronText: '', chronRecent: [],
        plaque: null, buffsEl: null, bellEl: null, eventEl: null, chronEl: null, guestEl: null, ordersEl: null, merchantsEl: null, merchantsPane: null,
      };
      k.plaque = document.createElement('div');
      k.plaque.className = 'clkf-plaque';
      st.stage.insertAdjacentElement('beforebegin', k.plaque);
      // Під сценою: бафи (після бафів ядра), пригода, хроніка — усе перед прилавком.
      const sell = st.el.querySelector('.clk-sell');
      const below = (el) => { if (sell) sell.insertAdjacentElement('beforebegin', el); else st.stage.insertAdjacentElement('afterend', el); };
      k.buffsEl = document.createElement('div');
      k.buffsEl.className = 'clkf-buffs';
      k.buffsEl.hidden = true;
      below(k.buffsEl);
      k.eventEl = document.createElement('div');
      k.eventEl.className = 'clkf-event';
      k.eventEl.hidden = true;
      below(k.eventEl);
      k.chronEl = document.createElement('div');
      k.chronEl.className = 'clkf-chron small';
      below(k.chronEl);
      const layer = api.layer(st, 'front', 'fair');
      k.bellEl = document.createElement('button');
      k.bellEl.type = 'button';
      k.bellEl.className = 'clkf-bell';
      k.bellEl.hidden = true;
      k.bellEl.textContent = '🔔 Пригода!';
      k.bellEl.onclick = () => {
        k.eventEl.scrollIntoView({ block: 'center', behavior: reduced() ? 'auto' : 'smooth' });
        k.eventEl.classList.remove('ping');
        void k.eventEl.offsetWidth;
        k.eventEl.classList.add('ping');
      };
      layer.appendChild(k.bellEl);
      k.guestEl = document.createElement('button');
      k.guestEl.type = 'button';
      k.guestEl.className = 'clkf-guest';
      k.guestEl.hidden = true;
      k.guestEl.addEventListener('pointerdown', (e) => catchGuest(st, api, e));
      k.guestEl.addEventListener('contextmenu', (e) => e.preventDefault());
      layer.appendChild(k.guestEl);
      k.ordersEl = document.createElement('div');
      k.ordersEl.className = 'clkf-store';
      k.merchantsEl = document.createElement('div');
      k.merchantsEl.className = 'clkf-merch';
      k.merchantsEl.innerHTML = '<div class="clk-sub clkf-title">🐴 Купці в дорогу<span class="muted small"> · беруть глеки й повертають більше</span></div>';
      // Смуга «Шлях виробу» здає замовлення своєю кнопкою «Далі» — щоб не лізти в чужі нутрощі, даємо їй дію.
      st.fairDeliver = (id, ev) => deliver(st, api, ev || { isTrusted: false }, id, 'as');
      ensureStore(st, api);
    },

    update(st, v, api) {
      const k = st.fair;
      const m = v.market;
      ensureStore(st, api);
      if (!m) return;
      k.m = m;
      // whoText — для рядка «Далі» у смузі шляху: там нема місця малювати цілу картку.
      k.orders = (m.orders || []).map((o) => Object.assign({}, o, { until: ms(o.until), whoText: whoOf(st, o) }));
      k.buffs = (m.buffs || []).map((b) => ({ kind: b.kind, src: b.src, mult: b.mult, until: ms(b.until) }));
      k.nextOrderAt = ms(m.nextOrderAt);
      k.eventAt = ms(m.eventAt);
      k.event = m.event ? Object.assign({}, m.event, { until: ms(m.event.until) }) : null;
      k.guest = m.guest ? { kind: m.guest.kind, at: ms(m.guest.at), until: ms(m.guest.until), x: m.guest.x || 0, y: m.guest.y || 0 } : null;
      // Шана виросла — зірочки й дзвін (перший вид лише запам'ятовує).
      const levels = {};
      for (const r of m.rep || []) levels[r.key] = r.level;
      if (k.levels) {
        for (const key of Object.keys(levels)) {
          if (levels[key] > (k.levels[key] || 0)) {
            api.sfx('rep-up');
            const vv = villageOf(st, key);
            api.popAt(st, '⭐ ' + (vv ? vv.name : '') + ' · шана ' + levels[key], 'big', 50, 22);
            api.sparks(st, st.fx, 20, true, 50, 28);
          }
        }
      }
      k.levels = levels;
      const ready = k.orders.filter((o) => o.have >= o.n).length;
      // Ремесло підписує вкладку «Комора · N»; дописуємо, скільки замовлень уже можна здати.
      const base = ((st.tabText && st.tabText.store) || 'Комора').replace(/ · 📜\d+$/, '');
      api.tabLabel(st, 'store', base + (ready ? ' · 📜' + ready : ''));
      paintPlaque(st, api);
      paintEvent(st, api);
      paintOrders(st, api);
    },

    frame(st, api) {
      paintGuest(st, api);
    },

    slow(st, api, sn) {
      const k = st.fair;
      if (!k || !k.m) return;
      ensureStore(st, api);
      countdowns(st, api, k.buffsEl);
      if (!k.eventEl.hidden) countdowns(st, api, k.eventEl);
      // Бафи, пригода, замовлення спливли — перемалювати без нового виду.
      if (k.buffs.some((b) => b.until <= sn && b.until > sn - 400)) paintPlaque(st, api);
      paintEvent(st, api);
      if (st.tab === 'store') {
        countdowns(st, api, k.ordersEl);
        if (k.orders.some((o) => o.until <= sn && o.until > sn - 400) || (k.react && Date.now() - k.react.at > REACT_MS && Date.now() - k.react.at < REACT_MS + 400)) paintOrders(st, api);
        // Дошка купців живе в коморі — її відліки ядро оновлює лише на своїй вкладці, тож тут — ми.
        if (k.merchantsPane) {
          for (const el of k.merchantsPane.querySelectorAll('.clk-cd')) {
            const left = +el.dataset.at - sn;
            const t = left > 0 ? api.mmss(left) : el.dataset.done || '0:00';
            if (el.textContent !== t) el.textContent = t;
          }
        }
      }
      // Час пригоди чи нового замовника настав, а гончар лише дивиться: раз питаємо свіжий вид.
      if (api.visible(st) && st.mine) {
        if (k.eventAt && !k.m.event && sn > k.eventAt + 1500 && k.eventLooked !== k.eventAt) { k.eventLooked = k.eventAt; api.act(st, 'look'); }
        else if (k.nextOrderAt && sn > k.nextOrderAt + 1500 && k.orderLooked !== k.nextOrderAt) { k.orderLooked = k.nextOrderAt; api.act(st, 'look'); }
      }
      if (Date.now() - k.chronAt >= CHRON_MS) {
        k.chronAt = Date.now();
        chronicle(st, api);
      }
    },

    unmount(st) {
      const k = st.fair;
      if (!k) return;
      for (const el of [k.plaque, k.buffsEl, k.bellEl, k.eventEl, k.chronEl, k.guestEl, k.ordersEl, k.merchantsEl]) if (el) el.remove();
      st.fairDeliver = null;
      st.fair = null;
    },
  });

})();
