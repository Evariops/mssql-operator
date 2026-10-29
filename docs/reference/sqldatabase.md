# SqlDatabase

A user database on a `SqlInstance` or a `SqlAvailabilityGroup`: created when missing, adopted when it
already exists, with the options you declare. On an availability group it is created on the primary
and joined to the AG for you. Short name: `sqldb`.

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata:
  name: orders-db
  namespace: sql
spec:
  availabilityGroupRef: { name: orders }     # or instanceRef: { name: reporting }
  databaseName: Orders
  collation: French_CI_AS                    # at creation only
  compatibilityLevel: 160
  owner: orders_owner                        # a login, created for instance by a SqlUser
  files:
    data: { size: 10Gi, growth: 1Gi }
    log:  { size: 2Gi, growth: 512Mi }
  availabilityGroup:                         # how it joins the AG (optional)
    logBackup: { mode: Local, retentionHours: 72 }
  deletionPolicy: Retain                     # default: deleting the resource keeps the database
```

## Main fields

| Field | Default | What it does |
|---|---|---|
| `instanceRef.name` / `availabilityGroupRef.name` | one required | Where the database lives, in the same namespace. Immutable. |
| `databaseName` | required | Name of the database. Immutable, not a system database. |
| `collation` | server default | Applied at creation. Immutable: a database keeps its collation. |
| `recoveryModel` | left as is (FULL) | `Full`, `Simple` or `BulkLogged`. Always `Full` in an AG. |
| `compatibilityLevel` | left as is | 100 to 170. |
| `owner` | left as is (`sa`) | Login owning the database. Applied once the login exists. |
| `files.data` / `files.log` | left as is | `size` (only grows), `growth` (`256Mi` or `10%`), `maxSize` (or `Unlimited`), of the primary data file and the log file. A size larger than the volume is refused. |
| `availabilityGroup` | | `seedingMode`, `logBackup`, `initialization`, `dataMovement`: see [`SqlAvailabilityGroupDatabase`](sqlavailabilitygroupdatabase.md). Each block you set is enforced; the others are left as they are. |
| `deletionPolicy` | `Retain` | `Retain`: deleting the resource keeps the database, and its AG membership. `Delete`: the database leaves its AG and is dropped, on every replica. |
| `automation.mode` | `Apply` | In `Plan` or `Observe`, a deletion with `Delete` waits; see [Automation](../guides/automation.md). |

Options you do not set are never changed: a database you adopt keeps them.

## Existing databases and ownership

The operator records the owner in the database itself (extended property `mssqlop.owner`), so it
follows the database through a failover.

| The database… | The operator… |
|---|---|
| does not exist | creates it |
| exists, no owner | adopts it (`status.adopted: true`, event `DatabaseAdopted`) |
| is owned by a former `SqlDatabase` of the same name (GitOps re-created it) | takes it back |
| is owned by another `SqlDatabase` that still exists | changes nothing: phase `Conflict` |

With `deletionPolicy: Delete`, a database is dropped only if this resource owns it.

## On an availability group

The operator creates the database on the primary, then a `SqlAvailabilityGroupDatabase` named after
the `SqlDatabase`, which takes the initialization backup, joins the database and seeds the
secondaries. An existing `SqlAvailabilityGroupDatabase` for the same database is adopted instead.
Edit the `SqlDatabase`, not the `SqlAvailabilityGroupDatabase`: the blocks of `availabilityGroup` are
copied onto it. A Basic availability group (Standard edition) holds a single database.

## Status

```console
$ kubectl get sqldb -n sql
NAME        PHASE   SERVER     DATABASE   ADOPTED   AGE
orders-db   Ready   orders-0   Orders     false     2d
```

- `status.phase`: `Pending` (target not running), `Creating`, `Joining` (AG: not synchronized yet),
  `Ready`, `Conflict`, `Failed` (retried every minute).
- `status.collation`, `recoveryModel`, `compatibilityLevel`, `owner`: as read on the server.
- `status.availabilityGroupDatabase` and `availabilityGroupPhase`: the joining resource and its phase.
- Conditions: `Ready`, `OwnerLoginMissing`, `CollationMismatch` (an adopted database with another collation).
