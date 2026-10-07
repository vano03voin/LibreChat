# SE-mind integration

This fork starts at LibreChat v0.8.8, commit
`e8f3be08623663d4ad7f7241e693c94469b63bb0`.
The Code Interpreter checkout is pinned to v1.10.4,
`a62d69e05a4541ac9a20722ac7ff3fcfecb6ed24`.

## Storage and rollout

All migration checkouts, backups, archives, logs, dependency caches and temporary
files live below `D:\semind-librechat`. The historical ZIP goes in `archives/`,
not on the Windows desktop. Production source data and old volumes must remain
available until rollback has been verified.

`compose.dev.yaml` describes LibreChat, MongoDB and Redis only. It is not a complete
gateway/Code API deployment. The current host-development setup uses LibreChat on
loopback port 49390 and the gateway on 49391, with separate dev databases. The dev
game route has been configured, but the Workshop ship scenario has not been run.

Prepare private `dev.env` outside Git before starting the stack. The configuration
contains no secrets; credentials are injected from that file. Build from this
checkout with its package lock. Never point dev at production databases.

## Verified on 2026-10-07/08

- Steam identity, one-use SSO exchange, parent-session checks/revocation, USER
  provisioning and owned default/selected agents have targeted HTTP and policy
  coverage. The login button and prepared-agent redirect are implemented.
- Explicit memory writes, profile/world partitions, exact agent/skill ownership,
  schedule scopes and fresh scheduled grants are implemented. History is retained
  permanently; the UI's removal action archives conversations. Schedules retain
  the one-hour minimum, ten-per-owner quota,
  no-overlap behavior and disable after five failures. World changes pause game
  schedules. Personal script-library actions do not require an online player.
- The shared web/game tool factory preserves cancellation. Workshop imports have
  a separate 240-second deadline; other gateway requests retain 30 seconds.
- Focused policy acceptance: 223 passing checks across nine suites; subsequent
  platform timeout/cancellation checks: 9/9; memory consent checks: 69/69. Actual
  Mongo provisioning/ownership checks: 5/5; Python identity HTTP checks: 12/12.
  API, data-schemas and data-provider typechecks passed. Client build/typecheck
  passed. These are source/component checks, not a full game acceptance result.
- Isolated Docker Code API: two concurrent owners, Python, CSV and PNG outputs,
  plus authentication checks passed. Actual Luna function-tool round trip and
  nonstreaming response passed.
- Native runtime component probe passed with two owners and two dialogues using
  the real agent initializer, tool loader, Run and execution handlers. It used
  synthetic reads and checked private history/foreign-owner denial; it issued no
  ship commands. Each owner's next turn recalled its own prior text and tool
  result; persisted history grew from four to six messages. Evidence:
  `D:/semind-librechat/logs/native-runtime-component.json`.
- Anonymous Steam download of Workshop item 914445138 returned the original
  40,907-byte script, and its dev virtual compilation returned `valid: true`.
  No programmable-block deployment, script run or ship movement was performed.
- The plugin ACK patch is staged only. Its isolated build passed 493 checks
  (249 Magnetar, 123 AiStore, 121 Live); the live plugin still has the old ACK
  behavior.
- Dev migrations 0051/0052 were applied after a private schema backup. Database
  and expected revision are both 0052; after restarting only the dev API,
  readiness and the actual identity endpoint returned HTTP 200.
- A focused refresh optimization reuses only the fresh user loaded by that
  auth operation, with an exact persisted-owner check. Other callers keep their
  fresh lookup, and session checks remain in place. Regression checks passed:
  5 helper tests and 155 auth-controller/service tests. This removes a repeated
  Mongo user lookup without introducing a shared user cache.
- Lighthouse passed the single repeat after that fix, against the seeded
  transcript and unchanged budgets: median LCP 4,301.3/4,500 ms,
  CLS 0.01684384183558066/0.1, TBT 327.038/500 ms. The prior baseline failed at
  LCP 4,532.586 ms; it remains historical evidence, not an open gate. Report:
  `D:/semind-librechat/logs/lighthouse-refresh-fixed.log`.

The historical base archive and local-production increment passed independent
full member/hash/size, SQLite integrity, count and expanded known-credential
audits. The base contains 6,435 files from 85 sources and 9,460 stored rows; the
increment contains 3,204 files from 55 sources and 4,772 rows. Repeated snapshots
are replacements, so these counts must not be summed as unique messages.
Evidence and exact archive hashes are recorded in
`D:/semind-librechat/README-assistant-history-archive.md`. Superseded intermediate
copies are kept in a restricted private quarantine. Local old-production intake
is stopped and its scoped drain is complete; dev and live game-plugin queues
still require a final drain before cutover.

## Remaining acceptance gates

- Complete a real browser Steam login/logout/revocation check through HTTPS and
  verify the same player account in web and game. Component fixtures do not
  replace this check.
- Verify external attachment ownership and complete the remaining dev/game
  drains before cutover. Preserve source history and volumes until rollback is
  verified; the audited local-production archive is not a full migration drain.
- Exercise live scheduled results, world-change pausing and cancellation/ACK
  behavior after the staged plugin is installed through an authorized game
  maintenance step.
- The user will test downloading item 914445138 and starting ship pursuit in
  the game. Do not start this scenario automatically. Native component and
  virtual compile passes do not establish that the ship scenario passes.
- Prepare and validate a reproducible production package and a single-engine
  cutover. Production SE-mind is intentionally STOPPED by the user's later
  instruction; do not start either the old or the new stack as part of staging.

## Production staging, without starting services

The following files still describe the old production engine. Do not broadly
rewrite the main site workspace while its concurrent changes are in progress.

| File                                                                        | Required staged change                                                                                                                                                                                                                               |
| --------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `semind/compose.prod.yaml` (new)                                            | Production LibreChat/MongoDB/Redis and Code API service definitions, distinct production volumes, pinned builds and readiness checks. Package the gateway and its Steam helper reproducibly; the dev host ports alone are not a production launcher. |
| `semind/librechat.prod.yaml` (new) and private production environment       | Explicit HTTPS portal, parent identity API, gateway/model and Code API endpoints, per-environment credentials, schedules and personal-owner policies. Never reuse dev databases or publish credentials.                                              |
| Main site `server/compose.prod.yaml`                                        | Remove old `assistant-runtime`, `assistant-game`, `openwebui`, `assistant-bootstrap` and `assistant-worker` from the future active engine configuration; wire the new chat route. Preserve historical volumes for offline export and rollback.       |
| Main site `server/deploy/assistant.caddy`                                   | Replace Open WebUI SSO/runtime/Socket.IO routes with the LibreChat HTTPS auth, API, SSE and WebSocket route; keep gateway/model/internal endpoints private.                                                                                          |
| Main site `server/deploy/Caddyfile.prod` and `server/deploy/Caddyfile.edge` | Point the chat origin at that route, preserving the public TLS/tunnel topology and trusted client-IP boundary.                                                                                                                                       |
| Main site `server/compose.prod.yaml` legacy `hermes.prod.caddy` mount       | Remove the unused old engine mount and routes; do not keep aliases to old contracts.                                                                                                                                                                 |
| Main site `server/.env.prod` (private) and deployment templates             | Align the neutral assistant URL and identity/runtime endpoints with the staged production package. Keep secrets outside Git and build contexts.                                                                                                      |

Validate the staged Compose/Caddy/config files and built artifacts without `up`
or `start`. Finish the offline archive/export and the remaining gates above, then
perform a future user-authorized cutover with exactly one active engine. Preserve
the current production STOP state until that authorization arrives. The neutral
`server/prod.ps1` wrapper can remain unless the selected Compose layout requires
a different project file.
