# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
