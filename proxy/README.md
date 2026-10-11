# DirectInput proxy

The installer places a season-specific proxy in the game folder as `dinput8.dll`: x86 for Season 1 and x64 for Season 2. The proxy forwards the six DirectInput 8 exports to the Windows system DLL. On the first `DirectInput8Create` call, it starts `CrowdPlayHost.exe` for that game process. If the host is unavailable, DirectInput still forwards to Windows.

`dinput8_proxy.c` is the shared source. The `.def` files list the exports for each architecture. The release DLLs were compiled with TinyCC 0.9.27; the x86 build required `fix_tcc_exports.py` to remove `WINAPI` name decoration. `inspect_pe.py` can inspect the PE machine type and exports. The public source does not contain the tested DLL binaries as loose files; their required release hashes and role in the final build are in [BUILDING.md](../BUILDING.md). The checked-in installer already contains the compiled proxies.

After rebuilding either proxy, verify all six public export names against `dinput8_proxy.def` and test on an isolated copy of the matching game. Do not put a proxy for one architecture in the other season's folder.
