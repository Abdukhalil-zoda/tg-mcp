# Contributing

Thanks for helping! TgMcp gives an AI assistant access to a real person's Telegram account, so a few
ground rules keep it safe and small. Please also read the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ground rules

1. **Security first.** Every tool is gated by the policy: a global switch (`features.x`) **and** a per-chat
   rule (feature list or write mode), deny rules always win. Calls are audited without message content.
   Tools in the "must not exist" list (payments/Stars/gifts, bot buttons and inline queries, account,
   privacy, 2FA or session settings, leaving/deleting chats, admin actions, Business settings,
   contact export, bulk operations) will not be accepted.
2. **No secrets or personal data** in code, tests, fixtures, issues, logs or screenshots. Use placeholder
   ids such as `-1001234567890` and usernames such as `example_channel`.
3. **English only** in code, comments, docs, log and error messages.
4. **Vulnerabilities** are reported privately (see [SECURITY.md](SECURITY.md)), never in public issues.

## Development setup (Docker only)

You only need Docker. Builds and tests run in the pinned .NET 11 SDK image (see `global.json`):

```sh
# full test suite, as CI runs it
docker build --target test .

# or interactively, with a NuGet cache in a local folder
docker run --rm -v "$PWD":/src -v "$PWD/.nuget":/root/.nuget/packages -w /src \
  mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3 dotnet test

# runtime image
docker build -t tgmcp:dev .
```

Warnings are errors, analyzers run at `latest`, and public APIs need XML docs. Unit tests never start
TDLib and never contact Telegram: tool behavior is tested against the fake client in
`tests/TgMcp.Application.Tests/Fakes.cs`. The opt-in `tests/TgMcp.IntegrationTests` run real TDLib against
Telegram's *test* DC only when `TGMCP_IT_*` variables are set (see README → Integration tests).

Layers (keep them clean):

| Project | Responsibility |
|---|---|
| `src/TgMcp.Domain` | policy model, config merge/validation, decision engine (pure, no I/O) |
| `src/TgMcp.Application` | tools, events, drafts, audit/rate-limit ports; depends only on Domain |
| `src/TgMcp.Infrastructure.Telegram` | TDLib adapter, login, file stores |
| `src/TgMcp.Server` | composition root: host, MCP stdio, CLI, config loader |

## Branches and pull requests

- `main` holds released versions only. `development` is the integration branch.
- Open pull requests against **`development`**. Keep them focused: one feature or fix per PR.
- CI (build, tests, Docker build and smoke test) must be green.
- Fill in the PR template, including the security impact.

## Commit style

- Imperative subject line, at most ~72 characters: `Add react_message tool`, `Fix topic check for replies`.
- A blank line, then a body explaining *why* when it isn't obvious. Wrap at ~72 characters.
- One logical change per commit; no secrets, ids or message content in commit messages.

## Adding a tool (checklist)

1. **Domain:** add the feature/flag if needed (`Feature`, `GlobalSwitch`, `Need`, the JSON/env parser and
   `config check` output) with tests in `tests/TgMcp.Domain.Tests`.
2. **Policy gating:** decide the needs (`Need.Read`, `Need.Write`, a feature) and how Deny, `sinceDays`,
   `topics` and limits apply. Denials go through `ToolContext.Deny` (opaque for deny rules, actionable
   hints otherwise).
3. **Application tool:** a small `XxxTool` class in `src/TgMcp.Application/Tools` that runs through
   `ToolRunner` (rate limit, global gate, readiness, one audit record). Never put message content in the
   audit reason.
4. **TDLib adapter:** add the method to `ITelegramClient` and implement it in `TdTelegramClient` using
   `TdSession.CallAsync` (timeout and FLOOD_WAIT handling). Verify method and type names against the
   TDLib.Api package, don't guess.
5. **Fake client + tests:** extend `FakeTelegram` and cover every gating path (global switch off, deny,
   missing permission, allowed, limits, errors, audit without content).
6. **MCP surface:** a thin `[McpServerTool]` method in `src/TgMcp.Server/Mcp/TelegramTools.cs` with an
   honest `[Description]` (say clearly if it acts as the owner).
7. **Docs:** README tools table, `tgmcp.example.jsonc`, `CHANGELOG.md` (Unreleased), and wiki notes.

## Labels

| Label | Meaning |
|---|---|
| `good first issue` | small, well-scoped, a good way to start |
| `help wanted` | maintainers would welcome a PR |
| `new-tool` | a proposal or PR for a new MCP tool |
| `security` | affects the security model (hardening, gating, audit) |
| `docs` | documentation only |
| `bug` / `enhancement` | set by the issue forms |
