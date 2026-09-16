# Дротовий контракт: хаб ↔ браузер, каркас ↔ модуль гри

Усе, що тут описано, — контракт між трьома незалежними роботами (сервер-каркас, клієнт-каркас, гри).
Міняти його можна лише правкою цього файлу разом з усіма сторонами. JSON на дроті — camelCase (типове
для SignalR у цьому проєкті: C#-record `Winners` → `winners`).

## 1. Методи хаба (`RadioHub`, браузер → сервер)

| Метод | Аргументи | Відповідь | Що робить |
|---|---|---|---|
| `CreateRoom` | `gameId: string`, `options: object\|null` | `RoomReply` | Створює кімнату, садить викликача на місце 0. `options` — ключі з `GameInfo.Options` + `stake` (число). Для `Immediate` стартує одразу. |
| `OpenSolo` | `gameId: string`, `key: string\|null` | `RoomReply` | Повертає (створює або відновлює) особисту кімнату соло-гри. `key=null` → гра сама визначає ключ (щоденна — за днем, клікер — за ніком). |
| `JoinRoom` | `roomId` | `RoomReply` | Сісти на перше вільне місце. |
| `LeaveRoom` | `roomId` | `RoomReply` | Встати. Посеред партії — техпоразка. |
| `StartRoom` | `roomId` | `RoomReply` | Господар стартує `ByHost`-гру, коли ≥ MinPlayers. |
| `Rematch` | `roomId` | `RoomReply` | «Ще раз» тим самим складом; місця обертаються (0↔1 для двох, зсув для N), `Round++`. Може натиснути будь-хто із сидячих після `Finished`. |
| `Act` | `roomId`, `action: string`, `payload: any` | `RoomReply` | Покроковий хід. Помилка — лише викликачу. |
| `Input` | `roomId`, `action: string`, `payload: any` | (нічого) | Реалтайм-ввід. Без відповіді. |
| `WatchRoom` / `UnwatchRoom` | `roomId` | (нічого) | Підписка на `room`/`frame` цієї кімнати. Після реконекту клієнт підписується заново. |

```ts
type RoomReply = { ok: boolean; message: string; roomId?: string };
```
`message` — українською, коротко, для тоста («Стіл на двох, місць уже нема»). При `ok` і порожньому
`message` тост не показується.

Помилки, які каркас віддає сам (гра їх не дублює): «Спершу скажи, як тебе кликати», «Такої гри тут нема»,
«Такої кімнати вже нема», «Ти вже за столом. Встань, якщо хочеш новий», «Столів уже задосить, дограйте ті, що є»,
«Ти вже в цій кімнаті», «Місць уже нема», «Чекаємо на гравців», «Партію зіграно, тисни «Ще раз»»,
«Ти тут не граєш», «Бракує черепків на ставку», «Не так швидко», «Почати може лише господар»,
«Замало гравців, треба щонайменше N», «Партія ще не скінчилась» (передчасне «Ще раз»),
«Забагато даних» (payload понад 8 КБ).

## 2. Події (сервер → браузер)

| Подія | Кому | Тіло |
|---|---|---|
| `rooms` | усім (і на підключенні) | `RoomSummary[]` — усі не-приватні кімнати |
| `room` | глядачам кімнати | `RoomView` |
| `frame` | глядачам кімнати | `{ id: string, f: any }` |
| `wallet` | з'єднанням ніка | `{ balance: number, delta: number, reason: string, text: string }` |
| `achievement` | з'єднанням ніка | `{ key, title, text, icon, reward }` |
| `toast` | з'єднанням ніка | `{ text: string, kind: 'ok'\|'err'\|'wait' }` |
| `invite` | усім | `{ roomId: string, by: string, text: string }` — «Влад кличе в Мафію». Свій заклик (`by` = мій нік) браузер відкидає сам |
| `chat` | усім | як зараз; рядки Журналу мають `kind: 'system'`, слова Глека — `'dj'`. Необов'язковий `roomId` — живий стіл, про який рядок: браузер малює біля нього кнопку «Сісти»/«Дивитись». Відповідь на `/столи` має `kind: 'tables'`, масив `rooms: string[]` і летить **лише** тому, хто спитав (у базу не лягає) |

`rooms` завжди випереджає `chat` в одній пачці: інакше рядок про стіл доходив би раніше за новину про
те, що там уже нема місця (ARCHITECTURE §5).

```ts
type RoomSummary = {
  id: string;
  game: string;                       // GameInfo.Id
  status: 'lobby' | 'playing' | 'finished';
  seats: { i: number; nick: string | null }[];   // довжина = maxPlayers
  seatNames: string[];                // Game.SeatName(i) для кожного місця («білі», «чорні»)
  host: string;                       // нік господаря
  minPlayers: number; maxPlayers: number;
  options: Record<string, string>;    // без stake
  stake: number;                      // черепків з кожного, 0 — без
  round: number;
  watchers: number;                   // скільки з'єднань дивиться
  result: { winners: number[]; draw: boolean; text: string } | null;
  createdAt: string; startedAt: string | null; finishedAt: string | null;
};

type RoomView = {
  room: RoomSummary;
  seat: number | null;                // моє місце в цій кімнаті, або null (глядач)
  view: any;                          // Game.View(seat) — свій для кожної гри
};
```

Каталог (HTTP, не хаб): `GET /api/games/catalog` →
```ts
{ games: { id, title, accusative, group: 'board'|'live'|'party'|'solo', minPlayers, maxPlayers,
           tickMs, start: 'whenFull'|'byHost'|'immediate', hidden, private, rated,
           options: { key, label, values: [value, label][], default, multi: boolean }[], hint, hasCss: boolean,
           daily: boolean, module: string }[],
  stakes: number[] }   // дозволені ставки, напр. [0, 5, 10, 25]
```

`module` — ім'я файла модуля без розширення: каркас вантажить `/games/<module>.js` (і `/games/<module>.css`,
якщо `hasCss`). Типово це `id`, але кілька ігор можуть ділити один файл (`ttt3` → `ttt`), і тоді він
вантажиться один раз. Ставку каркас показує в попапі створення там само, де її приймає сервер: `maxPlayers == 2`
і `rated`.

`multi` — опцію можна обрати кількома значеннями (теми «Скільки?»): попап малює чипи, у `CreateRoom` і в
`room.options` значення йдуть одним рядком через кому (`"ukraine,science"`; масив рядків сервер теж приймає).
`default` такої опції — «усе»: з рештою не поєднується, і до нього сервер зводить порожній або чужий вибір.

Інші HTTP (WP1): `GET /api/games/leaderboard?game=&period=`, `GET /api/games/profile?nick=`,
`GET /api/games/daily`, `GET /api/games/wallet` (свій баланс). Форми відповідей — у ARCHITECTURE §7 і в
`Economy/*` з коментарями; клієнтський каркас читає їх як є.

## 3. Модуль гри на клієнті (`web/games/<id>.js`)

Файл завантажується після `core.js`; у ньому один виклик:

```js
HGames.register({
  id: 'chess',                     // == GameInfo.Id
  icon: '<svg class="gico" viewBox="0 0 16 16">…</svg>',   // 16×16, кольори через var(--accent)/var(--ok)/var(--clay)
  // Заголовок, підказка, група, гравці, опції — беруться з каталогу сервера; тут лише те, чого сервер не знає:
  seatNames: ['білі', 'чорні'],    // або функція (i, room) => string; для чіпів місць
  seatClass: ['x', 'o'],           // CSS-клас чіпа місця (кольори: x=accent, o=ok, c=clay, d=muted)

  mount(root, ctx) {},             // один раз, коли картка кімнати з'явилась; root — <div class="gbody">
  update(root, ctx) {},            // на кожну подію `room` (і одразу після mount)
  frame(root, ctx, f) {},          // на кожну подію `frame` (реалтайм); може не бути
  unmount(root, ctx) {},           // картка зникає; прибрати таймери/rAF
  onKey(e, ctx) { return false },  // keydown, коли ця кімната активна; true — оброблено (preventDefault)
  status(ctx) { return '' },       // рядок статусу під тілом; порожньо → каркас пише своє («Твій хід», «Ходить X»)
});
```

`ctx` (той самий об'єкт живе, поки картка на екрані; поля оновлюються перед `update`):
```ts
type Ctx = {
  room: RoomSummary; seat: number | null; view: any; me: { nick: string; role: string };
  frame: any;                       // останній `frame` цієї кімнати (null, поки не було); для status() реалтайм-ігор
  playing: boolean;                 // room.status === 'playing'
  mine: boolean;                    // seat !== null
  myTurn: boolean;                  // якщо view.turn існує і === seat
  act(action: string, payload?: any): Promise<RoomReply>;   // тост на помилку — сам каркас
  input(action: string, payload?: any): void;
  toast(text: string, kind?: 'ok'|'err'|'wait'): void;
  esc(s: string): string;
  seatName(i: number): string; nickOf(i: number): string | null;
  css(varName: string, fallback: string): string;           // getComputedStyle(:root)
  ui: typeof HGames.ui;
};
```

`HGames` крім `register` має: `registerPanel({ id, title, icon, mount(host, ctx), update? })` — своя панель
поруч із «Профілем» (окремого `registerTile` нема: плитки лобі каркас будує сам із каталогу), `has(id)`,
`ui`, `call(method, ...args)` / `send(method, ...args)` (хаб напряму), `catalog`.

`HGames.ui` (хелпери каркаса, реалізує WP2):
- `grid(root, { cols, rows, cell(i) → html | { html, cls, disabled }, onCell(i), cls })` — кнопкова сітка як у хрестиків. Об'єктна форма `cell(i)` потрібна, щоб позначити виграшний ряд і заблокувати чужий хід.
- `canvas(root, { w, h, cls })` — `<canvas>` із DPR-масштабом; повертає `{ el, ctx, w, h, resize() }`.
- `dpad(root, onDir(0..3), dirs?)` — хрестовина для телефона (з'являється лише при `pointer: coarse`). `dirs` — які саме кнопки показати, типово всі чотири.
- `keyboardUa(root, onKey(ch), state)` — екранна українська клавіатура (Глек-слово, віселиця).
- `lerp(a, b, t)`, `Interp()` — інтерполятор кадрів для 25 Гц ігор: `push(f)` кладе кадр, `at()` віддає `{ a, b, t }` — два останні кадри й коефіцієнт на «зараз мінус один інтервал» (змішує поля сам модуль через `lerp`, бо форма кадра в кожної гри своя), `reset()` забуває обидва.
- `timerArc(root, untilIso, totalMs)` — дуга-таймер фаз (мафія, «Скільки?», дуель). Кликати можна з кожного `update()`: повторний виклик лише переставляє час тій самій дузі. Повертає `{ el, set(untilIso, totalMs), stop() }`.
- `css(varName, fallback)` — значення CSS-змінної з `:root` (те саме, що `ctx.css`), `coarse()` — чи це палець.
- `hand(root, items, { onItem, selectable, multi?, render? })` — віяло карт (дурень) / кісток (доміно). `onItem(item, i, on)`; старе ім'я `onCard` теж працює. `multi` — можна вибрати кілька, `render(item, i)` — свій HTML картки. Повертає `{ el, selected(), clear() }`.

Усі хелпери ідемпотентні: їх кличуть із `mount()` і з кожного `update()`, елемент при цьому один, а колбеки й
дані беруться з останнього виклику — можна сміливо будувати `onCell`/`onItem` по свіжому стану.

`HGames.register` до `attach` — нормально: модулі вантажаться асинхронно, каркас домальовує кімнати, коли
модуль з'явився.

## 4. Домовленості для видів (`Game.View`)

- Завжди є поле `turn: number|null` для покрокових ігор на N — каркас малює «Твій хід»/«Ходить X»
  (`status()` модуля може перекрити).
- Для `Hidden` ігор вид глядача (`seat == null`) не містить прихованого (карти в руці, міни, ролі).
  Тест на це обов'язковий.
- Кадри (`Frame`) — короткі масиви чисел, без вкладених об'єктів на кожну клітинку.
- Час у видах — ISO-рядки (`DateTimeOffset`) або `msLeft` (число), не тики.
- Тексти для гравця — українською; не пхати HTML у вид.

## 5. Групи SignalR і з'єднання

- `room:<id>` — глядачі кімнати (кадри й публічні види).
- Per-seat вид `Hidden`-ігор іде `Clients.Client(connId)` на кожне з'єднання-глядача окремо.
- `Presence.ConnectionsOf(nick)` — усі з'єднання ніка (для `wallet`, `achievement`, `toast`).
- На `OnDisconnectedAsync`: з'єднання прибирається зі всіх `Watchers`; місця звільняються не одразу, а через
  grace 20 с, якщо нік більше ніде не онлайн (`Rooms.DropIfGone` з `TickEngine`).
