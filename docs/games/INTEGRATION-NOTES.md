# Нотатки інтеграції — читати перед тим, як писати гру

Це не заміна [ARCHITECTURE.md](ARCHITECTURE.md) і [PROTOCOL.md](PROTOCOL.md), а витяг із них плюс те, що
з'ясувалось, коли чотири пакети хвилі 1 вперше зійшлись докупи й поїхали в браузері. Порядок читання для
автора гри: spec своєї гри → цей файл → ARCHITECTURE/PROTOCOL за потребою → [TESTING.md](TESTING.md).

Усе тут перевірено на живому сервері (порт 8090, дві вкладки) 9 вересня 2026.

---

## 1. Гра на сервері

### Клас

`src/Hlechyky/Games/Impl/<Name>.cs`, публічний клас-нащадок `Game` з **публічним конструктором без
параметрів** — реєстр знайде його сам, вписувати нікуди не треба.

```csharp
public sealed class Wordle : Game, IDailyGame
{
    public override GameInfo Info { get; } = new(
        "wordle", "Глек-слово", "Глек-слово", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true,
        Score: ScoreOrder.LowerIsBetter, Hint: "Слово дня на шість спроб");

    public override void Start() { … }                              // обов'язково
    public override object View(int? seat) => new { … };            // обов'язково
    public override ActResult Act(int seat, string action, JsonElement payload) => …;
    public override TickResult Tick() => …;                         // тільки для TickMs > 0
    public override object? Frame() => new { … };                   // компактний кадр реалтайму
    public override string SeatName(int seat) => …;
    public override void Configure(IReadOnlyDictionary<string, string> options) { … }
    public override void OnLeave(int seat) { … }                    // типово — техпоразка
    public override string? Save() / public override void Load(string json)
    public override string SoloKey(string nickKey, IClock clock) => …;
}
```

`GameInfo` має ще поле `Client` (додано в WP3): ім'я файла модуля в браузері без розширення. Порожнє —
беруть `Id`. Родина ігор в одному файлі (`ttt` + `ttt3` → `ttt.js`, майбутні режими змійки → `snake.js`)
пише `Client: "snake"` у всіх, крім першої.

### Хто і коли це кличе

| Метод | Коли | Під замком кімнати |
|---|---|---|
| `Configure(options)` | один раз, при створенні кімнати, ще до першого `Start` | ні (тому важке робити тут не гріх) |
| `Start()` | Lobby→Playing і на кожен «Ще раз» (`Ctx.Round` уже збільшено) | так |
| `Act(seat, action, payload)` | хаб-метод `Act` (покрокові) і хаб-метод `Input` (реалтайм) | так |
| `Tick()` | `TickEngine`, раз на `Info.TickMs`, лише поки `Playing` | так |
| `View(seat)` / `Frame()` | перед кожною розсилкою | так |
| `OnLeave(seat)` | хтось встав або не повернувся після grace | так |
| `Save()` | після кожного успішного `Act` і на `Finish` (лише `Persistent` + є `Key`) | так, а запис у базу — вже поза |
| `Load(json)` | у `OpenSolo`, якщо в сховищі щось лежить | так |

Важливе:

- **`Act` один на два входи.** Покроковий хід приходить із `Act` (є відповідь, помилка стає тостом), а
  реалтайм-ввід — з `Input` (відповіді нема, `ActResult.Fail` і `GameError` мовчки ковтаються). Це той
  самий метод гри; розрізняйте за `action`.
- **Що повертати з `Act`.** Хід прийнято — `ActResult.Done`. Прийнято, і є що сказати тому, хто ходив, —
  `ActResult.Accept("Запропонував нічию")`. Відмова — `ActResult.Fail("Зараз не твій хід")`. Методу з іменем
  `Ok` у `ActResult` нема — `Ok` там `bool`-поле запису.
- **Нелегальний хід не рахується ходом.** `ActResult.Fail(...)` або `throw new GameError("…")` — стан не
  міняти. Каркас відкотить лічильник ходів; текст побачить лише той, хто ходив.
- **Кінець партії — тільки `Ctx.Finish`.** Другий виклик у тій самій партії ігнорується з попередженням.
  Після `Finish` каркас сам відбиває `Act` («Партію зіграно, тисни «Ще раз»») і перестає тикати.
- **Виняток із `Act`/`Tick`/`Start` не валить сервер**: кімната закривається нічиєю «партія зламалась,
  вибачте», решта кімнат живе далі. Це страховка, а не спосіб писати ігри.
- **`View` мусить бути новим об'єктом.** Анонімний запис із клонами масивів — на дроті буде camelCase.
  Для покрокових ігор тримайте поле `turn: int?` — з нього каркас малює «Твій хід» / «Ходить X».

### Що каркас робить сам (а гра не повинна)

- рядок Журналу «X і Y сіли грати в …» на кожен новий склад за столом;
- техпоразка при виході посеред партії (типовий `OnLeave`) і рядок про це;
- «Ще раз»: місця **обертаються** (0↔1 на двох, зсув по колу на N), `Round++`, `Start()` знову, ставка
  списується знову. Гра, яка тримає рахунок серії, має сама помітити обертання — див. `SnakeGame.Start`;
- ставки: списання на старті, банк переможцям, повернення на нічию, «Бракує черепків на ставку»;
- нагороди за партію, рейтинг Ело, ачівки платформи, запис результату;
- знімок лобі, види по місцях, кадри глядачам, grace 20 с на F5;
- квоти: 10 `Act` і 30 `Input` на секунду з одного з'єднання, payload ≤ 8 КБ.

Чого робити не можна: правити `core.js`, `app.js`, `Rooms.cs`, `Contracts.cs`, `Program.cs`, `Db.cs`;
кликати модель (Anthropic) синхронно в `Act`/`Tick`; брати `DateTime.UtcNow` і `Random.Shared` замість
`Ctx.Clock` і `Ctx.Rng`; писати в базу з-під замка кімнати.

### `IRoomContext` — що просити в каркаса

```csharp
Ctx.RoomId; Ctx.Players; Ctx.Round; Ctx.Rng; Ctx.Clock; Ctx.Options; Ctx.Services;
Ctx.NickOf(seat); Ctx.Seated(seat);
Ctx.Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null);
Ctx.Log(text);      // рядок Журналу всім
Ctx.Say(text);      // Дядько Глек у Балачки
Ctx.Score(seat, value);            // соло-результат у таблицю
Ctx.Award(seat, shards, reason);   // черепки/ачівка поза стандартною виплатою
```

- `Finish([], "…")` — нічия. Індекси поза межами місць каркас відкине.
- `Score(seat, value)` піднімає `SoloScoreEvent` із `Key` кімнати. Для щоденної гри ключ має вигляд
  `daily:<гра>:<рррр-мм-дд>:<nick_key>` — саме з нього сервіси дістають день. Домовленість про значення:
  **менше 1000 — це спроби, 1000 і більше — мілісекунди**.
- `Award(seat, 0, "ach:<key>")` — попросити ачівку, якої платформа сама не бачить (роль у мафії, слово на
  30 очок). Нуль тут не «нічого», а сигнал: черепки платить сама ачівка з каталогу. **У WP1 каркас нуль
  відсікав — виправлено у WP3, тест `An_award_of_zero_shards_still_reaches_the_services`.**
- `Award(seat, n, "daily:<гра>")` — щоденна нагорода (нуль → типова з налаштувань, `Economy:DailyReward`),
  `"clicker"` — обмін глеків, `"ad:winner"`/`"ad:vote"` — конкурс реклами, решта причин ідуть через
  спільну добову стелю `Economy:AwardDailyCap`.
- `Ctx.Services` — сервіси сервера: `Ctx.Services.GetRequiredService<Words>()` і т. ін. Ігри створюються
  без параметрів, тому залежності беруть тут, у `Configure()`/`Start()`.

### Соло, приватні й щоденні кімнати

- `MaxPlayers == 1` → `OpenSolo` замість `CreateRoom`; клієнт малює кнопку «Грати».
- Ключ кімнати: `SoloKey(nickKey, clock)`, типово `"<id>:<nick_key>"`. Щоденна гра перекриває його і додає
  день: `$"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}"`.
- `Persistent: true` + непорожній `Save()` → стан лягає в `game_state` і піднімається при наступному
  `OpenSolo`. `Private: true` → кімнати нема в лобі, дивитись може лише господар.
- Соло-кімната переживає закриття вкладки (її прибирає прибиральник через 30 хв), на відміну від столів.
- Маркер `IDailyGame` вмикає гру в панель «Щоденний глек» і в каталог (`daily: true`).

---

## 2. `RoomHarness` — тести гри без браузера

`tests/Hlechyky.Tests/Support/RoomHarness.cs`. Кімната збирається через справжній `Registry` і справжній
`Rooms`, тож тест грає рівно в те, у що гратимуть люди.

```csharp
var h = new RoomHarness("ttt", options: new { variant = "classic" }, seed: 42);
h.Join("Оля");                       // перший — створює кімнату і стає господарем
h.Join("Петро");                     // WhenFull → партія почалась
h.Act(0, "move", new { cell = 4 }).Ok.Should().BeTrue();
h.View(1).GetProperty("turn").GetInt32();          // JsonElement — те, що піде на дріт
h.Tick(times: 10);                                 // FakeClock += TickMs і Rooms.Tick
h.Input(1, "turn", new { dir = 3 });               // реалтайм-ввід
h.Leave("Петро");                                  // техпоразка, якщо партія йшла
h.Rematch();                                       // місця обернулись, Round++
Assert.Equal(RoomStatus.Finished, h.Room.Status);
Assert.Contains("1:0", h.Outbox.OfType<Journal>().Last().Text);
```

Що є ще: `h.Solo("Оля", key)` (соло/щоденна кімната), `h.Start()` (для `StartMode.ByHost`), `h.Reply`
(останній `RoomReply` — щоб перевірити текст відмови), `h.Room`, `h.RoomId`, `h.NickOf(seat)`,
`h.Clock` (`FakeClock.AdvanceMs`), `h.Stakes` (`FakeStakes` — можна покласти баланс), `h.Store`
(`FakeStore` для `Save/Load`), `h.Outbox` (усі `Outgoing` за весь час), `h.Finished` / `h.Scores` /
`h.Awards` (події для сервісів), `h.Registry`, `h.Rooms`.

Сервіс для гри: `new RoomHarness("wordle", services: RoomHarness.WithService(new Words(dir)))`.
Кілька сервісів — свій `ServiceCollection().BuildServiceProvider()`.

`Views.Json(obj)` серіалізує вид у той самий JSON, що піде на дріт (camelCase) — перевіряйте форму видів
саме ним, а не C#-типами. `Views.Payload(obj)` робить `JsonElement` із анонімного об'єкта.

---

## 3. Модуль гри в браузері

`web/games/<id>.js` (+ `<id>.css`, якщо треба). Файл вантажиться після `core.js`; усередині —
`HGames.register({...})`. Кілька `register` в одному файлі — нормально: так живуть `ttt` і `ttt3`.
Головне — щоб у `GameInfo` тих ігор, чий файл зветься інакше, стояло `Client: "<файл>"`, інакше реєстр
попередить у лог, а браузер сходить по 404.

```js
HGames.register({
  id: 'ttt',
  icon: '<svg class="gico" viewBox="0 0 16 16">…</svg>',   // 16×16, кольори через var(--accent)
  seatNames: ['✕', '◯'],          // або (i, room) => string
  seatClass: ['x', 'o'],          // клас чіпа місця: x | o | c | d
  mount(root, ctx) {},            // один раз, коли картка з'явилась; root — <div class="gbody">
  update(root, ctx) {},           // на кожну подію 'room' (і одразу після mount)
  frame(root, ctx, f) {},         // на кожну подію 'frame' (реалтайм)
  unmount(root, ctx) {},          // прибрати таймери/rAF
  onKey(e, ctx) { return false }, // keydown, коли ця кімната активна; true — оброблено
  status(ctx) { return '' },      // рядок під тілом; порожньо — каркас напише своє
});
```

Заголовок, підказка, група, кількість гравців і опції беруть із каталогу сервера — у модулі їх не дублюють.

`onKey(e, ctx)`: розкладконезалежні клавіші читайте з `e.code` (`KeyW`, `ArrowUp` — WASD працюватиме і на
українській розкладці), літери — з `e.key`. Синтетичний `keydown` із панелі браузера приходить без `e.code`,
тож обробник, зав'язаний на `code`, такою підробкою не перевіряється — тисніть кнопки `ui.dpad` або клавіші руками.

`ctx` (той самий об'єкт живе, поки картка на екрані):

```
room, seat, view, me, frame, playing, mine, myTurn,
act(action, payload) → Promise<RoomReply>, input(action, payload),
toast(text, kind), esc(s), seatName(i), nickOf(i), css(varName, fallback), ui
```

`ctx.frame` — останній кадр цієї кімнати (`null`, поки не було). Для реалтайм-ігор фаза й відлік живуть
саме в кадрах, а не у видах: `status()` має дивитись спершу в `ctx.frame`.

### `HGames.ui` — справжні сигнатури (з `core.js`)

```js
ui.grid(host, { cols, rows, cls, cell(i) → html | { html, cls, disabled }, onCell(i, btn) })
ui.canvas(host, { w, h, cls })            → { el, ctx, w, h, resize() }
ui.dpad(host, onDir(0..3), dirs?)         // з'являється лише при pointer: coarse
ui.keyboardUa(host, onKey(ch), state)     // state: { 'а': 'G'|'Y'|'B' }
ui.timerArc(host, untilIso, totalMs)      → { el, set(untilIso, totalMs), stop() }
ui.hand(host, items, { onItem(item, i, on), selectable, multi?, render? }) → { el, selected(), clear() }
ui.gamepad(onDir(0..3|-1), onPress(btn))  → { active, stop() }   // Steam Deck, Xbox, DualShock
ui.lerp(a, b, t);  ui.Interp() → { push(f), at() → { a, b, t }, reset() }
ui.css(varName, fallback);  ui.coarse()
```

Усі вони ідемпотентні: кличте з `mount()` і з кожного `update()` — елемент буде один, а колбеки братимуться
з останнього виклику. Напрямки скрізь однакові: **0 праворуч, 1 вниз, 2 ліворуч, 3 вгору**.

`ui.gamepad` — виняток: його кличуть **раз у `mount()`** і спиняють у `unmount()` (`handle.stop()`), бо він
крутить власний rAF. Браузер не шле подій про стан кнопок, тільки про під'єднання, тому стан опитується;
`onDir` приходить лише на зміну напрямку (хрестовина, а як її не чіпають — лівий стік із зоною 0.5),
`onPress` — на натиск кнопки (`a`, `b`, `x`, `y`, `l1`, `r1`, `l2`, `r2`; Start і Select не віддаємо — на
Steam Deck вони системні). Поки геймпада нема, не кличеться нічого, тож клавіатура й палець не страждають.
`handle.active` — чи бачимо геймпад зараз (для рядка підказки). Два застереження: браузер ховає геймпад,
доки на ньому не натиснуть **хоч одну кнопку**, а rAF спить, поки вкладку не видно — тобто й опитування теж.
Колбеки ставляться раз, а `ctx` міняється на кожному `update` — беріть живий `ctx` зі свого стану, не з
замикання.

Панель поруч із «Профілем» і «Таблицею» — `HGames.registerPanel({ id, title, icon, mount(host, ctx), update? })`.
Окремого `registerTile` нема: плитки лобі каркас будує сам із каталогу сервера. Ще є `HGames.has(id)`,
`HGames.call(method, …)` / `HGames.send(method, …)` (хаб напряму) і `HGames.catalog`.

### CSS

Кольори — тільки через змінні. З `web/static/style.css`: `--bg`, `--bg2`, `--panel`, `--panel2`, `--panel3`,
`--line`, `--text`, `--muted`, `--accent`, `--accent2`, `--accent-ink`, `--danger`, `--ok`, `--clay`,
`--radius`, `--font`. З `web/games/core.css`: `--gwin` (виграшний ряд), `--gsnake2`, `--gshade`. Свою змінну
оголошуйте в `:root` у власному `<id>.css` і завжди давайте фолбек: `ctx.css('--gmine', '#c5763a')`.

Готові класи каркаса: `.board` / `.board .cell` (+ `.x`, `.o`, `.win`, `.fading`, `.discs`), `.gcanvas`,
`.dpad`, `.gkbd`, `.garc`, `.ghand`/`.gcard`, `.gscore`, `.gwait`, `.gempty`, `.chip`, `.muted`, `.small`.

---

## 4. Словники (`Words`)

Береться з `Ctx.Services.GetRequiredService<Words>()`. Ніколи не кидає: нема файлів — порожні списки й
`Loaded == false`, і гра має сказати гравцеві «нема словника», а не впасти.

```csharp
bool  Loaded         // малі списки на місці: Глек-слово і Віселиця можуть грати
bool  FullLoaded     // великий словник (uk-all.db) підключено — Ерудит приймає будь-яку форму
Task  FullReady      // завершується, коли з великим словником усе зрозуміло (для тестів)
WordsStats Stats     // скільки слів у кожному списку

string Daily5ForDay(string day)      // слово дня за «рррр-мм-дд» (стала перестановка, без повторів роками)
bool   IsValid5(string word)         // чи можна ввести як спробу: 5 українських літер і слово зі списків
string RandomHangman(Random rng, int minLen = 5, int maxLen = 12)   // випадкове слово для Віселиці
bool   IsWord(string word)           // є в словнику (з великим — будь-яка словоформа)
static string? Normalize(string word)  // нижній регістр; null, якщо є апостроф, латиниця, дефіс, цифри
```

Слів з апострофом у списках нема свідомо. Випадковість — тільки з `Ctx.Rng`, інакше партія не відтвориться.
У проді на цій машині великого словника нема (`uk-all.db` збирається `setup.ps1`), тож `FullLoaded == false` —
гра має працювати й так.

---

## 5. Економіка: що платить каркас, а що просить гра

Каркас платить сам, гра не робить нічого:

| За що | Скільки (`appsettings.json`, секція `Economy`) | Стеля |
|---|---|---|
| перемога в мультиплеєрній партії | `WinReward=5` | `RewardedGamesPerDay=10` на гру на ніка |
| нічия | `DrawReward=2` | та сама |
| участь (програв, але дограв) | `PlayReward=1` | та сама |
| онлайн на сайті | `ListenReward=1` за `ListenEveryMinutes=10` | `ListenDailyCap=12`/день |
| ставки | нуль-сумові: банк переможцю, нічия — повернення | поза стелями |

**Партія має бути «справжньою»:** нагорода йде, якщо ходів було ≥ `MinRewardMoves` (6) **або** партія
тривала ≥ `MinRewardSeconds` (20 с). У реалтайм-ігор ходів нема взагалі (`Moves == 0`), тож для них
працює лише час: змійка, яку зіграли за вісім секунд, черепків не приносить. Це не баг.

`GrantResult`: `Applied` (нарахували), `Duplicate` (той самий `ref` уже був), `Capped` (уперлись у стелю
дня), `Skipped` (нуль або порожній нік). Ідемпотентність тримають `ref`-ключі, тож повторна подія нічого
не подвоює.

Ачівки, які платформа бачить сама (грі робити нічого): `first-game`, `first-win`, `ten-wins`, `streak-3`,
`all-boards`, `high-roller`, `rich-100`, `listener-10h/100h`, `chess-5`, `checkers-5`, `duel-10`
(з результатів партій), `duel-fast`, `potter-1k/100k` (з `Ctx.Score`), `wordle-2`, `wordle-7`,
`mines-fast` (з результату дня).

Ачівки, яких платформа не бачить — гра просить сама через `Ctx.Award(seat, 0, "ach:<key>")`:
`scrabble-30`, `mafia-win`, `sheriff`, `ad-winner`. Ключ має бути в `AchievementCatalog`, інакше нічого
не станеться (і це не помилка).

«Настільний» (`all-boards`) — перемога в кожній настільній грі реєстру. Нова настільна гра піднімає планку
для тих, хто ачівки ще не має; здобуту ніхто не забирає. Поки настільних ігор у реєстрі менше трьох, ачівка
не видається взагалі (`Achievements.cs`) — у своєму worktree з однією-двома настільними іграми ви її не
побачите, і це не поламана ачівка.

---

## 6. Перевірка в браузері на своєму порту

1. `appsettings.Local.json` у своєму worktree (він у `.gitignore`). **Обов'язково відведіть ефір убік** —
   інакше ваш сервер знайде живий liquidsoap проду на `127.0.0.1:1234` і почне ним керувати:
   Свій порт беріть із таблиці в [PLAN.md](PLAN.md) §6 (8100 + номер гри) — нижче для прикладу 8101.
   ```json
   {
     "Site": { "ListenPort": 8101 },
     "YtDlp": { "FfmpegDir": "D:/or/tools/yt-dlp", "BinaryPath": "D:/or/tools/yt-dlp/yt-dlp.exe" },
     "Auth": { "AdminKey": "dev" },
     "Liquidsoap": { "Port": 11234 },
     "Icecast": { "StatusUrl": "http://127.0.0.1:18000/status-json.xsl" },
     "AutoDj": { "Enabled": false },
     "DjBot": { "Enabled": false },
     "Deploy": { "Enabled": false }
   }
   ```
   Ефір покаже «↓» — так і має бути, ігор це не стосується.
2. Запуск (PowerShell, зі свого worktree). Каталог `logs/` у `.gitignore`, у свіжому worktree його нема —
   без першого рядка `Start-Process` падає з «Could not find a part of the path»:
   ```powershell
   New-Item -ItemType Directory -Force logs | Out-Null
   dotnet build src/Hlechyky/Hlechyky.csproj -nodeReuse:false
   $p = Start-Process dotnet -ArgumentList 'src/Hlechyky/bin/Debug/net10.0/Hlechyky.dll' `
        -WorkingDirectory <worktree> -PassThru `
        -RedirectStandardOutput logs/dev.out.log -RedirectStandardError logs/dev.err.log
   ```
   Зупинка — **тільки** `Stop-Process -Id $p.Id`. `Stop-Process -Name dotnet` уб'є живе радіо.
3. Перше, що дивимось у `logs/dev.out.log`: рядок `Now listening on`. Якщо його нема, а процес живий —
   сервер завмер на старті, і сторінка просто не відкриється (див. граблі §7).
4. Дві вкладки: `http://localhost:<порт>` і `http://127.0.0.1:<порт>` — різні origin, різні `localStorage`,
   різні ніки. Третій глядач — `http://127.0.0.2:<порт>`.
5. Мінімальний прогін своєї гри: створити стіл → сісти другим → дограти до кінця → Журнал → тост гаманця →
   «Ще раз» (місця обернулись) → «Встати» посеред партії (техпоразка) → F5 посеред партії (місце має
   вціліти, grace 20 с) → третя вкладка бачить «Дивишся збоку» → вузьке вікно (≤ 480 px): картка не ламає
   розкладку, керування є для пальця.
6. Файли `web/` віддаються з диска без кешу — після правки `<id>.js` досить F5, сервер перезапускати не треба.

---

## 7. Граблі, на які вже наступили

1. **Коло залежностей у DI кладе сервер мовчки.** `Rooms` просить `IStakes` (економіка), економіка —
   `IOutbox`, `Broadcaster` — знову `Rooms`. Контейнер на такому колі не кидає виняток, а просто завмирає:
   у лозі є тільки перший рядок, `Now listening on` нема, порт закритий. Виправлено (`DeferredOutbox`), але
   якщо ваш сервіс замикає нове коло — симптом буде такий самий. Ознака: процес живий, CPU нуль, лог обірваний.
2. **Ваш дев-сервер бачить liquidsoap проду.** Порт 1234 на цій машині зайнятий живим радіо: без правки
   `appsettings.Local.json` (§6) ваш `RadioEngine` «усиновить» ефір друзів. Перевірка: у лозі має бути
   `liquidsoap not reachable at start-up`.
3. **Сервер тримає свою dll.** `dotnet build`/`dotnet test` під час роботи сервера падає з MSB3027
   («file is locked by .NET Host»). Порядок: зупинити свій сервер → зібрати → запустити знову.
4. **Payload ходу — це те, що читає ваш `Act`.** Модуль `c4` слав `{ col }`, а `GridGame` читає `cell` —
   хід просто зникав, без жодної помилки на екрані. Пишіть тест «сервер приймає рівно те, що шле модуль»
   (`The_move_payload_is_always_cell_even_when_it_means_a_column`) і не покладайтесь на очі.
5. **Кнопка картки залежить від чужого столу.** «Сісти» не показують тому, хто вже сидить за іншим
   мультиплеєрним столом. Картка перемальовує кнопки за підписом свого стану, тож глобальні речі треба
   в цей підпис класти — інакше людина встає з-за одного столу, а кнопка на іншому не з'являється.
   Виправлено; якщо додаєте свою кнопку з умовою «ззовні кімнати» — пам'ятайте про це.
6. **`ttt3` не має свого `ttt3.js`.** Дві гри в одному файлі — законно, але тепер це треба оголосити
   (`Client: "ttt"`), інакше 404 у браузері й попередження в лозі.
7. **Нуль черепків — це сигнал, а не «нічого».** `Award(seat, 0, "ach:key")` каркас колись відсікав. Якщо
   ваша ачівка не приходить — спершу перевірте, що подія взагалі піднялась (`h.Awards` у тесті).
8. **`GameNames` більше не сканує збірку.** Список ігор економіка бере з реєстру; у тестах його будують
   явно (`new GameNames([])` + `Learn(info)`). Не пишіть тестів, які залежать від того, скільки ігор уже
   лежить в `Impl/` — вони зламаються від наступної гри.
9. **`keydown` із `target === document`** валив обробник клавіатури каркаса (`t.matches is not a function`)
   і мовчки з'їдав усі клавіші. Виправлено, але сама наука лишається: обробник, який кидає, у консолі видно,
   а на екрані — ні.
10. **Тост гаманця вже містить суму.** Сервер шле готовий текст «+5 черепків: перемога — Хрестики-нолики»;
    ліпити до нього ще одне число не треба.
11. **Ело в таблиці не залежить від періоду.** `period` для рейтингових таблиць приймається, але рядки
    завжди за весь час — так і задумано.
12. **Реалтайм і нагороди.** `Moves == 0` для реалтайму: коротка партія (< 20 с) черепків не приносить.
    Не лякайтесь, коли тестуєте змійку.
