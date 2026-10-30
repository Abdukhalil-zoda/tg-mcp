# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- Audit log failures now fail closed: calls are denied before contacting Telegram if the audit file
  is not writable, and results are withheld if the audit record cannot be written afterwards.
- Permission denials name the chat and include a one-line config hint for the operator; Deny-rule
  hits return only "denied by policy".

### Added
- `TGMCP_MediaDir` (default `/data/media`) to bind-mount a host folder for downloads, and
  `TGMCP_MediaHostDir` to report the matching host path in `download_media` results.

## [0.1.0] - 2026-10-30

### Added
- MCP stdio server (`serve`) with read-only tools: `list_chats`, `read_messages`, `read_topic`,
  `get_message`, `search_chat`, `search_all`, `download_media`.
- Env-only policy (`TGMCP_*`): master switch, global feature switches, defaults, ordered chat rules
  (id/username/title, read, write, features, since-days, topics), deny rules (id/username/type/folder),
  limits. Strict validation; invalid config denies everything.
- `config check` command showing the effective policy per rule.
- Interactive `login` command (phone, code, 2FA password without echo); session named "TgMcp".
- JSONL audit log without message content; config sha256 at startup.
- Token-bucket rate limit and bounded FLOOD_WAIT retry.
- Hardened Docker image (non-root, read-only friendly).
