# Security and privacy

CrowdPlay Respawned runs a voting server on the host's computer. Share the join URL and room code only with intended guests. The room has no accounts, password, lock, or moderation system. A browser cookie helps a guest change an earlier vote; it is not a strong identity check.

## Network boundaries

The game-to-host control listener is loopback-only: port 17861 for Season 1 and 17862 for Season 2. Phone requests cannot use its `/control/*` routes. The control listener rejects browser navigation headers and unexpected Host values. These checks separate the phone interface from the game bridge; they do not protect against another program already running on the host computer.

The phone server listens on loopback and reachable private IPv4 addresses. Its default ports are 26844 and 26845, with up to ten higher ports tried when needed. Local mode uses **unencrypted HTTP**. On an untrusted local network, other parties may be able to observe traffic.

Online mode starts a hidden `cloudflared.exe` child process and exposes the phone server through a generated HTTPS `trycloudflare.com` URL. Browser TLS terminates at Cloudflare; traffic then reaches the local host through the tunnel. Cloudflare is a third party and may process connection metadata and voting traffic under its own terms. The host closes the managed tunnel when Online mode ends, Crowd Play is turned Off, or the game process exits. It also uses a Windows job object where possible to limit orphaned tunnel processes after an abrupt exit.

While Online mode is active, the host requests its public `/keepalive` route about every 15 seconds. The browser separately polls for state about every 400 ms. There is no project-operated remote vote database and no persistent voting WebSocket.

## Data on the host

The current room code, guest activity, cookie IDs, and votes are kept in the host process's memory. Guest cookies are marked `HttpOnly` and `SameSite=Lax`; the recognized tunnel host also receives the `Secure` flag. These settings reduce some browser exposure but do not provide one-person-one-vote enforcement. Inactive guest entries leave the displayed count after about 30 seconds.

Host preferences are stored in `CrowdPlayLAN/host-settings.xml` in the game folder. **Save Debug Report** creates a local file containing the room code, local address, current Online address if present, guest count, poll identifiers and aggregate vote counts, and host settings. Review or delete that report before sharing a game folder or filing a public issue. The uninstaller removes known generated settings and report files.

## Installer warnings and review

The installer is an unsigned Windows application. It places a DirectInput proxy DLL beside the game, runs a local host process, and can start a managed tunnel process. Those behaviors can trigger security software warnings. A warning alone does not determine whether a file is harmful, and we cannot guarantee that any build is completely safe. Obtain the installer from the project release, check its published hash, and inspect the source and third-party components if you need further assurance. The [technical design](TECHNICAL.md) explains the processes and network routes.

Please do not post active room links, private network addresses, diagnostic reports, or exploit details in a public issue. Use GitHub private vulnerability reporting when available; otherwise, open a brief issue without sensitive details to request a private contact channel.
