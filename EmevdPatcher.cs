using SoulsFormats;

namespace DS1_Enemy_Multiplier;

public class EmevdPatcher
{
    private readonly BossEventPatcher _bossPatcher = new();

    /// <summary>
    /// Patches an EMEVD file so that all cloned enemies have proper event coverage.
    /// </summary>
    public byte[] Patch(
        byte[] emevdBytes,
        IReadOnlyDictionary<int, int[]> entityIdToClones,
        string mapName = "",
        int multiplier = 2)
    {
        if (entityIdToClones.Count == 0)
            return emevdBytes;

        var emevd = EMEVD.Read(emevdBytes);
        var knownIds = entityIdToClones.Keys.ToHashSet();

        var event0 = emevd.Events.FirstOrDefault(e => e.ID == 0);
        if (event0 == null)
            return emevdBytes;

        var originalInstructions = event0.Instructions.ToList();
        var newInstructions = new List<EMEVD.Instruction>();

        // Track slots per event ID — always increment, never reset
        var maxSlotPerEventId = new Dictionary<int, uint>();
        foreach (var instr in originalInstructions)
        {
            if (instr.Bank != 2000 || instr.ID != 0 || instr.ArgData.Length < 8) continue;
            uint slot = BitConverter.ToUInt32(instr.ArgData, 0);
            int evtId = BitConverter.ToInt32(instr.ArgData, 4);
            if (!maxSlotPerEventId.TryGetValue(evtId, out uint current) || slot > current)
                maxSlotPerEventId[evtId] = slot;
        }

        uint GetNextSlot(int evtId)
        {
            uint next = maxSlotPerEventId.TryGetValue(evtId, out uint cur) ? cur + 1 : 0;
            maxSlotPerEventId[evtId] = next;
            return next;
        }

        // Events that must NOT be cloned — handled exclusively by per-boss patching
        var bossExcludedEventIds = _bossPatcher.GetExcludedEventIds(mapName);

        // ── Phase 1: Duplicate Event 0 InitializeEvent calls that pass entity IDs ──
        foreach (var instr in originalInstructions)
        {
            if (instr.Bank != 2000 || instr.ID != 0 || instr.ArgData.Length < 8) continue;

            int calledEventId = BitConverter.ToInt32(instr.ArgData, 4);

            // Skip events handled by per-boss patching
            if (bossExcludedEventIds.Contains(calledEventId)) continue;

            var entityIdsInParams = new List<(int paramOffset, int entityId)>();

            for (int offset = 8; offset + 3 < instr.ArgData.Length; offset += 4)
            {
                int value = BitConverter.ToInt32(instr.ArgData, offset);
                if (knownIds.Contains(value))
                    entityIdsInParams.Add((offset, value));
            }

            if (entityIdsInParams.Count == 0) continue;

            int cloneCount = entityIdToClones[entityIdsInParams[0].entityId].Length;

            for (int k = 0; k < cloneCount; k++)
            {
                var newArgData = (byte[])instr.ArgData.Clone();
                uint nextSlot = GetNextSlot(calledEventId);
                BitConverter.GetBytes(nextSlot).CopyTo(newArgData, 0);

                foreach (var (paramOffset, entityId) in entityIdsInParams)
                {
                    if (entityIdToClones.TryGetValue(entityId, out var cloneIds) && k < cloneIds.Length)
                        BitConverter.GetBytes(cloneIds[k]).CopyTo(newArgData, paramOffset);
                }

                newInstructions.Add(new EMEVD.Instruction(2000, 0, newArgData));
            }
        }

        // ── Phase 2: Duplicate hardcoded events (non-boss, non-parameterized) ──
        var existingEventIds = emevd.Events.Select(e => e.ID).ToHashSet();
        var eventsToProcess = emevd.Events.Where(e => e.ID != 0).ToList();

        // Events handled via Event 0 param scanning
        var handledViaParams = new HashSet<long>();
        foreach (var instr in originalInstructions)
        {
            if (instr.Bank != 2000 || instr.ID != 0 || instr.ArgData.Length < 8) continue;
            for (int offset = 8; offset + 3 < instr.ArgData.Length; offset += 4)
            {
                int value = BitConverter.ToInt32(instr.ArgData, offset);
                if (knownIds.Contains(value))
                {
                    handledViaParams.Add(BitConverter.ToInt32(instr.ArgData, 4));
                    break;
                }
            }
        }

        foreach (var evt in eventsToProcess)
        {
            if (handledViaParams.Contains(evt.ID)) continue;
            if (bossExcludedEventIds.Contains(evt.ID)) continue;

            // Skip boss events — handled per-boss by BossEventPatcher.
            // Boss events are identified by DisplayBossHealthBar (2003/11 or 2003/12)
            // or KillBoss/AwardItemLot (2004/26 or 2004/27).
            // NOTE: Bank=2004 ID=5 (SetCharacterHPBarDisplay) is NOT a reliable boss
            // indicator — it's also used for regular enemies (e.g. skeleton wakeup events).
            bool isBossEvent = evt.Instructions.Any(i =>
                (i.Bank == 2003 && (i.ID == 12 || i.ID == 11)) ||
                (i.Bank == 2004 && (i.ID == 26 || i.ID == 27)));
            if (isBossEvent) continue;

            var entityIdsInEvent = new HashSet<int>();
            foreach (var instr in evt.Instructions)
                entityIdsInEvent.UnionWith(ScanArgDataForEntityIds(instr.ArgData, knownIds));

            if (entityIdsInEvent.Count == 0) continue;

            int cloneCount = entityIdsInEvent
                .Where(id => entityIdToClones.ContainsKey(id))
                .Select(id => entityIdToClones[id].Length)
                .DefaultIfEmpty(0).Min();

            if (cloneCount == 0) continue;

            // Seed nested slot tracker from all existing events
            var nestedMaxSlots = new Dictionary<int, uint>();
            foreach (var existingEvt in emevd.Events)
                foreach (var existingInstr in existingEvt.Instructions)
                {
                    if (existingInstr.Bank != 2000 || existingInstr.ID != 0 || existingInstr.ArgData.Length < 8) continue;
                    uint s = BitConverter.ToUInt32(existingInstr.ArgData, 0);
                    int nestedId = BitConverter.ToInt32(existingInstr.ArgData, 4);
                    if (!nestedMaxSlots.TryGetValue(nestedId, out uint cur) || s > cur)
                        nestedMaxSlots[nestedId] = s;
                }

            for (int k = 0; k < cloneCount; k++)
            {
                long candidateId = evt.ID + 100_000L * (k + 1);
                if (existingEventIds.Contains(candidateId))
                    candidateId = FindFreeEventId(existingEventIds);
                existingEventIds.Add(candidateId);

                var newEvent = new EMEVD.Event(candidateId, evt.RestBehavior);

                foreach (var instr in evt.Instructions)
                {
                    var newArgData = (byte[])instr.ArgData.Clone();

                    for (int offset = 0; offset + 3 < newArgData.Length; offset += 4)
                    {
                        int value = BitConverter.ToInt32(newArgData, offset);
                        if (entityIdsInEvent.Contains(value) &&
                            entityIdToClones.TryGetValue(value, out var cloneIds))
                            BitConverter.GetBytes(cloneIds[k]).CopyTo(newArgData, offset);
                    }

                    if (instr.Bank == 2000 && instr.ID == 0 && newArgData.Length >= 8)
                    {
                        int nestedEvtId = BitConverter.ToInt32(newArgData, 4);
                        uint nextNestedSlot = nestedMaxSlots.TryGetValue(nestedEvtId, out uint maxNestedSlot)
                            ? maxNestedSlot + 1 : 0;
                        nestedMaxSlots[nestedEvtId] = nextNestedSlot;
                        BitConverter.GetBytes(nextNestedSlot).CopyTo(newArgData, 0);
                    }

                    newEvent.Instructions.Add(new EMEVD.Instruction(instr.Bank, instr.ID, newArgData));
                }

                emevd.Events.Add(newEvent);

                var originalCall = originalInstructions.FirstOrDefault(i =>
                    i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 &&
                    BitConverter.ToInt32(i.ArgData, 4) == (int)evt.ID);

                int initArgSize = originalCall != null ? originalCall.ArgData.Length : 12;
                var initArgs = new byte[initArgSize];
                uint dupSlot = GetNextSlot((int)candidateId);
                BitConverter.GetBytes(dupSlot).CopyTo(initArgs, 0);
                BitConverter.GetBytes((int)candidateId).CopyTo(initArgs, 4);
                newInstructions.Add(new EMEVD.Instruction(2000, 0, initArgs));
            }
        }

        // Add new Event 0 instructions
        foreach (var instr in newInstructions)
            event0.Instructions.Add(instr);

        // ── Phase 3: Per-boss patching (case-by-case) ──
        _bossPatcher.Patch(mapName, emevd, entityIdToClones, multiplier);

        return emevd.Write();
    }

    private static long FindFreeEventId(HashSet<long> existingIds)
    {
        for (long id = 90_000_000; id <= 99_999_999; id++)
        {
            if (!existingIds.Contains(id))
                return id;
        }
        throw new InvalidOperationException("No free event IDs in fallback range.");
    }

    public static HashSet<int> ScanArgDataForEntityIds(byte[] argData, IReadOnlySet<int> knownIds)
    {
        var found = new HashSet<int>();
        for (int offset = 0; offset + 3 < argData.Length; offset += 4)
        {
            int value = BitConverter.ToInt32(argData, offset);
            if (knownIds.Contains(value))
                found.Add(value);
        }
        return found;
    }
}
