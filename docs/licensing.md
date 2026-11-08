# SQL Server licensing

mssql-operator deploys the SQL Server container images that Microsoft publishes. Running them requires a license from Microsoft, which the operator neither grants nor checks.

## Accepting the license agreement

The SQL Server container starts only once its end-user license agreement is accepted, through the `ACCEPT_EULA` environment variable. The operator sets this variable only when the manifest sets `spec.acceptEula: true`, on a `SqlInstance` or on a `SqlAvailabilityGroup`. The field is required and must be `true`: by setting it, the author of the manifest accepts the agreement for the SQL Server instances the resource creates. The operator never accepts it on anyone's behalf.

The agreement is linked from the page of the image on the [Microsoft Artifact Registry](https://mcr.microsoft.com/product/mssql/server/about).

A resource created by an earlier version of the operator may lack the field. The operator then leaves its StatefulSet as it is and raises `NeedsAttention` with the reason `EulaNotAccepted` ([License agreement not accepted](failure-modes.md#license-agreement-not-accepted)).

## Choosing the edition

`spec.edition` is required too, because it decides the license the instance needs. It is passed to SQL Server as `MSSQL_PID` and cannot be changed in place.

| Edition | Licensing and limits |
|---|---|
| `Developer` | Free, with all the features of Enterprise, and licensed for development and test only, never for production. |
| `Express` | Free, with limits on the size of each database, the memory and the compute the engine uses. No availability group. |
| `Standard` | Paid. An availability group in Standard is a basic availability group: two replicas and one database. |
| `Enterprise` | Paid. Availability groups of two to nine replicas. |
| `EnterpriseCore` | Enterprise licensed per core, for a `SqlInstance`. |

A `SqlAvailabilityGroup` accepts `Developer`, `Standard` or `Enterprise`. The limits of each edition change between versions of SQL Server: check the editions comparison of the version you run.

## Your responsibility

The license you need depends on the edition, on the cores the pods can use, and on how Microsoft counts them in containers and virtual machines. The operator does not track any of it. The terms are in the [Microsoft Product Terms](https://www.microsoft.com/licensing/terms/).

## Trademarks

Microsoft and SQL Server are trademarks of Microsoft Corporation. mssql-operator is not affiliated with, endorsed by or sponsored by Microsoft.
