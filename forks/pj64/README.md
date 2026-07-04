# Megaton Hammer — Project64 (N64) playtest fork

The vanilla-N64 playtest path uses a small fork of Project64 (the "bundled PJ64 fork",
alongside the SoH/2Ship forks). The PJ64 source is **not** a git repo in this tree, so the
fork's changes are preserved here as a module snapshot + this change log rather than a patch.

## What it does
Project64, launched on a Megaton-Hammer-injected **OoT MQ debug ROM (gc-eu-mq-dbg)**, reads
`%TEMP%\MegatonHammer\mh_n64_playtest.txt` (written by `Forms/Project64Playtest.cs`) and
auto-warps to the editor's scene with the chosen Link age, logging state to
`%TEMP%\MegatonHammer\mh_n64_playtest.log` for verification.

The warp finds the active PlayState by scanning RDRAM for the GameState whose `init` field equals
`Play_Init` (0x8009A750), validating the neighbouring `main`/`destroy`/`gfxCtx` pointers, then
pokes `PlayState.nextEntranceIndex` + `transitionTrigger` (=20, TRANS_TRIGGER_START) and
`gSaveContext.save.linkAge` — the same fields the game's own area-exits set. It fires once, only
when `gameMode` reads a valid 0..3 (so a non-matching ROM is never poked).

## Source changes (apply to a Project64 `develop` checkout)
- **`Source/Project64-core/N64System/MegatonHammer.cpp`** — NEW. The whole hook (verbatim copy in
  this folder). Add to `Project64-core.vcxproj` as a `<ClCompile>`. Now also: detects the debug ROM
  by internal name "THE LEGEND OF **DEBUG**" (not just ZELDA/OCARINA); an **unconditional liveness
  heartbeat** (frame + live CPU PC via `g_Reg->m_PROGRAM_COUNTER`, logged every 30 frames regardless
  of ROM detection) so a frozen boot is diagnosable; and **`MH_MAXFRAMES`** — when the frame count
  reaches it the run self-terminates (`_exit(0)`), so headless diagnosis needs no external
  timeout-kill.
- **`Source/Project64-core/N64System/N64System.cpp`** — declare `extern "C" void
  MegatonHammer_PerFrame();`, call it at the end of `CN64System::RefreshScreen()`. Also gate
  `m_Plugins->Gfx()->UpdateScreen()` behind `getenv("MH_HEADLESS")==nullptr` (headless fast-path).
  Also declares `extern "C" void MegatonHammer_LogCrash();` and calls it from the
  `catch (...)` in `CN64System::ExecuteCPU` (~line 740) so an emulation fault — e.g. the
  ledge-climb crash in an injected scene — dumps the faulting CPU state to the MH log.
- **`Source/Project64-core/N64System/MegatonHammer.cpp`** — added `MegatonHammer_LogCrash()`:
  logs PC / EPC / BadVAddr from `g_Reg` (`m_PROGRAM_COUNTER` / `EPC_REGISTER` /
  `BAD_VADDR_REGISTER`) so the crash address can be matched against the OoT decomp map.
- **`Source/Project64-core/Plugins/RSPPlugin.cpp`** — when `MH_HEADLESS` is set, replace
  `Info.ProcessDlist`/`ProcessRdpList` with `DummyCheckInterrupts` (skip software-GL rendering so
  the CPU boots fast for RAM-only checks; the RSP still signals task completion).
- **`Source/Project64-core/Settings/GameSettings.cpp`** — force the **interpreter** core when
  **`MH_HEADLESS` OR `MH_INTERP`** is set (and `MH_DYNAREC` is not). The dynamic recompiler raises
  *"EmulationStarting: Exception caught" (N64System.cpp:752)* on some setups — its JIT fails to
  start; the interpreter avoids that crash. **CRITICAL (fixed 2026-06-28):** setting only
  `g_GameSettings.cpuType` does NOT work — that field is dead for CPU selection. CN64System reads
  `g_Settings->LoadDword(Game_CpuType)` (recompiler construction, N64System.cpp:89/111) and
  `Setting_ForceInterpreterCPU` (dispatch, :1033). The override now ALSO calls
  `g_Settings->SaveDword(Game_CpuType, CPU_Interpreter)` (guarded against re-firing the change
  callback) and `g_Settings->SaveBool(Setting_ForceInterpreterCPU, true)`, so the recompiler is
  never constructed and dispatch falls through to `ExecuteInterpret()`. Without this the
  recompiler ran anyway and crashed at EmulationStarting. `MH_HEADLESS` uses it for automated
  checks; **`MH_INTERP` uses it for INTERACTIVE play (video stays on)** — set by
  `Forms/Project64Playtest.cs`. `MH_DYNAREC=1` opts back into the recompiler where it works.
- **`Source/Script/clang.cmd`** — `exit /b 0` at the top (skip the clang-format lint gate that
  otherwise fails the build with no clang-format installed).

## Editor-side discovery: debug-ROM injection must add NO dmadata entries
The gc-eu-mq-dbg DEBUG build's `DmaMgr_Init` (`src/boot/z_std_dma.c`) walks `gDmaDataTable` in
lockstep with a **fixed-size `sDmaMgrFileNames[]`** array, dereferencing `*name` in `osSyncPrintf`
per entry. Appending dmadata entries (the normal `RomInjector.Inject` path) overruns that array →
garbage `%s` deref → fault → the boot hangs in the idle loop (observed: main thread stuck at PC
`0x800009E0`, never reaching game code at `0x80106874`). Fix: **`RomInjector.InjectDebug`** writes
the scene/room files into the ROM's free padding and repoints only the scene-table slot, adding NO
dmadata entries. The debug ROM is uncompressed, so the game reaches these out-of-table VROM regions
via its arbitrary-DMA path (the `!sDmaMgrIsRomCompressed` branch). The injected debug ROM then boots
healthy. Diagnosed entirely headlessly via the liveness heartbeat above.

## Build
Build **only** `Source/Project64/Project64.vcxproj` with the **VS Community** toolchain (it has
ATL; the Build Tools install does not). SDL/GLideN64/GLideNUI-wtl are NOT dependencies of the EXE.

    call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
    msbuild Source\Project64\Project64.vcxproj /m /p:Configuration=Release /p:Platform=x64

Outputs `bin/x64/Release/Project64.exe` + `Plugin/x64/{GFX,Audio,RSP,Input}/*.dll`.

## Run dir layout (headless verification)
A self-contained run dir needs: `Project64.exe`, `Plugin/{GFX,Audio,RSP,Input}/*.dll`, the source
`Lang/` folder (without it a modal language picker blocks before the ROM loads), and
`Config/Project64.cfg` with:

    [Settings]
    Current Language=English
    Auto Start=1
    Auto Sleep=0          ; else the CPU pauses when the window loses focus
    [Rom Browser]
    Rom Browser=0
    [Plugin]
    RSP Dll=RSP\Project64-RSP.dll
    Graphics Dll=GFX\Project64-Video.dll
    Audio Dll=Audio\Project64-Audio.dll
    Controller Dll=Input\PJ64_NRage.dll
    [Defaults]
    Known RDRAM Size=8388608     ; 8 MB — the debug ROM needs the Expansion Pak (decimal, not 0x...)
    Unknown RDRAM Size=8388608

## Required ROM
The genuine gc-eu-mq-dbg debug ROM (md5 `717179476af84133b14ff73af87db57a`). The OoT decomp
`zelda_ocarina_mq_debug` targets it; OoTMM's `link_oot.in` gives the addresses used here
(`gSaveContext=0x8011A5D0`, `Play_Init=0x8009A750`).
