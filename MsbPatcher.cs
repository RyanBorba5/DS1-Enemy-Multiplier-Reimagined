using SoulsFormats;
using System.Numerics;

namespace DS1_Enemy_Multiplier;

public class MsbPatcher
{
    /// <summary>
    /// Position overrides for clones of specific entity IDs.
    /// Clones of these entities will be placed at the specified position
    /// instead of the original's position. Used for bosses that spawn
    /// on towers/ledges with scripted fly-down animations that don't work for clones.
    /// </summary>
    private static readonly Dictionary<int, Vector3> ClonePositionOverrides = new()
    {
        // Bell Gargoyles on church tower — move clones to roof level
        // Original 1010801 is at (15.0, 69.8, 132.1) — tower
        // Original 1010802 is at (6.1, 69.8, 132.4) — tower
        // Roof level is Y≈48.9 (where 1010800 spawns)
        { 1010801, new Vector3(14.0f, 48.9f, 108.0f) },
        { 1010802, new Vector3(8.0f, 48.9f, 108.0f) },
    };
    /// <summary>
    /// Entity IDs that must never be cloned.
    /// </summary>
    private static readonly HashSet<int> NeverClone = new()
    {
        1210801, // Sanctuary Guardian #2
        1210802, // Sanctuary Guardian #3

        // O&S super forms — phase 2, cloned but disabled until phase transition
        // 1510801 = Super Ornstein, 1510811 = Super Smough
        // These ARE cloned in MSB so phase 2 has multiplied super forms,
        // but they're disabled by the trigger event (11515392) until phase transition.

        // Undead Asylum scripted entities
        1810200, 1810205, 1810206, 1810207, 1810209, 1810210, 1810213,
        1813902,

        // ── NPCs (merchants, trainers, quest NPCs, etc.) ──────────────────────
        // Firelink Shrine
        6130, // Crestfallen Warrior (m10_00)
        6260, // Anastacia of Astora (m10_00)
        6541, // Kingseeker Frampt (m10_00)
        6562, // Ingward (m10_00)
        6591, // Petrus of Thorolund (m10_00)

        // Undead Burg
        6001, // Solaire of Astora (m10_01)
        6040, // Undead Merchant Male (m10_01)
        6072, // Griggs of Vinheim (m10_01)
        6300, // Knight Lautrec of Carim (m10_01)
        6370, // Undead Merchant Female (m10_01)
        6540, // Lautrec (m10_01)
        6580, // Vince of Thorolund (m10_01)
        6590, // Nico of Thorolund (m10_01)

        // Undead Parish / Darkroot
        6031, // Andre of Astora (m10_02)
        6041, // Oswald of Carim (m10_02)
        6131, 6181, 6261, 6270, 6287, 6292, 6301, 6322, // Various friendly NPCs (m10_02)
        // 6070, 6080, 6090, 6100 NOT blacklisted — potentially hostile NPCs (m10_02)

        // Depths
        6312, // Domhnall of Zena (m11_00)
        6422, // Laurentius of the Great Swamp (m11_00)
        6570, // NPC (m11_00)

        // Darkroot
        6050, 6051, // Alvina / Shiva (m12_00)
        6310, // NPC (m12_00)
        6420, // Dusk of Oolacile (m12_00)
        6521, // NPC (m12_00)
        1200999, // NPC (m12_00)

        // DLC NPCs
        6700, // Elizabeth (m12_01)
        6740, // Marvelous Chester (m12_01)

        // Catacombs
        6320, // Patches the Hyena (m13_00)
        6550, // Blacksmith Vamos (m13_00)

        // Tomb of Giants
        6071, // Reah of Thorolund (m13_01)
        6091, 6101, // Vince/Nico (m13_01)
        6321, // Patches (m13_01)
        6551, // NPC (m13_01)

        // Painted World — all c0000 entities are hostile here, not blacklisted

        // Sen's Fortress
        6132, // Siegmeyer of Catarina (m14_00)
        6170, // Crestfallen Merchant (m14_00)
        6261, 6282, 6311, 6421, 6530, 6531, // Various NPCs (m14_00)

        // Demon Ruins / Lost Izalith
        6002, 6004, // Solaire / NPC (m14_01)
        6286, // Quelana of Izalith (m14_01)
        6542, // Eingyi (m14_01)
        6560, 6561, // The Fair Lady / NPC (m14_01)
        6620, // NPC (m14_01)

        // Crystal Caves / Duke's Archives
        6030, // Big Hat Logan (m15_00)
        6043, // Sieglinde of Catarina (m15_00)
        6250, 6280, 6510, 6600, // Various NPCs (m15_00)
        // 1500999 is NOT blacklisted — crystalline soldier, hostile

        // Anor Londo
        6003, // Darkmoon Knightess (m15_01)
        6010, // Giant Blacksmith (m15_01)
        6283, // Princess Gwynevere (m15_01)
        6302, // NPC (m15_01)
        6543, 6640, 6650, // Various friendly NPCs (m15_01)
        // 6490, 6500 NOT blacklisted — Dark Anor Londo hostile NPCs

        // New Londo Ruins
        6180, // Ingward (m16_00)
        6220, // Darkstalker Kaathe (m16_00)
        6262, 6271, 6520, // Various NPCs (m16_00)

        // Seath's area
        6032, 6033, 6073, 6291, // Various friendly NPCs (m17_00)
        // 6610 NOT blacklisted — hostile crystalline soldier in Duke's Archives

        // Kiln
        6544, // NPC (m18_00)

        // Undead Asylum NPCs
        6023, 6024, // Oscar / NPC (m18_01)
    };

    /// <summary>
    /// Clones all enemies and dummy enemies in the MSB by the given multiplier.
    /// Returns the patched MSB bytes and a map of originalEntityId -> cloneEntityIds[].
    /// </summary>
    public (byte[] PatchedBytes, Dictionary<int, int[]> CloneIdMap) Patch(
        byte[] msbBytes, int multiplier)
    {
        var msb = MSB1.Read(msbBytes);

        // Snapshot originals before modifying the lists
        var originalEnemies = msb.Parts.Enemies.ToList();
        var originalDummies = msb.Parts.DummyEnemies.ToList();

        var cloneIdMap = new Dictionary<int, int[]>();

        // Clone regular enemies
        foreach (var original in originalEnemies)
        {
            if (original.EntityID != -1 && NeverClone.Contains(original.EntityID))
                continue;

            var cloneIds = original.EntityID != -1
                ? new int[multiplier - 1]
                : null;

            for (int k = 1; k < multiplier; k++)
            {
                var clone = (MSB1.Part.Enemy)(MSB1.Part)original.DeepCopy();
                clone.Name = original.Name + "_clone" + k;

                if (original.EntityID != -1)
                {
                    clone.EntityID = original.EntityID + 10_000_000 * k;
                    cloneIds![k - 1] = clone.EntityID;

                    // Apply position override if this entity has one
                    if (ClonePositionOverrides.TryGetValue(original.EntityID, out var overridePos))
                        clone.Position = overridePos;
                }
                else
                {
                    clone.EntityID = -1;
                }

                msb.Parts.Enemies.Add(clone);
            }

            if (original.EntityID != -1 && cloneIds != null)
                cloneIdMap[original.EntityID] = cloneIds;
        }

        // Clone dummy enemies
        foreach (var original in originalDummies)
        {
            if (original.EntityID != -1 && NeverClone.Contains(original.EntityID))
                continue;

            var cloneIds = original.EntityID != -1
                ? new int[multiplier - 1]
                : null;

            for (int k = 1; k < multiplier; k++)
            {
                var clone = (MSB1.Part.DummyEnemy)(MSB1.Part)original.DeepCopy();
                clone.Name = original.Name + "_clone" + k;

                if (original.EntityID != -1)
                {
                    clone.EntityID = original.EntityID + 10_000_000 * k;
                    cloneIds![k - 1] = clone.EntityID;

                    // Apply position override if this entity has one
                    if (ClonePositionOverrides.TryGetValue(original.EntityID, out var overridePos))
                        clone.Position = overridePos;
                }
                else
                {
                    clone.EntityID = -1;
                }

                msb.Parts.DummyEnemies.Add(clone);
            }

            if (original.EntityID != -1 && cloneIds != null)
                cloneIdMap[original.EntityID] = cloneIds;
        }

        return (msb.Write(), cloneIdMap);
    }
}
