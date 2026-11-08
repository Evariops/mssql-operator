# Contributing

This page describes how a change is proposed and merged. [docs/contributing.md](docs/contributing.md) covers the build, the test suites and the conventions the build enforces.

## Proposing a change

- Open an issue first for a new feature or a change of behaviour, so that the design is discussed before the code.
- Open the pull request against `main`. Its title follows [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `ci:`, `chore:`…): the release notes are built from the titles.
- CI must pass: build, unit and integration tests, formatting, chart validation, image scan and the end-to-end test on kind.
- A CRD change is made by hand in the YAML under `charts/mssql-operator/templates/crds/`, together with the C# entity ([CRD changes](docs/contributing.md#crd-changes)).

## Signed commits

Every commit of a pull request must be signed: the protection of `main` refuses unsigned commits. With an SSH key:

```bash
git config --global gpg.format ssh
git config --global user.signingkey ~/.ssh/id_ed25519.pub
git config --global commit.gpgsign true
```

Add the same public key to your GitHub account as a signing key (Settings › SSH and GPG keys › New SSH key, key type "Signing Key"). To sign the commits a branch already has:

```bash
git rebase --exec 'git commit --amend --no-edit -S' origin/main
git push --force-with-lease
```

## License

Contributions are licensed under the [Apache License 2.0](LICENSE), like the project (section 5 of the license). No contributor agreement and no sign-off are required.
