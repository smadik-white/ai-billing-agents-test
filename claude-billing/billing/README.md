# Billing fragment for a SaaS platform

ASP.NET Core 10 · EF Core · PostgreSQL · Docker Compose · xUnit + Testcontainers.
Платёжный провайдер — тестовый адаптер с HMAC-подписью (`TestPay`).

## Запуск

```bash
docker compose up --build        # API: http://localhost:8080, PostgreSQL: localhost:5432
dotnet test                      # нужен запущенный Docker: Testcontainers поднимает свой PostgreSQL
```

Секрет подписи вебхуков: `WEBHOOK_SECRET` (по умолчанию `dev-webhook-secret`).

## Структура

```
src/Billing.Api
  Domain/Entities.cs              сущности и статусы
  Data/BillingDbContext.cs        модель, уникальные индексы (основа идемпотентности)
  Payments/TestPay.cs             IPaymentProvider (заглушка), формат вебхука, HMAC-подпись
  Services/BillingService.cs      организации, подписки, счета, платёжные попытки, проверка доступа
  Services/WebhookProcessor.cs    приём → inbox → транзакционная обработка → журнал ошибок → recovery
  Services/WebhookEventApplier.cs бизнес-эффекты событий (оплата, неудача, полный возврат)
  Services/WebhookRecoveryService.cs  фоновый повтор необработанных событий
  Endpoints.cs / Program.cs
tests/Billing.Tests               интеграционные тесты на реальном PostgreSQL
```

## API

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/api/plans` | тариф (`code, name, productCode, priceMinor, currency, periodDays`) |
| POST | `/api/organizations` | организация |
| POST | `/api/subscriptions` | подписка в статусе `Pending` (`organizationId, planCode`) |
| POST | `/api/invoices` | счёт; без `items` и с `subscriptionId` создаётся позиция по цене тарифа |
| GET  | `/api/invoices/{id}` | счёт с позициями |
| POST | `/api/invoices/{id}/payment-attempts` | платёжная попытка (заголовок `Idempotency-Key` опционален) |
| POST | `/api/webhooks/testpay` | вебхук провайдера |
| GET  | `/api/organizations/{id}/access/{productCode}` | есть ли доступ к продукту |
| GET  | `/api/subscriptions/{id}` | подписка |
| GET/POST | `/api/admin/webhook-events`, `/api/admin/webhook-events/recover` | журнал вебхуков / «повторить сейчас» (без авторизации — только для упражнения) |

Деньги — `long` в минимальных единицах (копейки), валюта — 3 буквы.

## Формат вебхука и подписи

```
X-TestPay-Signature: t=<unix-секунды>,v1=<hex(HMAC-SHA256(secret, "<t>.<сырое тело>"))>
```

Тело: `{"event_id","type","payment_id","amount","currency","refund_id","reason"}`,
`type` ∈ `payment.succeeded | payment.failed | refund.succeeded`.
Окно допуска по времени — 5 минут, сравнение подписи — `FixedTimeEquals`.

Ручной вызов:

```bash
BODY='{"event_id":"evt_1","type":"payment.succeeded","payment_id":"<tp_...>","amount":99000,"currency":"RUB"}'
TS=$(date +%s); SECRET=dev-webhook-secret
SIG=$(printf '%s.%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | sed 's/^.* //')
curl -i -X POST localhost:8080/api/webhooks/testpay -H "X-TestPay-Signature: t=$TS,v1=$SIG" -d "$BODY"
```

Ответы: `200` — processed / duplicate / rejected (повтор от провайдера бессмыслен);
`202` — событие сохранено, но применить пока нельзя, повтор берём на себя;
`401` — неверная подпись (ничего не сохраняется); `400` — битое тело.

## Как обеспечена надёжность

**Идемпотентность.** Каждое подписанное событие пишется в таблицу `WebhookEvents` с уникальным
`ProviderEventId`. Повторная доставка упирается в unique-индекс и не создаёт второй записи.

**Конкурентность.** Обработка идёт в одной транзакции, которая сначала берёт `SELECT … FOR UPDATE`
на строку события, затем — строго в одном порядке — на попытку платежа → счёт → подписку.
Поэтому:
- 20 параллельных доставок одного события: одна обрабатывает, остальные ждут блокировку и видят `Processed`;
- разные `event_id` для одного платежа: вторая транзакция видит `Succeeded` и ничего не делает;
- два разных платежа по одному счёту: счёт оплачивается один раз, второй платёж фиксируется как
  `DuplicatePayment` в аудите (нужен ручной возврат), подписка не продлевается дважды.
Единый порядок блокировок исключает взаимные блокировки.

**Машина состояний платежа.** `Pending → Succeeded → Refunded`, `Pending|Failed → Succeeded`.
Запоздавший `payment.failed` после успеха состояние не понижает.

**Активация и доступ.** Счёт `Open → Paid` ровно один раз; если он привязан к подписке, то подписка
`Pending → Active` на `PeriodDays` (продление идёт от конца текущего периода), а `ProductAccess`
создаётся или продлевается (уникален по организации и продукту).

**Полный возврат.** Попытка → `Refunded`, запись в `Refunds`, счёт → `Refunded`, подписка → `Cancelled`,
доступ отзывается. Частичный возврат, несовпадение суммы/валюты и повторное использование `refund_id` —
постоянная ошибка (`Rejected`), состояние не меняется.

**Журналирование и восстановление.** Любая ошибка откатывает бизнес-изменения, а событие
помечается `Failed` с текстом ошибки, счётчиком попыток и временем следующей (экспоненциальный backoff
5 с … 10 мин). `WebhookRecoveryService` повторяет `Received`/`Failed` события — это покрывает
падение процесса между сохранением и обработкой, а также события не по порядку (возврат раньше оплаты).
После `MaxWebhookAttempts` событие уходит в `DeadLetter`. Бизнес-переходы пишутся в `AuditLogs`.
Обработка не прерывается отменой HTTP-запроса: событие уже сохранено.

## Допущения и ограничения

- Схема создаётся `EnsureCreated`; для продакшена — миграции EF (`dotnet ef migrations add Initial`).
- Только полный возврат; возврат оплаты продления отменяет подписку целиком.
- Вызов провайдера при создании попытки идёт до записи в БД; при сбое записи остаётся «осиротевший»
  платёж на стороне провайдера (для заглушки безразлично, для реального провайдера нужна сверка).
- Нет аутентификации/авторизации API и admin-эндпоинтов.
- Версии пакетов в `.csproj` плавающие (`10.0.*`, `4.*`) — для воспроизводимости зафиксируйте их.
