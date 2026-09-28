# Реальный статус документа СУЗ по GUID

This ExecPlan is maintained according to `.agent/PLANS.md`. Its living sections must remain current.

## Purpose / Big Picture

Пользователь открывает «Статус документа СУЗ» в каталоге или API Console, вводит GUID и получает квитанции обработки заказа, отчёта или блока кодов, включая статус, ошибки и события. Mock отвечает локально; Production читает СУЗ с отдельным токеном, полученным по сертификату профиля и отдельному ConnectionId. История сохраняется в текущем профиле. Поддерживаются Квитирование 2.0 и ГИС МТ без МДЛП.

## Progress

- [x] (2026-09-27) Пользователь утвердил контракт, Production, отдельный ConnectionId и границы функциональности; изучены инструкции и текущие порты.
- [x] (2026-09-27) Реализованы Application, модели квитанций и шесть Mock-сценариев.
- [x] (2026-09-27) Реализованы авторизация, транспорт и точные сетевые ограничения, зарегистрированы отдельные HTTP-клиенты.
- [x] (2026-09-27) Подключены интерфейс, каталог, настройки и сохранённые результаты.
- [x] (2026-09-28) Выполнены Debug Web и Release test builds, 390 Release-тестов, независимый обзор и Mock-проверка интерфейса.
- [x] (2026-09-28) Зафиксирован результат; реальная проверка СУЗ ожидает запуска пользователем на сервере.

## Surprises & Discoveries

Существующая операция `suz.order-status` является локальной демонстрацией. Она сохранена локальной даже в Production. Рабочее дерево содержит пользовательские изменения каталога операций, истории и других функций; они сохранены. Независимый security review выявил передачу произвольного exception.Message от HTTP/подписи в историю; на границе СУЗ добавлены фиксированные безопасные сообщения и тесты секретного маркера. Повторный read-only review подтвердил исправление. Первый полный запуск: 389 passed, 1 failed из 390; ошибка была в новом тесте legacy demo, передававшем нехарактерный относительный URL вместо вызова реального DemoHandler. Тест исправлен на пользовательский путь через handler.

## Decision Log

2026-09-27: пользователь выбрал общий метод квитанций без старых документов и МДЛП, автоматическую авторизацию с отдельным ConnectionId. Добавляется операция `suz.document-status`; миграции не требуются. Токен СУЗ изолирован от True API/НК. Invalidate принимает отклонённый токен, чтобы запоздалый HTTP 401 не удалял уже обновлённый токен.

## Outcomes & Retrospective

Реализация готова. Debug web и Release test builds: 0 warnings/errors. Финальный запуск Release-тестов: 390 passed, 0 failed, 0 skipped. Все пять HTTP-маршрутов отвечают 200. В браузере получены две Mock-квитанции, раскрыты события и ошибки, результат восстановлен из истории; на viewport 380x760 document clientWidth=scrollWidth=365, горизонтального переполнения нет. В изолированной SQLite-истории проверены длительность, correlation ID и маскирование; лог тестового сервера без необработанных ошибок. Пользователь явно перенёс реальную проверку на свой сервер (ответ «проверю на сервере»); здесь Production-запросы не выполнялись.

## Context and Orientation

Domain содержит IntegrationRequest/Exchange и клиентские порты. Application регистрирует обработчики через IApiOperationHandler; ExecuteApiOperationService маскирует и сохраняет результат. Clients выполняет HTTP, Web содержит регистрацию сервисов и Razor. UI использует Application, не транспорт. Новый SuzConnectionContext передаёт OmsId, ConnectionId и BaseUrl через IntegrationRequest; ISuzTokenProvider получает отдельный токен.

## Plan of Work

Добавить SuzDocumentStatusHandler и SuzDocumentStatusResults в Application: один GUID, относительный путь `api/v3/receipts/receipt?omsId=...&resultDocId=...`, контекст профиля, строгое чтение обязательных полей и сохранение неизвестных details. Контракт API СУЗ 3.0: раздел 4.4.18, стр. 392–395; статусы 0 SUCCESS, 1 FAILED, 2 IN_PROGRESS, 3 PARTIALLY на стр. 478; квитанции и события в разделе 10, стр. 607–609. Ответ содержит results[] с resultDocId/resultDocDate/sourceDocId/sourceDocDate/state/code/description, необязательные workflow/workflowVersion/details/operations. Время — Unix milliseconds. Пустые results означают отсутствие квитанций; повреждённый контракт — ошибка ответа, а не успех документа.

Добавить RealSuzClient, SuzTokenProvider и отдельные сетевые guards в Clients. Разрешить только HTTPS suzgrid.crpt.ru:443 GET /api/v3/receipts/receipt с ровно omsId и resultDocId UUID. Перед авторизацией проверять метод, путь, запрос и настройки. Авторизация через HTTPS markirovka.crpt.ru:443 GET /api/v3/true-api/auth/key и POST /api/v3/true-api/auth/simpleSignIn/{ConnectionId}; подпись присоединённая PKCS#7 через существующий ICertificateSignatureService. ConnectionId ограничен ASCII буквами/цифрами/дефисом/подчёркиванием, 1–128 символов. Токен хранится 9 часов в памяти с отдельным ключом по профилю/ИНН/сертификату/OmsId/ConnectionId/адресам и сериализацией выпуска. Один повтор чтения после 401; никаких повторов для других кодов. Отдельные HTTP-клиенты имеют disabled redirects, Mock guard первым и секретобезопасные логи даже при полном логировании.

В Web зарегистрировать новый handler и адаптер согласно Crpt:Mode. Добавить карточки всех квитанций, русские статусы, раскрываемые события/ошибки/JSON, ручной повтор стандартным запуском. SavedResultView восстанавливает те же карточки в истории. В настройках пояснить отдельное подключение, OmsId и фиксированный разрешённый адрес. Существующие данные профилей не изменять автоматически.

## Concrete Steps

Рабочая директория: D:\Артём\Projects\C#\СZHELPER. Сборки:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Тесты запускать вне sandbox с разрешением. Если сборка заблокирована работающим приложением, использовать отдельный OutputPath, не останавливать пользовательский процесс. Запустить отдельный Mock на свободном localhost-порту с отдельной временной базой. Проверить /, /console, /operations, /history, /settings и серверные логи.

## Validation and Acceptance

Новые тесты проверяют запрос, GUID, отмену, состояния, несколько квитанций, пустой и повреждённый JSON, неизвестные поля, ошибки событий. Проверяют точные HTTP-ограничения до авторизации, изоляцию/срок/конкурентность токенов, единственный повтор 401, маскирование и отсутствие реальной сети в Mock. SQLite-тест проверяет историю, корреляцию, длительность, восстановление и изоляцию профилей, старые результаты SUZ. Браузер должен показать карточки и историю на широком и узком экране. Ожидаются ноль ошибок сборки и ноль проваленных тестов; прежний ориентир 320 будет обновлён фактическим числом.

## Idempotence and Recovery

Нет миграций, отправки документов или автоматической регистрации устройств. Повторный GET безопасен. Реальная проверка требует настроенного отдельного подключения и известного GUID; не использовать чужое подключение и не объявлять успех без ответа СУЗ. Сохранять исходные пользовательские изменения и не удалять рабочие данные.

## Artifacts and Notes

Контракт исследован из docs/crpt-reference/suz-cloud-api-3.0.pdf в этом чате. Сетевые запросы Production ещё не выполнялись.

## Interfaces and Dependencies

IntegrationRequest получает необязательный SuzConnectionContext последним аргументом, сохраняя существующие вызовы. ISuzTokenProvider.GetTokenAsync принимает IntegrationAuthenticationContext, SuzConnectionContext, correlationId и CancellationToken; Invalidate принимает те же контексты и rejectedToken. ISuzClient остаётся общим портом. Новых пакетов и изменений схемы БД не требуется.

Revision 2026-09-27: создан план по утверждённому пользователем заданию.

Revision 2026-09-27: обновлены реализация, исправление security review и фактические промежуточные проверки; реальная проверка перенесена пользователем на сервер.

Revision 2026-09-28: завершён повторный полный Release-прогон после последних UI/Application правок: 390 passed, 0 failed; документированы Mock/браузер/SQLite-проверки и граница реальной проверки.
