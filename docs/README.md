# Documentation

## Usage

- [Getting started](getting-started.md): install the operator, create an instance and a three-replica availability group with a database and a login, run a planned failover.
- [Availability groups](guide/availability-groups.md): prerequisites, declaration, connections, durability, failovers, rolling updates, placement.
- [Databases, logins and server objects](guide/databases-and-access.md): `SqlDatabase`, `SqlUser`, `SqlConfiguration`, `SqlAgentJob`, `SqlStoredProcedure`.
- [Backups and encryption](guide/protecting-data.md): backups taken by the operator, log backups, TDE, key material, service master key restore, decommissioning.
- [Operations](guide/operating.md): installing and upgrading the operator, SQL Server builds, automation modes and approvals, safety levels, monitoring, production hardening.
- [Limitations](limitations.md).

## Incidents

[Failure modes](failure-modes.md) starts with a table that maps each `NeedsAttention` reason to its section. Each section describes a failure, the operator's response, the events and conditions to expect, and the manual steps when there are any. The runbooks (lost node, cpuset changes, full-text indexes) are part of it.

## Design

1. [Architecture overview](architecture/overview.md): components, reconciliation runtime, the steps of an AG reconcile, status and view API, admission, CRDs, image catalog, code layout.
2. [Availability group internals](architecture/availability.md): commit rule, health pass, automatic failover from detection to promotion, fencing, return of the old primary, planned and forced failovers, durability profiles, rolling updates, database seeding and re-seeding, lost nodes.
3. [Safety mechanisms](architecture/safety.md): actuation gateway, control record, operator epoch, safety levels, the level 2 protocol, decision core simulation, ownership registry, key material handling, isolation between servers.
4. [Security model](architecture/security.md): trust boundaries, RBAC, admission, TLS, network policies, residual risks.

[Contributing](contributing.md) covers building, the test suites and CRD changes.

## Reference

- [Resources](reference/resources.md): every resource with an example, its main fields, annotations and status.
- [Operator](reference/operator.md): Helm values, view API, metrics.
