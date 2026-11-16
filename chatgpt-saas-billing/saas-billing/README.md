# SaaS Billing — runnable backend slice

Небольшой backend-фрагмент биллинга на **ASP.NET Core 10 + EF Core 10 + PostgreSQL**. Внешний платёжный API намеренно заменён локальным `test-provider`: никакие реальные платежи и сетевые вызовы в платёжные системы не выполняются.

## Что реализовано

- Организации и подписки с продуктовым кодом, ценой, валютой и длиной периода.
- Счета с immutable-подобными позициями; сумма счёта должна совпасть с ценой подписки.
- Одна или несколько платёжных попыток на счёт, но только одна одновременно в `Pending`.
- Вебхуки `payment.succeeded`, `payment.failed` и `payment.refunded` с HMAC-SHA256 подписью исходных байтов HTTP body.
- Идемпотентность по `(Provider, ProviderEventId)`, защита от конкурентных доставок и блокировка платежа при обработке разных событий через PostgreSQL `FOR UPDATE`.
- Активация подписки и создание/обновление entitlement только после подтверждённого платежа.
- Полный возврат: счёт получает `Refunded`, подписка — `Suspended`, доступ к продукту отзывается.
- Журнал событий и ошибок, автоматический повтор временно неуспешных событий с экспоненциальной задержкой, dead-letter для постоянных ошибок, ручной повтор через ops API.
- xUnit integration tests с PostgreSQL в Testcontainers.

## Запуск через Docker Compose

Нужны Docker Engine и Docker Compose plugin.

```bash
cp .env.example .env
docker compose up --build
```

API будет доступен на `http://localhost:8080`, PostgreSQL — на `localhost:5432`. Проверка готовности:

```bash
curl http://localhost:8080/health
```

**До локального/публичного развёртывания** замените значения `PAYMENTS_HMAC_SECRET`, `OPS_API_KEY` и пароль PostgreSQL. Примерные секреты в конфигурации предназначены только для разработки. Не коммитьте `.env`.

## Сквозной сценарий

Примеры используют `jq` для извлечения идентификаторов. Все суммы передаются десятичными строками/JSON-числами с точностью до двух знаков.

### 1. Организация и подписка

```bash
ORG=$(curl -fsS http://localhost:8080/api/organizations \
  -H 'Content-Type: application/json' \
  -d '{"name":"Acme Ltd"}')
ORG_ID=$(echo "$ORG" | jq -r .id)

SUB=$(curl -fsS "http://localhost:8080/api/organizations/$ORG_ID/subscriptions" \
  -H 'Content-Type: application/json' \
  -d '{"productCode":"analytics-pro","price":49.90,"currency":"EUR","billingPeriodDays":30}')
SUB_ID=$(echo "$SUB" | jq -r .id)
```

### 2. Счёт с позициями и платёжная попытка

```bash
INV=$(curl -fsS "http://localhost:8080/api/subscriptions/$SUB_ID/invoices" \
  -H 'Content-Type: application/json' \
  -d '{"lines":[{"description":"Analytics Pro — 30 days","quantity":1,"unitPrice":49.90}]}')
INV_ID=$(echo "$INV" | jq -r .id)

PAY=$(curl -fsS -X POST "http://localhost:8080/api/invoices/$INV_ID/payment-attempts")
PAY_ID=$(echo "$PAY" | jq -r .id)
echo "$PAY" | jq '{id,status,amount,currency,checkoutUrl}'
```

`checkoutUrl` — фиктивный URL тестового адаптера. Он не принимает деньги; для сценария нужно отправить тестовый вебхук вручную.

### 3. Подписанный webhook об успешной оплате

Формат тела:

```json
{
  "eventId": "evt-0001",
  "eventType": "payment.succeeded",
  "paymentId": "<PAY_ID>",
  "amount": 49.90,
  "currency": "EUR",
  "occurredAt": "2026-11-17T10:00:00Z"
}
```

Подпись — `sha256=` + lowercase hex от `HMAC-SHA256(secret, raw_request_body)`. Подписываются именно исходные байты тела, без повторной сериализации JSON.

```bash
export PAYMENTS_HMAC_SECRET='change-this-dev-hmac-secret'
PAYLOAD=$(jq -nc --arg paymentId "$PAY_ID" \
  '{eventId:"evt-0001",eventType:"payment.succeeded",paymentId:$paymentId,amount:49.90,currency:"EUR",occurredAt:"2026-11-17T10:00:00Z"}')
SIGNATURE=$(printf '%s' "$PAYLOAD" | openssl dgst -sha256 -hmac "$PAYMENTS_HMAC_SECRET" | awk '{print $2}')

curl -i http://localhost:8080/api/webhooks/test-provider \
  -H 'Content-Type: application/json' \
  -H "X-Test-Signature: sha256=$SIGNATURE" \
  --data-binary "$PAYLOAD"
```

После обработки проверьте:

```bash
curl -fsS "http://localhost:8080/api/subscriptions/$SUB_ID" | jq
curl -fsS "http://localhost:8080/api/organizations/$ORG_ID/entitlements" | jq
curl -fsS "http://localhost:8080/api/invoices/$INV_ID" | jq
```

### 4. Полный возврат

Повторите подпись и отправку для нового `eventId`, того же `paymentId`, полной суммы и `eventType: "payment.refunded"`. После обработки подписка будет `Suspended`, entitlement — `isActive: false`, счёт и платёжная попытка — `Refunded`.

### 5. Операционный журнал и ручной повтор

```bash
export OPS_API_KEY='change-this-dev-ops-key'
curl -fsS http://localhost:8080/api/ops/webhooks \
  -H "X-Ops-Key: $OPS_API_KEY" | jq

curl -i -X POST "http://localhost:8080/api/ops/webhooks/<WEBHOOK_EVENT_ID>/retry" \
  -H "X-Ops-Key: $OPS_API_KEY"
```

Автоматический worker повторяет временные ошибки. Ошибки проверки бизнес-условий (неизвестный платёж, неподдерживаемый event type, сумма/валюта не совпали, неполный возврат) перемещаются в `DeadLetter`. Ops retry сбрасывает счётчик автоматических попыток и запускает событие повторно после исправления причины.

## API

| Метод | Путь | Назначение |
|---|---|---|
| `POST` | `/api/organizations` | Создать организацию |
| `GET` | `/api/organizations/{id}` | Организация и её подписки |
| `POST` | `/api/organizations/{id}/subscriptions` | Создать подписку |
| `GET` | `/api/subscriptions/{id}` | Состояние подписки |
| `POST` | `/api/subscriptions/{id}/invoices` | Выписать счёт и позиции |
| `GET` | `/api/invoices/{id}` | Счёт, позиции и попытки оплаты |
| `POST` | `/api/invoices/{id}/payment-attempts` | Создать платёжную попытку |
| `GET` | `/api/payment-attempts/{id}` | Состояние платёжной попытки |
| `POST` | `/api/webhooks/test-provider` | Принять подписанный webhook |
| `GET` | `/api/organizations/{id}/entitlements` | Доступ к продукту организации |
| `GET` | `/api/ops/webhooks` | Журнал вебхуков (нужен `X-Ops-Key`) |
| `GET` | `/api/ops/audit?take=100` | Общий аудит биллинга (нужен `X-Ops-Key`) |
| `POST` | `/api/ops/webhooks/{id}/retry` | Ручной повтор (нужен `X-Ops-Key`) |
| `GET` | `/health` | Health endpoint |

## Запуск тестов

Нужны .NET 10 SDK и работающий Docker daemon. Testcontainers самостоятельно поднимет PostgreSQL 17 для тестового набора.

```bash
dotnet test SaaSBilling.sln
```

Тесты проверяют конкурентное создание одинаковой подписки, 12 параллельных повторных доставок одного события, гонку `payment.succeeded`/`payment.refunded`, выдачу entitlement ровно один раз, полный возврат, отложенный возврат с ручным восстановлением, позднее повторное списание после возврата и отклонение неверной HMAC-подписи.

## Важные границы этого фрагмента

Это рабочий учебный срез, но не законченный production billing. В частности:

1. Авторизация пользователей/tenant isolation, RBAC, rate limiting и аутентификация ops API должны быть добавлены до внешней эксплуатации. Сейчас ops API защищён отдельным статическим ключом.
2. При старте используется `EnsureCreated` для удобства локального старта. Для эксплуатации замените его на версионируемые EF migrations и отдельный migration step.
3. Период подписки задаётся в днях. Нет планировщика продления/просрочки, dunning, proration, налогов, купонов, нескольких тарифных компонентов и валютной конвертации.
4. Нет проверки статуса оплаты у реального провайдера: тестовый адаптер доверяет корректно подписанным входящим событиям.
5. Событие `payment.refunded` означает полный возврат. Частичный возврат преднамеренно попадает в dead-letter.
6. При двух успешно захваченных попытках по одному счёту вторая отмечается `RequiresManualRefund`; автоматическая компенсация не выполняется.
7. Таблица журналирования не имеет политики retention/архивации, а raw webhook payload сохраняется для расследования. Настройте сроки хранения и редактирование чувствительных данных под свои требования.

## Использованные основные пакеты

- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 — провайдер EF Core 10 для PostgreSQL.
- `Microsoft.EntityFrameworkCore.Design` / `Microsoft.AspNetCore.Mvc.Testing` 10.0.15.
- `Testcontainers.PostgreSql` 4.16.0.
- xUnit 2.9.3.
