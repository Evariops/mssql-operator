#!/usr/bin/env bash
# Node maintenance bench on kind (one control plane, three workers). Installs the operator
# with nodeMaintenance in Enforce, runs a three-replica AG under a continuous writer, then drains
# the node of the primary and the node of a synchronous secondary, as `kubectl drain` does.
# The evicted replica stays Pending while its node is cordoned (local volume): the reboot window.
# Asserts: every drain completes within its deadline, the primary is failed over (planned, reason
# NodeMaintenance) before it leaves, and no acknowledged write is lost.
# Expects: the kind cluster of kind.yaml, the operator image loaded as mssqloperator:e2e.
set -euo pipefail

NS=mssql-bench
AG=bench
OPERATOR_NS=mssql-operator
HERE="$(cd "$(dirname "$0")" && pwd)"
SELECTOR="mssql-operator.evariops.com/ag=$AG"

log() { printf '\n=== %s ===\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
ag() { kubectl -n "$NS" get sqlavailabilitygroup "$AG" -o jsonpath="$1" 2>/dev/null; }

wait_healthy() {
  for _ in $(seq 1 120); do
    if [ "$(ag '{.status.phase}')" = Running ] && [ "$(ag '{.status.replicas[*].synchronizationState}')" = "HEALTHY HEALTHY HEALTHY" ]; then
      return 0
    fi
    sleep 5
  done
  fail "AG not healthy after 10 min: phase=$(ag '{.status.phase}') replicas=$(ag '{.status.replicas[*].synchronizationState}')"
}

# Drains the SQL pods of a node (the node is cordoned, the other pods stay), times it, uncordons.
drain() {
  local node=$1 label=$2 start end
  log "Drain $node ($label)"
  start=$(date +%s)
  kubectl drain "$node" --ignore-daemonsets --delete-emptydir-data --pod-selector "$SELECTOR" --timeout=660s \
    || fail "drain of $node did not complete"
  end=$(date +%s)
  echo "drain of $node ($label) took $((end - start)) s"
  echo "$label $((end - start))" >> /tmp/bench-drains
  sleep 20 # the reboot window: the evicted replica stays Pending on its cordoned node
  kubectl uncordon "$node"
  wait_healthy
}

log "Install chart (nodeMaintenance Enforce)"
helm install mssql-operator charts/mssql-operator \
  --namespace "$OPERATOR_NS" --create-namespace \
  --set image.repository=mssqloperator --set image.tag=e2e --set image.pullPolicy=Never \
  --set operator.nodeMaintenance.mode=Enforce \
  --wait --timeout 5m
kubectl -n "$OPERATOR_NS" rollout status deploy --timeout=180s

log "Create the AG"
kubectl create namespace "$NS"
# Generated for the run; SQL Server's policy wants upper, lower, digit and symbol.
generated() { printf '%s' "Gen-$(openssl rand -hex 12)-1aA!"; }
generated > /tmp/bench-sa && generated > /tmp/bench-dmk
kubectl -n "$NS" create secret generic bench-sa --from-file=password=/tmp/bench-sa
kubectl -n "$NS" create secret generic bench-dmk --from-file=password=/tmp/bench-dmk
kubectl apply -f "$HERE/ag.yaml"
wait_healthy
for _ in $(seq 1 60); do
  [ "$(kubectl -n "$NS" get sqldatabase bench-app -o jsonpath='{.status.phase}' 2>/dev/null)" = Ready ] && break
  sleep 5
done
kubectl -n "$NS" get sqlavailabilitygroup,sqldatabase,pods -o wide

log "Start the writer"
kubectl apply -f "$HERE/writer.yaml"
kubectl -n "$NS" wait --for=condition=Ready pod/writer --timeout=300s
for _ in $(seq 1 60); do
  [ "$(kubectl -n "$NS" logs writer 2>/dev/null | grep -c ' ok ')" -ge 20 ] && break
  sleep 2
done
: > /tmp/bench-drains

log "Drain the node of the primary"
PRIMARY=$(ag '{.status.primaryReplica}')
NODE=$(kubectl -n "$NS" get pod "$PRIMARY" -o jsonpath='{.spec.nodeName}')
drain "$NODE" primary
NEW_PRIMARY=$(ag '{.status.primaryReplica}')
[ "$NEW_PRIMARY" != "$PRIMARY" ] || fail "the primary $PRIMARY did not move"
[ "$(ag '{.status.lastFailover.reason}')" = NodeMaintenance ] || fail "last failover reason: $(ag '{.status.lastFailover.reason}')"
kubectl -n "$NS" get events.events.k8s.io --field-selector reason=MaintenanceFailover -o name | grep -q . \
  || fail "no MaintenanceFailover event"

log "Drain the node of a synchronous secondary"
SECONDARY=$(kubectl -n "$NS" get sqlavailabilitygroup "$AG" -o json \
  | python3 -c 'import json,sys; s=json.load(sys.stdin)["status"]; print(next(r["name"] for r in s["replicas"] if r["role"]=="SECONDARY" and r["availabilityMode"]=="SynchronousCommit"))')
NODE=$(kubectl -n "$NS" get pod "$SECONDARY" -o jsonpath='{.spec.nodeName}')
drain "$NODE" synchronous-secondary

log "Check the writes"
# The log first (the writer goes on: the rows can only exceed the acknowledged writes).
kubectl -n "$NS" logs writer > /tmp/bench-writer.log
P=$(ag '{.status.primaryReplica}')
ROWS=$(kubectl -n "$NS" exec "$P" -c mssql -- bash -c \
  'SQLCMDPASSWORD="$(cat /var/run/secrets/mssql-sa/password)" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM app.dbo.w"' | tr -d '\r ')
python3 "$HERE/pauses.py" /tmp/bench-writer.log "$ROWS"
cat /tmp/bench-drains
log "maintenance bench passed"
