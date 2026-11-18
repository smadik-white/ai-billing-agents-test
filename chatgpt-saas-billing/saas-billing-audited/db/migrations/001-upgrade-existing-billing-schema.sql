-- Upgrade the schema created by the original SaaS Billing sample to the audited billing model.
-- Run once against the existing PostgreSQL database before deploying the new application version:
--   psql "$BILLING_DB_CONNECTION" -v ON_ERROR_STOP=1 -f db/migrations/001-upgrade-existing-billing-schema.sql
-- The script is idempotent and preserves existing organizations, subscriptions, invoices,
-- payment attempts, webhook events, entitlements, and audit rows.

BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

ALTER TABLE "Subscriptions"
    ADD COLUMN IF NOT EXISTS "CancelledAtUtc" timestamp with time zone NULL;

DO $$
DECLARE
    had_billing_period_days boolean;
BEGIN
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'Invoices'
          AND column_name = 'BillingPeriodDays'
    ) INTO had_billing_period_days;

    ALTER TABLE "Invoices"
        ADD COLUMN IF NOT EXISTS "AmountPaid" numeric(18,2) NOT NULL DEFAULT 0,
        ADD COLUMN IF NOT EXISTS "AmountRefunded" numeric(18,2) NOT NULL DEFAULT 0,
        ADD COLUMN IF NOT EXISTS "BillingPeriodDays" integer NOT NULL DEFAULT 30;

    IF NOT had_billing_period_days THEN
        UPDATE "Invoices" AS i
        SET "BillingPeriodDays" = s."BillingPeriodDays"
        FROM "Subscriptions" AS s
        WHERE s."Id" = i."SubscriptionId";
    END IF;
END $$;

-- Reconstruct gross captures/refunds from the original attempt state before enabling aggregates.
UPDATE "Invoices" AS i
SET "AmountPaid" = COALESCE((
        SELECT SUM(p."Amount")
        FROM "PaymentAttempts" AS p
        WHERE p."InvoiceId" = i."Id"
          AND p."Status" IN ('Succeeded', 'Refunded')
    ), CASE WHEN i."Status" IN ('Paid', 'Refunded') THEN i."TotalAmount" ELSE 0 END),
    "AmountRefunded" = COALESCE((
        SELECT SUM(p."Amount")
        FROM "PaymentAttempts" AS p
        WHERE p."InvoiceId" = i."Id"
          AND p."Status" = 'Refunded'
    ), CASE WHEN i."Status" = 'Refunded' THEN i."TotalAmount" ELSE 0 END)
WHERE i."AmountPaid" = 0 AND i."AmountRefunded" = 0;

ALTER TABLE "PaymentAttempts"
    ALTER COLUMN "ProviderPaymentId" DROP NOT NULL,
    ALTER COLUMN "CheckoutUrl" DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS "IdempotencyKey" varchar(200),
    ADD COLUMN IF NOT EXISTS "RequestFingerprint" varchar(64),
    ADD COLUMN IF NOT EXISTS "RefundedAmount" numeric(18,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "ProviderCreateAttempts" integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "ProviderRetryAfterUtc" timestamp with time zone NULL,
    ADD COLUMN IF NOT EXISTS "ProviderLastError" varchar(2000) NULL,
    ADD COLUMN IF NOT EXISTS "CapturedAtUtc" timestamp with time zone NULL;

UPDATE "PaymentAttempts"
SET "IdempotencyKey" = 'legacy-' || "Id"::text
WHERE "IdempotencyKey" IS NULL OR "IdempotencyKey" = '';

UPDATE "PaymentAttempts"
SET "RequestFingerprint" = repeat('0', 64)
WHERE "RequestFingerprint" IS NULL OR "RequestFingerprint" = '';

UPDATE "PaymentAttempts"
SET "RefundedAmount" = "Amount"
WHERE "Status" = 'Refunded' AND "RefundedAmount" = 0;

UPDATE "PaymentAttempts"
SET "CapturedAtUtc" = "UpdatedAtUtc"
WHERE "Status" IN ('Succeeded', 'Refunded') AND "CapturedAtUtc" IS NULL;

ALTER TABLE "PaymentAttempts"
    ALTER COLUMN "IdempotencyKey" SET NOT NULL,
    ALTER COLUMN "RequestFingerprint" SET NOT NULL;

ALTER TABLE "WebhookEvents"
    ADD COLUMN IF NOT EXISTS "PayloadHash" varchar(64),
    ADD COLUMN IF NOT EXISTS "ProviderRefundId" varchar(200) NULL;

UPDATE "WebhookEvents"
SET "PayloadHash" = encode(digest(convert_to("RawPayload", 'UTF8'), 'sha256'), 'hex')
WHERE "PayloadHash" IS NULL OR "PayloadHash" = '';

ALTER TABLE "WebhookEvents"
    ALTER COLUMN "PayloadHash" SET NOT NULL;

CREATE TABLE IF NOT EXISTS "PaymentRefunds" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "PaymentAttemptId" uuid NOT NULL,
    "Provider" varchar(50) NOT NULL,
    "ProviderRefundId" varchar(200) NOT NULL,
    "Amount" numeric(18,2) NOT NULL,
    "Currency" character(3) NOT NULL,
    "WebhookEventId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "FK_PaymentRefunds_PaymentAttempts_PaymentAttemptId"
        FOREIGN KEY ("PaymentAttemptId") REFERENCES "PaymentAttempts"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_PaymentRefunds_Amount_Positive" CHECK ("Amount" > 0)
);

CREATE TABLE IF NOT EXISTS "SubscriptionPeriodGrants" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "SubscriptionId" uuid NOT NULL,
    "InvoiceId" uuid NOT NULL,
    "IsActive" boolean NOT NULL,
    "PeriodStartUtc" timestamp with time zone NOT NULL,
    "PeriodEndUtc" timestamp with time zone NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "FK_SubscriptionPeriodGrants_Subscriptions_SubscriptionId"
        FOREIGN KEY ("SubscriptionId") REFERENCES "Subscriptions"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_SubscriptionPeriodGrants_Period" CHECK ("PeriodEndUtc" > "PeriodStartUtc")
);

CREATE TABLE IF NOT EXISTS "WebhookConflicts" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Provider" varchar(50) NOT NULL,
    "ProviderEventId" varchar(200) NOT NULL,
    "ExistingWebhookEventId" uuid NULL,
    "ExistingPayloadHash" varchar(64) NOT NULL,
    "IncomingPayloadHash" varchar(64) NOT NULL,
    "IncomingRawPayload" text NOT NULL,
    "Reason" varchar(500) NOT NULL,
    "ReceivedAtUtc" timestamp with time zone NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentAttempts_InvoiceId_IdempotencyKey"
    ON "PaymentAttempts" ("InvoiceId", "IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_PaymentAttempts_Status_ProviderRetryAfterUtc"
    ON "PaymentAttempts" ("Status", "ProviderRetryAfterUtc");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentRefunds_Provider_ProviderRefundId"
    ON "PaymentRefunds" ("Provider", "ProviderRefundId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionPeriodGrants_InvoiceId"
    ON "SubscriptionPeriodGrants" ("InvoiceId");
CREATE INDEX IF NOT EXISTS "IX_SubscriptionPeriodGrants_SubscriptionId_IsActive_PeriodEndUtc"
    ON "SubscriptionPeriodGrants" ("SubscriptionId", "IsActive", "PeriodEndUtc");
CREATE INDEX IF NOT EXISTS "IX_WebhookConflicts_Provider_ProviderEventId_ReceivedAtUtc"
    ON "WebhookConflicts" ("Provider", "ProviderEventId", "ReceivedAtUtc");

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Invoices_Amounts_NonNegative') THEN
        ALTER TABLE "Invoices" ADD CONSTRAINT "CK_Invoices_Amounts_NonNegative"
            CHECK ("TotalAmount" > 0 AND "AmountPaid" >= 0 AND "AmountRefunded" >= 0 AND "AmountRefunded" <= "AmountPaid");
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Invoices_Period_Positive') THEN
        ALTER TABLE "Invoices" ADD CONSTRAINT "CK_Invoices_Period_Positive"
            CHECK ("BillingPeriodDays" BETWEEN 1 AND 366);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_PaymentAttempts_Amounts_NonNegative') THEN
        ALTER TABLE "PaymentAttempts" ADD CONSTRAINT "CK_PaymentAttempts_Amounts_NonNegative"
            CHECK ("Amount" > 0 AND "RefundedAmount" >= 0 AND "RefundedAmount" <= "Amount");
    END IF;
END $$;

-- The old model persisted only the current subscription window. Reconstruct its current grant
-- for active subscriptions; historical period boundaries were not stored in the old schema.
INSERT INTO "SubscriptionPeriodGrants" (
    "Id", "SubscriptionId", "InvoiceId", "IsActive", "PeriodStartUtc", "PeriodEndUtc", "CreatedAtUtc")
SELECT gen_random_uuid(), s."Id", i."Id", TRUE, s."CurrentPeriodStartUtc", s."CurrentPeriodEndUtc", now()
FROM "Subscriptions" AS s
JOIN LATERAL (
    SELECT i0."Id"
    FROM "Invoices" AS i0
    WHERE i0."SubscriptionId" = s."Id" AND i0."Status" = 'Paid'
    ORDER BY i0."CreatedAtUtc" DESC
    LIMIT 1
) AS i ON TRUE
WHERE s."Status" = 'Active'
  AND s."CurrentPeriodStartUtc" IS NOT NULL
  AND s."CurrentPeriodEndUtc" IS NOT NULL
ON CONFLICT ("InvoiceId") DO NOTHING;

COMMIT;
