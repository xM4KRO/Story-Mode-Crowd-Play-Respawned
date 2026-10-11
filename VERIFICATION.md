# Release verification

This source folder includes the exact current patch-based `CrowdPlay-Respawned.exe`. Its SHA-256 is `6284032D22395B772DD616177F9F08D4E47A624BC255290D148B37BA20F0E0AC` and its size is 48,954,368 bytes. The copied EXE matches the final build artifact and `build/build-report.json` byte for byte. [RELEASE-CHECKSUMS.txt](RELEASE-CHECKSUMS.txt) gives the check command.

We checked the public source tree for complete `.ttarch`/`.ttarch2` archives, `.modentry` files, loose `.patch` files, game executables, and extra DLL or EXE binaries. It contains none of those except the single current installer EXE. The release payload auditor reported zero full game archives and zero bundled game artwork in each season's payload. The payloads do contain binary deltas; they are inside the reviewed installer and are not published as loose source files.

The copied build code and assets match the current local-patch build tree by SHA-256. The three project-authored Lua files match the current mod source. All Python source files parsed, and the C# host, installer, and uninstaller compiled from this folder using the already generated local patch payloads. Local Markdown links were checked. These checks confirm packaging and compilation, but they are not a substitute for installing and playing on every machine or independently auditing every dependency.

The mod's behavior was tested in both seasons before this source package was prepared. This source-release work did not change the installer binary or the installed game files.
