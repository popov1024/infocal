# Infocal — инфраструктура событий Гомеля

Три .NET-сервиса: собирают расписания (катки, квизы) со внешних сайтов и отдают их как
JSON API, HTML-страницы и iCalendar-подписки. Прод — `infocal.popov.by` (Docker Compose + Traefik).

## Структура

| Проект | Тип | Роль |
| --- | --- | --- |
| `src/Infocal.Calendar` | Minimal API | Хранилище и API событий, SQLite + EF Core, отдаёт `.ics` |
| `src/Infocal.Scraper` | Worker + Quartz | Парсит источники по расписанию, пушит события в Calendar |
| `src/Infocal.Admin` | Minimal API | HTML-админка, проксирует запросы в Calendar |

`net11.0`, `Nullable enable`, `runtime-async=on`. Пакеты: EF Core 11 + Sqlite, Ical.Net,
HtmlAgilityPack, Quartz, `Microsoft.Extensions.Http`.

## Сборка и запуск

```bash
dotnet build Infocal.slnx           # единственная проверка, которая есть
dotnet run --project src/Infocal.Calendar   # http://localhost:5223
dotnet run --project src/Infocal.Admin      # http://localhost:5037, проксирует в 5223
dotnet run --project src/Infocal.Scraper    # джобы стартуют сразу после запуска
docker compose up -d --build                # calendar:5253, admin:5254
```

Тестовых проектов нет. Валидация = сборка без warnings + ручной прогон затронутого сценария.
Не выдумывай `dotnet test`, его нечего запускать.

## Контракт между сервисами

Calendar — единственный, кто пишет в БД. Scraper и Admin только ходят к нему по HTTP,
базовый адрес из `CalendarApi:BaseUrl` (прод: `http://calendar`, env-ключ `CalendarApi__BaseUrl`).

Запись защищена заголовком `X-Api-Key` = `INFOCAL_API_KEY`. Дефолт `dev-key-change-me`
годится только локально; в проде ключ лежит в `.secret`.

Публичные URL — стабильный контракт, на них подписаны календари пользователей:

```
GET /                              HTML-страница со ссылками на календари
GET /calendar.ics                  все события
GET /calendar/{**rest}.ics         фильтры путём: city/…, category/…, type/…, комбинации
GET  /events?category=&city=&type= JSON выборка
POST /events                       требует X-Api-Key
DELETE /events/by-source?sourceUrl= требует X-Api-Key
```

## Данные

SQLite, файл `calendar.db` в volume `calendar-data`. Схема создаётся `EnsureCreatedAsync()`,
миграций нет. `DbSeed` наполняет категории/города/типы при старте, проверяя каждый slug.

Повторный парсинг защищён индексом `(SourceUrl, Start)`: перед вставкой источник удаляет
свои старые записи через `DELETE /events/by-source`. Не ломай эту пару — иначе появятся дубли.

## Scraper

Quartz-джобы: `GomelMassSkatingJob` (агрессивный и ленивый cron из `ScrapeSchedule:*`),
`WowQuizJob`, `MozgoBoynyaJob`, `VkImageScheduleJob` (ежедневно 9:00). Все четыре
принудительно триггерятся при старте воркера.

Каждый источник — отдельный `*ScraperService` + `IJob`, результат мапится в payload и
уходит POST-ом в Calendar. Новый источник расписаний-картинок VK добавляется одной строкой
в статический массив `VkImageScheduleJob.Sources`.

`VisionService` — единственная платная внешняя зависимость: шлёт картинку в DeepSeek
(`Vision:Endpoint`, `Vision:Model` = `deepseek-flash`, `Vision:ApiKey`), ждёт строгий JSON
`{is_schedule, city, games[{date, time, title}]}`. Промпт на русском, ответ валидируется.
Любое изменение промпта = изменение качества данных и денег, проверяй на одном посте.

`ScrapedEvent.cs` — демонстрация C# 15 union, **в прод-джобы не подключена**. Не строй на ней.

## Секреты

`.secret` в git не попадает, подключается как `env_file` в compose. Ключи:
`INFOCAL_API_KEY`, `VkApi__Token`, `Vision__ApiKey`. Значения не читай, не логируй,
не копируй в код, в конфиги по умолчанию и в коммиты.

## Конвенции кода

- Файловая область видимости namespace, primary constructors, collection expressions `[...]`.
- `.editorconfig`: 4 пробела, `utf-8-bom`, финальный перевод строки. Файлы уже с BOM.
- Комментарии, логи и пользовательские строки — по-русски.
- Секции в крупных файлах разделяются комментариями вида `// ── HTTP clients ──`.
- Коммиты — conventional commits, тип + область, английский: `feat(scraper): add VK schedule source`.

## Границы для агентов

- Публичные `.ics`-маршруты и их параметры не меняй без явного разрешения: подписчики сломаются.
- `.secret` не трогай вообще.
- Переход `EnsureCreated` → EF-миграции, смена схемы, смена портов или хоста — только после согласования.
- Не запускай Scraper по всем источникам ради проверки: это удары по чужим сайтам и платные
  вызовы Vision. Проверяй на одном источнике или на сохранённом ответе.
- Если задача требует правки в двух и более сервисах — сначала опиши контракт, потом код.
