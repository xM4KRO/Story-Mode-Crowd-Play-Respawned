# CrowdPlay Respawned

YouTube Showcase Link: https://www.youtube.com/watch?v=5s3w1kBAH7M

Reddit Post: https://www.reddit.com/r/MinecraftStoryMode/comments/1x3h8dm/title_i_revived_crowdplay_for_s2_and_introduced/

CrowdPlay Respawned adds browser voting to the supported 64-bit Windows GOG releases of *Minecraft: Story Mode* Season 1 and Season 2. A host plays the game while guests use a phone or desktop browser to vote on story choices or host-created polls. The same installation supports a local network room and an Online room using a Cloudflare Quick Tunnel.

This is our first public release. The repository includes the current patch-based `CrowdPlay-Respawned.exe` and the source used for its installer, host, proxy, voting pages, and patching pipeline. It contains no complete stock game archives, original game executables, or private modified sidecar archives. Older installer versions are not part of this release.

## Install and play

1. Own and install a supported GOG copy of the season you want to mod. Close the game.
2. Run `CrowdPlay-Respawned.exe`, choose the season, and select that season's game folder. The installer checks the original game files, builds its mod files from them locally, and installs loose overrides. Run the EXE again for the other season if needed.
3. Open the game's Crowd Play menu and turn Crowd Play **On**. **In Season 2, start Crowd Play from the dedicated Crowd Play menu, not from the episode selector.**
4. Share the displayed room code, full join link, or QR code with guests. Local mode works on a reachable LAN. Online mode displays a generated HTTPS link after its tunnel connects.
5. Start an episode choice or a manual poll. Guests vote in the browser; the host's selected choice is shown and counted too.

The voting pages resize for phones and desktop screens. They use season-specific banners, a dirt background, the included font, a room-code disclosure control, and a custom cursor reconstructed from the owner's game files during installation. The game menus include guest refresh, new code, QR generation, vote controls, and a debug report. Story-choice timers stop while the game is paused; manual polls use their own duration.

See [USAGE.md](USAGE.md) for the full installation and voting guide, [TECHNICAL.md](TECHNICAL.md) for the host and game integration, and [LIMITATIONS.md](LIMITATIONS.md) for supported builds and network limits.

## Remove the mod

Close the game and run `UninstallCrowdPlay.exe` from its game folder. Each season has its own uninstaller. It checks the installed-file manifest, removes files owned by CrowdPlay Respawned and known generated settings, and leaves original game files in place. It stops for review if an owned file was changed after installation.

## Source and rebuilding

The project code is available here under [PolyForm Noncommercial 1.0.0](LICENSE.md). That license applies to project-owned code and artwork; bundled third-party components keep their own licenses. [THIRD-PARTY-LICENSES.txt](THIRD-PARTY-LICENSES.txt) identifies those components and their notices.

The source **can be compiled**, but a clean public checkout cannot reproduce the release EXE by itself. Building requires the supported unmodified copies of both games, our private modified sidecar build as patch targets, the pinned Python, xdelta3, and Cloudflared inputs, Windows build tools, and the included assets. The private sidecars are intentionally excluded because they contain game-derived material. [BUILDING.md](BUILDING.md) gives the exact input layout, commands, validation steps, and reproducibility limits. The checked-in EXE is the release artifact built from those inputs. [VERIFICATION.md](VERIFICATION.md) records the release checks. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to keep future source changes game-file free.

## Security and project status

Windows security software may describe the unsigned EXE as a virus or unwanted application because it installs a proxy DLL beside a game, starts a local host process, and can start a tunnel process. Those are part of how this mod works. A warning does not by itself establish that the file is malware. The release is intended to perform only the mod functions described here; verify the [published SHA-256 hash](RELEASE-CHECKSUMS.txt), scan the file, and review the source before running it. We cannot guarantee that any software is completely safe. [SECURITY.md](SECURITY.md) describes the network and data boundaries.

This is an independent fan project. It is not affiliated with or endorsed by Mojang, Microsoft, Telltale, GOG, or Cloudflare. You need your own supported game installation. The project license does not grant rights to anyone else's game, marks, art, or software. See [LEGAL.md](LEGAL.md) for the rights and redistribution scope.
