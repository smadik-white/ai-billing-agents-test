# SaaS Billing — рабочий фрагмент биллинга

Небольшой, но сквозной backend-фрагмент SaaS-биллинга на **ASP.NET Core 10**, **EF Core 10**,
**PostgreSQL**, **Docker Compose** и **xUnit v3 + Testcontainers**. React и реальный платёжный
провайдер не нужны: используется детерминированный тестовый адаптер с HMAC-подписью вебхуков.

Проект специально сделан как демонстрация корректной обработки денежных состояний, а не как
«CRUD ради CRUD»: здесь есть inbox, транзакции, блокировки строк, unique-ограничения,
идемпотентность, аудит и фоновое восстановление.

## Что реализовано

| # | Требование | Где |
|---|---|---|
| 1 | Организация и подписка | `POST /api/organizations`, `POST /api/organizations/{id}/subscriptions` |
| 2 | Счёт и позиции | `POST /api/invoices` |
| 3 | Платёжная попытка | `POST /api/invoices/{id}/payment-attempts` c `Idempotency-Key` |
| 4 | Вебхук с проверкой HMAC | `POST /api/webhooks/test`, `TestPaymentProvider` |
| 5 | Повторные и конкурентные события | inbox unique `(provider,event_id)`, claim + `FOR UPDATE`, идемпотентные переходы |
| 6 | Активация подписки после оплаты | `Invoice.RegisterPayment` + `Subscription.Activate` в одной транзакции |
| 7 | Доступ к продукту | `ProductAccess`, unique `(organization_id, product_code)` |
| 8 | Полный возврат | `payment.refunded` → счёт/платёж/подписка `Refunded`, доступ `Revoked` |
| 9 | Журналирование и восстановление | `audit_logs`, `webhook_events`, `WebhookRecoveryService`, ProblemDetails + correlation id |

## Структура

```
src/SaasBilling.Domain       домен: деньги, счёт, платёж, подписка, доступ, состояния
src/SaasBilling.Api          ASP.NET Core: EF Core, endpoints, HMAC-адаптер, webhook inbox/processor
tests/SaasBilling.UnitTests  быстрые тесты домена и HMAC (без Docker)
tests/SaasBilling.IntegrationTests  API + PostgreSQL через Testcontainers
```

### Поток оплаты

```
POST /organizations
        │
        ▼
POST /organizations/{id}/subscriptions   subscription = PendingActivation
        │
        ▼
POST /invoices                            invoice = Open, lines[]
        │
        ▼
POST /invoices/{id}/payment-attempts      attempt = Pending (provider="test")
        │
        ▼
payment.succeeded webhook  ──► inbox (unique event id)
        │
        ▼  одна транзакция, invoice row lock
 attempt = Succeeded
 invoice = Paid (когда AmountPaid == Total)
 subscription = Active
 product_access = Active
 audit_logs += PaymentSucceeded / SubscriptionActivated / ProductAccessGranted
```

При `payment.refunded` на полную сумму:

```
invoice = Refunded, attempt = Refunded, subscription = Refunded,
product_access = Revoked, audit_logs += InvoiceFullyRefunded / ProductAccessRevoked
```

## API

Базовый URL при Docker Compose: `http://localhost:8080`.

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/health` | Проверка БД |
| GET | `/api/plans` | Каталог планов (starter/pro/team) |
| POST | `/api/organizations` | Создать организацию |
| GET | `/api/organizations/{id}` | Организация + подписки |
| POST | `/api/organizations/{id}/subscriptions` | Создать подписку (по умолчанию `PendingActivation`) |
| GET | `/api/organizations/{id}/access` | Доступы к продуктам |
| GET | `/api/organizations/{id}/audit` | Журнал аудита (до 500 записей) |
| GET | `/api/subscriptions/{id}` | Подписка |
| POST | `/api/invoices` | Создать счёт с позициями |
| GET | `/api/invoices/{id}` | Счёт + позиции + попытки оплаты |
| POST | `/api/invoices/{id}/payment-attempts` | Создать попытку; требует `Idempotency-Key` |
| POST | `/api/webhooks/test` | Принять подписанный вебхук |
| GET | `/api/webhooks/{id}` | Состояние inbox-события |
| POST | `/api/webhooks/{id}/retry` | Ручной retry (не для dead-letter) |

### Пример создания счёта

```json
{
  "organizationId": "00000000-0000-0000-0000-000000000000",
  "subscriptionId": "00000000-0000-0000-0000-000000000000",
  "currency": "USD",
  "dueAt": "2026-12-01T00:00:00Z",
  "lines": [
    {
      "description": "Starter plan",
      "productCode": "starter-product",
      "quantity": 1,
      "unitAmount": 29.00
    }
  ]
}
```

Суммы считаются на сервере (`quantity * unitAmount`, округление до копеек), клиент не передаёт
`total`. Счёт можно создать без подписки; тогда после оплаты будут выданы только продукты из
позиций счёта.

### HMAC-контракт тестового провайдера

Заголовок:

```text
X-Billing-Signature: t=<unix-seconds>,v1=<hex hmac-sha256(secret, "{t}.{rawBody}")>
```

- Подпись считается по **сырому телу** запроса, а не по пересобранному JSON.
- Допуск по времени — 300 секунд (`Payments:Test:ToleranceSeconds`).
- Сравнение — `CryptographicOperations.FixedTimeEquals`.
- Секрет — `Payments:Test:Secret` (в Docker Compose — `WEBHOOK_SECRET`, по умолчанию dev-secret).
- Событие идентифицируется полем `id`; повторный `id` не обрабатывается повторно.
- Поддерживаемые типы: `payment.succeeded`, `payment.failed`, `payment.refunded`.

Тело:

```json
{
  "id": "evt_01J...",
  "type": "payment.succeeded",
  "createdAt": "2026-11-16T12:00:00Z",
  "data": {
    "paymentId": "pay_...",
    "invoiceId": "...",
    "amount": 29.00,
    "currency": "USD"
  }
}
```

## Идемпотентность и конкурентность

1. **Inbox.** Каждое подписанное событие сначала коммитится в `webhook_events` с unique
   `(provider, event_id)`. Бизнес-эффекты начинаются только после этого. Падение процесса не
   теряет уже принятое событие.
2. **Claim.** Обработчик захватывает событие под `SELECT ... FOR UPDATE`, переводит в
   `Processing`, увеличивает `Attempts`. Параллельный доставщик видит `Processing` и получает
   `202`/`duplicate`, а не применяет событие второй раз.
3. **Invoice lock.** Все переходы денег по счёту сериализуются блокировкой строки счёта
   `FOR UPDATE`. Поэтому success и refund не могут одновременно изменить один счёт.
4. **Идемпотентные переходы.** `MarkSucceeded`, `Activate`, `ApplyRefund`, `ReGrant` возвращают
   `false`, если состояние уже достигнуто. Повторный success не увеличивает `AmountPaid`.
5. **Уникальные ключи.** `(invoice_id, idempotency_key)` для платёжной попытки,
   `(provider, provider_payment_id)` для платежа, `(organization_id, product_code)` для доступа.
6. **Late success после refund.** Событие authentic, но не может реактивировать подписку: оно
   фиксируется как `WebhookIgnored`, счёт остаётся `Refunded`.

## Ошибки, аудит и восстановление

- Ожидаемые ошибки → `application/problem+json` с корректным статусом:
  `400`, `401` (подпись), `404`, `409`, `422` (аутентичное, но неприменимое событие).
- У каждого HTTP-запроса есть `X-Correlation-Id`; он попадает в лог и в `audit_logs.trace_id`.
- `audit_logs` пишется **в той же транзакции**, что и изменение состояния. Это не «лог ради лога»:
  если транзакция откатилась, записи аудита тоже нет.
- Ошибка обработки вебхука сохраняется в `webhook_events.last_error`, `attempts`,
  `next_retry_at`; успешное событие — `processed`.
- `WebhookRecoveryService` каждые N секунд выбирает `Pending`/`Failed` с наступившим backoff и
  «зависшие» `Processing` со старым lease, затем вызывает тот же процессор. Захват защищён
  блокировкой строки, поэтому несколько экземпляров API не обработают событие дважды.
- После исчерпания `MaxAttempts` событие переходит в `DeadLettered` и не ретраится автоматически;
  его можно исследовать через `GET /api/webhooks/{id}`.
- Ошибки, которые никогда не станут валидными (неверная сумма/валюта, неизвестный объект,
  неподдерживаемый тип), сразу переводятся в `DeadLettered` и возвращают `422`.

## Запуск через Docker Compose

```bash
docker compose up --build
```

API: `http://localhost:8080`, PostgreSQL: `localhost:5432` (`billing/billing`, БД `saas_billing`).
Миграции применяются при старте API (`Database__AutoMigrate=true`).

```bash
curl http://localhost:8080/health
curl http://localhost:8080/api/plans
```

### Локальный запуск без Docker

Нужен PostgreSQL и .NET 10 SDK:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=saas_billing;Username=billing;Password=billing"
dotnet run --project src/SaasBilling.Api
```

Можно поднять только БД:

```bash
docker compose up -d postgres
```

### Демо-сценарий

После запуска API:

```powershell
pwsh ./scripts/demo.ps1
```

Скрипт создаёт организацию, подписку, счёт, платёжную попытку, подписывает success-вебхук и
печатает состояния счёта/подписки/доступа. Для полного возврата:

```powershell
pwsh ./scripts/demo.ps1 -Refund
```

## Тесты

```bash
dotnet test
```

- `SaasBilling.UnitTests` — домен и HMAC, Docker не нужен.
- `SaasBilling.IntegrationTests` — `WebApplicationFactory` + настоящий PostgreSQL в
  Testcontainers (`postgres:16-alpine`), миграции, HTTP-сценарии, конкурентные вебхуки.
- Если Docker недоступен, integration-тесты помечаются `skipped`, а не падают. На машине с
  Docker они запускаются автоматически.
- Для окружений без Docker тесты можно направить на уже существующий PostgreSQL:

```bash
export BILLING_TEST_CONNECTION="Host=localhost;Port=5432;Database=saas_billing;Username=billing;Password=billing"
dotnet run --project tests/SaasBilling.IntegrationTests
```

Запуск только unit-тестов:

```bash
dotnet run --project tests/SaasBilling.UnitTests
```

## Решения и ограничения

- Реальный провайдер не интегрирован: `IPaymentProvider`/`TestPaymentProvider` — точка замены.
- Каталог планов статический (`PlanCatalog`), валюта планов — USD; счёт поддерживает одну валюту.
- Аутентификация/авторизация не входит в задание; API предполагается вызывать из доверенного
  контура. HMAC защищает только вебхуки.
- Секрет вебхука в `appsettings.json` — только dev-значение; в production задаётся переменной
  окружения/секретом.
- Суммы — `decimal(18,2)`, округление `AwayFromZero`; сравнение полной оплаты/возврата идёт по
  округлённым до копеек значениям.
- Частичные платежи и частичные возвраты поддержаны моделью: подписка активируется только при
  `AmountPaid == Total`, доступ отзывается только при `AmountRefunded == Total`.
