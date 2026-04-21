namespace DS1_Enemy_Multiplier.Models;

public class PatchResult
{
    public int EnemiesCloned { get; set; }
    public int EventsPatched { get; set; }
    public int FmgEntriesAdded { get; set; }
    public int MapsProcessed { get; set; }

    public string Summary() =>
        $"Done! Processed {MapsProcessed} maps, cloned {EnemiesCloned} enemies, " +
        $"patched {EventsPatched} event files, added {FmgEntriesAdded} boss name entries.";
}
