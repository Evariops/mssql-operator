# Documentation

Start with [Getting started](getting-started.md).

| I want to… | Read |
|---|---|
| install the operator and create a first AG | [Getting started](getting-started.md) |
| run an availability group: failovers, durability, upgrades | [Availability groups](guides/availability-groups.md) |
| see what the operator will do, make it ask first | [Automation and approvals](guides/automation.md) |
| back up, encrypt, keep the keys safe | [Backups and encryption](guides/backups-and-encryption.md) |
| create logins and grant permissions | [Users and permissions](guides/users.md) |
| manage server settings, Agent jobs, stored procedures | [Configuration, jobs and procedures](guides/configuration.md) |
| monitor and alert | [Observability](guides/observability.md) |
| prepare for production | [Security](guides/security.md) |
| upgrade the operator or SQL Server | [Upgrading](guides/upgrading.md) |
| look up a field or annotation | [Reference](reference/) |
| fix an incident | [Runbooks](runbooks/) |
| understand how it works inside | [Internals](internals/) |

## Reference

[SqlInstance](reference/sqlinstance.md) ·
[SqlAvailabilityGroup](reference/sqlavailabilitygroup.md) ·
[SqlDatabase](reference/sqldatabase.md) ·
[SqlAvailabilityGroupDatabase](reference/sqlavailabilitygroupdatabase.md) ·
[SqlUser](reference/sqluser.md) ·
[SqlConfiguration](reference/sqlconfiguration.md) ·
[SqlAgentJob](reference/sqlagentjob.md) ·
[SqlStoredProcedure](reference/sqlstoredprocedure.md) ·
[SqlTdeCertificate](reference/sqltdecertificate.md) ·
[SqlControlRecord](reference/sqlcontrolrecord.md) ·
[View API](reference/view-api.md) ·
[Helm values](reference/helm-values.md)

## Runbooks

- [A node is lost](runbooks/lost-node.md)
- [SQL Server stops when its cpuset changes](runbooks/cpuset-changes.md)
- [Restore key material](runbooks/key-material.md)
- [Full-text indexes and upgrades](runbooks/fulltext.md)

## Internals

[Architecture](internals/architecture.md) ·
[High availability](internals/high-availability.md) ·
[Safety](internals/safety.md) ·
[Security model](internals/security-model.md) ·
[Keys and data](internals/keys-and-data.md) ·
[Testing](internals/testing.md)
