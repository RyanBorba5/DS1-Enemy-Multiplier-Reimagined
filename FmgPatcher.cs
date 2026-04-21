using SoulsFormats;

namespace DS1_Enemy_Multiplier;

public class FmgPatcher
{
    /// <summary>
    /// Patches menu.msgbnd.dcx to add numbered boss name entries for each clone.
    /// entityIdToClones maps originalEntityId -> array of clone entity IDs.
    /// </summary>
    public byte[] Patch(
        byte[] menuBndBytes,
        IReadOnlyDictionary<int, int[]> entityIdToClones)
    {
        if (entityIdToClones.Count == 0)
            return menuBndBytes;

        var bnd = BND3.Read(menuBndBytes);

        // Find the NpcName FMG file
        var npcNameFile = bnd.Files.FirstOrDefault(f =>
            f.Name.Contains("NPC_name_", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("NpcName", StringComparison.OrdinalIgnoreCase));

        if (npcNameFile == null)
            throw new InvalidOperationException(
                "Could not find NpcName FMG in menu.msgbnd.dcx. " +
                "Make sure the game is properly unpacked.");

        var fmg = FMG.Read(npcNameFile.Bytes);

        // Snapshot entries to avoid modifying while iterating
        var originalEntries = fmg.Entries.ToList();
        int addedCount = 0;

        foreach (var entry in originalEntries)
        {
            if (string.IsNullOrEmpty(entry.Text))
                continue;

            if (!entityIdToClones.TryGetValue(entry.ID, out var cloneIds))
                continue;

            for (int k = 0; k < cloneIds.Length; k++)
            {
                fmg.Entries.Add(new FMG.Entry(cloneIds[k], entry.Text + " #" + (k + 2)));
                addedCount++;
            }
        }

        npcNameFile.Bytes = fmg.Write();
        return bnd.Write();
    }
}
