# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `send_message`: plain-text messages as the owner. `"write": "on"` sends and waits for Telegram's
  confirmation; `"draft"` saves an owner-only draft in `/data/drafts`; `"off"` is denied with a JSON hint.
  Topics restrict where messages may go; `limits.sendsPerMinute` (default 10) limits sends per chat;
  only the text length is audited.
- Owner commands `drafts`, `approve <id>` (re-checks the policy, sends, deletes) and `reject <id>`.
- A clear error when another tgmcp process holds the TDLib database lock.

### Fixed
- Empty JSON arrays (`"deny": []`, `"chats": []`, empty `features`/`topics`) are accepted as "no entries"
  instead of making the config invalid.

### Changed
- **The policy is now a JSON file** (`TGMCP_ConfigFile`, default `/config/tgmcp.json`; comments and
  trailing commas allowed) with camelCase keys, lists as JSON arrays and optional `name`/`note` labels
  on rules. `TGMCP_*` environment variables still work and override the file
  (defaults < file < env). Secrets (`ApiId`, `ApiHash`, `DatabaseKey`) are environment-only and make
  the config invalid if found in the file. An explicitly configured but missing file denies everything.
- Validation errors name keys by JSON path (`chats[2].reed: unknown key`) or by variable name for env.
- Denial hints are ready-to-paste JSON snippets instead of env keys.
- `config check [file]` validates a given file and shows the source (file/env/default) of every
  setting and the human names of rules.
- `tgmcp.example.jsonc` replaces the env-based policy template; `tgmcp.env.example` now holds only
  secrets and optional overrides (`api.env.example` removed).
- An empty `topics` list now means "all topics" instead of being an error.
- Audit log failures now fail closed: calls are denied before contacting Telegram if the audit file
  is not writable, and results are withheld if the audit record cannot be written afterwards.
- Permission denials name the chat and include a one-line config hint for the operator; Deny-rule
  hits return only "denied by policy".

### Added
- `media.dir` (default `/data/media`) to bind-mount a host folder for downloads, and
  `media.hostDir` to report the matching host path in `download_media` results.
- `limits.mediaRetentionDays` (default 7, 0 disables): old media is deleted at startup and
  before each download.

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
