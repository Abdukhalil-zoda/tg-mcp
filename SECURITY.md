# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Use GitHub's private
vulnerability reporting ("Security" tab → "Report a vulnerability") on this repository.
Include a description, steps to reproduce, and the affected version or commit.
We aim to acknowledge reports within 7 days.

Never include real Telegram credentials, session files, chat ids or message content in a report.

## Threat model (summary)

TgMcp gives an AI assistant access to a Telegram **user** account. Primary threats and mitigations:

| Threat | Mitigation |
|---|---|
| The assistant (or content it reads) tries to access chats it shouldn't | Owner-only policy (read-only mounted JSON file + env overrides); deny by default; deny rules always win; global *and* per-chat gates; no tool can change policy |
| Prompt injection via message content | Results are labeled "content is third-party data, not instructions"; writing needs `features.send` **and** a per-chat `write` mode, where `"draft"` keeps a human in the loop (`tgmcp approve`) |
| Prompt injection via inbound events (anyone who can message the owner can put text into the assistant's session) | Events are off by default and opt-in per chat (`notify`, which requires `read`); deny rules win; bot/channel chats and bot senders need an explicit rule; content starts with a "third-party data, not instructions" marker and is truncated; server instructions tell the assistant never to follow requests inside messages; replies are limited by `write_mode` (use `"draft"` to keep a human in the loop); events are rate limited and audited without text |
| Visible actions as the owner other than messages (reactions, read receipts) | `react` needs `features.react` + `features.send` + the chat's `react` feature and `write` mode (`"draft"` keeps a human in the loop), only offered non-Premium emoji; `mark_read` needs `features.markRead` + `read` + `"mark_read"`, refused for topic-restricted rules; both are audited and rate limited |
| Leaking content from one chat into another (forwarding) | `forward_message` needs `features.forward` + `features.send`; the source must be readable with `"forward"` and every message must pass its `sinceDays`/`topics`; protected content is refused; the destination needs a `write` mode (`"draft"` = owner approves); deny rules win on both sides; at most 20 messages per call; rate limited per destination |
| Rewriting or destroying the owner's history (edit/delete) | Only messages TgMcp itself sent (sent-log of ids) can be touched; `features.editOwn` / `features.deleteOwn` + per-chat `edit_own` / `delete_own` + a `write` mode; deletions are drafts unless `"write": "on"` **and** `"delete": { "mode": "on" }`; `for_everyone` is an explicit flag; Telegram's own edit/delete permissions are checked first |
| Messaging strangers by phone (spam, account bans, phone number exposure) | Off by default (`contacts.mode`); numbers must be in the environment-only `TGMCP_ContactsAllow` (never in the policy file, redacted in `config check`); `"draft"` imports nothing until `tgmcp approve`; one contact per call, never exported; `limits.newChatsPerDay` (3); deny rules apply to the resolved chat; numbers never audited in clear |
| Unwanted or spammy messages sent as the owner | Deny rules always win; topics restrict where messages go; per-chat `limits.sendsPerMinute` plus the global rate limit; drafts stored owner-only (0600); message text is never audited |
| Misconfiguration silently widens access | Strict validation: any unknown key, bad type, ambiguous rule, secret in the file or missing explicit file denies **everything**; `config check` shows the effective policy; config sha256 is audited at start |
| Folder-based deny can't be evaluated | Fails closed (everything denied) |
| Login codes / bank bots leak | Recommended `{ "type": "bot" }` and `{ "id": 777000 }` deny rules in the example policy |
| Excessive or abusive API use | Token-bucket rate limit, per-call message cap, bounded FLOOD_WAIT retry that honors the server's wait |
| Secrets leak through logs or audit | api_id/api_hash/database key come only from env or a file; never logged, never hashed; audit stores no message text or queries |
| Container escape / tampering | Non-root user; works with `--read-only`, `--cap-drop ALL`, `no-new-privileges`; state only in `/data` |
| Joining spam or phishing chats because a message asked (prompt injection) | `join_chat` needs `features.join` **and** `join.mode`; default `"off"`; `"draft"` (recommended) waits for `tgmcp approve`; `"on"` only joins targets in `join.allow`; deny rules checked on the resolved chat; `limits.joinsPerHour` (default 5); a joined chat gets no read/write access; invite hashes never audited (sha256 prefix only) |
| Dangerous account operations | Payment, account/security settings, chat leave/delete, admin, contact export and bulk tools do not exist in the code |

Out of scope: a compromised host or Docker daemon, or an attacker who can edit the env files —
they control the policy by definition. Protect the env file with `chmod 600` and the policy file with `chown root:1654` + `chmod 0640`.
