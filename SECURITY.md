# Security policy

## Supported versions

Security fixes go to the latest minor release. The API is `v1alpha1`: upgrade to the latest release to get a fix.

## Reporting a vulnerability

Report a vulnerability through the Security tab of this repository, with "Report a vulnerability". It opens a private advisory that only you and the maintainers can see. Do not open a public issue: it cannot be taken back.

Include the versions of the operator and of its chart, the manifests involved and the steps that show the problem. You get an acknowledgement within a few days.

## Scope

The [security model](docs/architecture/security.md) describes the trust boundaries of the operator, the rights of each identity and the controls on each channel. A way around one of these controls is in scope: a manifest that obtains rights its author does not have, an operator action outside the namespace of the resource, a secret that leaves the Secrets it belongs to, a call to the webhook or the view API that bypasses authentication or authorization.

The residual risks listed in the security model and in [Limitations](docs/limitations.md) are known: the operator connects to SQL Server as `sa`, does not authenticate the SQL Server certificates by default, and reads Secrets in every namespace. A report that shows a practical way to exploit them beyond what these pages describe is welcome.
