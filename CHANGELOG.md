# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `forward_message(from_chat, message_ids, to_chat, to_topic_id?, send_copy?)`: forwards up to 20 messages as
  the owner. Source needs `read` + `"forward"` (`features.forward`) and each message must pass
  `sinceDays`/`topics` and be forwardable; destination needs a `write` mode (`"on"` forwards, `"draft"`
  saves a forward draft for `tgmcp approve`), no read required. Deny rules win on both sides.
- `react(chat, message_id, emoji?)`: adds an emoji reaction as the owner or removes the owner's reactions.
  Needs `features.react` + `features.send`, and `read` + `"react"` + a `write` mode in the chat rule:
  `"on"` applies it, `"draft"` saves a reaction draft for `tgmcp approve`. Validated against
  `getMessageAvailableReactions` (Premium-only, paid and custom reactions are rejected).
- `mark_read(chat, up_to_message_id?)`: marks a chat read as the owner (`viewMessages` with force_read;
  without an id also `readAllChatMentions`/`readAllChatReactions`). Needs `features.markRead` and
  `read` + `"mark_read"` in the chat rule; refused for topic-restricted rules. Changes the owner's
  visible unread state and read receipts.
- `join_chat` and `preview_chat`: join (or preview) a group/channel by `@username`, `t.me/username` or an
  invite link. Gated by `features.join` and the new `join` section: `"mode": "off"` (default) | `"draft"`
  (owner approves with `tgmcp approve`) | `"on"` (only targets in `join.allow`; `"*"` allowed but flagged).
  Deny rules apply to the resolved chat; `limits.joinsPerHour` (default 5); joined chats get no access
  until a rule is added (the result shows it); invite hashes are never audited (sha256 prefix only).
- Inbound events: with `events.enabled`, new messages from chats with `"notify": true` (requires `read`)
  are pushed as `notifications/claude/channel` (experimental `claude/channel` capability), with an
  untrusted-data marker, truncation (`events.maxTextChars`), media descriptions, string meta including
  `write_mode`, a rate limit with drop summaries (`events.maxPerMinute`), optional catch-up
  (`events.catchUpMinutes`, state in `/data/events-state.json`) and text-free audit records.
  Bot/channel chats and bot senders need an explicit rule. TDLib starts eagerly when events are on.
- Server `instructions` describing the untrusted-data rules and how to reply.
- Media in message text is described with names and durations (`[document report.pdf]`, `[voice 12s]`).
- `send_message`: plain-text messages as the owner. `"write": "on"` sends and waits for Telegram's
  confirmation; `"draft"` saves an owner-only draft in `/data/drafts`; `"off"` is denied with a JSON hint.
  Topics restrict where messages may go; `limits.sendsPerMinute` (default 10) limits sends per chat;
  only the text length is audited.
- Owner commands `drafts`, `approve <id>` (re-checks the policy, sends, deletes) and `reject <id>`.
- A clear error when another tgmcp process holds the TDLib database lock.

### Fixed
- Outgoing events (`events.includeOutgoing`) now carry the final message id: messages still being
  sent are skipped and emitted on `updateMessageSendSucceeded`; failed sends emit nothing.
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
