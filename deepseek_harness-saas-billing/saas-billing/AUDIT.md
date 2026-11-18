# Технический аудит биллинга

Дата: 2026-11-17. Аудит проведён по существующему проекту `saas-billing` без переписывания
архитектуры. Ниже — найденные дефекты, внесённые исправления, решения и фактическая проверка.

## 1. Найденные проблемы и исправления

### 1.1. Конкурентное создание платежа

**Было**

- `CreatePaymentAttemptAsync` вызывал провайдера, удерживая транзакцию и `FOR UPDATE` на invoice.
  Это длинная транзакция с сетевым I/O.
- Хранился только `Idempotency-Key`; параметры запроса не фиксировались. Повтор с тем же ключом
  и другой суммой молча возвращал старую попытку.
- Тестовый провайдер генерировал случайный `provider_payment_id` при каждом вызове. Падение
  между созданием intent и INSERT оставляло возможность создать второй intent при повторе.

**Исправлено**

- В `PaymentAttempt` добавлен `RequestFingerprint` (SHA-256 нормализованных параметров) и
  миграция `AddPaymentRequestFingerprint`.
- Сначала выполняется быстрый поиск по `(invoice_id, idempotency_key)`.
- Провайдер вызывается **вне транзакции**.
- После ответа провайдера короткая транзакция повторно блокирует invoice, проверяет состояние,
  ищет concurrent-попытку и только затем делает INSERT.
- Уникальный ключ остаётся последней защитой; при `23505` возвращается существующая попытка
  после проверки fingerprint.
- Одинаковый ключ с другой суммой → `409 Conflict`.
- `TestPaymentProvider` теперь детерминирован: `pay_{sha256(provider:invoice:key)}`. Повтор после
  сбоя возвращает тот же `provider_payment_id`.
- `PaymentIntent` теперь содержит amount/currency; локальная попытка использует сумму,
  подтверждённую провайдером, а не повторно вычисленный outstanding.

**Тесты**

- 8 одновременных запросов с одним ключом → одна строка, один provider id.
- Тот же ключ, другая сумма → `409`, в БД одна попытка.
- Провайдер бросает исключение после создания intent → `500`, в БД 0 попыток; повтор с тем же
  ключом создаёт одну попытку с тем же provider id.
- Провайдер заблокирован на TCS; параллельный `UPDATE invoices` завершается, значит invoice-lock
  не удерживается во время вызова провайдера.

### 1.2. Повторное зачисление и конфликтующие webhook

**Было (критично)**

- Для частично оплаченного invoice повторный success того же PaymentAttempt проходил в
  `invoice.RegisterPayment` второй раз. `Invoice` был `Open`, поэтому early-return для `Paid`
  не срабатывал: 500 + 500 могли превратиться в 1000 при одном фактическом платеже.
- Один и тот же `event_id` с другим телом считался обычным дублем: payload hash не сравнивался.
- Второй полный success по уже оплаченному invoice мог пометить другой attempt как `Succeeded`,
  хотя деньги к invoice не добавлялись.
- Dead-letter не оставлял отдельной audit-записи.

**Исправлено**

- Success применяется только если `attempt.Status` ещё не `Succeeded`; иначе
  `WebhookIgnored` + audit, без изменения сумм.
- Если invoice уже `Paid` другим attempt — `PermanentWebhookException`, событие
  `DeadLettered`, attempt не становится успешным «в кредит».
- `Invoice.RegisterPayment` вызывается только после успешного перехода attempt.
- Для повторного `event_id` сравнивается `PayloadHash`:
  - одинаковый payload — обычный duplicate;
  - другой payload — `409`, audit `WebhookPayloadConflict`, оригинальное событие не меняется.
- Dead-letter пишет `WebhookDeadLettered` в `audit_logs` и сохраняет `LastError`/`Attempts`.
- Late success после refund игнорируется и аудируется.

**Тесты**

- 6 разных `event_id` об одном одном PaymentAttempt одновременно → `AmountPaid` увеличился
  ровно один раз, `PaymentSucceeded` в аудите ровно один.
- Два success с разными event id для одного частичного платежа → зачислено 500, не 1000.
- Один event id с другим payload → `409`, audit, оригинальный inbox не перезаписан.
- Второй полный платёж по оплаченному invoice → `422`, attempt остаётся `Pending`, invoice
  остаётся 1000, событие `DeadLettered` с audit.
- Повторный success после полного refund → счёт остаётся `Refunded`, доступ не возвращается.

### 1.3. Частичные платежи и возвраты

**Было**

- Частичные платежи фактически поддерживались, но из-за дефекта выше повторное подтверждение
  одного частичного платежа могло удвоить `AmountPaid`.
- Частичные refunds учитывались в attempt и invoice, но не было тестов на повторную доставку,
  конкурентность и превышение лимита.
- Полный refund отзывал доступ только по `InvoiceId`; при нескольких оплаченных периодах
  доступ от другого invoice мог остаться активным у `Refunded` подписки.

**Исправлено**

- Повторный success одного attempt не зачисляется (см. 1.2).
- Invoice-lock сериализует конкурентные refunds.
- `PaymentAttempt.ApplyRefund` ограничивает сумму на attempt, `Invoice.ApplyRefund` — на invoice;
  проверка идёт по накопленным значениям.
- Полный refund терминален: подписка `Refunded`, **все** активные доступы организации отозваны.
- Повторный refund того же attempt с новым event id игнорируется после полного возврата.

**Тесты**

- Invoice 1000: два платежа по 500 → `Paid`, `AmountPaid = 1000`, два `Succeeded`.
- Тот же частичный платёж с новым event id → сумма не растёт.
- Refund 200 дважды одним event id → 200, не 400.
- Два конкурентных refund по 200 → 600 суммарно с ранее учтённым 200.
- Refund 401 при уже возвращённых 600 из 1000 → `422`, `DeadLettered`, суммы не меняются.
- Полный refund → invoice/attempt/subscription `Refunded`, доступ `Revoked`.

### 1.4. Продление подписки

**Было**

- `Subscription.Activate` был no-op для `Active`, всегда ставил `PeriodEnd = now + 1 месяц` и не
  учитывал `Plan.PeriodMonths`.
- Не было продления, блокировки строки подписки и определённой политики для отменённой
  подписки.
- При оплате отменённой подписки старый код бросал исключение, откатывал платёж и переводил
  событие в dead-letter: деньги фактически не фиксировались в invoice.

**Исправлено**

- Добавлен `Subscription.ActivateOrRenew(now, periodMonths)`:
  - первый период — от `now`;
  - досрочное продление — от текущего `PeriodEnd`;
  - после истечения — от `now`;
  - `PeriodEnd = start + Plan.PeriodMonths`.
- Продление блокирует строку подписки `FOR UPDATE` после invoice-lock. Два платежа за разные
  периоды применяются последовательно, ни один оплаченный месяц не теряется.
- Добавлен план `quarterly` с `PeriodMonths = 3`.
- Добавлен `POST /api/subscriptions/{id}/cancel`; отмена идемпотентна и отзывает доступ.
- Терминальная подписка не реактивируется. Поздний платёж фиксируется в invoice и аудите
  `PaymentReceivedForTerminalSubscription`, доступ не выдаётся; нужен ручной возврат.

**Тесты**

- Первый период + второй invoice → `PeriodStart` = старый `PeriodEnd`, `PeriodEnd` += 1 месяц.
- Повторный webhook второго периода не продлевает третий месяц.
- Два разных периода оплачиваются одновременно → `PeriodEnd` += 2 месяца, оба invoice `Paid`.
- Оплата после истечения → новый период начинается от времени оплаты.
- `quarterly` → период ровно 3 месяца.
- Отмена отзывает доступ; поздняя оплата оставляет подписку `Canceled`, invoice `Paid`, доступа нет.

### 1.5. Транзакционность и восстановление

**Было**

- Провайдер вызывался внутри транзакции создания платежа (см. 1.1).
- Не было теста на падение после частичного изменения сущностей.
- Dead-letter нельзя было повторно обработать вручную.
- Audit не фиксировал dead-letter.

**Исправлено**

- Вызов провайдера вынесен из транзакции.
- Webhook применяется одной транзакцией: invoice/attempt/subscription/access/audit.
- Inbox-запись коммитится до бизнес-транзакции; при сбое событие остаётся `Processing` и
  переобрабатывается worker'ом после lease.
- `retry?force=true` позволяет вручную повторить `DeadLettered` событие после разбора.
- `WebhookDeadLettered` audit.
- Фоновый `WebhookRecoveryService` проверен отдельным тестом.

**Тесты**

- SQL-триггер на `audit_logs` бросает ошибку после изменения invoice/attempt → transaction
  rollback: invoice `Open`, attempt `Pending`, доступа нет; после снятия триггера retry
  переводит всё в `Paid`.
- Событие в `Processing` с просроченным lease → retry обрабатывает его и увеличивает `Attempts`.
- 6 одновременных retry одного Failed-события → событие `Processed`, refund учтён один раз.
- Фоновый worker обрабатывает `Pending`-событие без HTTP-вызова.

## 2. Изменённые компоненты

### Domain

- `src/SaasBilling.Domain/Subscription.cs` — `ActivateOrRenew`, период из тарифа.
- `src/SaasBilling.Domain/PaymentAttempt.cs` — `RequestFingerprint`, идемпотентные переходы.
- `src/SaasBilling.Domain/PlanCatalog.cs` — план `quarterly` (3 месяца).

### API

- `src/SaasBilling.Api/Services/BillingService.cs` — fingerprint, provider вне транзакции,
  проверка под локом, cancel subscription.
- `src/SaasBilling.Api/Services/WebhookService.cs` — защита от повторного зачисления,
  payload-conflict, dead-letter audit, renewal, subscription lock, `force` retry.
- `src/SaasBilling.Api/Endpoints/BillingEndpoints.cs` — cancel и `?force=true`.
- `src/SaasBilling.Api/Payments/TestPaymentProvider.cs` — детерминированный idempotent intent,
  amount/currency в `PaymentIntent`.
- `src/SaasBilling.Api/Data/BillingDbContext.cs` — маппинг fingerprint.
- `src/SaasBilling.Api/Data/Migrations/20261117000229_AddPaymentRequestFingerprint.*` — миграция.

### Tests

- `tests/SaasBilling.UnitTests/InvoiceLifecycleTests.cs` — fingerprint, частичные refunds,
  лимиты.
- `tests/SaasBilling.UnitTests/SubscriptionLifecycleTests.cs` — новый файл: активация,
  продление, истечение, 3-месячный тариф, терминальные состояния.
- `tests/SaasBilling.IntegrationTests/BillingApiTests.cs` — 20 новых integration-сценариев
  (конкурентность, invoice/refund, renewal, recovery, отмена).
- `tests/SaasBilling.IntegrationTests/Infrastructure/ControllablePaymentProvider.cs` — новый
  управляемый провайдер для сбоя после intent и проверки отсутствия lock на время вызова.

### Документация

- `README.md` — обновлённые правила, API и ограничения.
- `AUDIT.md` — этот отчёт.

## 3. Принятые архитектурные решения

1. **Источник истины по деньгам — БД.** Внешний intent не считается оплатой; invoice меняется
   только при проверенном webhook.
2. **Один invoice-lock на финансовые переходы.** Success/refund/частичные операции
   сериализуются; затем при необходимости блокируется subscription.
3. **Inbox-first.** Аутентифицированное событие сначала сохраняется, затем обрабатывается.
   Падение процесса не теряет событие.
4. **Short transactions.** Сетевой вызов провайдера — вне транзакции; webhook не делает
   внешних вызовов.
5. **Идемпотентность на нескольких уровнях:** `event_id` + payload hash, `(invoice,key)` +
   request fingerprint, статусные guard'ы attempt/invoice, unique-ограничения.
6. **Терминальные состояния не воскрешаются автоматически.** Refund/cancel — явные решения;
   поздние платежи фиксируются, но требуют ручного разбора.
7. **Периоды подписки.** Один invoice = один период; раннее продление стыкуется с `PeriodEnd`,
   просроченное — начинается от даты оплаты.

## 4. Фактическая проверка

Выполнено:

```text
dotnet build SaasBilling.sln -c Release
-> 0 ошибок, 0 предупреждений

SaasBilling.UnitTests
-> 25 passed, 0 failed

SaasBilling.IntegrationTests
-> 30 passed, 0 failed (PostgreSQL 16.11)
```

Интеграционные тесты запускались через `dotnet run` xUnit v3 runner'а против реального
PostgreSQL. В окружении аудита `dotnet test` не доходит до выполнения тестов: песочница DSH
блокирует MTP named pipe (`UnauthorizedAccessException`/`Access denied`). Это ограничение
окружения, а не тестового кода; на обычной машине с Docker используется стандартный
`dotnet test` и Testcontainers.

## 5. Оставшиеся ограничения и риски

1. **Refund до полной оплаты invoice** отклоняется. Это сделано осознанно, чтобы не смешивать
   gross `AmountPaid` и `AmountRefunded`. Если нужен refund после частичного платежа — нужна
   отдельная модель refund-транзакций и пересчёт net paid.
2. **Провайдер обязан поддерживать idempotency key.** Если реальный провайдер создаёт capture
   в момент создания intent, перед вызовом нужна reservation-строка или обязательный void
   orphan-intent.
3. **Overpayment** второго полного attempt не зачисляется и попадает в dead-letter; автоматически
   инициировать возврат у провайдера система не умеет.
4. **Отменённая подписка + поздняя оплата**: деньги фиксируются в invoice, но сервис не
   активируется; возврат/реактивация — ручное решение.
5. **Нет chaos-теста реального падения PostgreSQL.** Транзакционный rollback проверен
   SQL-триггером; stale lease и фоновый worker — отдельными тестами.
6. **Нет аутентификации/RBAC** на административных endpoint'ах (`cancel`, `retry force`).
7. **Один invoice = один период.** Proration, mid-cycle upgrade/downgrade и несколько периодов
   в одном invoice не моделируются.
8. **Полный refund отзывает все доступы организации** — консервативно; при нескольких
   независимых продуктах нужна связь `ProductAccess → Subscription`.
9. **Фоновый worker — at-least-once.** Финансовые эффекты защищены блокировками и guard'ами,
   но внешние side effects (email/webhook-уведомления) не реализованы.
