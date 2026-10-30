# Contributing

Thanks for helping! A few ground rules keep TgMcp safe and small:

1. **Security first.** New tools must be gated by the policy (global switch + per-chat rule), audited
   without message content, and covered by tests using the fake Telegram client. Tools in the
   "must not exist" list (payments, account settings, chat join/leave/delete, admin, bulk, contact export)
   will not be accepted.
2. **No secrets or personal data** in code, tests, fixtures, issues or logs. Use placeholder ids
   such as `-1001234567890` and usernames such as `example_channel`.
3. **Build and test** before opening a PR:
   ```sh
   dotnet build && dotnet test            # .NET 11 SDK, see global.json
   docker build -t tgmcp:dev .
   ```
   Warnings are errors; public APIs need XML docs.
4. Keep layers clean: `Domain` is pure, `Application` depends only on `Domain` and ports,
   TDLib lives only in `Infrastructure.Telegram`, wiring only in `Server`.
5. Update `CHANGELOG.md` and the README env reference when behavior or config changes.
