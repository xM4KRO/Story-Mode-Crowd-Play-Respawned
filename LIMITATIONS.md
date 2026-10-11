# Requirements and limitations

## Supported installations

`CrowdPlay-Respawned.exe` supports the two tested Windows GOG releases of *Minecraft: Story Mode* on 64-bit Windows. The patch helper embeds 64-bit Python. The installer checks exact hashes of each season's executable and required original archives. Other storefront editions, game updates, modified stock archives, and other operating systems have not been validated and may be rejected before installation. Each season must be installed in its own matching game folder. The game must be closed for installation and removal, and the host and installer require the Windows .NET Framework 4.8 runtime.

**Season 2 must start Crowd Play from the dedicated Crowd Play menu, not the episode selector.** The episode selector is not a supported way to initialize the mod's room.

## Network availability

- Local mode uses unencrypted HTTP over a private network. Guests need a route to the host. A firewall, isolated guest Wi-Fi, VPN, or unavailable private IPv4 address can prevent access.
- Online mode depends on Cloudflare Quick Tunnels and internet access. The public URL can take time to appear, change after a tunnel restart, or stop working if the third-party service or connection fails. The mod has no permanent hostname or uptime guarantee.
- The host sends a request through the public tunnel about every 15 seconds while Online mode is active. This reduces idle periods but cannot guarantee tunnel availability. Browser voting itself uses short HTTP polls, not a persistent WebSocket.
- Turning Crowd Play Off closes the room and invalidates the prior code. Switching Online to Local stops the tunnel. Guests may need a new URL after a mode change or tunnel restart.

## Voting and capacity

- Automatic polls depend on eligible story choice events exposed by the supported game builds. The in-game manual poll interface accepts 2–4 choices of up to 80 characters and a 5–120 second duration. The host API's broader limits are 2–6 choices and 1–120 seconds.
- Pausing the game freezes an episode story poll. A manual poll keeps running because it uses wall-clock time.
- Browser cookies identify votes, not people. Clearing cookies or opening another browser can create another voting identity. There are no guest accounts, room passwords, moderation tools, room lock, or strict one-person-one-vote guarantee.
- The game bridge reports a crowd maximum of 50 and represents at most 50 guests in its legacy player list. The host does not enforce a 50-browser connection limit; practical capacity depends on the host computer and network.
- The host's in-game selection contributes to the displayed totals. Ties can require a random selection. If no one votes, the mod supplies no winner and leaves the choice to the game.
- Room, guest, and vote state are held in memory and disappear when the host exits. There is no account database or cross-session vote history.

## Installation and source builds

The installer will not overwrite an unrelated file or silently replace a changed mod file. A mismatched or old installation without a recognized ownership manifest may need its original uninstaller. The uninstaller removes manifest-owned files and known runtime settings and reports; it does not delete unrelated game-generated files.

The public source package deliberately omits complete stock game files, private modified sidecars, loose generated binary patches, and some build inputs. The included release EXE contains its patch payload, but cannot be rebuilt from a clean checkout alone. [BUILDING.md](BUILDING.md) lists the required lawful local inputs and pinned tools. See [LEGAL.md](LEGAL.md) for the release's rights scope.

