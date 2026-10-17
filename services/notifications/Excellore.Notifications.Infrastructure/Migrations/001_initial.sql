CREATE TABLE inbox (                         -- [P]
  event_id uuid PRIMARY KEY,
  received_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE notification_deliveries (       -- [T]
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  event_id uuid NOT NULL UNIQUE,
  user_id uuid NOT NULL,
  template_key text NOT NULL CHECK (template_key ~ '^[a-z0-9][a-z0-9-]{2,80}$'),
  channel text NOT NULL CHECK (channel IN ('email','sms','push','in_app')),
  status text NOT NULL CHECK (status IN ('pending','sending','sent','failed','suppressed')),
  attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 3),
  failure_code text CHECK (failure_code IS NULL OR failure_code ~ '^[a-z0-9][a-z0-9-]{1,63}$'),
  lease_until timestamptz,
  sent_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX notification_deliveries_pending_idx
  ON notification_deliveries (tenant_id, status, created_at);

ALTER TABLE notification_deliveries ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification_deliveries FORCE ROW LEVEL SECURITY;
CREATE POLICY notification_deliveries_tenant_policy ON notification_deliveries
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

COMMENT ON TABLE inbox IS 'Class P: idempotency markers contain opaque event IDs only';
COMMENT ON TABLE notification_deliveries IS 'Class T: tenant notification delivery state without recipient PII or rendered content';

REVOKE ALL ON inbox, notification_deliveries FROM PUBLIC;
GRANT SELECT, INSERT ON inbox TO notifications_worker;
GRANT SELECT, INSERT, UPDATE ON notification_deliveries TO notifications_worker;
