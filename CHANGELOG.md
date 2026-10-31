# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-10-31

First public release: a policy-gated MCP server (stdio) for a Telegram **user** account, built on TDLib and
.NET 11, shipped as a hardened Docker image.

### Policy and configuration
- JSON policy file (`TGMCP_ConfigFile`, default `/config/tgmcp.json`) with comments and trailing commas,
  camelCase keys, optional `name`/`note` labels; `TGMCP_*` environment variables override it
  (defaults < file < env). Commented template `tgmcp.example.jsonc`.
- Deny by default: master switch `enabled`, global `features.*` switches **and** per-chat grants
  (`read`, `write` off/draft/on, `features`, `sinceDays`, `topics`, `notify`); first matching chat rule
  wins, otherwise `defaults`; `deny` rules by id, username, type or chat folder always win and are opaque.
- Fail closed: unknown keys (reported by JSON path), bad types or values, ambiguous selectors, secrets or
  runtime settings in the file, a missing explicit file, unresolvable folder denies or an unwritable audit
  log deny everything. Empty arrays are valid.
- Secrets (`ApiId`, `ApiHash`, `DatabaseKey`) and `TGMCP_ContactsAllow` are environment-only.
- `tgmcp config check [file]`: effective policy, the source of every value, rule names, redacted
  sensitive values; never contacts Telegram.
- Actionable denials with ready-to-paste JSON rule snippets.
- Limits: `maxMessagesPerCall`, `ratePerMinute` (token bucket), `maxMediaMb`, `mediaRetentionDays`,
  `sendsPerMinute` (per chat), `joinsPerHour`, `newChatsPerDay`, `listChats` modes.

### Tools (17 MCP tools)
- Read: `list_chats`, `read_messages`, `read_topic`, `get_message`, `search_chat`, `search_all`
  (results from non-allowed chats dropped), `download_media` (size cap, retention, optional host path),
  `summarize_message` / `translate_message` (Telegram's AI features, usually Premium), `preview_chat`.
- Write, as the owner, with a `"draft"` mode for owner approval (except `mark_read`, which acts at once):
  `send_message` (also to allow-listed phone numbers via `contacts.mode`), `react` (offered non-Premium
  emoji only), `forward_message` (source needs read + forward, destination a write mode),
  `edit_message` / `delete_message` (only messages TgMcp sent, tracked in a sent-log; deletions are drafts
  unless `delete.mode` is `"on"`), `join_chat` (`join.mode` off/draft/on with an allow list),
  `mark_read`.
- Owner CLI: `login` (interactive, session shown as "TgMcp"), `drafts`, `approve <id>`, `reject <id>`
  for message, reaction, forward, edit, delete, join and phone-message drafts; clear error when another
  process holds the TDLib database.

### Inbound events
- With `events.enabled`, new messages from chats with `"notify": true` (requires `read`) are pushed to
  Claude Code as `notifications/claude/channel` (experimental `claude/channel` capability): untrusted-data
  marker, truncation, media descriptions, string meta incl. `write_mode`, rate limit with drop summaries,
  optional catch-up, final ids for outgoing messages. Server instructions describe the untrusted-data rules.

### Security and operations
- JSONL audit log without message text, titles, queries, invite hashes or phone numbers (hash prefixes
  only); config sha256 at start; fails closed.
- Bounded FLOOD_WAIT retry honoring Telegram's wait, per-request timeouts.
- Non-root image (uid 1654) that works with `--read-only --cap-drop ALL --security-opt no-new-privileges`.
- Release workflow: signed (cosign keyless) multi-arch images (linux/amd64, linux/arm64) on GHCR with SBOM
  and provenance.
- Opt-in integration tests against Telegram's test DC (`tests/TgMcp.IntegrationTests`, manual workflow).
- CI, Dependabot, issue forms, PR template, code of conduct, contributing guide, wiki.

[Unreleased]: https://github.com/Abdukhalil-zoda/tg-mcp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Abdukhalil-zoda/tg-mcp/releases/tag/v0.1.0
