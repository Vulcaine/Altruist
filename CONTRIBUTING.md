# Contributing to Altruist

Thanks for helping. Bug reports, fixes, docs and new systems are all welcome.

## Before you start

- **Bugs:** open an issue with the steps to reproduce it, what you expected and what happened.
- **Larger changes** (a new system, a public API change): open an issue first so we can agree on the shape before you write the code.
- **Security problems:** don't open a public issue. See [SECURITY.md](SECURITY.md).

## Branches

- Open pull requests against `develop`.
- `main` is the release branch. Every push to `main` publishes the NuGet packages, so only maintainers merge into it.

## Building and testing

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download). Node.js 22.18 or newer (it runs the `.ts` tests directly) is needed only for the TypeScript packages, the dashboard app and the VS Code extension.

```bash
dotnet restore ./Tests/Tests.csproj
dotnet build ./Tests/Tests.csproj --no-restore
dotnet test ./Tests/Tests.csproj --no-build --filter "Category!=Integration"
```

The integration tests need a Postgres 16 database:

```bash
docker run -d --name altruist-pg -p 5432:5432 \
  -e POSTGRES_USER=altruist -e POSTGRES_PASSWORD=altruist -e POSTGRES_DB=postgres postgres:16
export ALTRUIST_TEST_PG="Host=localhost;Port=5432;Username=altruist;Password=altruist;Database=postgres"
dotnet test ./Tests/Tests.csproj --no-build --filter "Category=Integration"
```

Each TypeScript package under `Clients/TypeScript/` has its own tests:

```bash
cd Clients/TypeScript/net && npm ci && npm test && npm run typecheck
```

CI runs the same commands on every pull request.

## Pull requests

- Keep each pull request to one change, and add tests for the behavior it adds or fixes.
- Add a line to the `[Unreleased]` section of [CHANGELOG.md](CHANGELOG.md) for anything a user of the framework would notice.
- If you change a public API or a config key, update the [docs](https://altruist-docs.vercel.app) too, or say in the pull request that they need it.
- Don't commit secrets, `.env` files, build output (`bin/`, `obj/`, `node_modules/`) or packaged `.vsix` files.

## License

Altruist is licensed under [Apache 2.0](LICENSE). By contributing, you agree that your contribution is licensed under it too.
