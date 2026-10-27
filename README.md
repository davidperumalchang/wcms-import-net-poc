# WCMS Content Import: Prototype (.NET)

A working prototype of the content import facility, built with .NET 10, Postgres and Redis. A customer starts an import, pages are copied from their current CMS into ours, and an upstream system (search, CDN, analytics) is notified once the content is saved.

The design, assumptions and trade-offs are in [docs/DESIGN.md](docs/DESIGN.md). This file covers how to run and use the prototype.

## What it does

- Imports pages from a source CMS in batches, in a background job.
- Validates each page. It rejects pages that cannot be used, fixes small problems, and records both in an error report.
- Saves pages so that re-running an import is safe: unchanged pages are skipped and edited pages are updated.
- Sends one signed webhook per import to an upstream system, with retries. The notification is only created when the content is saved.

## Architecture

![Architecture diagram](docs/Architecture.png)

*How the boxes map to containers:*
- **Import API** is `api`
- **Import Worker** is `importer` (`ImportService` is its loop that reads the stream and `ImportWorker` runs each job)
- **Notification service** is `notifier`
- **Source CMS** is `mock-cms`
- **Upstream** is `mock-upstream`. "Update job queue status" is the notifier setting the outbox row to `sent`, `pending` (retry) or `dead`.

In short: the Import API records a job in Postgres and puts its id on a Redis Stream. The Import Worker takes the job, reads pages from the Source CMS through a connector, validates them and saves them to Postgres in batches. A final transaction closes the job and writes an outbox row. The Notification service polls that outbox and sends a signed webhook to the upstream.

There is one executable, `WcmsImport`, built into one image. Each container starts it with a different role: `api`, `importer`, `notifier`, `mock-cms` or `mock-upstream` (see `Program.cs`). Paths below are relative to `src/WcmsImport/`.

| Service | Code | Role |
|---|---|---|
| `api` (Import API) | `Api/ImportApi.cs` | Create jobs, enqueue them, report status, errors and content |
| `redis` | `Common/ImportQueue.cs` | Queue (a Redis Stream) that hands each job to one importer |
| `importer` (Import Worker) | `Importer/ImportService.cs` | Takes jobs off the stream and runs them |
| | `Importer/ImportWorker.cs` | Runs one job: read, transform, validate, save, close |
| | `Importer/Connectors.cs` | One mapper per source CMS, to a standard page shape |
| | `Importer/Validation.cs`, `Importer/JobRules.cs` | Pure rules: validation, status, notify decision |
| `notifier` (Notification service) | `Notifier/NotificationService.cs`, `Notifier/Webhook.cs` | Polls the outbox, signs and sends events, retries |
| `postgres` | `sql/init.sql` | Jobs, content, errors and the outbox |
| `mock-cms` (Source CMS) | `Mock/MockCms.cs` | Fake source CMS with 50 pages, some deliberately bad |
| `mock-upstream` (Upstream) | `Mock/MockUpstream.cs` | Fake upstream that verifies signatures and ignores repeated events |

### How an import runs

1. `POST /imports` writes a job row (`QUEUED`), adds the job id to the Redis Stream, and returns `202` with a job id.
2. The Import Worker reads the job from the stream using a consumer group. It acknowledges the message only after the job is closed in the database.
3. The Import Worker reads pages in batches of 10. Each batch is saved in one transaction together with the job's checkpoint and counters, so a failed batch leaves nothing half-saved and a restarted job continues from the checkpoint.
4. When all batches are done, one more transaction sets the final job status and inserts the outbox row.
5. The Notification service polls the outbox, signs the event (HMAC-SHA256) and sends it to the upstream. Failed sends are retried after 2, 4, 8 and 16 seconds, after which the row is marked `dead`.

## Requirements

- Docker with Compose
- .NET 10 SDK, for the tests and for building outside Docker

## Run it

Create a `.env` file from the template. Docker Compose reads it automatically and will not start without `POSTGRES_PASSWORD` and `WEBHOOK_SECRET`.

```bash
cp .env.example .env
```

Then replace the `POSTGRES_PASSWORD` placeholder in `.env` with a password of your own.

Start the stack:

```bash
docker compose up --build -d
```

In a second terminal:

```bash
./scripts/demo.sh happy
```

Stop and delete all data:

```bash
docker compose down -v
```

## Ports

| Port | Service |
|---|---|
| 3000 | API |
| 4000 | Mock upstream |
| 5000 | Mock source CMS |
| 5434 | Postgres (user and database are `wcms`, password is `POSTGRES_PASSWORD` from `.env`) |

## API

| Request | Purpose |
|---|---|
| `POST /imports` | Start an import. Body: `{"source": "wordpress" \| "flat"}`. Returns `202` and a `jobId`. |
| `GET /imports/{id}` | Job status, counts, checkpoint and notification status |
| `GET /imports/{id}/errors` | Per-item errors and warnings |
| `GET /content?importJobId={id}` | Pages saved by that job. This is the link in the notification. |
| `GET /outbox` | Contents of the outbox table (for inspection) |

Job status: `QUEUED`, `RUNNING`, `COMPLETED`, `COMPLETED_WITH_ERRORS`, `FAILED`.
Outbox status: `pending`, `sent`, `dead`.

Example:

```bash
curl -X POST localhost:3000/imports -H 'Content-Type: application/json' -d '{"source":"wordpress"}'
curl localhost:3000/imports/<jobId>
curl localhost:4000/received        # what the upstream received
```

### Notification

```
POST <UPSTREAM_URL>
X-WCMS-Event-Id:   evt_1a2b3c4d
X-WCMS-Attempt:    1
X-WCMS-Signature:  sha256=<HMAC of the body>

{
  "id": "evt_1a2b3c4d",
  "type": "content.import.completed",
  "version": "1.0",
  "occurredAt": "2026-01-01T09:00:00.000Z",
  "tenant": "acme",
  "data": {
    "jobId": "…",
    "jobStatus": "COMPLETED_WITH_ERRORS",
    "summary": { "created": 44, "updated": 0, "unchanged": 0, "failed": 6 },
    "changedContentUrl": "/content?importJobId=…"
  }
}
```

The event carries counts and a link, not the content. Receivers should ignore an event id they have already processed, because delivery is at-least-once.

## Demo scenarios

`./scripts/demo.sh <scenario>` runs one scenario and prints the job and what the upstream received. Watch `docker compose logs -f importer notifier` alongside.

| Scenario | What it shows |
|---|---|
| `happy` | A first import: pages created, some rejected (no title, body too large), some fixed with a warning (script tag removed, duplicate slug renamed). One notification is delivered. |
| `rerun` | The same import again. Everything is `unchanged` and no new notification is created. |
| `edit` | Four pages are edited in the source. Only those are `updated`, and one notification is sent. |
| `flat` | The same pages from a second source with a different data shape. |
| `flaky` | The upstream returns 503 twice. The notifier retries and then delivers. |
| `rollback` | Batch 3 fails once after writing its pages. It is rolled back and retried, with no duplicates. |
| `closefail` | The final transaction (job status and outbox row) fails twice. It rolls back whole and is retried, and the job still ends `COMPLETED` with one notification. |
| `crash` | The importer process dies after batch 2. Docker restarts it and it resumes from the checkpoint (`RESUMING job … at item 20` in the log). |

Run `rerun` after `happy`: the database keeps its content between runs. Use `docker compose down -v` to start clean.

The `rollback` and `crash` scenarios use job options (`failBatch`, `crashAfterBatch`, `lostReplyBatch` for a lost commit reply, and `failClose` for a failing closing transaction). They exist to demonstrate failures and are defined in `Importer/Faults.cs`.

## Configuration

Set in `docker-compose.yml`, except `POSTGRES_PASSWORD` and `WEBHOOK_SECRET`, which are read from `.env`. Configuration is standard .NET configuration, so every value can also be set in `appsettings.json` or on the command line.

| Variable | Default | Used by | Meaning |
|---|---|---|---|
| `POSTGRES_PASSWORD` | | postgres, and the Postgres connection string | Postgres password. Set in `.env`; there is no default. |
| `WEBHOOK_SECRET` | | notifier, mock-upstream | Shared secret used to sign events. Set in `.env`. |
| `ConnectionStrings__Postgres` | | api, importer, notifier | Npgsql connection string, built from `POSTGRES_PASSWORD` |
| `ConnectionStrings__Redis` | | api, importer | StackExchange.Redis connection string |
| `SOURCE_BASE_URL` | | importer | Base URL of the source CMS |
| `UPSTREAM_URL` | | notifier | Where notifications are sent |
| `BATCH_SIZE` | 10 | importer | Pages per batch (and per transaction) |
| `CLAIM_IDLE_MS` | 8000 | importer | How long a job may sit unacknowledged before another importer claims it |
| `COUNT` | 50 | mock-cms | Number of pages in the fake source |
| `PORT` | 3000 / 4000 / 5000 | api / mock-upstream / mock-cms | HTTP port |

## Tests

Unit tests need no Docker:

```bash
dotnet test tests/WcmsImport.UnitTests
```

They cover validation, content fingerprint, slug uniqueness, source mapping, final job status, the notify decision, the checkpoint decision, event shape, webhook signing and the retry schedule.

End-to-end tests run against the running stack and need an empty database:

```bash
docker compose down -v && docker compose up --build -d
dotnet test tests/WcmsImport.E2ETests
```

They run in order (each builds on the content the last one saved) and cover a first import, single delivery of the notification, a silent re-run, updating only edited pages, rollback and retry of a failed batch, a batch retried after its commit had succeeded, a failing closing transaction that is retried without marking the job failed, notifier retry during an upstream outage, and a rejected request. The crash and resume case is run manually with `./scripts/demo.sh crash`.

Running `dotnet test` on the whole solution runs both projects, so the end-to-end tests fail if the stack is not up.

## Scope

This is a prototype. The following are deliberately left out, and each is listed in an `OMITTED` comment at the top of the file where it would be built: authentication and multiple tenants, media and file import, custom field mapping, a cancel endpoint, a registry of multiple subscribers, replay of dead notifications, metrics and alerting, and database migrations. See "Production gaps" in [docs/DESIGN.md](docs/DESIGN.md) for the full list.

## Project layout

```
WcmsImport.slnx                 solution
docker-compose.yml              services
.env.example                    template for .env (copy it and set the Postgres password)
Dockerfile                      one image; each service passes its role as the command
sql/init.sql                    database schema
src/WcmsImport/
  Program.cs                    picks the role from the first argument
  Api/                          Import API
  Importer/                     import service, worker, connectors, rules, fault switches
  Notifier/                     notification service and webhook rules
  Mock/                         fake source CMS and fake upstream
  Common/                       configuration, database helpers, Redis stream
tests/WcmsImport.UnitTests/     unit tests
tests/WcmsImport.E2ETests/      end-to-end tests
scripts/demo.sh                 demo scenarios
docs/DESIGN.md                  design, assumptions, decisions, failure handling
docs/Architecture.png           architecture diagram
docs/Sequence_Diagram.png       sequence of one import
docs/ERD.png                    data model
```
