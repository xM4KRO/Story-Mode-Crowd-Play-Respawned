# Technical design

CrowdPlay Respawned connects game choice events to a browser voting server on the host's computer. `CrowdPlay-Respawned.exe` contains separate Season 1 and Season 2 payloads. The games use the same host architecture, with season-specific integration, proxy DLLs, ports, and banners. **Season 2 requires starting Crowd Play from its dedicated Crowd Play menu, not the episode selector.**

## Installation and ownership

The installer first checks the selected game executable and original archives against the supported GOG build hashes. It extracts its patch helper, applies bundled xdelta3 patches to bytes read from the user's own game archives in a temporary directory, rebuilds four mod sidecar files, and reconstructs the voting cursor. Base, patch, and result hashes are checked before files are placed in the game folder. Season 1's cursor comes from its own archive; a Season 2 texture is decoded and recolored to the matching cursor pixels. The installer does not replace the original executable or stock archives.

Each season receives two loose `CrowdPlayLAN` `.ttarch2` sidecar archives, two resource descriptors, a season-specific `dinput8.dll` proxy, `CrowdPlayHost.exe`, `cloudflared.exe`, voting resources, `UninstallCrowdPlay.exe`, and `CrowdPlay.install.xml`. The installer embeds the matching voting banner and installs it automatically. Runtime settings and a requested debug report live under `CrowdPlayLAN`.

The manifest records installed paths and SHA-256 hashes. A repeat installation validates the existing manifest and owned files, uses a staged transaction, and does not overwrite an unrelated file. The uninstaller stops the matching host, checks the manifest, removes owned and known runtime files, and leaves stock game files intact. If an owned file has changed, it stops for review rather than discarding that change.

## Processes and ports

The season-specific DirectInput 8 proxy forwards the game's normal system-library exports and launches the host for that game process. Season 1 uses a 32-bit proxy; Season 2 uses a 64-bit proxy. The host watches the game process and exits when it closes, including after a crash. The host and tunnel are separate child processes, not a project-run remote voting service.

| Season | Default phone/browser port | Loopback game-control port |
| --- | ---: | ---: |
| Season 1 | 26844 | 17861 |
| Season 2 | 26845 | 17862 |

If the default phone port is occupied, the host tries up to ten higher ports and advertises the port it bound. The game-control listener binds to loopback only. The phone listener binds to loopback and available private IPv4 addresses. The game bridge sends control commands and reads poll state through the loopback channel.

Local mode advertises an HTTP join link using the host's private address. Online mode starts a hidden `cloudflared.exe` Quick Tunnel to the active local phone port, reads the generated HTTPS `trycloudflare.com` URL, and supplies it to the game menu, clipboard action, and QR page. The host sends an HTTP request through the *public* `/keepalive` URL about every 15 seconds while Online mode is active. It stops the tunnel on Local, Crowd Play Off, or host exit; a Windows job object helps clean up the child after an abrupt host exit. A failed tunnel can be retried, which may change the public URL.

The browser uses ordinary HTTP requests and polls `/api/state` about every 400 ms. Voting does **not** use WebSockets. Public routes include `/`, `/join/CODE`, `/qr/CODE`, `/api/state`, `/api/vote`, and `/api/leave`. The `/control/*` routes belong to the loopback game channel and are unavailable through the phone listener or tunnel.

## Room and vote state

An eight-character random code identifies the current room. Turning Crowd Play Off or requesting a new code invalidates the old link. Guest identity is an in-memory random browser cookie, not an account. A browser can replace its vote while a poll is active. Guest presence expires from the displayed count after about 30 seconds without a request; leaving removes presence immediately but does not retract an already cast vote.

The game bridge starts eligible story polls with the choices and game timer, records the host's in-game selection, and reports pause and result state. A story poll freezes when the game is paused and resumes with its deadline shifted by the paused duration. Manual polls use wall-clock time. The host API accepts 2–6 choices and 1–120 seconds; the in-game manual poll interface limits that to 2–4 choices, 80 characters each, and 5–120 seconds.

The host's selection is marked on the voting page and included in public totals. In Host Decides mode, a valid host choice takes priority. In Crowd Decides mode, the game bridge uses combined host and browser totals. A tie among browser votes is resolved with a cryptographic random selection; for a combined tie, the browser winner is preferred when it remains among the tied choices, otherwise a tied choice is selected at random. If neither host nor browsers vote, the mod provides no winner and lets the game handle the choice. Polls and votes live only in memory; replaying an episode starts a fresh poll.

**Phone totals** controls live count visibility, while final counts remain visible when a poll ends. The pages adapt to phone and desktop screens, use the selected season's banner and the included voting font, and display the cursor reconstructed during installation. The room code can be collapsed. Disconnected guests can open the code-entry form to join another room.

## Source and build boundary

The public source contains our installer, host, proxy, page, and patching code, applicable third-party notices, and the current patch-based `CrowdPlay-Respawned.exe`. It omits complete stock game files, private modified sidecars, generated game bytecode, and loose binary patch payloads. The included EXE contains the deltas needed for installation. A source checkout alone cannot rebuild that EXE: the exact supported clean games, private modified build targets, pinned tool inputs, assets, and Windows build environment are required. See [BUILDING.md](BUILDING.md) for the input list and build procedure.

