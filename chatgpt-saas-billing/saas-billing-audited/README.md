# SaaS Billing — audited billing backend

Учебный, но транзакционно согласованный backend-фрагмент биллинга: **ASP.NET Core 10, EF Core 10, PostgreSQL, Docker Compose, xUnit и Testcontainers**. Платёжный провайдер — детерминированный тестовый адаптер, реальные списания не выполняются.

## Запуск

Нужны .NET 10 SDK для локального запуска, либо Docker Engine и Compose plugin для контейнерного запуска.

```bash
cp .env.example .env
docker compose up --build
```

API: `http://localhost:8080`; PostgreSQL: `localhost:5432`.

```bash
curl http://localhost:8080/health
```

Замените примерные секреты `PAYMENTS_HMAC_SECRET`, `OPS_API_KEY` и пароль БД перед любым развёртыванием. Не коммитьте `.env`.

### Обновление ранее созданной БД

`EnsureCreated` создаёт только пустую схему и не обновляет уже существующие таблицы. При переходе с предыдущей версии проекта сначала сделайте резервную копию, затем выполните идемпотентный upgrade-скрипт на существующей БД:

```bash
export BILLING_DB_CONNECTION='Host=localhost;Port=5432;Database=billing;Username=billing;Password=...'
psql "$BILLING_DB_CONNECTION" -v ON_ERROR_STOP=1 -f db/migrations/001-upgrade-existing-billing-schema.sql
```

Скрипт сохраняет организации, подписки, счета, попытки платежей, webhook-журнал и аудит; старые агрегаты capture/refund восстанавливаются из прежних статусов платежных попыток. Из-за того, что старая схема не хранила историю каждого оплаченного периода, скрипт восстанавливает grant только для текущего периода активной подписки. Перед production-развёртыванием используйте ревьюируемые версионируемые EF migrations вместо стартового `EnsureCreated`.

## Сквозной сценарий

### 1. Создать организацию, подписку и счёт

```bash
ORG=$(curl -fsS http://localhost:8080/api/organizations \
  -H 'Content-Type: application/json' \
  -d '{"name":"Acme Ltd"}')
ORG_ID=$(echo "$ORG" | jq -r .id)

SUB=$(curl -fsS "http://localhost:8080/api/organizations/$ORG_ID/subscriptions" \
  -H 'Content-Type: application/json' \
  -d '{"productCode":"analytics-pro","price":1000.00,"currency":"EUR","billingPeriodDays":30}')
SUB_ID=$(echo "$SUB" | jq -r .id)

INV=$(curl -fsS "http://localhost:8080/api/subscriptions/$SUB_ID/invoices" \
  -H 'Content-Type: application/json' \
  -d '{"lines":[{"description":"Analytics Pro — 30 days","quantity":1,"unitPrice":1000.00}]}')
INV_ID=$(echo "$INV" | jq -r .id)
```

`BillingPeriodDays` копируется в счёт на момент его выставления: более позднее изменение настроек тарифа не меняет уже выставленный период.

### 2. Создать платёжную попытку

`Idempotency-Key` обязателен. Для частичной оплаты передайте `amount` в query string. Без него резервируется весь незарезервированный остаток счёта.

```bash
PAY=$(curl -fsS -X POST "http://localhost:8080/api/invoices/$INV_ID/payment-attempts?amount=500.00" \
  -H 'Idempotency-Key: invoice-1-part-1')
PAY_ID=$(echo "$PAY" | jq -r .id)
echo "$PAY" | jq '{id,status,amount,currency,checkoutUrl}'
```

Для оплаты оставшихся 500 EUR используйте новый ключ:

```bash
PAY2=$(curl -fsS -X POST "http://localhost:8080/api/invoices/$INV_ID/payment-attempts?amount=500.00" \
  -H 'Idempotency-Key: invoice-1-part-2')
PAY2_ID=$(echo "$PAY2" | jq -r .id)
```

Повтор запроса с тем же ключом и теми же параметрами возвращает ту же платёжную попытку. Тот же ключ с другим `amount` возвращает `409 Conflict`. Один ключ уникален в пределах счёта.

**Почему безопасно при таймауте провайдера:** сначала в PostgreSQL фиксируется попытка `Creating` с уникальным ключом и отпечатком запроса. Затем, уже после commit, вызывается провайдер. В провайдер передаётся стабильный ID попытки в качестве provider idempotency key. Если удалённый провайдер создал платёж, но ответ потерялся или процесс упал до сохранения provider response, повтор вызывает тот же ключ и должен вернуть тот же платёж. `PaymentAttemptRecoveryWorker` автоматически повторяет просроченные `Creating` попытки. Не подключайте адаптер реального провайдера, если он не поддерживает такую идемпотентность.

### 3. Подписать и отправить webhook

Формат успешной оплаты:

```json
{
  "eventId": "evt-0001",
  "eventType": "payment.succeeded",
  "paymentId": "<PAY_ID>",
  "amount": 500.00,
  "currency": "EUR",
  "occurredAt": "2026-11-17T10:00:00Z"
}
```

Для `payment.succeeded` и `payment.failed` сумма должна совпасть с суммой платёжной попытки. Для `payment.refunded` передавайте сумму конкретного возврата и стабильный `refundId`:

```json
{
  "eventId": "evt-refund-200",
  "eventType": "payment.refunded",
  "paymentId": "<PAY_ID>",
  "amount": 200.00,
  "currency": "EUR",
  "occurredAt": "2026-11-17T10:10:00Z",
  "refundId": "provider-refund-unique-200"
}
```

Подпись — `sha256=` + lowercase hex от `HMAC-SHA256(secret, raw_request_body)`. Подписываются исходные байты запроса.

```bash
export PAYMENTS_HMAC_SECRET='change-this-dev-hmac-secret'
PAYLOAD=$(jq -nc --arg paymentId "$PAY_ID" \
  '{eventId:"evt-0001",eventType:"payment.succeeded",paymentId:$paymentId,amount:500.00,currency:"EUR",occurredAt:"2026-11-17T10:00:00Z"}')
SIGNATURE=$(printf '%s' "$PAYLOAD" | openssl dgst -sha256 -hmac "$PAYMENTS_HMAC_SECRET" | awk '{print $2}')

curl -i http://localhost:8080/api/webhooks/test-provider \
  -H 'Content-Type: application/json' \
  -H "X-Test-Signature: sha256=$SIGNATURE" \
  --data-binary "$PAYLOAD"
```

После успешного подтверждения счёт агрегирует сумму подтверждённых платежей, подписка получает один grant за данный счёт, а entitlement активируется только когда net paid достигает полной суммы счёта.

### 4. Частичный и полный возврат

Каждый возврат требует уникальный стабильный `refundId`. Один и тот же `refundId` с теми же платёжной попыткой и суммой учитывается ровно один раз, даже если провайдер прислал другой `eventId`. Повторное использование `refundId` с другими параметрами переводит webhook в dead-letter. Общая сумма возвратов не может превысить фактически подтверждённую сумму соответствующей попытки.

Если возврат уменьшил net paid по счёту ниже полной суммы, деактивируется grant, выданный именно этим счётом. Периоды, оплаченные другими счетами, не отзываются. При полном возврате всех средств счёт получает статус `Refunded`.

### 5. Отмена подписки

```bash
curl -X POST "http://localhost:8080/api/subscriptions/$SUB_ID/cancel"
```

Политика в этом срезе — **немедленная отмена**: активные grants и entitlement отзываются в той же транзакции, статус становится `Cancelled`. Успешный webhook после отмены учитывается в финансовом журнале, но не реактивирует подписку и помечает попытку `RequiresManualRefund`.

### 6. Журнал webhook и диагностика

```bash
export OPS_API_KEY='change-this-dev-ops-key'
curl -fsS http://localhost:8080/api/ops/webhooks -H "X-Ops-Key: $OPS_API_KEY" | jq
curl -fsS http://localhost:8080/api/ops/webhook-conflicts -H "X-Ops-Key: $OPS_API_KEY" | jq
curl -fsS http://localhost:8080/api/ops/payment-attempts -H "X-Ops-Key: $OPS_API_KEY" | jq
curl -fsS http://localhost:8080/api/ops/audit?take=100 -H "X-Ops-Key: $OPS_API_KEY" | jq

# Retry a provider-create intent after investigating the provider's status:
curl -i -X POST "http://localhost:8080/api/ops/payment-attempts/<PAYMENT_ATTEMPT_GUID>/retry" \
  -H "X-Ops-Key: $OPS_API_KEY"

# Retry a failed/dead-letter webhook after correcting the root cause:
curl -i -X POST "http://localhost:8080/api/ops/webhooks/<WEBHOOK_EVENT_GUID>/retry" \
  -H "X-Ops-Key: $OPS_API_KEY"
```

При повторном `eventId` с другим содержимым сохраняется отдельная запись `WebhookConflict` с hash исходного и конфликтующего тела. Такие события доступны для диагностики через `/api/ops/webhook-conflicts`.

## Ключевые гарантии и архитектурные решения

- **Payment creation idempotency:** уникальный индекс `(InvoiceId, IdempotencyKey)` + SHA-256 fingerprint запроса + постоянный provider idempotency key (`PaymentAttempt.Id`). Операция создаётся до обращения по сети; сетевой вызов не удерживает транзакцию БД.
- **Reservations:** под `FOR UPDATE` блокируется счёт, доступная сумма рассчитывается с учётом net paid и всех `Creating`/`Pending` резервов. Поэтому параллельные ключи не резервируют одну сумму дважды.
- **Webhook idempotency:** уникальный `(Provider, ProviderEventId)`, hash сырого payload, `FOR UPDATE` на событии, попытке и счёте. Разные event ID для одного платежа проходят через блокировку той же `PaymentAttempt` и не создают повторный capture.
- **Atomic financial effects:** capture, refund row, изменение агрегатов invoice, period grant, entitlement, аудит и статус webhook коммитятся одной транзакцией PostgreSQL. Исключение до commit откатывает весь финансовый эффект.
- **Partial refunds:** `PaymentRefund` уникален по `(Provider, ProviderRefundId)`. Сумма проверяется и обновляется под блокировкой платежной попытки.
- **Renewals:** `SubscriptionPeriodGrant` уникален по `InvoiceId`; блокировка subscription row сериализует grant для разных счетов. Следующий оплаченный счёт продлевает период после актуального конца; просроченный период начинается с текущего UTC времени.
- **Cancellation:** отмена немедленная и терминальная. Поздние capture фиксируются, но не возвращают доступ.
- **Recovery:** webhook остаётся в durable журнале до обработки; retry с backoff и dead-letter. Отдельный worker повторяет `Creating` provider intents по idempotency key. `SubscriptionExpiryWorker` деактивирует завершившиеся grants и переводит подписку в `Suspended`; сам endpoint доступа дополнительно проверяет `ValidUntilUtc`, чтобы не было окна доступа после истечения периода. Ручной retry защищён ops key.

### Основные финансовые инварианты

- `Invoice.AmountPaid` — сумма подтверждённых captures, не повторяется при повторном webhook.
- `Invoice.AmountRefunded` — сумма записанных уникальных refunds; ограничена суммой captures.
- `Invoice.NetPaidAmount = AmountPaid - AmountRefunded`.
- Резерв новых платежей не может превышать `TotalAmount - NetPaidAmount`.
- `PaymentAttempt.RefundedAmount` не может превышать `PaymentAttempt.Amount`.
- Один `Invoice` даёт не более одного активного grant; повторная доставка success не продлевает период.
- Позднее дополнительное списание, переплата или capture после отмены/возврата фиксируются и отмечаются `RequiresManualRefund`, а не активируют доступ повторно.

## Автоматические тесты

Нужны .NET 10 SDK и работающий Docker daemon; Testcontainers поднимает PostgreSQL 17.

```bash
dotnet build SaaSBilling.sln
dotnet test SaaSBilling.sln
```

В тестовом наборе есть unit tests финансовых правил и fingerprint, а также integration tests на настоящем PostgreSQL:

- конкурентные запросы с одинаковым `Idempotency-Key`; несовпадающие параметры с тем же ключом;
- потеря ответа после provider-side create и восстановление тем же provider idempotency key;
- задержка провайдера при уже committed payment intent;
- многократная доставка одного event ID и разные event ID для одной payment attempt;
- повторное использование event ID с другим payload и сохранение конфликта;
- конкуренция success/refund и no-op success после возврата;
- два частичных платежа, дублирующий capture, частичный повторный refund, конкурентные возвраты и over-refund;
- продление нескольких периодов, конкурентное продление разных invoice и оплата после истечения периода;
- отмена с поздним success;
- автоматическое завершение оплаченного периода, отзыв эффективного доступа и persisted-переход подписки в `Suspended`;
- fault injection после `SaveChanges` и до commit, проверка rollback SQL-изменений и ручное восстановление;
- недоступность PostgreSQL и повторная доставка события после восстановления.

## Ограничения и production checklist

1. Реальный PSP должен поддерживать внешний idempotency key для создания платежа. Если он не поддерживает его, гарантировать отсутствие дубликатов при неоднозначном сетевом исходе невозможно; потребуется provider-specific reconciliation/status lookup и ручное разрешение неопределённого исхода.
2. Тестовый адаптер имитирует provider-side идемпотентность на основе ID операции; он не подтверждает реальное зачисление денег.
3. Политика отмены сейчас немедленная; нет cancel-at-period-end, grace period, автоматического выставления renewal invoice или dunning. Оплаченные периоды корректно выдаются и истекают, но само выставление очередного счёта инициирует внешний scheduler/клиент.
4. Нет налогов, proration, купонов, мультивалютной конвертации, chargeback/dispute flows и частичного оказания entitlement по доле оплаты.
5. Доступ пользователя и tenant isolation/RBAC, rate limiting, аутентификация ops API и защита от массовых webhook retries должны быть добавлены до внешней эксплуатации.
6. Приложение использует `EnsureCreated` для новой пустой схемы. Для существующей старой БД выполните `db/migrations/001-upgrade-existing-billing-schema.sql`; в production замените стартовый `EnsureCreated` на ревьюируемые EF migrations и отдельный migration step. Исторические границы оплаченных периодов из старой схемы полностью восстановить невозможно.
7. Установите retention/архивацию для сырых webhook payload и аудита; примените редактирование данных по требованиям приватности.
8. Для реального PSP нужно дополнительно проверять сумму, валюту, идентификаторы merchant/account, timestamp/replay window и текущий статус операции по API провайдера.
