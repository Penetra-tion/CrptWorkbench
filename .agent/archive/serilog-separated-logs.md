# Раздельные журналы Serilog

Этот ExecPlan поддерживается согласно [`.agent/PLANS.md`](../PLANS.md). Разделы Progress, Surprises & Discoveries, Decision Log и Outcomes & Retrospective обновляются при реализации.

## Purpose / Big Picture

Оператор сможет читать действия интерфейса отдельно от диагностики сервера. Serilog — библиотека записи структурированных событий; она принимает существующие вызовы ILogger. В appsettings можно менять уровни обоих журналов, пути, размеры и количество файлов. Интерфейс Blazor Interactive Server исполняется на сервере, поэтому client означает события интерфейса, а не отдельный браузерный процесс.

## Progress

- [x] (2026-09-16) Изучены текущие Console/EventLog, компоненты и установка Windows; пользователь утвердил разделение UI/server и только файлы.
- [x] (2026-09-16) Добавлены зависимости, конфигурация двух выходов, безопасная обработка событий и основные события UI/операций.
- [x] (2026-09-16) Debug Web и Release test builds: 0 warnings / 0 errors. Добавлены 4 теста; 63 passed / 0 failed вне sandbox.
- [x] (2026-09-16) Все четыре страницы HTTP 200; браузерная mock-операция 200, 85 ms, correlation ID 89e0047bff184697b8839b71bf40e953 в обоих журналах и SQLite-истории. Авторизация Bearer ********, mock header true. Проверены избранное и сохранение профиля. Документация и установщик обновлены.
- [x] (2026-09-16) Финальные Web build и Release tests: 0 warnings / 0 errors; 63 passed / 0 failed. Startup invalid TokenCacheHours завершился кодом 1 без console output; server содержит только тип InvalidOperationException. Scoped status не содержит БД, ключей, журналов или build outputs.

## Surprises & Discoveries

Системные библиотеки могут записывать строки подключения и тексты исключений, поэтому одного исключения тел HTTP недостаточно. Перед конфигурируемыми выходами установлен обработчик, который удаляет исключение и оставляет тип; события внешних категорий заменяются безопасным сообщением с EventId.

## Decision Log

Decision: Разделять по SourceContext, то есть категории ILogger, а не по scope всей пользовательской операции. Rationale: вызванный из UI бизнес-сервис должен продолжать писать server. Date/Author: 2026-09-16, Codex.

Decision: Использовать root Verbose и независимые LevelSwitches $server/$client с Information по умолчанию. Rationale: общий уровень является верхним ограничением детализации, а переключатели позволяют менять уровни файлов независимо; существующие переключатели обновляются при reload. Date/Author: 2026-09-16, Codex.

Decision: Пути фиксировать при запуске относительно Crpt:DataDirectory, сохраняя reload исходной конфигурации для уровней. Rationale: данные сохраняются при обновлении Windows-службы. Date/Author: 2026-09-16, Codex.

## Outcomes & Retrospective

Основная приёмка пройдена: 63 теста, HTTP 200 и реальный UI/mock сценарий. Независимые переключатели уровней работают после reload. Продакшен allowlist и миграции не меняются. Финальная проверка также прошла: startup failure безопасно записан с кодом выхода 1, cancellation диагностируется без изменения поведения.

## Context and Orientation

`src/CrptWorkbench.Web/Program.cs` — сборка приложения и регистрация сервисов. `src/CrptWorkbench.Web/WorkbenchLogging.cs` — создание logger, разрешение путей и защита событий. `src/CrptWorkbench.Web/ClientCircuitLogging.cs` — диагностика интерактивного соединения Blazor (circuit). Razor-компоненты используют ILogger своей категории. `src/CrptWorkbench.Application/Operations.cs` исполняет операции и сохраняет маскированную SQLite-историю; ему добавлен необязательный ILogger для совместимости существующих конструкторов тестов. Domain не получает зависимость Serilog.

## Plan of Work

Подключить Serilog в Web вместо Console/EventLog и установить try/finally для сброса файлов. Настроить два вложенных logger через Serilog.Settings.Configuration и Serilog.Expressions: клиентский фильтр включает категории компонентов, ClientDiagnostics и системную диагностику circuit; серверный использует обратный фильтр. По умолчанию оба файла ротируются ежедневно и по 20 МБ, сохраняют максимум 31 файл. Добавить UI navigation после интерактивного render, действия настроек, избранного и исполнение операции без содержимого полей. Добавить серверные события HTTP без URL query и выполнения операции с существующим correlation ID. Диагностика не должна подавлять или изменять исключения UI. Обновить Windows installer для чтения настроенных файлов, запретить журналы в ZIP и Git. Тесты разместить в существующем test project с ссылкой на Web для реальной проверки конфигурации и защиты.

## Concrete Steps

Рабочий каталог — корень СZHELPER. Выполнить:

    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Последнюю команду запускать вне sandbox с одобрением, так как Windows runner требует локальный named pipe. Ожидать 0 ошибок и 0 проваленных тестов, baseline 63 теста. Запустить Web в Mock с временным App_Data каталогом; проверить `/`, `/console`, `/history`, `/settings` и mock operation. Проверить разделение, correlation ID, отсутствие тестовых секретов и наличие X-CRPT-Workbench-Mock в истории.

## Validation and Acceptance

Тесты должны использовать настоящую конфигурацию appsettings и временные файлы: UI событие только client, application только server, изменение переключателей работает после reload, относительные и абсолютные пути корректны, framework exception с тестовым секретом не записывает его. Runtime должен отвечать HTTP 200, записывать действия интерфейса и серверное исполнение без дублей и не выполнять реальные сетевые запросы. Финальный scoped git diff не должен содержать БД, ключей, логов и build outputs.

## Idempotence and Recovery

Миграции не добавляются; сохранённые данные не удаляются. Проверки используют временные каталоги. Если восстановление пакетов блокируется sandbox network, повторить restore с approval. При неудачном тестировании не запускать известный небезопасный sandbox runner.

## Artifacts and Notes

Шаблон файлов содержит Timestamp, Level, SourceContext, CorrelationId и Message. Message внешних библиотек намеренно заменён Framework event с идентификатором, чтобы произвольные строки и исключения не утекали при включении Debug.

## Interfaces and Dependencies

Web использует Serilog.AspNetCore 10.0.0, Serilog.Settings.Configuration 10.0.0, Serilog.Sinks.File 7.0.0, Serilog.Expressions 5.0.0. WorkbenchLogging.CreateLogger(IConfiguration, string) возвращает владеющий выходами Logger; ResolvePath(string, string) разрешает пути. CircuitHandler регистрируется scoped. Application использует Microsoft.Extensions.Logging.Abstractions без ссылок на Web или Infrastructure. Новых HTTP API нет.

Revision 2026-09-16: зафиксированы утверждённые границы, конфигурация и безопасная запись перед верификацией.


Revision 2026-09-16: завершены финальные проверки; проверочный процесс удерживал DLL, остановлен перед успешной повторной сборкой. Windows diagnostics сохраняет совместимость с Windows PowerShell 5.1; инструкция локального запуска больше не ссылается на отключённую console logging.



Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
