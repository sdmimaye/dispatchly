# Testing

Tests live under `test/`. They use xUnit v3 (`xunit.v3.mtp-v2`) and `Assert.*`. Do not add FluentAssertions.

Test projects are executables (`OutputType` is `Exe`). `dotnet test` uses Microsoft Testing Platform via `global.json`.

PostgreSQL tests use Testcontainers and require Docker. They live in `Dispatchly.Transport.PostgreSql.Tests`. SQL Server tests do the same in `Dispatchly.Transport.SqlServer.Tests`. Entity Framework tests live in `Dispatchly.EntityFrameworkCore.Tests` and use Testcontainers for both databases. There is no shared testing assembly.

```bash
dotnet test
```

Name test classes `{TypeUnderTest}Tests` and methods `{Method}_{Scenario}`.
