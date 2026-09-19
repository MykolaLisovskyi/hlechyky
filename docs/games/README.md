# docs/games — ігрова платформа

Читати в такому порядку:

1. [ARCHITECTURE.md](ARCHITECTURE.md) — як усе влаштовано: кімнати, ігри, тик, розсилка, черепки, таблиці,
   ачівки, щоденне, словники, клієнтський каркас, обмеження.
2. [PROTOCOL.md](PROTOCOL.md) — контракт на дроті (хаб-методи, події, DTO) і контракт модуля гри в браузері.
3. [TESTING.md](TESTING.md) — як тестуємо: проєкт, підтримка, що обов'язково для платформи і для кожної гри.
4. [INTEGRATION-NOTES.md](INTEGRATION-NOTES.md) — витяг для автора гри: сигнатури, RoomHarness, клієнтський
   модуль, словники, економіка, як перевірити в браузері і на які граблі вже наступили.
5. [PLAN.md](PLAN.md) — робочі пакети, хвилі, правила для агентів, порти, порядок злиття.
6. [MCP.md](MCP.md) — як аі-агент підключається до села й грає нарівні з людьми (`POST /mcp`).
7. [specs/](specs/) — по файлу на гру: правила, дії, вид, клієнт, тести.

Хто пише гру: spec своєї гри → INTEGRATION-NOTES → ARCHITECTURE/PROTOCOL за потребою → TESTING.

| Spec | Пункт | Гра |
|---|---|---|
| specs/chess.md | 22 | Шахи (класика, Фішера, піддавки) |
| specs/checkers.md | 13 | Шашки (російські) |
| specs/battleship.md | 15 | Морський бій |
| specs/mines.md | 17, 84 | Сапер-дуель і Сапер дня |
| specs/scrabble.md | 23 | Ерудит |
| specs/domino.md | 24 | Доміно |
| specs/durak.md | 25 | Дурень підкидний |
| specs/snake-modes.md | 28, 29 | Мотоцикли (Tron), Змійка на двох |
| specs/pong.md | 32 | Понг |
| specs/curve.md | 33 | Кривуля |
| specs/bomber.md | 34 | Бомбер |
| specs/tanks.md | — | Танчики |
| specs/duel.md | 37 | Дуель-вестерн на реакцію |
| specs/territory.md | 38 | Земля (splix) |
| specs/chat-commands.md | 39 | /coin, /choose, /8ball |
| specs/hangman.md | 40 | Віселиця |
| specs/wordle.md | 41 | Глек-слово |
| specs/daily.md | 84 | Щоденний глек (каркас) |
| specs/skilky.md | 44 | Скільки? |
| specs/pictionary.md | — | Піктіонарі |
| specs/telephone.md | — | Зіпсований телефон |
| specs/melody.md | — | Вгадай мелодію |
| specs/svoya.md | — | Своя гра (поле тем і цін, кіт, аукціон, фінал; ведучий-автомат із голосом або жива людина; конструктор пакетів, імпорт .siq) |
| specs/tournament.md | — | Турнір на вечір (кілька ігор, 👑 чемпіону) |
| specs/mafia.md | 46 | Мафія |
| specs/pozyvni.md | — | Позивні (кодові імена) |
| specs/ad-contest.md | 73 | Озвуч рекламу |
| specs/clicker.md | 82 | Гончарне коло |
| ARCHITECTURE §6–8 | 78, 80 | Черепки, ачівки, таблиці |
| ARCHITECTURE §4.6, §4.4, §5 | 85, 86, 87 | Спільний тик, кімнати на N, види по місцях |
