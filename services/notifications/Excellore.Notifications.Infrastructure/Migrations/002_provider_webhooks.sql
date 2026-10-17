ALTER TABLE notification_deliveries
  ADD COLUMN provider_correlation_id uuid NOT NULL DEFAULT gen_random_uuid(),
  ADD COLUMN provider_status text,
  ADD COLUMN provider_email_id uuid,
  ADD COLUMN provider_updated_at timestamptz,
  ADD CONSTRAINT notification_deliveries_provider_status_check CHECK (
    provider_status IS NULL OR provider_status IN
      ('sent','delivered','delayed','bounced','complained','failed','suppressed')
  );
CREATE UNIQUE INDEX notification_deliveries_provider_correlation_idx
  ON notification_deliveries (provider_correlation_id);

CREATE TABLE notification_provider_index (        -- [P]
  correlation_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  delivery_id uuid NOT NULL UNIQUE,
  created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO notification_provider_index (correlation_id, tenant_id, delivery_id)
SELECT provider_correlation_id, tenant_id, id FROM notification_deliveries;

CREATE TABLE provider_webhook_receipts (          -- [P]
  provider text NOT NULL CHECK (provider IN ('resend')),
  event_id text NOT NULL CHECK (
    length(event_id) BETWEEN 3 AND 160 AND event_id ~ '^[A-Za-z0-9._-]+$'
  ),
  received_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (provider, event_id)
);

COMMENT ON TABLE notification_provider_index IS
  'Class P: immutable opaque provider correlation to tenant delivery routing index; contains no personal data';
COMMENT ON TABLE provider_webhook_receipts IS
  'Class P: signed provider webhook idempotency markers without payloads or personal data';
COMMENT ON COLUMN notification_deliveries.provider_correlation_id IS
  'Random opaque email Message-ID correlation; contains no tenant, user, or delivery identifier';

REVOKE ALL ON notification_provider_index, provider_webhook_receipts FROM PUBLIC;
REVOKE ALL ON notification_deliveries FROM notifications_app;
REVOKE INSERT, UPDATE ON notification_deliveries FROM notifications_worker;
GRANT SELECT, INSERT ON notification_provider_index TO notifications_worker;
GRANT INSERT (id, tenant_id, event_id, user_id, template_key, channel, status,
  provider_correlation_id) ON notification_deliveries TO notifications_worker;
GRANT UPDATE (status, attempts, failure_code, lease_until, sent_at, updated_at)
  ON notification_deliveries TO notifications_worker;
GRANT SELECT ON notification_provider_index TO notifications_app;
GRANT SELECT, INSERT ON provider_webhook_receipts TO notifications_app;
GRANT SELECT (id, tenant_id, provider_correlation_id, provider_status, provider_updated_at)
  ON notification_deliveries TO notifications_app;
GRANT UPDATE (provider_status, provider_email_id, provider_updated_at)
  ON notification_deliveries TO notifications_app;
