# Using CrowdPlay Respawned

CrowdPlay Respawned adds browser voting to the supported GOG releases of *Minecraft: Story Mode* Season 1 and Season 2 on 64-bit Windows. Guests vote from a phone or desktop browser while the host plays. This guide describes `CrowdPlay-Respawned.exe`.

## Install

1. Close the game.
2. Run `CrowdPlay-Respawned.exe`, select the season, and choose that season's game folder. Season 1 contains `GameApp.exe`; Season 2 contains `Minecraft2.exe`.
3. Wait for the installer to verify the game and finish rebuilding its mod files. It uses the original files in your own game installation. If the selected game build is not supported, installation stops.
4. To install both seasons, run the same EXE once for each game folder.

The installer includes the patching tools, both season banners, the voting font, and the other mod components. You do not need to supply a separate banner folder. It leaves the game's original executable and archives in place. See [Technical design](TECHNICAL.md) for the installed files and the [build guide](BUILDING.md) for source build requirements.

## Host a room

**For Season 2, start Crowd Play from the dedicated Crowd Play menu. Do not start it from the episode selector.** Turn Crowd Play **On** there before launching an episode. In Season 1, open the game's Crowd Play menu and turn it **On**.

The room starts in **Mode: Local**. The **Address** button shows the host's LAN address and active port. Guests on a reachable local network can use the room code, the copied full join link, or **Generate QR Code**. The QR button opens a scannable page in the host's default browser.

Select **Mode: Online** to host through a Cloudflare Quick Tunnel. Wait until **Address** changes from Connecting to the generated `https://…trycloudflare.com` address. The copied join link and QR code then use that address. Online mode needs an internet connection and Cloudflare availability. Switching back to Local changes the shared address back to the local server.

The Crowd Play menu also shows the room code and guest count. **Guests (Refresh)** requests the latest count. **New Code (Guests Rejoin)** replaces the code; the old join link stops working and guests must rejoin. In Season 1, the other controls are under **Host controls and manual poll**. In Season 2, they are under **LAN tools and manual poll**.

**Who decides?** selects whether the host's choice or the crowd outcome controls a story choice. **Phone totals** controls whether guests see live vote totals; final totals still appear after voting. Season 1's in-game overlay follows Crowd Play's enabled state. Season 2 retains its native **Display Game Code** setting. **Save Debug Report** writes a local report for troubleshooting.

## Vote from a browser

1. Open the host's join link, scan the QR code, or enter the eight-character room code on the code-entry page.
2. Keep the page open until a poll appears. Eligible episode story choices start polls automatically. The host can also start a manual poll from the in-game controls.
3. Select a choice. You can change your vote while the poll is open. The page shows the host's selected option and, when enabled, live totals. It then shows the result before returning to the waiting state.

The room code can be hidden or revealed on the voting page. **Leave Room** removes the browser from the displayed guest count, but a vote already cast in the current poll remains counted. If the room disconnects, **Join Another Room** opens the code-entry form.

Pausing the game freezes the browser timer for an episode story choice. Manual polls use their own wall-clock duration and continue while the game is paused. The manual poll menu accepts two to four choices of up to 80 characters each and a duration from 5 to 120 seconds.

## Stop or remove the mod

Turning Crowd Play **Off** closes the room and invalidates its code. In Online mode, switching to Local, turning Crowd Play Off, or closing the game stops the managed tunnel. A tunnel or network failure may require guests to use a newly generated link.

To uninstall, close the game and run `UninstallCrowdPlay.exe` in that game's folder. Run each season's uninstaller separately if you installed both. The uninstaller uses the recorded ownership manifest to remove the mod's files and generated settings while preserving unrelated files and the original game files. It stops rather than silently deleting an installed mod file that has changed since installation.

