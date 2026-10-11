# Building CrowdPlay Respawned

This document describes the build of `CrowdPlay-Respawned.exe`. The installer source is available here, but a clean checkout **cannot compile the release EXE by itself**. We do not commit stock game files or the private modified game sidecars used as patch targets. The checked-in EXE is a release artifact; it contains the binary deltas and installation tools that its source build produces. The deltas are not also committed as loose files.

Building requires the exact supported, unmodified Windows GOG installations of both seasons **and** the private modified reference build for both seasons. Owning only the original games is not enough to regenerate the current patches. Keep private target sidecars, locally extracted game scripts, and generated patch files out of the source repository.

## Inputs

Use Windows and supply these inputs before running `build_release.py`:

| Input | Required location or role |
| --- | --- |
| Python 3.12 developer runtime | Runs `build_release.py` and the developer patch generator. This is separate from the consumer runtime embedded below. |
| PowerShell and .NET Framework 4.8 C# compiler | `installer/BuildHost.ps1` and `installer/Build.ps1` use `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe`. The finished installer and host need .NET Framework 4.8 on the consumer's Windows PC. |
| Clean Season 1 GOG game folder | Contains `GameApp.exe`, original Project and Menu archives, and the Project texture archive used for the cursor. The executable and required archive hashes must match `installer/InstallCore.cs`; the patch manifest checks the texture entry. |
| Clean Season 2 GOG game folder | Contains `Minecraft2.exe`, corresponding original Project and Menu archives, and the Project texture archive used for the cursor. The same exact-build checks apply. |
| Private modified reference sidecars | `--modified-build-root` must contain the four files per season shown below. These are the tested target bytes for patch generation, not public source assets. |
| Python 3.12.10 embeddable Windows x64 ZIP | Put the official `python-3.12.10-embed-amd64.zip` at `build/python-3.12.10-embed-amd64.zip`, or pass its local path with `--python-embed`. SHA-256: `4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3`. Obtain it from the [Python 3.12.10 release directory](https://www.python.org/ftp/python/3.12.10/). Its license is packed with the runtime. |
| xdelta3 v3.2.0 Windows executable | Put `xdelta3.exe` at `tools/xdelta3/xdelta3.exe`. SHA-256: `6E812B38484D0C764291779FADEE83FFBE2ECEACCB4E3A21F8A043A02654A01E`. Obtain the matching release from [xdelta](https://github.com/jmacd/xdelta/releases/tag/v3.2.0). Its Apache 2.0 license is at `tools/xdelta3/LICENSE`. |
| Cloudflare `cloudflared` 2026.9.3 | Put `cloudflared-windows-386.exe` at `third_party/cloudflared/cloudflared-windows-386.exe`. SHA-256: `9B95DDC2EBA67B86ED3DC4CC2A15881960563031B52CE564376AF41FB91AD402`. Use [Cloudflare's 2026.9.3 release](https://github.com/cloudflare/cloudflared/releases/tag/2026.9.3) and retain `third_party/cloudflared/LICENSE` (Apache 2.0). |
| Two DirectInput proxy DLLs | Put the tested `dinput8_x86.dll` and `dinput8_x64.dll` at `proxy/`. Season 1 needs x86; Season 2 needs x64. Their C source and export definitions are in `proxy/`, but the release script does not compile them. |
| Voting resources | `installer/assets/` contains the two project banners, dirt background, installer icon, web pages, QR encoder, and the separately licensed `Minecraft.otf` voting font. Keep the adjacent license notices. |
| Archive inspection code | `tools/vendor/ttg_cli_inspector/` is the vendored MIT-licensed archive reader/packer used by the patch helper. `tools/vendor/ttg-cli-inspector-LICENSE.txt` carries its notice. No separate Python package install is required for it. |

The source build expects these private target paths under `<private-build>`:

```text
<private-build>/s1/Archives/MCSM_pc_CrowdPlayLAN_Project.ttarch2
<private-build>/s1/Archives/MCSM_pc_CrowdPlayLAN_Menu.ttarch2
<private-build>/s1/Archives/_resdesc_50_CrowdPlayLAN_Project.lua
<private-build>/s1/Archives/_resdesc_50_CrowdPlayLAN_Menu.lua
<private-build>/s2/Archives/MC2_pc_CrowdPlayLAN_Project.ttarch2
<private-build>/s2/Archives/MC2_pc_CrowdPlayLAN_Menu.ttarch2
<private-build>/s2/Archives/_resdesc_50_CrowdPlayLAN_Project.lua
<private-build>/s2/Archives/_resdesc_50_CrowdPlayLAN_Menu.lua
```

Those sidecars are not original game archives, but they contain modified game entries. The repository includes our project-authored bridge and overlay sources. It does not include the private modified stock-script sources or a way to regenerate the exact eight target sidecar files solely from a clean game. Regenerating those targets requires the separate, lawful game-integration development inputs and a compatible game Lua compiler. The release build described below begins with already-built private targets; it does **not** call a Lua compiler.

The release's proxy DLLs were built with TinyCC 0.9.27 for x86 and x64. `proxy/dinput8_proxy.c` is shared source. The x86 `WINAPI` exports need the plain DirectInput names: after compiling with an x86 TinyCC toolchain, run `python proxy/fix_tcc_exports.py proxy/dinput8_x86.dll` and inspect the six exports before using it. A 64-bit TinyCC toolchain produces `proxy/dinput8_x64.dll`. The release binaries have SHA-256 values `D215CC8FD4EDD23C2F4BC9C9597983CF29175F53765BFDD5FC31F36272D37DCA` (x86) and `F63F86EB6383952555C5B17EC4A6D5CA499BC343F8053964A38D6F3F3ECF95B6` (x64). The build script accepts files at these paths but does not pin their hashes; verify and test any replacement DLLs separately.

The release build pins `S1_Banner.png` to SHA-256 `3166B641257DAB0E2A42537E9956F1E8934EE736D6BCCFE47FDE80FEA60BB658`, `S2_Banner.png` to `56FE183A7E43FBBEA74D2F14BC9C2FFA8085D4AF14FF79BD7D5DA6DA8164103F`, and `Minecraft.otf` to `EBC7A7BE9F69479F02875AF1FE79A2B93CC868B83BE4F6369ECCEB45FABE5EE6`. The font's SIL Open Font License notice remains in `installer/assets/Minecraft-OFL.LICENSE`.

## Build command

Run this from the repository root in PowerShell after placing the required tools and proxy binaries. Replace the example paths with folders on your own machine; do not commit those local paths or inputs.

```powershell
$python = 'python' # Python 3.12 developer runtime on PATH
& $python .\build_release.py `
  --season1-game-root 'D:\Games\Minecraft - Story Mode' `
  --season2-game-root 'D:\Games\Minecraft Story Mode - Season Two' `
  --modified-build-root 'D:\Private\CrowdPlayTargetBuild' `
  --python-embed '.\build\python-3.12.10-embed-amd64.zip' `
  --output '.\build\rebuilt\CrowdPlay-Respawned.exe'
```

`build_release.py` runs the following steps in order:

1. `tools/generate_patches.py` reads each clean game archive and its private modified sidecar target. It decrypts the relevant stock Lua entries locally, generates xdelta3 patches against the target bytecode, and writes a manifest with base, patch, and result hashes. The two Season 1 entries that have no stock counterpart are stored as mod-created entries. The consumer helper re-encrypts reconstructed results when it packs the sidecars.
2. `tools/build_tools_zip.py` bundles the pinned Python embeddable runtime, xdelta3, the installer patch helper, the vendored archive code, and licenses. These are temporary installation tools; they are not left in the game folder.
3. `installer/BuildHost.ps1` compiles `CrowdPlayHost.exe` and embeds the HTML and QR encoder source into it.
4. `build_release.py` creates distinct Season 1 and Season 2 payload ZIPs containing the matching proxy DLL, host, tunnel, font, dirt background, and patch manifest/files. `tools/audit_payloads.py` rejects full stock archives or unexpected install files in those payloads.
5. `installer/Build.ps1` compiles the uninstaller and installer, embeds the two season payloads, temporary tools, and two season banners, and applies the icon. The top-level script copies the result to `CrowdPlay-Respawned.exe` and writes `build/build-report.json` with hashes.

The consumer's EXE uses the user's own matching game files to reconstruct the sidecars and cursor during installation. It does not ship full stock game archives. Binary deltas and mod-created entries in the EXE are nevertheless game-related release content; this build design alone is not a rights determination.

## Verify a build

The release EXE's SHA-256 is `6284032D22395B772DD616177F9F08D4E47A624BC255290D148B37BA20F0E0AC`. Check the file you intend to distribute and compare the payload hashes in `build/build-report.json`. A different hash means the bytes differ; it does not automatically mean the program is broken.

Run `tests/PackagedPatchSmoke.ps1` with the required local game and private target inputs to check the *packaged* Python/xdelta helper against both seasons' reference sidecars. Run `tests/InstallerSmoke.ps1` only in isolated copies of the supported games to check install, repeat install, and uninstall. The smoke tests require inputs omitted from a public checkout and should never be pointed at a live game installation just to make a source release pass.

The build ZIPs use fixed metadata, but we do not claim a byte-for-byte reproducible EXE from an arbitrary Windows setup. Exact output depends on the reference sidecars, pinned binaries and assets, proxy DLLs, compiler output, and build environment. The source and documented inputs explain how the EXE is assembled; they are not a substitute for the omitted private targets.

Do not add clean game files, extracted scripts or textures, private target sidecars, reconstructed `Archives/` outputs, generated patch directories, or test game copies to the public repository. Keep `build/`, `generated/`, bytecode caches, and temporary reconstruction folders out of source control. The checked-in release EXE is the separately reviewed binary artifact; its embedded patch content must be assessed on its own terms.
