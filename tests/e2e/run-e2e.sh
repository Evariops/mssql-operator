#!/usr/bin/env bash
# Smoke test: install the chart on a kind cluster, provision a minimal
# Express SqlInstance and assert the operator's core contracts end to end.
# Expects: kind cluster running, image loaded, kubectl + helm contexts set.
set -euo pipefail

NS=mssql-e2e
RELEASE=mssql-operator
OPERATOR_NS=mssql-operator
MANIFESTS="$(cd "$(dirname "$0")" && pwd)"

log() { printf '\n=== %s ===\n' "$*"; }

fail() { echo "FAIL: $*" >&2; exit 1; }

log "Install chart (breakout enabled, fencingDriver=none — exercises service startup)"
helm install "$RELEASE" charts/mssql-operator \
  --namespace "$OPERATOR_NS" --create-namespace \
  --set image.repository=mssqloperator \
  --set image.tag=e2e \
  --set image.pullPolicy=Never \
  --set breakout.enabled=true \
  --set breakout.fencingDriver=none \
  --wait --timeout 3m

kubectl -n "$OPERATOR_NS" rollout status deploy --timeout=120s

log "Assertion 4a: APIService view.mssql-operator.evariops.com becomes Available"
kubectl wait --for=condition=Available apiservice/v1alpha1.view.mssql-operator.evariops.com --timeout=180s

log "Assertion 4b: aggregated API answers (empty list)"
kubectl get agreplicastates -A

log "Assertion 2: webhook rejects an invalid spec (edition: Banana)"
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
if kubectl apply -f "$MANIFESTS/invalid-instance.yaml" 2>/tmp/webhook-err; then
  fail "invalid SqlInstance was accepted"
fi
grep -qi "edition" /tmp/webhook-err || fail "rejection message does not mention edition: $(cat /tmp/webhook-err)"
echo "rejected as expected: $(cat /tmp/webhook-err)"

log "Apply minimal Express SqlInstance"
kubectl apply -f "$MANIFESTS/instance.yaml"

log "Assertion 1: StatefulSet created and pod Ready (generous timeout, image pull included)"
for i in $(seq 1 60); do
  kubectl -n "$NS" get statefulset sql-e2e >/dev/null 2>&1 && break
  sleep 5
done
kubectl -n "$NS" get statefulset sql-e2e || fail "StatefulSet sql-e2e was never created"
kubectl -n "$NS" rollout status statefulset/sql-e2e --timeout=600s
kubectl -n "$NS" wait --for=condition=Ready pod/sql-e2e-0 --timeout=120s

log "Assertion 1b: the operator reaches the SqlInstance to phase=Running"
# Catches a broken operator→SQL connection even when the pod itself is Ready — e.g. the
# Globalization Invariant Mode failure that the plain (no-ICU) chiseled image caused.
for i in $(seq 1 40); do
  [ "$(kubectl -n "$NS" get sqlinstance sql-e2e -o jsonpath='{.status.phase}' 2>/dev/null)" = "Running" ] && break
  sleep 3
done
PHASE=$(kubectl -n "$NS" get sqlinstance sql-e2e -o jsonpath='{.status.phase}' 2>/dev/null)
[ "$PHASE" = "Running" ] || fail "SqlInstance never reached Running (phase=$PHASE) — operator cannot serve SQL"

log "Assertion 3: printer columns visible on kubectl get sqlinstances"
# -o wide so priority>0 columns (Version, Endpoint) are included, not just the default view.
# No `| head -1` here: head closes the pipe after the first line, kubectl gets a SIGPIPE
# and `set -o pipefail` returns 141, which kills the script. The shell splits the output instead.
LISTING=$(kubectl -n "$NS" get sqlinstances -o wide)
HEADERS=${LISTING%%$'\n'*}
echo "$HEADERS"
echo "$HEADERS" | grep -q "PHASE" || fail "printer column PHASE missing"
echo "$HEADERS" | grep -q "VERSION" || fail "printer column VERSION missing"

log "Assertion 5: deleting the CR garbage-collects STS/Service/Secrets"
kubectl -n "$NS" delete sqlinstance sql-e2e --timeout=120s
kubectl -n "$NS" wait --for=delete statefulset/sql-e2e --timeout=300s || fail "StatefulSet not GC'd"
for i in $(seq 1 30); do
  if ! kubectl -n "$NS" get service sql-e2e >/dev/null 2>&1; then break; fi
  sleep 5
done
kubectl -n "$NS" get service sql-e2e >/dev/null 2>&1 && fail "Service not GC'd"

log "e2e smoke test passed"
