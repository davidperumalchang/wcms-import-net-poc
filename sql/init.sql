-- OMITTED (production): migrations, row-level tenant isolation, indexes beyond the primary keys
-- (for example jobs by tenant and status, outbox by status and next_attempt_at), partitioning or
-- archiving of old job_errors/outbox rows, and encryption of anything customer-supplied.
-- One job per import run. "checkpoint" = how many source items are already committed.
CREATE TABLE jobs (
  id          uuid PRIMARY KEY,
  tenant      text NOT NULL,
  source      text NOT NULL,
  status      text NOT NULL,             -- QUEUED | RUNNING | COMPLETED | COMPLETED_WITH_ERRORS | FAILED
  options     jsonb NOT NULL DEFAULT '{}',
  total       int,
  checkpoint  int NOT NULL DEFAULT 0,
  processed   int NOT NULL DEFAULT 0,
  created     int NOT NULL DEFAULT 0,
  updated     int NOT NULL DEFAULT 0,
  unchanged   int NOT NULL DEFAULT 0,
  failed      int NOT NULL DEFAULT 0,
  error       text,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now()
);

-- "Our CMS": the imported content. The key is (tenant, source, source_id), so re-imports are safe.
CREATE TABLE contents (
  tenant      text NOT NULL,
  source      text NOT NULL,
  source_id   text NOT NULL,
  title       text NOT NULL,
  slug        text NOT NULL,
  body        text NOT NULL,
  status      text NOT NULL,
  hash        text NOT NULL,             -- fingerprint, used to skip unchanged items
  job_id      uuid NOT NULL,             -- last job that created or changed this item
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant, source, source_id),
  UNIQUE (tenant, slug)
);

CREATE TABLE job_errors (
  id         serial PRIMARY KEY,
  job_id     uuid NOT NULL,
  source_id  text NOT NULL,
  level      text NOT NULL,              -- error | warning
  message    text NOT NULL
);

-- The outbox: written in the same transaction that closes the job.
-- The notifier polls this table. UNIQUE(job_id) means one event per job, even if the close step is retried.
CREATE TABLE outbox (
  id               text PRIMARY KEY,
  job_id           uuid NOT NULL UNIQUE,
  payload          jsonb NOT NULL,
  status           text NOT NULL DEFAULT 'pending',   -- pending | sent | dead
  attempts         int NOT NULL DEFAULT 0,
  next_attempt_at  timestamptz NOT NULL DEFAULT now(),
  last_error       text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  sent_at          timestamptz
);
