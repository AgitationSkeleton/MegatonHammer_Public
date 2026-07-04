using System;

namespace MegatonHammer.Rom;

/// <summary>
/// Patches the OoT gc-eu-mq-dbg DEBUG ROM (ZELOOTMA, "THE LEGEND OF DEBUG") to boot straight into the
/// injected level — the OoT counterpart of MM's auto-boot. OoT's boot gamestate is TitleSetup_InitImpl
/// (SaveContext_Init() + SET_NEXT_GAMESTATE(Title)). We detour it so it keeps SaveContext_Init(), then runs
/// Sram_InitDebugSave() (the debug map-select's save), sets the target entranceIndex, and redirects the next
/// gamestate to Gameplay_Init instead of Title — booting straight into gameplay with no overlay ever loaded.
/// (Detouring mid-Title crashed: the title overlay was live and its state half-built.) Unlike MM, OoT needs
/// no runtime-context reset — the debug-save path already yields a fully playable state (HUD/items).
///
/// Addresses reverse-engineered from ZELOOTMA + the oot decomp (gc-eu-mq-dbg, code rom/vram base
/// 0xA94000 / 0x8001CE60, uncompressed so file offset == rom offset):
///   TitleSetup_InitImpl 0x800C40B0 -> rom 0xB3B250   (detour target; prologue 0x27BDFFE8). Its caller
///                                     TitleSetup_Init already set gameState->destroy = TitleSetup_Destroy.
///   SaveContext_Init    0x80063640   (void)
///   Sram_InitDebugSave  0x800A82C8   (void, no SramContext, no IO)
///   Gameplay_Init       0x800BCA64   (= Play_Init, code-resident)
///   gSaveContext        0x8015E660,  .entranceIndex @ +0x00
///   sizeof(GlobalContext) 0x12518
///   GameState: destroy@0x08, init@0x0C, size@0x10, running@0x98 (u32)
///   free code zero-run @ rom 0xBA8A30 (vram 0x80131890), 0x354 bytes
/// </summary>
public static class OotDebugAutoBoot
{
    private const int TitleSetupImplRom   = 0xB3B250;   // TitleSetup_InitImpl[0]
    private const int RoutineRom          = 0xBA8A30;   // free code zero-run
    private const uint RoutineVram        = 0x80131890;
    private const uint TitleSetupImplPrologue = 0x27BDFFE8;

    // R/I-type MIPS encoders. regs: t0=8 t1=9 t2=10 t3=11 zero=0 sp=29 ra=31 a0=4
    private static uint Lui(int rt, int imm)           => 0x3C000000u | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Ori(int rt, int rs, int imm)   => 0x34000000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Addiu(int rt, int rs, int imm) => 0x24000000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Lw(int rt, int off, int b)     => 0x8C000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Sw(int rt, int off, int b)     => 0xAC000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Jal(uint target)               => 0x0C000000u | ((target >> 2) & 0x03FFFFFFu);
    private static uint Jmp(uint target)               => 0x08000000u | ((target >> 2) & 0x03FFFFFFu);

    private static void W32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }
    private static uint R32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>Applies the auto-boot detour for <paramref name="entranceIndex"/> (e.g. 0x0094 = SCENE_TEST01).
    /// No-op (returns false) unless the ROM is the expected gc-eu-mq-dbg layout (verified via ConsoleLogo_Main's
    /// prologue), so a different/unknown debug ROM is left untouched rather than corrupted. Re-fixes the CRC.</summary>
    /// <summary>True if this is the gc-eu-mq-dbg debug ROM by LAYOUT (TitleSetup_InitImpl's prologue at the
    /// known uncompressed offset), independent of the internal name — that ROM is named "THE LEGEND OF ZELDA",
    /// not "...DEBUG", so name-sniffing missed it and the playtest fell back to the crashing retail path.</summary>
    public static bool IsRecognized(byte[] rom) =>
        rom.Length >= RoutineRom + 0x80 && R32(rom, TitleSetupImplRom) == TitleSetupImplPrologue;

    public static bool Patch(byte[] rom, int entranceIndex, int age = 0)
    {
        if (rom.Length < RoutineRom + 0x80) return false;
        if (R32(rom, TitleSetupImplRom) != TitleSetupImplPrologue) return false;      // not the expected ROM
        if (R32(rom, RoutineRom) != 0) return false;                                  // free space not free

        const int T0 = 8, T1 = 9, T2 = 10, T3 = 11, ZERO = 0, SP = 29, RA = 31, A0 = 4;
        uint[] r =
        {
            Addiu(SP, SP, -0x20),
            Sw(RA, 0x1C, SP),
            Sw(A0, 0x18, SP),                    // save GameState* (the boot gamestate)
            Jal(0x80063640), 0x00000000,         // SaveContext_Init()    (delay nop)
            Jal(0x800A82C8), 0x00000000,         // Sram_InitDebugSave()  (delay nop)
            Lw(A0, 0x18, SP),                    // restore GameState*
            Lui(T0, 0x8016),
            Ori(T1, ZERO, entranceIndex & 0xFFFF),
            Sw(T1, unchecked((short)0xE660), T0),// gSaveContext.entranceIndex (0x8015E660) = entrance
            // Link age BEFORE the scene loads so Player_Init picks the correct model (0=adult, 1=child). The
            // debug save's own linkAge would otherwise stick — the fork's post-load age poke can't swap the
            // already-loaded model. linkAge is the s32 @ gSaveContext+0x04 = 0x8015E664.
            Ori(T1, ZERO, age & 0xFFFF),
            Sw(T1, unchecked((short)0xE664), T0),// gSaveContext.linkAge = age
            // Force DAYTIME (the debug save defaults to night, muting day-only field music). dayTime u16 @
            // gSaveContext+0x0C = 0x8015E66C, nightFlag s32 @ +0x10 = 0x8015E670. dayTime goes in the high
            // half of the word (big-endian) so the low half (padding at 0x0E) stays 0.
            Lui(T1, 0x6000),                     // $t1 = 0x60000000  (dayTime 0x6000)
            Sw(T1, unchecked((short)0xE66C), T0),// gSaveContext.dayTime = 0x6000 (day)
            Sw(ZERO, unchecked((short)0xE670), T0),// gSaveContext.nightFlag = 0 (day)
            Lui(T2, 0x800B), Ori(T2, T2, unchecked((short)0xCA64)),  // Gameplay_Init = 0x800BCA64
            Sw(T2, 0x0C, A0),                    // gameState->init = Gameplay_Init
            Lui(T3, 0x0001), Ori(T3, T3, 0x2518),                    // sizeof(GlobalContext) = 0x12518
            Sw(T3, 0x10, A0),                    // gameState->size
            Sw(ZERO, unchecked((short)0x98), A0),// gameState->running = 0 (u32) -> switch gamestate next frame
            Lw(RA, 0x1C, SP),
            Addiu(SP, SP, 0x20),
            0x03E00008, 0x00000000,              // jr $ra ; nop
        };
        for (int i = 0; i < r.Length; i++) W32(rom, RoutineRom + i * 4, r[i]);
        W32(rom, TitleSetupImplRom, Jmp(RoutineVram));        // TitleSetup_InitImpl[0] -> j routine
        W32(rom, TitleSetupImplRom + 4, 0x00000000);          // [1] -> nop (was `sw $a0,0x18($sp)` with the
                                                              // un-decremented $sp; the routine never falls
                                                              // through to it). SaveContext_Init runs in the
                                                              // routine; destroy was set by TitleSetup_Init.
        OotCrc.Update(rom);
        return true;
    }
}
