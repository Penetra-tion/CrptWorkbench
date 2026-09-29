# Получение публичных сведений о ВСД по УИД

This ExecPlan is a living document. Maintain it according to `.agent/PLANS.md`.

## Purpose / Big Picture

Оператор открывает вкладку «Меркурий» в `/operations`, вводит УИД и видит статус, дату оформления и сведения о продукции. Тот же результат можно открыть из истории без повторного запроса. Публичная страница «Меркурия» не требует учётной записи. В Mock ответ создаётся локально.

## Progress

- [x] (2026-09-29) Проверена публичная форма и конкретная страница ВСД.
- [x] (2026-09-29) Добавлены операция, клиент, транспортный фильтр, Mock и карточки результата.
- [x] (2026-09-29) Сборка прошла без предупреждений; финальный прогон Release: 459 успешно, 0 ошибок.
- [x] (2026-09-29) Пять маршрутов вернули HTTP 200; узкая форма и карточка операции проверены в браузере.
- [ ] Проверить интерактивное выполнение Mock в браузере: текущий встроенный браузер показывает форму, но его нажатия не вызвали Blazor обработчик.

## Surprises & Discoveries

- Публичная форма принимает восемь групп по четыре шестнадцатеричных символа и открывает `GET /pub/operatorui?_action=findVetDocumentFormByUuid&uuid=...`.
- Отсутствующий УИД возвращает HTML со строкой «Ветеринарный сопроводительный документ с указанным кодом не найден»; номер HTTP сам по себе не отличает этот случай.
- Рабочее дерево уже содержало незавершённые изменения в Console, History, Program и AGENTS. Они сохраняются.

## Decision Log

- Decision: Поддерживать три формы ввода УИД, отправлять только восемь групп по четыре символа. Rationale: Это формат публичной формы. Date/Author: 2026-09-29, Codex.
- Decision: Хранить только девять запрошенных видов сведений в JSON, а не HTML. Rationale: В странице есть персональные и посторонние сведения. Date/Author: 2026-09-29, Codex.
- Decision: Считать неизвестную структуру страницы ошибкой 502. Rationale: Иначе страница ошибки могла бы стать успешным пустым результатом. Date/Author: 2026-09-29, Codex.

## Outcomes & Retrospective

Операция добавлена; Release сборка и 459 тестов прошли. Статическая разметка маршрутов и узкого экрана проверена. Интерактивное выполнение во встроенном браузере не подтвердилось: нажатие кнопки не изменило страницу, хотя сервер открыл Blazor circuit. Поведение Mock и сохранённого результата подтверждено автоматическими тестами.

## Context and Orientation

`src/CrptWorkbench.Domain/Contracts.cs` задаёт порт клиента. `src/CrptWorkbench.Application` содержит описание операции и результат. `src/CrptWorkbench.Clients` выполняет публичное чтение. `src/CrptWorkbench.Web/Program.cs` регистрирует зависимости; страницы `Operations.razor`, `ApiConsole.razor` и `History.razor` показывают операцию. История сохраняется общим `ExecuteApiOperationService` с идентификатором активного профиля.

## Plan of Work

Добавить значение Mercury в конец ApiSystem. Добавить обработчик, который проверяет форму УИД до вызова клиента. В отдельном HTTP-клиенте запретить перенаправления и все адреса, кроме одного публичного GET. Читать страницу с лимитом 1 МБ, извлекать подписанные поля из строк `profile-info-row` и сериализовать только требуемые сведения. Возвращать 404 для явного отсутствия ВСД и 502 при неожиданной странице. Зарегистрировать локальный Mock. Показывать результат общей карточкой в консоли и истории.

## Concrete Steps

Из корня `D:\Артём\Projects\C#\СZHELPER` выполнить:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:OutputPath=bin/mercury-check/
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -p:OutputPath=bin/mercury-test/
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed -p:OutputPath=bin/mercury-test/

Тестовую команду запускать вне ограниченной среды: локальный канал тестового раннера в sandbox блокируется. Ожидаются ноль ошибок и все тесты пройдены. Затем запустить Mock приложение и проверить `/`, `/console?operation=mercury.vsd-info`, `/operations`, `/history`, `/settings`.

## Validation and Acceptance

Три формы УИД приводят к одному адресу. Неверный УИД не достигает клиента. Страница с несколькими партиями и элементами упаковки сохраняет их все. Текст неизвестного статуса сохраняется. Лишние сведения страницы не попадают в JSON. Mock-ответ имеет `X-CRPT-Workbench-Mock: true`. Публичный адрес разрешён только по HTTPS, с точным путём, действием и УИД. Сохранённый результат читается без нового запроса.

## Idempotence and Recovery

Изменения добавочные, схема базы не меняется. Повторный запуск сборки и тестов безопасен. При изменении HTML публичной страницы операция возвращает ошибку формата; исправлять сопоставление полей после проверки фактической новой структуры.

## Artifacts and Notes

Пример УИД документа пользователя: `8332-6981-984B-455F-881A-A48E-7C9A-3586`. Данные скриншота используются для проверки структуры, в Mock применяется синтетический УИД. Публичная форма: `https://mercury.vetrf.ru/pub/operatorui?_action=checkVetDocument`.

## Interfaces and Dependencies

`IMercuryClient.ExecuteAsync(IntegrationRequest, CancellationToken)` возвращает общий `IntegrationExchange`. `MercuryVsdInfoHandler.Parse` нормализует УИД. `MercuryVsdInfoResult.Parse` читает сохранённый JSON. Новых пакетов и миграций нет.
