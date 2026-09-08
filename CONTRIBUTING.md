# Як долучитись

Код живе на GitHub: https://github.com/kartatyi/hlechyky. Прод (https://hlechyky.pp.ua) крутиться на ПК власника, і туди доїжджає тільки гілка `main`. Все, що описано нижче, потрібно, щоб підняти свою копію радіо вдома, щось у ній зробити і віддати це через Pull Request.

## Що треба

- Windows 10/11. Скрипти запуску написані на PowerShell; на Linux/macOS сервер теж збирається, але yt-dlp/ffmpeg і шляхи в `appsettings.json` доведеться підправити руками.
- git.
- .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
- Docker Desktop, тільки якщо хочеш чути ефір (Icecast + liquidsoap). Для правок інтерфейсу можна й без нього.

## Перший запуск

```powershell
git clone https://github.com/kartatyi/hlechyky.git
cd hlechyky
powershell -ExecutionPolicy Bypass -File setup.ps1
docker compose -f liquidsoap\docker-compose.dev.yml up -d
dotnet run --project src\Hlechyky
```

- `setup.ps1` качає yt-dlp і ffmpeg у `tools\` (близько 100 МБ) і створює `appsettings.Local.json` та `liquidsoap\.env` з випадковими ключами. Ці два файли в `.gitignore`, вони тільки твої.
- `docker compose … up -d` підіймає Icecast і liquidsoap на цій машині. Пропустив цей крок: сайт працює, але ефір у шапці червоний, треки не грають, і все, що штовхає чергу в liquidsoap, відповідає "liquidsoap не відповідає". Для верстки, пошуку, чату, плейлистів цього досить.
- Далі http://localhost:8080. Адмінка: `http://localhost:8080/?k=<AdminKey>`, ключ у своєму `appsettings.Local.json`.
- Windows Firewall при першому запуску може спитати дозвіл для `dotnet`/`Hlechyky.exe`. Потрібен, щоб liquidsoap із Docker достукався до сервера (callback про зміну треку).
- Замість `dotnet run` можна `powershell -ExecutionPolicy Bypass -File start.ps1 start`: той самий сценарій, що на проді, у фоні (лог у `logs\server.log`). Без `D:\radio` він сам підіймає Icecast з `docker-compose.dev.yml`, без `tools\caddy\caddy.exe` пропускає Caddy. `start.ps1 status`, `stop`, `logs` теж працюють.
- Last.fm необов'язковий: без ключа Дядько Глек радить тільки з YouTube Music. Ключ безкоштовний (https://www.last.fm/api/account/create), вписується в `LastFm:ApiKey` у `appsettings.Local.json`.

## Де що лежить

Розділ "Структура" в [README.md](README.md) описує все дерево. Коротко:

- `src/Hlechyky/` сервер. `Endpoints.cs` це HTTP API, `RadioEngine.cs` черга і те, що в ефірі, `AutoDj.cs` поради Дядька Глека, `RadioHub.cs` SignalR (усе, що летить у браузери), `Db.cs` SQLite.
- `web/` фронт без збірки: `index.html`, `app.js`, `static/style.css`. Зберіг файл, натиснув F5 у браузері, готово. Сервер віддає статику з `no-cache`.
- `liquidsoap/radio.liq` аудіоконвеєр. Коментарі там і розділ "Чому раніше одна пісня грала двічі" в README описують граблі, на які вже наступали. Перш ніж чіпати, прочитай.
- Налаштування підхоплюються без рестарту, `appsettings.Local.json` перекриває `appsettings.json`.

## Як здавати зміни

1. Онови `main` (`git pull`) і зроби гілку: `git switch -c <що-робиш>`.
2. Одна зміна на гілку. Перевір локально: `dotnet build` без помилок, сайт відкривається, фіча працює на телефоні теж, якщо це інтерфейс.
3. Запуш гілку і відкрий Pull Request на GitHub. В описі: що зробив, навіщо, як перевірити. Для інтерфейсу скриншот або гіфка.
4. GitHub Actions збирає проєкт на кожен PR. Червоний хрестик правимо до мерджу.
5. Мерджить власник, він же викочує на прод (`git pull` + `start.ps1 restart`, черга при цьому не губиться).

У `main` напряму не пушимо.

## Домовленості

- Секрети (ключі, паролі, `cookies.txt`) не комітимо. `appsettings.Local.json`, `liquidsoap/.env`, `data/`, `cache/`, `tools/` уже в `.gitignore`; якщо додаєш новий секрет, туди ж.
- Тексти інтерфейсу і репліки Дядька Глека українською. Коміти коротко і по суті.
- Фронт лишається чистим JS без збірки і фреймворків. Сервер лишається minimal API без нових шарів заради шарів.
- Нове налаштування: дефолт у `appsettings.json` і класі в `Config.cs`, рядок у таблиці "Налаштування, які захочеться крутити" в README.
- Змінив поведінку, яку описує README, онови README у тому ж PR.

## Що робити

Задачі в Issues: https://github.com/kartatyi/hlechyky/issues. Є ідея або знайшов баг: заведи issue або напиши в балачках на сайті.
