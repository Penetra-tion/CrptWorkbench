# Подключить боевой True API и подготовить Windows-службу

This ExecPlan is a living document. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.

План поддерживается по правилам `../PLANS.md`.

## Purpose / Big Picture

После реализации оператор, подключившийся к Windows Server по RDP, сможет открыть CRPT Workbench на `http://127.0.0.1:5080`, выбрать сертификат УКЭП из `LocalMachine/My`, проверить боевую авторизацию True API и запросить сведения по одному или нескольким кодам маркировки через промышленный `POST /cises/info`. Mock-режим останется безопасным значением по умолчанию, а Национальный каталог и СУЗ продолжат работать только локально. В репозитории появятся самодостаточный win-x64 publish-процесс, сценарии установки Windows-службы и пошаговая инструкция без сертификатов, закрытых ключей и иных секретов.

## Progress

- [x] (2026-09-15 00:00Z) Изучены архитектура, mock-граница, локальная спецификация True API, текущая официальная схема авторизации, хранилище сертификатов и исходные тесты.
- [x] (2026-09-15 00:00Z) Подтверждён baseline: web-проект собирается без предупреждений и ошибок, 38 тестов проходят вне ограниченной sandbox.
- [x] (2026-09-15 11:20Z) Добавлены конфигурация, production guard, CryptoPro-подпись, получение и кэширование токена, реальный True API client.
- [x] (2026-09-15 11:28Z) Обновлены настройки, индикаторы режимов, проверка соединения и health endpoints.
- [x] (2026-09-15 11:32Z) Runtime-данные перенесены в настраиваемый каталог с DPAPI-защитой; добавлены Windows Service hosting и Event Log.
- [x] (2026-09-15 12:04Z) Добавлены автоматические тесты production-пути, allowlist, retry/cache, маскирования, логов и CryptoPro cleanup; итог — 53 passed.
- [x] (2026-09-15 12:07Z) Добавлены self-contained publish, install/update/verify/rollback scripts и руководство развёртывания.
- [x] (2026-09-15 12:10Z) Выполнены Release-сборка без предупреждений, тесты, mock runtime smoke, финальная self-contained package smoke и инспекция ZIP.

## Surprises & Discoveries

- Observation: Git-корень находится в `D:/Артём/Projects/C#`, а каталог `СZHELPER` целиком считается untracked вместе с соседними проектами.
  Evidence: `git rev-parse --show-toplevel` вернул родительский каталог. Все проверки изменений должны ограничиваться точными путями внутри `СZHELPER`.
- Observation: Microsoft Testing Platform не запускается в ограниченной sandbox из-за именованного канала.
  Evidence: sandbox-запуск завершился `UnauthorizedAccessException`; разрешённый запуск вне sandbox дал 38 passed, 0 failed.
- Observation: существующий handler одновременно проверяет mock URL и вставляет фиктивный Bearer-токен, поэтому реальный transport нельзя включить одной заменой DI.
  Evidence: `TrueApiCodeInfoHandler` разрешает только `.mock.invalid`, а `AuthenticationHandler` добавляет `mock-pipeline-token`.
- Observation: `Microsoft.Extensions.Hosting.WindowsServices` отсутствовал в исходном графе зависимостей и потребовал отдельного restore для Windows-службы.
  Evidence: после добавления пакета версии 10.0.11 Release build и self-contained publish прошли с нулём предупреждений и ошибок.
- Observation: Windows PowerShell 5.1 не гарантирует наличие `Path.IsPathFullyQualified`, поэтому install script использует явную проверку drive/UNC absolute path.
  Evidence: все deployment scripts прошли PowerShell parser check, а проверка принимает `C:\...` и UNC, отклоняя relative и rooted-without-drive пути.
- Observation: безопасная очистка после timeout должна дождаться завершения убитого CryptoPro-процесса, иначе он может удерживать временный output.
  Evidence: process runner теперь убивает дерево и ждёт до пяти секунд; cleanup перезаписывает временные файлы, делает до пяти попыток и тестируется для success, timeout и cancellation.

## Decision Log

- Decision: Production включается только через `Crpt:Mode=Production`, а значение по умолчанию остаётся `Mock`.
  Rationale: случайный developer/local запуск не должен обращаться в промышленный ЧЗ.
  Date/Author: 2026-09-15 / Codex
- Decision: Разрешён только `https://markirovka.crpt.ru/api/v3/true-api` и три read-only/auth маршрута; автоматические redirect отключены.
  Rationale: это предотвращает превращение консоли в произвольный HTTP-клиент и ограничивает первый релиз согласованным сценарием.
  Date/Author: 2026-09-15 / Codex
- Decision: Подпись формируется установленным CryptoPro `csptest.exe`, а не стандартным `SignedCms`.
  Rationale: промышленная УКЭП обычно использует ГОСТ-провайдер CryptoPro; CLI создаёт требуемую присоединённую подпись и работает с ключом без экспорта PFX.
  Date/Author: 2026-09-15 / Codex
- Decision: Токен считается непрозрачной строкой, хранится только в памяти девять часов и один раз обновляется после 401.
  Rationale: True API переходит между JWT и UUID, а опубликованный максимальный срок действия составляет десять часов.
  Date/Author: 2026-09-15 / Codex
- Decision: Приложение устанавливается как self-contained Windows Service на loopback без внутреннего login UI.
  Rationale: пользователь выбрал доступ только через доверенный RDP-сеанс; внешний порт и IIS не требуются.
  Date/Author: 2026-09-15 / Codex
- Decision: Установщик удаляет унаследованные ACL приложения и данных, оставляя полный доступ только `SYSTEM`/Administrators и минимальный доступ сервисной учётной записи.
  Rationale: machine-scoped DPAPI должен дополняться файловой границей, иначе другой локальный пользователь с доступом к key ring сможет использовать защищённые данные.
  Date/Author: 2026-09-15 / Codex

## Outcomes & Retrospective

Реализация завершена в согласованном объёме. Mock остаётся режимом по умолчанию; production выбирается только конфигурацией службы. Реальный transport допускает только три согласованных method/path pair на `markirovka.crpt.ru:443`, не следует redirects, не принимает URL профиля и сохраняет в историю только masked exchange `/cises/info`. Авторизация поддерживает непрозрачные JWT- и UUID-подобные токены, девятичасовой in-memory cache, конкурентный single refresh и ровно один retry после 401.

Release build завершился с 0 warnings и 0 errors; 53 теста прошли, 0 упали. Самодостаточный `win-x64` ZIP содержит 382 записи, не содержит `App_Data`, SQLite, key ring, PFX/P12 или `.key`. Финальный package smoke дал HTTP 200 для `/`, `/console`, `/history`, `/settings`, `/health/live`, `/health/ready`, без необработанных исключений.

Готовый пакет: `artifacts/windows/CrptWorkbench-win-x64.zip`, 53,739,682 bytes, SHA-256 `8153819AE790D31A4ABD45783C0D84F0C160C7CF88A8F9407EA4A842A720F9D3`.

Не выполнялись фактическая установка Windows-службы и сетевой вызов промышленного ЧЗ: для них нужны администратор сервера, сервисная учётная запись, лицензированный CryptoPro, её реальный сертификат/закрытый ключ и известный КИ. Это соответствует исходной границе задачи, где удалённое подключение и установка на сервер не входят в локальную реализацию. Руководство завершает acceptance на сервере через token check и один `/cises/info`.

## Context and Orientation

Решение состоит из Domain, Application, Clients, Infrastructure и Web. `src/CrptWorkbench.Domain/Contracts.cs` определяет transport-порты. `src/CrptWorkbench.Application/TrueApiCodeInfoHandler.cs` валидирует ввод и формирует `/cises/info`. `src/CrptWorkbench.Clients/MockClients.cs` сейчас создаёт ответы без сети, а `src/CrptWorkbench.Clients/HttpPipelineHandlers.cs` содержит mock guard. `src/CrptWorkbench.Infrastructure/WindowsCertificateService.cs` читает `CurrentUser/My`. `src/CrptWorkbench.Web/Program.cs` выбирает реализации и хранит SQLite/Data Protection в `App_Data`.

Единая аутентификация True API состоит из `GET /auth/key`, подписи полученного `data` присоединённой УКЭП в Base64 и `POST /auth/simpleSignIn`; результат содержит Bearer-токен. `POST /cises/info` принимает JSON-массив от одного до 1000 нормализованных КИ длиной 18–74 символа. Реальный production base URL — `https://markirovka.crpt.ru/api/v3/true-api`. Challenge, подпись и токен являются служебными данными авторизации и никогда не должны попадать в persisted history.

## Plan of Work

Сначала Domain получит контекст аутентификации в `IntegrationRequest`, интерфейс присоединённой подписи и интерфейс token provider. Application перестанет вставлять mock Authorization и будет передавать profile ID, ИНН и thumbprint. Clients получит options, production allowlist handler, auth transport/token cache и `RealTrueApiClient`; mock-клиенты останутся неизменным default path.

Infrastructure получит конфигурируемое чтение `LocalMachine/My` и CryptoPro signer. Signer проверит thumbprint, сертификат, путь executable и тайм-аут, создаст уникальный временный каталог, передаст аргументы без shell interpolation, прочитает Base64, удалит переносы строк и гарантированно удалит каталог. Web выберет mock или production реализации на основании валидированных options, перенесёт state в настраиваемый data directory, включит DPAPI и Windows Service/Event Log только в production-hosting сценарии.

Settings получит проверку сертификата и боевой авторизации без показа токена. Layout и Console будут отображать реальный статус True API и отдельно mock НК/СУЗ. Minimal health endpoints разделят liveness и readiness; readiness не обращается в ЧЗ.

После автоматических тестов появятся `deploy/windows` scripts: build-package создаёт self-contained win-x64 ZIP, install/update безопасно раскладывает бинарники и данные, задаёт ACL и службу, verify проверяет процесс и health. `docs/deployment/windows-production.md` опишет установку CryptoPro/сертификата, service account, первый login, auth-check, реальный КИ, обновление и rollback.

## Concrete Steps

Рабочий каталог: `D:\Артём\Projects\C#\СZHELPER`.

Восстановление, сборка и тесты:

    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Локальный mock smoke test запускает Web с временным `Crpt__DataDirectory`, проверяет `/`, `/console`, `/history`, `/settings`, `/health/live`, `/health/ready` и останавливает только созданный процесс. Publish-команда находится в `deploy/windows/Build-Package.ps1`; результатом является ZIP для win-x64 без runtime state и секретов.

## Validation and Acceptance

Mock mode должен по-прежнему возвращать `X-CRPT-Workbench-Mock: true` и блокировать все реальные хосты. Production mode должен запускаться только с валидным фиксированным base URL; redirect, другой hostname, query/userinfo/fragment в base URL или неподдерживаемый endpoint должны завершаться до сети.

Тестовый handler должен доказать точные запросы `/auth/key`, `/auth/simpleSignIn` и `/cises/info`, передачу одного correlation ID, принятие JWT- и UUID-подобных токенов, девятичасовой cache, coalescing конкурентного refresh и единственный повтор после 401. Fake process runner должен доказать присоединённый signing command, timeout/cancellation и cleanup.

SQLite history и логи не должны содержать token, signature, auth challenge или исходный Authorization. Реальный ручной acceptance на сервере: `/health/ready` успешен, кнопка проверки соединения получает токен, запрос известного КИ возвращает данные без mock header, history показывает `Bearer ********`, служба переживает перезагрузку Windows и не слушает внешний интерфейс.

## Idempotence and Recovery

Build/package scripts очищают только собственный staging под `artifacts/` и создают новый ZIP. Install script проверяет абсолютные целевые пути, останавливает только службу `CrptWorkbench`, создаёт timestamped backup текущих бинарников и не удаляет `C:\ProgramData\CrptWorkbench`. Повторная установка обновляет бинарники с сохранением БД и ключей. При ошибке служба остаётся остановленной, а инструкция описывает восстановление последнего backup. Сертификат и закрытый ключ не копируются сценарием.

## Artifacts and Notes

Исходный baseline:

    Web build: 0 warnings, 0 errors
    Tests: 38 passed, 0 failed

Локальный PDF `docs/crpt-reference/true-api-gis-mt-v515-2025-06-04.pdf` подтверждает `/auth/key`, присоединённую подпись, `/auth/simpleSignIn`, Bearer и массив `/cises/info`. Перед финализацией контракт сверяется с текущей официальной страницей `https://docs.crpt.ru/gismt/True_API/`.

## Interfaces and Dependencies

В Domain добавляются `IntegrationAuthenticationContext`, `ICertificateSignatureService` и `ITrueApiTokenProvider`; `IntegrationRequest` получает optional authentication context, чтобы существующие mock-вызовы оставались совместимыми. Token provider принимает base URL, profile ID, ИНН, thumbprint и correlation ID и возвращает только token string; invalidate выполняется по тому же cache key.

Domain добавляет `CrptIntegrationOptions` и `CrptIntegrationMode`. Clients добавляет `ProductionTrueApiNetworkGuardHandler`, `TrueApiTokenProvider` и `RealTrueApiClient`. Infrastructure добавляет `CryptoProCertificateSignatureService` и process runner abstraction для тестирования. Web добавляет connection check через `ProfileService` и scoped readiness service, но Razor-компоненты не получают прямого доступа к HttpClient, CryptoPro или EF Core.

Новая package dependency `Microsoft.Extensions.Hosting.WindowsServices` версии, согласованной с .NET 10, используется только Web-проектом. Иные криптографические NuGet-пакеты не добавляются.

Revision note (2026-09-15): создан исполняемый план после утверждения production-only True API `/cises/info`, Windows Service localhost, LocalMachine CryptoPro certificate и package/script delivery.

Revision note (2026-09-15): зафиксированы завершённая реализация, 53 passed tests, строгие ACL, final package smoke, SHA-256 и вынесенный на реальный сервер acceptance.
