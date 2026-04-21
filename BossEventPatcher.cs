using SoulsFormats;

namespace DS1_Enemy_Multiplier;

/// <summary>
/// Handles per-boss EMEVD patching on a case-by-case basis.
/// Each boss has unique event logic that cannot be handled generically.
///
/// For each boss, the goals are:
/// 1. All clone health bars show alongside the original
/// 2. All clones must die before victory triggers
/// 3. Souls drop only once after all clones die
/// 4. Boss AI activates correctly for all clones
///
/// Pattern used across all bosses:
/// - XXX5392 = boss fight trigger: patch in-place to enable/show clones
/// - XXX0001 = boss death handler: patch in-place to wait for all clones dead
/// - Mirror loop: every Bank=2004 ID=1 (AI state) and Bank=2003 ID=11 (HP bar)
///   call for the primary boss entity gets mirrored to all clones
/// </summary>
public class BossEventPatcher
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ReadInt(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
    private static void WriteInt(byte[] data, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(data, offset);
    private static void WriteUInt(byte[] data, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(data, offset);

    private static EMEVD.Instruction Clone(EMEVD.Instruction src, int entityOffset, int newEntityId)
    {
        var args = (byte[])src.ArgData.Clone();
        WriteInt(args, entityOffset, newEntityId);
        return new EMEVD.Instruction(src.Bank, src.ID, args);
    }

    /// <summary>
    /// Patches the boss fight trigger event (XXX5392 pattern) in-place.
    /// Adds clone entity enable/show after the original, and mirrors AI enable at the end.
    /// skipFlagOffset: byte offset of the skip-count in the first Bank=1003 ID=1 instruction.
    /// </summary>
    private static void PatchTriggerEvent(
        EMEVD.Event evt,
        int bossId,
        int[] clones,
        int skipCountMin = 3,
        int skipCountMax = 15)
    {
        if (evt == null) return;
        var instrs = new List<EMEVD.Instruction>();

        foreach (var instr in evt.Instructions)
        {
            // Adjust skip count for "skip if boss already dead"
            if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
            {
                int skip = ReadInt(instr.ArgData, 0);
                if (skip >= skipCountMin && skip <= skipCountMax)
                {
                    var a = (byte[])instr.ArgData.Clone();
                    WriteInt(a, 0, skip + clones.Length * 2);
                    instrs.Add(new EMEVD.Instruction(1003, 1, a));
                    continue;
                }
            }

            instrs.Add(instr);

            // After show HP bar (2004/5) for bossId → add for clones
            if (instr.Bank == 2004 && instr.ID == 5 && instr.ArgData.Length >= 4 &&
                ReadInt(instr.ArgData, 0) == bossId)
                foreach (var c in clones) instrs.Add(Clone(instr, 0, c));

            // After enable entity (2004/4) for bossId → add for clones
            // Only mirror enable calls (arg1 != 0), not disable calls
            if (instr.Bank == 2004 && instr.ID == 4 && instr.ArgData.Length >= 8 &&
                ReadInt(instr.ArgData, 0) == bossId && ReadInt(instr.ArgData, 4) != 0)
                foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
        }

        evt.Instructions = instrs;
    }

    /// <summary>
    /// Patches the boss death handler event (XXX0001 pattern) in-place.
    /// Adds IfCharacterDead waits for all clones before the victory trigger.
    /// </summary>
    private static void PatchDeathHandler(
        EMEVD.Event evt,
        int bossId,
        int[] clones)
    {
        if (evt == null) return;
        var instrs = new List<EMEVD.Instruction>();
        bool inserted = false;

        foreach (var instr in evt.Instructions)
        {
            // Find IfCharacterDead (Bank=4 ID=0) for bossId with Dead=1
            if (!inserted && instr.Bank == 4 && instr.ID == 0 &&
                instr.ArgData.Length >= 12 &&
                ReadInt(instr.ArgData, 4) == bossId &&
                ReadInt(instr.ArgData, 8) == 1)
            {
                // Change slot to AND_01 (1)
                var modArgs = (byte[])instr.ArgData.Clone();
                WriteUInt(modArgs, 0, 1);
                instrs.Add(new EMEVD.Instruction(4, 0, modArgs));

                foreach (var c in clones)
                {
                    var waitArgs = new byte[12];
                    WriteUInt(waitArgs, 0, 1);
                    WriteInt(waitArgs, 4, c);
                    WriteInt(waitArgs, 8, 1);
                    instrs.Add(new EMEVD.Instruction(4, 0, waitArgs));

                    var andArgs = new byte[4];
                    WriteInt(andArgs, 0, 65792); // AND_01
                    instrs.Add(new EMEVD.Instruction(0, 0, andArgs));
                }

                inserted = true;
                continue;
            }

            instrs.Add(instr);
        }

        evt.Instructions = instrs;
    }

    /// <summary>
    /// Mirrors every Bank=2004 ID=1 (AI state) and Bank=2003 ID=11 (DisplayBossHealthBar)
    /// call for bossId to all clones across all events in the EMEVD.
    /// This is the core mechanism that makes clones behave exactly like the original.
    /// </summary>
    private static void MirrorAiAndHealthBar(
        EMEVD emevd,
        int bossId,
        int[] clones)
    {
        foreach (var evt in emevd.Events)
        {
            if (evt.ID == 0) continue;
            var instrs = new List<EMEVD.Instruction>();
            bool modified = false;

            foreach (var instr in evt.Instructions)
            {
                instrs.Add(instr);

                // Mirror AI state (2004/1) — entityId at offset 0
                if (instr.Bank == 2004 && instr.ID == 1 &&
                    instr.ArgData.Length >= 8 &&
                    ReadInt(instr.ArgData, 0) == bossId)
                {
                    foreach (var c in clones)
                    {
                        instrs.Add(Clone(instr, 0, c));
                        modified = true;
                    }
                }

                // Mirror DisplayBossHealthBar (2003/11) — entityId at offset 4
                if (instr.Bank == 2003 && instr.ID == 11 &&
                    instr.ArgData.Length >= 12 &&
                    ReadInt(instr.ArgData, 4) == bossId)
                {
                    int baseSlot = ReadInt(instr.ArgData, 0);
                    int musicId = ReadInt(instr.ArgData, 8);
                    for (int k = 0; k < clones.Length; k++)
                    {
                        var a = (byte[])instr.ArgData.Clone();
                        WriteInt(a, 0, baseSlot + k + 1);
                        WriteInt(a, 4, clones[k]);
                        WriteInt(a, 8, musicId + k + 1);
                        instrs.Add(new EMEVD.Instruction(2003, 11, a));
                        modified = true;
                    }
                }
            }

            if (modified) evt.Instructions = instrs;
        }
    }

    /// <summary>
    /// Mirrors every Bank=2004 ID=1 (AI state) call for bossId to all clones.
    /// Does NOT mirror DisplayBossHealthBar — used for shared-HP fights where
    /// only one health bar should be shown.
    /// </summary>
    private static void MirrorAiOnly(
        EMEVD emevd,
        int bossId,
        int[] clones)
    {
        foreach (var evt in emevd.Events)
        {
            if (evt.ID == 0) continue;
            var instrs = new List<EMEVD.Instruction>();
            bool modified = false;

            foreach (var instr in evt.Instructions)
            {
                instrs.Add(instr);

                if (instr.Bank == 2004 && instr.ID == 1 &&
                    instr.ArgData.Length >= 8 &&
                    ReadInt(instr.ArgData, 0) == bossId)
                {
                    foreach (var c in clones)
                    {
                        instrs.Add(Clone(instr, 0, c));
                        modified = true;
                    }
                }
            }

            if (modified) evt.Instructions = instrs;
        }
    }

    /// <summary>
    /// Adds SetCharacterFollowTarget(clone, masterId) for each clone.
    /// This makes clones share the master's HP pool — when a clone dies,
    /// its HP is subtracted from the master's bar.
    /// </summary>
    private static void AddFollowTargets(
        EMEVD.Event triggerEvt,
        int masterId,
        int[] clones)
    {
        if (triggerEvt == null || clones.Length == 0) return;

        // Append SetCharacterFollowTarget(clone, masterId) at the end of the trigger
        foreach (var c in clones)
        {
            var args = new byte[8];
            WriteInt(args, 0, c);
            WriteInt(args, 4, masterId);
            triggerEvt.Instructions.Add(new EMEVD.Instruction(2004, 33, args));
        }
    }

    /// <summary>
    /// Returns event IDs that must NOT be cloned by Phase 2 for the given map.
    /// These are events patched in-place by this class.
    /// </summary>
    public HashSet<long> GetExcludedEventIds(string mapName)
    {
        return mapName switch
        {
            "m10_00_00_00" => new HashSet<long> { 11005392, 11000001 }, // Gaping Dragon
            // m10_01: Taurus Demon (1010700) + Bell Gargoyles (1010800/1010801) + Capra Demon (1010750)
            "m10_01_00_00" => new HashSet<long> { 11015392, 11010001, 11015396, 11010901, 11010902, 11015384, 11015372 },
            "m11_00_00_00" => new HashSet<long> { 11105392, 11100000, 11105396, 11105397, 11105398, 11100710 }, // Priscilla — DT transplant
            // m12_00: Wolf Sif (1200800) + Moonlight Butterfly (1200801)
            // 11200810 = Sif death handler (parameterized) — patched in-place
            "m12_00_00_00" => new HashSet<long> { 11205392, 11200001, 11200810, 11205382, 11200900 },
            // m12_01: Sanctuary Guardian (1210800) + Artorias (1210820) + Manus (1210840) + Kalameet (1210401)
            "m12_01_00_00" => new HashSet<long> { 11215003, 11210000, 11215013, 11210001, 11215023, 11210002, 11215063, 11210005 },
            "m13_00_00_00" => new HashSet<long> { 11305392, 11300001 },
            "m13_01_00_00" => new HashSet<long> { 11315392, 11310001 },
            "m14_00_00_00" => new HashSet<long> { 11405392, 11400001 },
            "m14_01_00_00" => new HashSet<long> { 11415392, 11410001, 11415372, 11410900, 11415342, 11410410, 11415382, 11410901 },
            "m15_00_00_00" => new HashSet<long> { 11505392, 11500001 },
            // m15_01: Ornstein (1510800) + Smough (1510810) — super forms (1510801/1510811) NOT cloned
            // 11515396 = phase transition, 11515397 = invincibility watcher — both patched in-place
            // Also exclude 11515393 (area check) to prevent Phase 2 cloning issues
            // Event 50 = Gwynevere NPC setup — cloning it triggers dark Anor Londo
            // Gwyndolin fog/NPC events — cloning breaks fog gate prompt
            // 11510450 = Gwyndolin interaction — cloning breaks fog gate
            "m15_01_00_00" => new HashSet<long> { 11515392, 11510001, 11515382, 11510900, 11515396, 11515397, 11515393,
                50, 11515381, 11515385, 11515394, 11510450,
                11510130, 11510240, 11510400, 11515040, 11515041, 11515110 },
            // m16_00: Four Kings — 1600800 is master HP pool, 1600801-1600804 are fighters
            // 11605350 = individual king spawner (parameterized) — we add clone init calls manually
            // 11605396 = king spawn manager — excluded to prevent independent cloning
            "m16_00_00_00" => new HashSet<long> { 11605392, 11600001, 11605382, 11600900, 11605350, 11605396 },
            "m17_00_00_00" => new HashSet<long> { 11705392, 11700001, 11705383, 11705382, 11705396 },
            // m18_00: Gwyn, Lord of Cinder (1800800) — c5370 model
            // Ceaseless Discharge is in a different area of the same map but has no HP bar event
            "m18_00_00_00" => new HashSet<long> { 11805392, 11800001 },
            "m18_01_00_00" => new HashSet<long> { 11815392, 11810001, 11810300, 11810310 },
            _ => new HashSet<long>()
        };
    }

    public void Patch(
        string mapName,
        EMEVD emevd,
        IReadOnlyDictionary<int, int[]> entityIdToClones,
        int multiplier)
    {
        switch (mapName)
        {
            case "m10_00_00_00": PatchGapingDragon(emevd, entityIdToClones); break;
            case "m10_01_00_00": PatchTaurusDemonAndGargoyles(emevd, entityIdToClones); break;
            case "m11_00_00_00": PatchPriscillaM11(emevd, entityIdToClones); break;
            case "m12_00_00_00": PatchSifAndMoonlightButterfly(emevd, entityIdToClones); break;
            case "m12_01_00_00": PatchOolacileBosses(emevd, entityIdToClones); break;
            case "m13_00_00_00": PatchPinwheel(emevd, entityIdToClones); break;
            case "m13_01_00_00": PatchNito(emevd, entityIdToClones); break;
            case "m14_00_00_00": PatchQuelaag(emevd, entityIdToClones); break;
            case "m14_01_00_00": PatchDemonRuinsBosses(emevd, entityIdToClones); break;
            case "m15_00_00_00": PatchIronGolem(emevd, entityIdToClones); break;
            case "m15_01_00_00": PatchOrnsteinSmoughAndGwyndolin(emevd, entityIdToClones); break;
            case "m16_00_00_00": PatchFourKings(emevd, entityIdToClones, multiplier); break;
            case "m17_00_00_00": PatchSeath(emevd, entityIdToClones); break;
            case "m18_00_00_00": PatchGwyn(emevd, entityIdToClones); break;
            case "m18_01_00_00": PatchAsylumDemon(emevd, entityIdToClones, multiplier); break;
        }
    }

    // ── m10_00_00_00: Gaping Dragon ───────────────────────────────────────────
    // Boss: 1000800 (c5260). Fight trigger: 11005392. Death handler: 11000001.
    private static void PatchGapingDragon(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        const int bossId = 1000800;
        if (!entityIdToClones.TryGetValue(bossId, out var clones)) return;

        PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == 11005392), bossId, clones);
        PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11000001), bossId, clones);
        MirrorAiAndHealthBar(emevd, bossId, clones);
    }

    // ── m10_01_00_00: Taurus Demon + Bell Gargoyles + Capra Demon ────────────
    // Taurus Demon: c2250 = 1010700. Trigger: 11015384. Death: 11010901.
    // Bell Gargoyles: c5350 = 1010800/1010801. Trigger: 11015392. Death: 11010001.
    //   Second gargoyle trigger: 11015396 (shows 1010801 HP bar when 1010800 at 50%)
    // Capra Demon: c2240 = 1010750. Trigger: 11015372. Death: 11010902.
    private static void PatchTaurusDemonAndGargoyles(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        // ── Taurus Demon (1010700) ────────────────────────────────────────────
        PatchBossGroup(emevd, entityIdToClones, 1010700, 11015384, 11010901, 3, 10);

        // ── Bell Gargoyles (1010800 primary, 1010801 second) ──────────────────
        const int gargoyle1Id = 1010800;
        const int gargoyle2Id = 1010801;
        entityIdToClones.TryGetValue(gargoyle1Id, out var garg1Clones);
        entityIdToClones.TryGetValue(gargoyle2Id, out var garg2Clones);

        // Fight trigger 11015392: enables 1010800, 1010801, 1010802, 1010810, 1010811
        if (garg1Clones != null)
            PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == 11015392), gargoyle1Id, garg1Clones, 3, 20);

        // Death handler 11010001: waits for 1010800 AND 1010801 both dead
        var evt001 = emevd.Events.FirstOrDefault(e => e.ID == 11010001);
        if (evt001 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            bool ins800 = false, ins801 = false;
            foreach (var instr in evt001.Instructions)
            {
                if (instr.Bank == 4 && instr.ID == 0 && instr.ArgData.Length >= 12 && ReadInt(instr.ArgData, 8) == 1)
                {
                    int entityId = ReadInt(instr.ArgData, 4);
                    if (!ins800 && entityId == gargoyle1Id && garg1Clones != null)
                    {
                        var mod = (byte[])instr.ArgData.Clone(); WriteUInt(mod, 0, 1);
                        instrs.Add(new EMEVD.Instruction(4, 0, mod));
                        foreach (var c in garg1Clones) { var w = new byte[12]; WriteUInt(w, 0, 1); WriteInt(w, 4, c); WriteInt(w, 8, 1); instrs.Add(new EMEVD.Instruction(4, 0, w)); var a = new byte[4]; WriteInt(a, 0, 65792); instrs.Add(new EMEVD.Instruction(0, 0, a)); }
                        ins800 = true; continue;
                    }
                    if (!ins801 && entityId == gargoyle2Id && garg2Clones != null)
                    {
                        var mod = (byte[])instr.ArgData.Clone(); WriteUInt(mod, 0, 2);
                        instrs.Add(new EMEVD.Instruction(4, 0, mod));
                        foreach (var c in garg2Clones) { var w = new byte[12]; WriteUInt(w, 0, 2); WriteInt(w, 4, c); WriteInt(w, 8, 1); instrs.Add(new EMEVD.Instruction(4, 0, w)); var a = new byte[4]; WriteInt(a, 0, 131328); instrs.Add(new EMEVD.Instruction(0, 0, a)); }
                        ins801 = true; continue;
                    }
                }
                instrs.Add(instr);
            }
            evt001.Instructions = instrs;
        }

        if (garg1Clones != null) MirrorAiAndHealthBar(emevd, gargoyle1Id, garg1Clones);
        if (garg2Clones != null) MirrorAiAndHealthBar(emevd, gargoyle2Id, garg2Clones);

        // Patch 11015396 (second gargoyle HP bar trigger) for clones
        var evt396 = emevd.Events.FirstOrDefault(e => e.ID == 11015396);
        if (evt396 != null && garg2Clones != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt396.Instructions)
            {
                instrs.Add(instr);
                if (instr.Bank == 2004 && instr.ID == 1 && instr.ArgData.Length >= 8 && ReadInt(instr.ArgData, 0) == gargoyle2Id)
                    foreach (var c in garg2Clones) instrs.Add(Clone(instr, 0, c));
            }
            evt396.Instructions = instrs;
        }

        // ── Capra Demon (1010750) ─────────────────────────────────────────────
        PatchBossGroup(emevd, entityIdToClones, 1010750, 11015372, 11010902, 3, 10);
    }

    // ── m11_00_00_00: Crossbreed Priscilla ───────────────────────────────────
    // Priscilla's invisibility/phase mechanics are too complex for generic patching.
    // Instead, transplant the DT mod's Priscilla events into our EMEVD, rewriting
    // the DT clone entity ID (11327921) to our clone entity ID.
    private static void PatchPriscillaM11(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        const int bossId = 1100160;
        const int dtCloneId = 11327921; // DT mod's clone entity ID for Priscilla
        if (!entityIdToClones.TryGetValue(bossId, out var clones) || clones.Length == 0) return;
        int ourCloneId = clones[0]; // Our first clone ID (e.g. 11100160)

        // Load the DT mod's EMEVD for m11_00
        const string dtEmevdPath = @"C:\Users\Hungr\Desktop\Double Trouble Mod\event\m11_00_00_00.emevd.dcx";
        if (!File.Exists(dtEmevdPath)) return;
        var dtEmevd = EMEVD.Read(File.ReadAllBytes(dtEmevdPath));

        // Events that the DT mod modifies for Priscilla
        long[] priscillaEvents = {
            11105392, 11100000, 11105396, 11105397, 11105398, 11100710
        };

        foreach (var evtId in priscillaEvents)
        {
            var dtEvt = dtEmevd.Events.FirstOrDefault(e => e.ID == evtId);
            var ourEvt = emevd.Events.FirstOrDefault(e => e.ID == evtId);
            if (dtEvt == null || ourEvt == null) continue;

            // Replace our event's instructions with the DT mod's, rewriting entity IDs
            ourEvt.Instructions.Clear();
            foreach (var instr in dtEvt.Instructions)
            {
                var newArgData = (byte[])instr.ArgData.Clone();
                // Rewrite DT clone ID → our clone ID
                for (int offset = 0; offset + 3 < newArgData.Length; offset += 4)
                {
                    int val = BitConverter.ToInt32(newArgData, offset);
                    if (val == dtCloneId)
                        BitConverter.GetBytes(ourCloneId).CopyTo(newArgData, offset);
                }
                ourEvt.Instructions.Add(new EMEVD.Instruction(instr.Bank, instr.ID, newArgData));
            }
        }
    }

    // ── m12_00_00_00: Wolf Sif + Moonlight Butterfly ─────────────────────────
    // Wolf Sif: c5210 = 1200800. Trigger: 11205392. Death: 11200001.
    //   No DisplayBossHealthBar event — HP bar via NPC params.
    //   Death handler 11200810 (parameterized, one per Sif entity) awards souls.
    //   Patch 11200810 to require all Sif clones dead before awarding.
    // Moonlight Butterfly: c3230 = 1200801. Trigger: 11205382. Death: 11200900.
    private static void PatchSifAndMoonlightButterfly(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        // ── Wolf Sif (1200800) ────────────────────────────────────────────────
        const int sifPrimaryId = 1200800;
        int[] sifIds = { 1200350, 1200351, 1200352 };

        // Patch Sif's fight trigger (11205392 — shows HP bar for 1200800)
        PatchBossGroup(emevd, entityIdToClones, sifPrimaryId, 11205392, 11200001, 3, 10);

        // Patch 11200810 (parameterized death handler) to require all Sif clones dead
        var allSifCloneIds = new List<int>();
        foreach (var id in sifIds)
            if (entityIdToClones.TryGetValue(id, out var c)) allSifCloneIds.AddRange(c);

        if (allSifCloneIds.Count > 0)
        {
            var evt810 = emevd.Events.FirstOrDefault(e => e.ID == 11200810);
            if (evt810 != null)
            {
                var instrs = new List<EMEVD.Instruction>();
                bool inserted = false;
                foreach (var instr in evt810.Instructions)
                {
                    if (!inserted && instr.Bank == 2003 && instr.ID == 36)
                    {
                        foreach (var cloneId in allSifCloneIds)
                        {
                            var w = new byte[12]; WriteUInt(w, 0, 1); WriteInt(w, 4, cloneId); WriteInt(w, 8, 1);
                            instrs.Add(new EMEVD.Instruction(4, 0, w));
                            var a = new byte[4]; WriteInt(a, 0, 65792); instrs.Add(new EMEVD.Instruction(0, 0, a));
                        }
                        inserted = true;
                    }
                    instrs.Add(instr);
                }
                if (inserted) evt810.Instructions = instrs;
            }
        }

        // ── Moonlight Butterfly (1200801) ─────────────────────────────────────
        // Trigger: 11205382. Death: 11200900.
        // The trigger sets gravity disabled + AI params for fly-down behavior.
        // We need to mirror ALL setup instructions for the clone, not just enable/show.
        const int mbId = 1200801;
        if (entityIdToClones.TryGetValue(mbId, out var mbClones))
        {
            var evt382 = emevd.Events.FirstOrDefault(e => e.ID == 11205382);
            if (evt382 != null)
            {
                var instrs = new List<EMEVD.Instruction>();
                foreach (var instr in evt382.Instructions)
                {
                    // Adjust skip count
                    if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
                    {
                        int skip = ReadInt(instr.ArgData, 0);
                        if (skip >= 3 && skip <= 10)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 0, skip + mbClones.Length * 2);
                            instrs.Add(new EMEVD.Instruction(1003, 1, a));
                            continue;
                        }
                    }

                    instrs.Add(instr);

                    // Mirror ALL instructions that reference mbId to clones:
                    // SetCharacterHPBarDisplay (2004/5), EnableCharacter (2004/4),
                    // SetCharacterGravity (2004/30), SetCharacterAIState (2004/1),
                    // SetCharacterAIParam (2004/9)
                    if (instr.ArgData.Length >= 4 && ReadInt(instr.ArgData, 0) == mbId)
                    {
                        bool shouldMirror =
                            (instr.Bank == 2004 && instr.ID == 5)  || // SetCharacterHPBarDisplay
                            (instr.Bank == 2004 && instr.ID == 4)  || // EnableCharacter
                            (instr.Bank == 2004 && instr.ID == 30) || // SetCharacterGravity
                            (instr.Bank == 2004 && instr.ID == 1)  || // SetCharacterAIState
                            (instr.Bank == 2004 && instr.ID == 9);    // SetCharacterAIParam

                        if (shouldMirror)
                            foreach (var c in mbClones) instrs.Add(Clone(instr, 0, c));
                    }
                }
                evt382.Instructions = instrs;
            }

            PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11200900), mbId, mbClones);
            MirrorAiAndHealthBar(emevd, mbId, mbClones);
        }
    }

    // ── m12_01_00_00: Sanctuary Guardian + Artorias + Manus + Kalameet ────────
    // Sanctuary Guardian: c3471 = 1210800. Trigger: 11215003. Death: 11210000.
    // Artorias: c4100 = 1210820. Trigger: 11215013. Death: 11210001.
    // Manus: c4500 = 1210840. Trigger: 11215023. Death: 11210002.
    // Kalameet: c4510 = 1210401/1210402. Trigger: 11215063. Death: 11210005.
    private static void PatchOolacileBosses(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1210800, 11215003, 11210000, 3, 15);
        PatchBossGroup(emevd, entityIdToClones, 1210820, 11215013, 11210001, 3, 15);
        PatchBossGroup(emevd, entityIdToClones, 1210840, 11215023, 11210002, 3, 10);
        PatchBossGroup(emevd, entityIdToClones, 1210401, 11215063, 11210005, 3, 15);
    }

    // ── m13_00_00_00: Pinwheel ────────────────────────────────────────────────
    // Boss: 1300800. Fight trigger: 11305392. Death handler: 11300001.
    private static void PatchPinwheel(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1300800, 11305392, 11300001, 3, 10);
    }

    // ── m13_01_00_00: Nito ────────────────────────────────────────────────────
    // Boss: 1310800. Fight trigger: 11315392. Death handler: 11310001.
    private static void PatchNito(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1310800, 11315392, 11310001, 3, 20);
    }

    // ── m14_00_00_00: Quelaag ─────────────────────────────────────────────────
    // Boss: 1400800 (c5280). Fight trigger: 11405392. Death handler: 11400001.
    private static void PatchQuelaag(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1400800, 11405392, 11400001, 3, 10);
    }

    // ── m14_01_00_00: Ceaseless Discharge + Firesage Demon + Centipede Demon ──
    // Ceaseless Discharge: c5250 = 1410600. Trigger: 11415372. Death: 11410900.
    // Firesage Demon: c2231 = 1410400. Trigger: 11415342. Death: 11410410.
    // Centipede Demon: c5200 = 1410700. Trigger: 11415382. Death: 11410901.
    // Primary boss entity 1410802 (trigger 11415392, death 11410001) also present.
    private static void PatchDemonRuinsBosses(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        // Primary centipede body — death handler waits for 1410802
        const int bossId = 1410802;
        if (entityIdToClones.TryGetValue(bossId, out var clones))
        {
            PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == 11415392), bossId, clones, 3, 15);
            PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11410001), bossId, clones);
            MirrorAiAndHealthBar(emevd, bossId, clones);
        }

        PatchBossGroup(emevd, entityIdToClones, 1410600, 11415372, 11410900, 3, 10); // Ceaseless Discharge
        PatchBossGroup(emevd, entityIdToClones, 1410400, 11415342, 11410410, 3, 10); // Firesage Demon
        PatchBossGroup(emevd, entityIdToClones, 1410700, 11415382, 11410901, 3, 10); // Centipede Demon
    }

    // ── m15_00_00_00: Iron Golem ──────────────────────────────────────────────
    // Boss: 1500800 (c2320). Fight trigger: 11505392. Death handler: 11500001.
    private static void PatchIronGolem(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1500800, 11505392, 11500001, 3, 15);
    }

    // ── m15_01_00_00: Ornstein & Smough + Gwyndolin ───────────────────────────
    // Matching the Double Trouble mod approach exactly:
    //   1510800 = Ornstein (phase 1)
    //   1510810 = Smough (phase 1)
    //   1510801 = Super Ornstein (phase 2)
    //   1510811 = Super Smough (phase 2)
    //
    // DT approach: mirror ALL Bank=2004 and Bank=2003 calls for all 4 entity types
    // to their clones across all 4 key events. Do NOT add clone death checks to
    // AND registers — only originals are checked. Clones just get mirrored actions.
    private static void PatchOrnsteinSmoughAndGwyndolin(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        const int ornId = 1510800;   // Ornstein (phase 1)
        const int smoId = 1510810;   // Smough (phase 1)
        const int sOrnId = 1510801;  // Super Ornstein (phase 2)
        const int sSmoId = 1510811;  // Super Smough (phase 2)

        // Build clone map for all 4 entity types
        var cloneOf = new Dictionary<int, int[]>();
        foreach (var id in new[] { ornId, smoId, sOrnId, sSmoId })
            if (entityIdToClones.TryGetValue(id, out var c))
                cloneOf[id] = c;

        if (cloneOf.Count == 0) goto Gwyndolin;

        // Helper: mirror an instruction for all clones of the referenced entity
        void MirrorForClones(List<EMEVD.Instruction> list, EMEVD.Instruction instr, int entityOffset)
        {
            if (instr.ArgData.Length <= entityOffset + 3) return;
            int entityId = ReadInt(instr.ArgData, entityOffset);
            if (!cloneOf.TryGetValue(entityId, out var clones)) return;
            foreach (var c in clones) list.Add(Clone(instr, entityOffset, c));
        }

        // ── Patch all 4 key events by mirroring entity references to clones ──
        long[] eventsToMirror = { 11515392, 11515396, 11515397, 11510001 };

        foreach (var evtId in eventsToMirror)
        {
            var evt = emevd.Events.FirstOrDefault(e => e.ID == evtId);
            if (evt == null) continue;

            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt.Instructions)
            {
                // Adjust skip counts (Bank=1003 ID=1)
                if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
                {
                    int skip = ReadInt(instr.ArgData, 0);
                    if (skip >= 3 && skip <= 20)
                    {
                        // Count how many extra instructions will be added in the skip range
                        int extraCount = 0;
                        int idx = evt.Instructions.IndexOf(instr) + 1;
                        for (int i = 0; i < skip && idx + i < evt.Instructions.Count; i++)
                        {
                            var si = evt.Instructions[idx + i];
                            // Count instructions that will get clones mirrored
                            if (si.Bank == 2004 && si.ArgData.Length >= 4)
                            {
                                int eid = ReadInt(si.ArgData, 0);
                                if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                            }
                        }
                        if (extraCount > 0)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 0, skip + extraCount);
                            instrs.Add(new EMEVD.Instruction(1003, 1, a));
                            continue;
                        }
                    }
                }

                // Strip the "skip cutscene if already seen" flag check in 11515392
                // SkipIfEventFlag(264, 11510000) — we want the cutscene to play every time
                if (evtId == 11515392 && instr.Bank == 1003 && instr.ID == 1 &&
                    instr.ArgData.Length >= 8 && ReadInt(instr.ArgData, 4) == 11510000)
                    continue;

                // Adjust SkipIfConditionGroupStateCompiled (Bank=1000 ID=7)
                // Encoding: lower 8 bits = skip count, bits 8-15 = state (1=PASS), bits 16+ = condition group
                if (instr.Bank == 1000 && instr.ID == 7 && instr.ArgData.Length >= 4)
                {
                    int skipVal = ReadInt(instr.ArgData, 0);
                    int rawSkip = skipVal & 0xFF; // lower byte is skip count
                    int extraCount = 0;
                    int idx = evt.Instructions.IndexOf(instr) + 1;
                    for (int i = 0; i < rawSkip && idx + i < evt.Instructions.Count; i++)
                    {
                        var si = evt.Instructions[idx + i];
                        // Count ALL instruction types that get mirrored to clones
                        if (si.Bank == 2004 && si.ArgData.Length >= 4)
                        {
                            int eid = ReadInt(si.ArgData, 0);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        if (si.Bank == 2003 && si.ID == 11 && si.ArgData.Length >= 12)
                        {
                            int eid = ReadInt(si.ArgData, 4);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        if (si.Bank == 2003 && si.ID == 12 && si.ArgData.Length >= 4)
                        {
                            int eid = ReadInt(si.ArgData, 0);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        if (si.Bank == 2010 && si.ID == 2 && si.ArgData.Length >= 4)
                        {
                            int eid = ReadInt(si.ArgData, 0);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        // IfCharacterDead mirrored in death handler and phase transition
                        if (si.Bank == 4 && si.ID == 0 && si.ArgData.Length >= 12 &&
                            (evtId == 11510001 || evtId == 11515396))
                        {
                            int eid = ReadInt(si.ArgData, 4);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                    }
                    if (extraCount > 0)
                    {
                        var a = (byte[])instr.ArgData.Clone();
                        a[0] = (byte)((rawSkip + extraCount) & 0xFF);
                        instrs.Add(new EMEVD.Instruction(1000, 7, a));
                        continue;
                    }
                }

                // Adjust SkipUnconditionally (Bank=1000 ID=3) in death handler
                // This skips over the second death path — needs to account for mirrored instructions
                if (instr.Bank == 1000 && instr.ID == 3 && instr.ArgData.Length >= 4 && evtId == 11510001)
                {
                    int skipVal = ReadInt(instr.ArgData, 0);
                    int extraCount = 0;
                    int idx = evt.Instructions.IndexOf(instr) + 1;
                    for (int i = 0; i < skipVal && idx + i < evt.Instructions.Count; i++)
                    {
                        var si = evt.Instructions[idx + i];
                        if (si.Bank == 2004 && si.ArgData.Length >= 4)
                        {
                            int eid = ReadInt(si.ArgData, 0);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        if (si.Bank == 2003 && (si.ID == 11 || si.ID == 12) && si.ArgData.Length >= 4)
                        {
                            int offset = si.ID == 11 ? 4 : 0;
                            if (si.ArgData.Length > offset + 3)
                            {
                                int eid = ReadInt(si.ArgData, offset);
                                if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                            }
                        }
                        if (si.Bank == 2010 && si.ID == 2 && si.ArgData.Length >= 4)
                        {
                            int eid = ReadInt(si.ArgData, 0);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                        if (si.Bank == 4 && si.ID == 0 && si.ArgData.Length >= 12)
                        {
                            int eid = ReadInt(si.ArgData, 4);
                            if (cloneOf.TryGetValue(eid, out var cl)) extraCount += cl.Length;
                        }
                    }
                    if (extraCount > 0)
                    {
                        var a = (byte[])instr.ArgData.Clone();
                        WriteInt(a, 0, skipVal + extraCount);
                        instrs.Add(new EMEVD.Instruction(1000, 3, a));
                        continue;
                    }
                }

                instrs.Add(instr);

                // Mirror Bank=2004 calls (entity ID at offset 0)
                if (instr.Bank == 2004 && instr.ArgData.Length >= 4)
                {
                    int id2004 = instr.ID;
                    // Mirror: SetCharacterHPBarDisplay(5), EnableCharacter(4),
                    // SetCharacterAIState(1), SetCharacterAnimState(29),
                    // SetCharacterInvincibility(12), SetCharacterImmortality(15),
                    // ForceCharacterDeath(15)
                    if (id2004 == 5 || id2004 == 4 || id2004 == 1 || id2004 == 29 ||
                        id2004 == 12 || id2004 == 15)
                        MirrorForClones(instrs, instr, 0);
                }

                // Mirror Bank=2003 ID=11 (DisplayBossHealthBar — entity ID at offset 4)
                if (instr.Bank == 2003 && instr.ID == 11 && instr.ArgData.Length >= 12)
                {
                    int entityId = ReadInt(instr.ArgData, 4);
                    if (cloneOf.TryGetValue(entityId, out var clones))
                    {
                        int baseSlot = ReadInt(instr.ArgData, 0);
                        int musicId = ReadInt(instr.ArgData, 8);
                        for (int k = 0; k < clones.Length; k++)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 0, baseSlot);
                            WriteInt(a, 4, clones[k]);
                            WriteInt(a, 8, musicId + k + 1);
                            instrs.Add(new EMEVD.Instruction(2003, 11, a));
                        }
                    }
                }

                // Mirror Bank=2003 ID=12 (KillBoss — entity ID at offset 0)
                if (instr.Bank == 2003 && instr.ID == 12 && instr.ArgData.Length >= 4)
                    MirrorForClones(instrs, instr, 0);

                // Mirror Bank=2010 ID=2 (SetCharacterHP — entity ID at offset 0)
                if (instr.Bank == 2010 && instr.ID == 2 && instr.ArgData.Length >= 4)
                    MirrorForClones(instrs, instr, 0);

                // Mirror IfCharacterDead (Bank=4 ID=0) — in death handler AND phase transition
                // In death handler (11510001): add clone checks so all must die
                // In phase transition (11515396): add clone checks to AND registers
                if (instr.Bank == 4 && instr.ID == 0 && instr.ArgData.Length >= 12 &&
                    (evtId == 11510001 || evtId == 11515396))
                {
                    int entityId = ReadInt(instr.ArgData, 4);
                    if (cloneOf.TryGetValue(entityId, out var clones))
                        foreach (var c in clones)
                        {
                            var w = (byte[])instr.ArgData.Clone();
                            WriteInt(w, 4, c);
                            instrs.Add(new EMEVD.Instruction(4, 0, w));
                        }
                }
            }

            evt.Instructions = instrs;
        }

        // NOTE: Do NOT call MirrorAiAndHealthBar for O&S entities here.
        // The event loop above already mirrors all Bank=2004 and Bank=2003 calls
        // for all 4 entity types. Calling MirrorAiAndHealthBar would duplicate them.

    Gwyndolin:
        // ── Gwyndolin (1510650) ───────────────────────────────────────────────
        // Custom patching to match DT mod approach:
        // - 11515382 (trigger): mirror HP bar hide/show, AI state, DisplayBossHealthBar to clones
        //   Skip count at [00] needs adjusting for extra HP bar hide instructions
        // - 11510900 (death): add clone death checks so all must die
        // - 11515381, 11515383, 11515385 are NOT touched — they work as-is
        const int gwynId = 1510650;
        if (entityIdToClones.TryGetValue(gwynId, out var gwynClones))
        {
            // ── 11515382: Boss fight trigger ──────────────────────────────────
            var evtTrigger = emevd.Events.FirstOrDefault(e => e.ID == 11515382);
            if (evtTrigger != null)
            {
                var instrs = new List<EMEVD.Instruction>();
                foreach (var instr in evtTrigger.Instructions)
                {
                    // Adjust SkipIfEventFlag skip count at [00]
                    // Vanilla: SkipIfEventFlag(2, 11510900) — skip 2 instructions if defeated
                    // We add clone HP bar hides after the original, so skip count increases
                    if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8 &&
                        ReadInt(instr.ArgData, 4) == 11510900)
                    {
                        int skip = ReadInt(instr.ArgData, 0);
                        var a = (byte[])instr.ArgData.Clone();
                        WriteInt(a, 0, skip + gwynClones.Length); // extra HP bar hides
                        instrs.Add(new EMEVD.Instruction(1003, 1, a));
                        continue;
                    }

                    instrs.Add(instr);

                    // After SetCharacterHPBarDisplay for Gwyndolin → add for clones
                    if (instr.Bank == 2004 && instr.ID == 5 && instr.ArgData.Length >= 4 &&
                        ReadInt(instr.ArgData, 0) == gwynId)
                        foreach (var c in gwynClones) instrs.Add(Clone(instr, 0, c));

                    // After SetCharacterAIState for Gwyndolin → add for clones
                    if (instr.Bank == 2004 && instr.ID == 1 && instr.ArgData.Length >= 8 &&
                        ReadInt(instr.ArgData, 0) == gwynId)
                        foreach (var c in gwynClones) instrs.Add(Clone(instr, 0, c));

                    // After DisplayBossHealthBar for Gwyndolin → add for clones
                    if (instr.Bank == 2003 && instr.ID == 11 && instr.ArgData.Length >= 12 &&
                        ReadInt(instr.ArgData, 4) == gwynId)
                    {
                        int baseSlot = ReadInt(instr.ArgData, 0);
                        int musicId = ReadInt(instr.ArgData, 8);
                        for (int k = 0; k < gwynClones.Length; k++)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 0, baseSlot + k + 1);
                            WriteInt(a, 4, gwynClones[k]);
                            WriteInt(a, 8, musicId + k + 1);
                            instrs.Add(new EMEVD.Instruction(2003, 11, a));
                        }
                    }
                }
                evtTrigger.Instructions = instrs;

                // Append explicit AI enable + team set for clones at the very end
                // Belt-and-suspenders: ensures clone AI is active even if mirroring missed it
                foreach (var c in gwynClones)
                {
                    var aiArgs = new byte[8]; WriteInt(aiArgs, 0, c); WriteInt(aiArgs, 4, 1);
                    evtTrigger.Instructions.Add(new EMEVD.Instruction(2004, 1, aiArgs));
                    var teamArgs = new byte[8]; WriteInt(teamArgs, 0, c); WriteInt(teamArgs, 4, 7);
                    evtTrigger.Instructions.Add(new EMEVD.Instruction(2004, 2, teamArgs));
                }
            }

            // ── 11510900: Death handler — wait for ALL clones dead ────────────
            PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11510900), gwynId, gwynClones);

            // NOTE: Do NOT call MirrorAiAndHealthBar for Gwyndolin.
            // The custom trigger code above already mirrors AI state and HP bars
            // in event 11515382. MirrorAiAndHealthBar would duplicate those calls.
        }
    }

    // ── m16_00_00_00: Four Kings ──────────────────────────────────────────────
    // Architecture:
    //   1600800 = master king — shared HP pool, never fights, never cloned.
    //   1600801–1600804 = fighting kings — enabled at fight start but hidden/invincible.
    //
    // With multiplier:
    //   - 1600800 HP is scaled by multiplier (more total health).
    //   - Clone kings (11600801, 21600801, etc.) are added to the spawn pool.
    //   - 11605350 gets new InitializeEvent calls for each clone king.
    //   - Clone kings are NOT pre-enabled — they spawn via the same timer mechanic.
    //   - 11605396 (spawner) is patched to also manage clone kings.
    //   - 11600001 (death handler) is NOT changed — victory still triggers when
    //     master 1600800 HP hits 0, which is correct since all kings share that pool.
    private static void PatchFourKings(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones,
        int multiplier)
    {
        const int masterId = 1600800;
        // Fighting kings — each gets cloned
        int[] kingIds = { 1600801, 1600802, 1600803, 1600804 };

        // Collect all clone king IDs
        var allCloneKingIds = new List<int>();
        foreach (var id in kingIds)
            if (entityIdToClones.TryGetValue(id, out var clones))
                allCloneKingIds.AddRange(clones);

        if (allCloneKingIds.Count == 0) return;

        // ── Scale master HP by multiplier ─────────────────────────────────────
        // 11600001 sets HP for all kings via Bank=2004 ID=48 (SetCharacterHP).
        // The value 1077936128 = float 1.0 in IEEE 754 = 100% HP.
        // We need to multiply the master's HP by the multiplier.
        // The master's HP is set in 11600001 after the HP check fails.
        // Actually, the HP scaling is done by adding clone kings to the shared pool —
        // each clone king that shares HP with the master effectively multiplies the pool.
        // We do this via SetCharacterFollowTarget in the fight trigger.

        // ── Fight trigger 11605392 ────────────────────────────────────────────
        // Vanilla: enables all 5 kings (1600800–1600804), sets follow target.
        // With clones: also enable clone kings (hidden/invincible like originals),
        //              set follow target for all clones → they share master HP pool.
        var evt392 = emevd.Events.FirstOrDefault(e => e.ID == 11605392);
        if (evt392 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt392.Instructions)
            {
                // Adjust skip count
                if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
                {
                    int skip = ReadInt(instr.ArgData, 0);
                    if (skip >= 3 && skip <= 20)
                    {
                        var a = (byte[])instr.ArgData.Clone();
                        WriteInt(a, 0, skip + allCloneKingIds.Count * 2);
                        instrs.Add(new EMEVD.Instruction(1003, 1, a));
                        continue;
                    }
                }

                instrs.Add(instr);

                // After each original king enable/show → add for its clones
                if (instr.Bank == 2004 && (instr.ID == 5 || instr.ID == 4 || instr.ID == 1) &&
                    instr.ArgData.Length >= 4)
                {
                    int arg0 = ReadInt(instr.ArgData, 0);
                    foreach (var id in kingIds)
                        if (arg0 == id && entityIdToClones.TryGetValue(id, out var clones))
                            foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
                }

                // After SetCharacterFollowTarget for each king → add for clones
                // SetCharacterFollowTarget(kingId, masterId) — arg0=king, arg1=master
                if (instr.Bank == 2004 && instr.ID == 33 && instr.ArgData.Length >= 8)
                {
                    int arg0 = ReadInt(instr.ArgData, 0);
                    foreach (var id in kingIds)
                        if (arg0 == id && entityIdToClones.TryGetValue(id, out var clones))
                            foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
                }
            }
            evt392.Instructions = instrs;
        }

        // ── Add 11605350 InitializeEvent calls for clone kings ────────────────
        // 11605350 is parameterized: each instance watches one king entity.
        // We need one instance per clone king so they spawn on the same timer.
        // NOTE: Phase 1 of EmevdPatcher may have already added duplicate init calls
        // for 11605350 (since clone king IDs appear as params). We deduplicate here.
        var event0 = emevd.Events.FirstOrDefault(e => e.ID == 0);
        if (event0 != null)
        {
            // Remove any Phase 1 auto-added 11605350 calls for clone kings
            // (they have clone entity IDs as params but wrong slot numbers)
            var cloneKingSet = new HashSet<int>(allCloneKingIds);
            event0.Instructions = event0.Instructions
                .Where(i => !(i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 12 &&
                              BitConverter.ToInt32(i.ArgData, 4) == 11605350 &&
                              cloneKingSet.Contains(BitConverter.ToInt32(i.ArgData, 8))))
                .ToList();

            // Find max slot for event 11605350 after cleanup
            uint maxSlot350 = 0;
            foreach (var i in event0.Instructions)
            {
                if (i.Bank != 2000 || i.ID != 0 || i.ArgData.Length < 8) continue;
                if (BitConverter.ToInt32(i.ArgData, 4) != 11605350) continue;
                uint s = BitConverter.ToUInt32(i.ArgData, 0);
                if (s > maxSlot350) maxSlot350 = s;
            }

            // Get arg size from existing 11605350 init call
            var sample350 = event0.Instructions.FirstOrDefault(i =>
                i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 &&
                BitConverter.ToInt32(i.ArgData, 4) == 11605350);

            if (sample350 != null)
            {
                foreach (var cloneId in allCloneKingIds)
                {
                    maxSlot350++;
                    var newArgs = (byte[])sample350.ArgData.Clone();
                    BitConverter.GetBytes(maxSlot350).CopyTo(newArgs, 0);
                    BitConverter.GetBytes(cloneId).CopyTo(newArgs, 8);
                    event0.Instructions.Add(new EMEVD.Instruction(2000, 0, newArgs));
                }
            }

            // Also add SetCharacterHP calls for clone kings in 11600001
            // (vanilla sets HP for 1600800–1600804; we need to set for clones too)
            var evt001 = emevd.Events.FirstOrDefault(e => e.ID == 11600001);
            if (evt001 != null)
            {
                var instrs = new List<EMEVD.Instruction>();
                foreach (var instr in evt001.Instructions)
                {
                    instrs.Add(instr);
                    // After SetCharacterHP (2004/48) for each king → add for clones
                    if (instr.Bank == 2004 && instr.ID == 48 && instr.ArgData.Length >= 8)
                    {
                        int arg0 = ReadInt(instr.ArgData, 0);
                        foreach (var id in kingIds)
                            if (arg0 == id && entityIdToClones.TryGetValue(id, out var clones))
                                foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
                    }
                    // After EnableCharacter (2004/4) for each king → add for clones
                    if (instr.Bank == 2004 && instr.ID == 4 && instr.ArgData.Length >= 4)
                    {
                        int arg0 = ReadInt(instr.ArgData, 0);
                        foreach (var id in kingIds)
                            if (arg0 == id && entityIdToClones.TryGetValue(id, out var clones))
                                foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
                    }
                }
                evt001.Instructions = instrs;
            }
        }

        // ── Mirror AI state for all clone kings ───────────────────────────────
        foreach (var id in kingIds)
            if (entityIdToClones.TryGetValue(id, out var clones))
                MirrorAiAndHealthBar(emevd, id, clones);

        // ── Darkwraith ────────────────────────────────────────────────────────
        PatchBossGroup(emevd, entityIdToClones, 1600810, 11605382, 11600900, 3, 10);
    }

    // ── m17_00_00_00: Seath the Scaleless ────────────────────────────────────
    // Boss: 1700800 (c5290). Fight trigger: 11705392. Death handler: 11700001.
    // 11705396: Crystal immortality — sets Seath invincible, removes after crystal breaks.
    //   Must mirror invincibility on/off to clones so breaking crystal affects all.
    // Fair Lady companion: 1700700 (trigger 11705383, no death handler).
    private static void PatchSeath(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        const int bossId = 1700800;
        const int sisterBossId = 1700700;
        if (!entityIdToClones.TryGetValue(bossId, out var clones)) return;
        entityIdToClones.TryGetValue(sisterBossId, out var sisterClones);

        PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == 11705392), bossId, clones, 3, 10);
        PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11700001), bossId, clones);

        // ── 11705396: Crystal immortality ─────────────────────────────────────
        // Mirror SetCharacterInvincibility (2004/12) and SetSpEffect (2004/8)
        // and ClearSpEffect (2004/21) to clones so crystal break affects all.
        var evt396 = emevd.Events.FirstOrDefault(e => e.ID == 11705396);
        if (evt396 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt396.Instructions)
            {
                instrs.Add(instr);

                if (instr.ArgData.Length >= 4 && ReadInt(instr.ArgData, 0) == bossId)
                {
                    bool shouldMirror =
                        (instr.Bank == 2004 && instr.ID == 12) || // SetCharacterInvincibility
                        (instr.Bank == 2004 && instr.ID == 8)  || // SetSpEffect
                        (instr.Bank == 2004 && instr.ID == 21);   // ClearSpEffect

                    if (shouldMirror)
                        foreach (var c in clones) instrs.Add(Clone(instr, 0, c));
                }
            }
            evt396.Instructions = instrs;
        }

        MirrorAiAndHealthBar(emevd, bossId, clones);

        if (sisterClones != null)
        {
            PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == 11705383), sisterBossId, sisterClones, 3, 10);
            MirrorAiAndHealthBar(emevd, sisterBossId, sisterClones);
        }
    }

    // ── m18_00_00_00: Gwyn, Lord of Cinder ───────────────────────────────────
    // Boss: 1800800 (c5370). Fight trigger: 11805392. Death handler: 11800001.
    // Note: Ceaseless Discharge is in a different area of the same map but has
    // no DisplayBossHealthBar event — it's handled generically.
    private static void PatchGwyn(
        EMEVD emevd, IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        PatchBossGroup(emevd, entityIdToClones, 1800800, 11805392, 11800001, 3, 10);
    }

    // ── m18_01_00_00: Asylum Demon (main) ────────────────────────────────────
    // Multiple HP bars — one per clone. All must die before victory.
    private static void PatchAsylumDemon(
        EMEVD emevd,
        IReadOnlyDictionary<int, int[]> entityIdToClones,
        int multiplier)
    {
        int bossId = 1810800;
        int strayId = 1810810;

        if (!entityIdToClones.TryGetValue(bossId, out var bossClones)) return;
        entityIdToClones.TryGetValue(strayId, out var strayClones);

        // ── 11815392: Boss fight trigger ──────────────────────────────────────
        var evt392 = emevd.Events.FirstOrDefault(e => e.ID == 11815392);
        if (evt392 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt392.Instructions)
            {
                if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
                {
                    int skipCount = ReadInt(instr.ArgData, 0);
                    if (skipCount >= 3 && skipCount <= 10)
                    {
                        var newArgs = (byte[])instr.ArgData.Clone();
                        WriteInt(newArgs, 0, skipCount + bossClones.Length * 2);
                        instrs.Add(new EMEVD.Instruction(1003, 1, newArgs));
                        continue;
                    }
                }

                instrs.Add(instr);

                if (instr.Bank == 2004 && instr.ID == 5 && instr.ArgData.Length >= 8 &&
                    ReadInt(instr.ArgData, 0) == bossId)
                    foreach (var c in bossClones) instrs.Add(Clone(instr, 0, c));

                if (instr.Bank == 2004 && instr.ID == 4 && instr.ArgData.Length >= 8 &&
                    ReadInt(instr.ArgData, 0) == bossId)
                    foreach (var c in bossClones) instrs.Add(Clone(instr, 0, c));

                if (instr.Bank == 3 && instr.ID == 0 && instr.ArgData.Length >= 8)
                {
                    int calledEvtId = ReadInt(instr.ArgData, 4);
                    for (int k = 0; k < bossClones.Length; k++)
                    {
                        long cloneEvtId = calledEvtId + 100_000L * (k + 1);
                        if (emevd.Events.Any(e => e.ID == cloneEvtId))
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 4, (int)cloneEvtId);
                            instrs.Add(new EMEVD.Instruction(3, 0, a));
                        }
                    }
                }
            }

            var lastInstr = instrs.LastOrDefault();
            if (lastInstr != null && lastInstr.Bank == 2004 && lastInstr.ID == 1 &&
                lastInstr.ArgData.Length >= 8 && ReadInt(lastInstr.ArgData, 4) == 1)
            {
                instrs.RemoveAt(instrs.Count - 1);
                var aiOrig = new byte[8]; WriteInt(aiOrig, 0, bossId); WriteInt(aiOrig, 4, 0);
                instrs.Add(new EMEVD.Instruction(2004, 1, aiOrig));
                foreach (var c in bossClones)
                {
                    var aiClone = new byte[8]; WriteInt(aiClone, 0, c); WriteInt(aiClone, 4, 0);
                    instrs.Add(new EMEVD.Instruction(2004, 1, aiClone));
                }
                instrs.Add(lastInstr);
            }

            evt392.Instructions = instrs;
        }

        // ── 11810310: Boss fall event ─────────────────────────────────────────
        var evt310 = emevd.Events.FirstOrDefault(e => e.ID == 11810310);
        if (evt310 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            foreach (var instr in evt310.Instructions)
            {
                instrs.Add(instr);
                if (instr.ArgData.Length >= 4 && ReadInt(instr.ArgData, 0) == bossId)
                {
                    bool dup = (instr.Bank == 2004 && instr.ID == 5) ||
                               (instr.Bank == 2004 && instr.ID == 1) ||
                               (instr.Bank == 2004 && instr.ID == 8) ||
                               (instr.Bank == 2004 && instr.ID == 21) ||
                               (instr.Bank == 2004 && instr.ID == 13);
                    if (dup) foreach (var c in bossClones) instrs.Add(Clone(instr, 0, c));
                }
            }
            evt310.Instructions = instrs;
        }

        // ── 11810001: Boss death handler — wait for ALL clones dead ───────────
        PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11810001), bossId, bossClones);

        // ── 11815382: Stray Demon trigger ─────────────────────────────────────
        if (strayClones != null)
        {
            var evt382 = emevd.Events.FirstOrDefault(e => e.ID == 11815382);
            if (evt382 != null)
            {
                var instrs = new List<EMEVD.Instruction>();
                foreach (var instr in evt382.Instructions)
                {
                    // Adjust skip count for "skip if boss already dead"
                    if (instr.Bank == 1003 && instr.ID == 1 && instr.ArgData.Length >= 8)
                    {
                        int skip = ReadInt(instr.ArgData, 0);
                        if (skip >= 3 && skip <= 10)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            // Each clone adds 1 extra instruction per mirrored call in the skip range
                            // Skip range contains: SetCharacterHPBarDisplay (2004/5) = +1 per clone
                            WriteInt(a, 0, skip + strayClones.Length);
                            instrs.Add(new EMEVD.Instruction(1003, 1, a));
                            continue;
                        }
                    }

                    instrs.Add(instr);
                    if (instr.Bank == 2004 && instr.ArgData.Length >= 4 &&
                        ReadInt(instr.ArgData, 0) == strayId)
                    {
                        bool dup = instr.ID == 1 || instr.ID == 5 || instr.ID == 15;
                        if (dup) foreach (var c in strayClones) instrs.Add(Clone(instr, 0, c));
                    }
                    if (instr.Bank == 2003 && instr.ID == 11 && instr.ArgData.Length >= 12 &&
                        ReadInt(instr.ArgData, 4) == strayId)
                    {
                        int baseSlot = ReadInt(instr.ArgData, 0);
                        int musicId = ReadInt(instr.ArgData, 8);
                        for (int k = 0; k < strayClones.Length; k++)
                        {
                            var a = (byte[])instr.ArgData.Clone();
                            WriteInt(a, 0, baseSlot + k + 1);
                            WriteInt(a, 4, strayClones[k]);
                            WriteInt(a, 8, musicId + k + 1);
                            instrs.Add(new EMEVD.Instruction(2003, 11, a));
                        }
                    }
                }
                evt382.Instructions = instrs;
            }

            PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == 11810900), strayId, strayClones);
        }

        // ── Mirror AI + HP bar (per-clone health bars) ────────────────────────
        MirrorAiAndHealthBar(emevd, bossId, bossClones);
        if (strayClones != null) MirrorAiAndHealthBar(emevd, strayId, strayClones);

        // ── Fix 11810300: Big Pilgrim's Key ───────────────────────────────────
        var evt300 = emevd.Events.FirstOrDefault(e => e.ID == 11810300);
        if (evt300 != null)
        {
            var instrs = new List<EMEVD.Instruction>();
            bool inserted = false;
            foreach (var instr in evt300.Instructions)
            {
                if (!inserted && instr.Bank == 4 && instr.ID == 2 &&
                    instr.ArgData.Length >= 16 && ReadInt(instr.ArgData, 4) == bossId)
                {
                    instrs.Add(instr);
                    foreach (var c in bossClones)
                    {
                        var a = (byte[])instr.ArgData.Clone();
                        WriteInt(a, 4, c);
                        instrs.Add(new EMEVD.Instruction(4, 2, a));
                    }
                    inserted = true;
                    continue;
                }
                instrs.Add(instr);
            }
            if (inserted) evt300.Instructions = instrs;
        }

        // ── Remove clone fall events from Event 0 ────────────────────────────
        var event0 = emevd.Events.FirstOrDefault(e => e.ID == 0);
        if (event0 != null)
        {
            var cloneFallIds = new HashSet<long>();
            for (int k = 1; k <= bossClones.Length; k++)
                cloneFallIds.Add(11810310L + 100_000L * k);
            event0.Instructions = event0.Instructions
                .Where(i => !(i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 &&
                              cloneFallIds.Contains(ReadInt(i.ArgData, 4))))
                .ToList();
        }
    }

    // ── Generic helper: patch a standard boss group ───────────────────────────
    private static void PatchBossGroup(
        EMEVD emevd,
        IReadOnlyDictionary<int, int[]> entityIdToClones,
        int bossId,
        long triggerEventId,
        long deathEventId,
        int skipMin,
        int skipMax)
    {
        if (!entityIdToClones.TryGetValue(bossId, out var clones)) return;
        PatchTriggerEvent(emevd.Events.FirstOrDefault(e => e.ID == triggerEventId), bossId, clones, skipMin, skipMax);
        PatchDeathHandler(emevd.Events.FirstOrDefault(e => e.ID == deathEventId), bossId, clones);
        MirrorAiAndHealthBar(emevd, bossId, clones);
    }
}
