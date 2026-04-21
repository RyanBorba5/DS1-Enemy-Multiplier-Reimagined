namespace DS1_Enemy_Multiplier.Models;

public record CloneContext(
    int Multiplier,
    string GameRoot,
    string BackupDir,
    IReadOnlySet<int> AllOriginalEntityIds
);
