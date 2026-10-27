#!/usr/bin/env bash
# Walks through the demo scenarios. Run the stack first:  docker compose up --build
# Usage: ./scripts/demo.sh <scenario>
set -euo pipefail
API=http://localhost:3000
UP=http://localhost:4000
CMS=http://localhost:5000

start() { curl -s -X POST "$API/imports" -H 'Content-Type: application/json' -d "$1" | tee /dev/stderr | sed -n 's/.*"jobId": "\([^"]*\)".*/\1/p'; }
wait_job() {
  for _ in $(seq 1 90); do
    s=$(curl -s "$API/imports/$1" | grep -oE '"status": "(COMPLETED[A-Z_]*|FAILED)"' | head -1 || true)
    [ -n "$s" ] && { echo "job finished: $s"; return; }
    sleep 1
  done; echo "timed out waiting for job"
}
show() { echo "--- job"; curl -s "$API/imports/$1"; echo "--- upstream received"; curl -s "$UP/received"; }

case "${1:-}" in
  happy)      # normal import, notification delivered once
    id=$(start '{"source":"wordpress"}'); wait_job "$id"; sleep 3; show "$id"
    echo "--- errors/warnings"; curl -s "$API/imports/$id/errors" ;;
  rerun)      # same import again: everything unchanged, NO new event
    id=$(start '{"source":"wordpress"}'); wait_job "$id"; sleep 2; show "$id" ;;
  edit)       # edit 4 pages in the old CMS, import again: only those are "updated"
    curl -s -X POST "$CMS/edit"; echo
    id=$(start '{"source":"wordpress"}'); wait_job "$id"; sleep 3; show "$id" ;;
  flaky)      # upstream down for 2 deliveries: notifier retries, then succeeds
    curl -s -X POST "$CMS/edit"; echo            # make sure something changed, so there IS a notification
    curl -s -X POST "$UP/fail?next=2"
    id=$(start '{"source":"wordpress"}'); wait_job "$id"; sleep 12; show "$id"; echo "--- outbox"; curl -s "$API/outbox" ;;
  closefail)  # the final (closing) transaction fails twice: it rolls back whole, is retried, and the job still completes
    curl -s -X POST "$CMS/edit"; echo
    id=$(start '{"source":"wordpress","options":{"failClose":2}}'); wait_job "$id"; sleep 3; show "$id"; echo "now read: docker compose logs importer" ;;
  flat)       # a second source system with a different data shape
    id=$(start '{"source":"flat"}'); wait_job "$id"; sleep 3; show "$id" ;;
  rollback)   # batch 3 fails once (simulated deadlock) after writing, is rolled back, retried, succeeds
    id=$(start '{"source":"wordpress","options":{"failBatch":3}}'); wait_job "$id"; echo "now read: docker compose logs importer" ;;
  crash)      # importer process dies after batch 2; Docker restarts it; it resumes at checkpoint 20
    id=$(start '{"source":"wordpress","options":{"crashAfterBatch":2}}'); wait_job "$id"; sleep 3; show "$id"; echo "now read: docker compose logs importer" ;;
  *) echo "scenarios: happy | rerun | edit | flaky | flat | rollback | closefail | crash"; exit 1 ;;
esac
echo
