#!/usr/bin/env bash
# Multi-node kind harness for the *node-fault* half of the HA story (the a test cluster cluster has only
# one amd64 node, so it can validate failover orchestration but not surviving a node loss).
#
# This stands up a kind cluster with several worker nodes, installs the operator, then hands off to
# the dedicated cluster-QA suite (MsSqlOperator.ClusterQa) pointed at the kind context. With
# QA_ANTIAFFINITY=1 the AG replicas are spread one-per-node, so draining/deleting the node that
# hosts the primary is a real node-fault test.
#
# ⚠️ SQL Server is amd64-only. On an arm64 host the kind nodes run arm64, so the mssql pods need
#    amd64 emulation (e.g. colima `--vm-type vz --vz-rosetta`, or `tonistiigi/binfmt --install amd64`).
#    Emulated SQL Server is slow — generous timeouts are expected. On an amd64 host this is native.
set -euo pipefail

CLUSTER="${CLUSTER:-mssql-ha}"
WORKERS="${WORKERS:-3}"
OPERATOR_IMAGE="${OPERATOR_IMAGE:-ghcr.io/evariops/mssql-operator:main}"
CHART="${CHART:-charts/mssql-operator}"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

log() { printf '\n=== %s ===\n' "$*"; }

log "Create kind cluster '$CLUSTER' (1 control-plane + $WORKERS workers)"
{
  echo "kind: Cluster"
  echo "apiVersion: kind.x-k8s.io/v1alpha4"
  echo "nodes:"
  echo "  - role: control-plane"
  for _ in $(seq 1 "$WORKERS"); do echo "  - role: worker"; done
} | kind create cluster --name "$CLUSTER" --config -

CTX="kind-${CLUSTER}"

log "Load the operator image into the cluster"
# Pull then load so kind gets the (possibly amd64-emulated) image present on the host.
docker pull --platform linux/amd64 "$OPERATOR_IMAGE"
kind load docker-image "$OPERATOR_IMAGE" --name "$CLUSTER"

log "Install CRDs + operator"
kubectl --context "$CTX" apply -f "$REPO_ROOT/charts/mssql-operator/crds/" 2>/dev/null || true
helm --kube-context "$CTX" upgrade --install mssql-operator "$REPO_ROOT/$CHART" \
  --namespace mssql-operator --create-namespace \
  --set "image.repository=${OPERATOR_IMAGE%:*}" \
  --set "image.tag=${OPERATOR_IMAGE##*:}" \
  --set image.pullPolicy=IfNotPresent \
  --wait --timeout 5m

log "Run the cluster-QA suite (anti-affinity ⇒ replicas spread across nodes ⇒ node-fault is real)"
QA_CLUSTER=1 \
QA_KUBE_CONTEXT="$CTX" \
QA_NAMESPACE="${QA_NAMESPACE:-mssql-ha-qa}" \
QA_ANTIAFFINITY=1 \
QA_MULTINODE=1 \
QA_MAX_FAILOVER_GAP_S="${QA_MAX_FAILOVER_GAP_S:-60}" \
  dotnet test "$REPO_ROOT/src/MsSqlOperator.ClusterQa/MsSqlOperator.ClusterQa.csproj"

log "Done. Delete with: kind delete cluster --name $CLUSTER"
