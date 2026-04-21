using DS1_Enemy_Multiplier.Models;

namespace DS1_Enemy_Multiplier;

public class MultiplierApp
{
    private readonly BackupManager _backupManager = new();
    private readonly MsbPatcher _msbPatcher = new();
    private readonly EmevdPatcher _emevdPatcher = new();
    private readonly FmgPatcher _fmgPatcher = new();

    public PatchResult Run(CloneContext ctx)
    {
        var result = new PatchResult();

        // Discover files
        var msbDir = Path.Combine(ctx.GameRoot, "map", "MapStudio");
        var eventDir = Path.Combine(ctx.GameRoot, "event");
        var menuBndPath = Path.Combine(ctx.GameRoot, "msg", "ENGLISH", "item.msgbnd.dcx");

        var msbPaths = Directory.GetFiles(msbDir, "*.msb").OrderBy(p => p).ToList();
        var emevdPaths = Directory.GetFiles(eventDir, "*.emevd.dcx").OrderBy(p => p).ToList();

        // Build relative paths for backup
        var relativeFilePaths = msbPaths
            .Select(p => Path.GetRelativePath(ctx.GameRoot, p))
            .Concat(emevdPaths.Select(p => Path.GetRelativePath(ctx.GameRoot, p)))
            .Append(Path.GetRelativePath(ctx.GameRoot, menuBndPath))
            .ToList();

        // Step 1: Ensure backup exists (create on first run, validate on subsequent runs)
        bool isFirstRun = _backupManager.EnsureBackup(ctx.GameRoot, ctx.BackupDir, relativeFilePaths);

        if (isFirstRun)
            Console.WriteLine("Backup created in EnemyMultiplierBackup/");
        else
            Console.WriteLine("Using existing backup as vanilla source.");

        // Step 2: Build entity ID registry from BACKUP MSBs (always vanilla)
        var backupMsbPaths = msbPaths
            .Select(p => Path.Combine(ctx.BackupDir, Path.GetRelativePath(ctx.GameRoot, p)))
            .ToList();
        var registry = EntityIdRegistry.BuildFromMsbs(backupMsbPaths);

        // Step 3: Patch MSBs from BACKUP and write to game folder
        var globalCloneIdMap = new Dictionary<int, int[]>();

        foreach (var msbPath in msbPaths)
        {
            Console.WriteLine($"  Patching map: {Path.GetFileName(msbPath)}");
            var rel = Path.GetRelativePath(ctx.GameRoot, msbPath);
            var backupPath = Path.Combine(ctx.BackupDir, rel);

            // Always read from backup (vanilla source)
            var bytes = File.ReadAllBytes(backupPath);
            var (patchedBytes, cloneIdMap) = _msbPatcher.Patch(bytes, ctx.Multiplier);

            foreach (var (origId, cloneIds) in cloneIdMap)
            {
                globalCloneIdMap[origId] = cloneIds;
                result.EnemiesCloned += cloneIds.Length;
            }

            AtomicWriter.Write(msbPath, patchedBytes);
            result.MapsProcessed++;
        }

        // Step 4: Patch EMEVD from BACKUP and write to game folder
        foreach (var emevdPath in emevdPaths)
        {
            Console.WriteLine($"  Patching events: {Path.GetFileName(emevdPath)}");
            var rel = Path.GetRelativePath(ctx.GameRoot, emevdPath);
            var backupPath = Path.Combine(ctx.BackupDir, rel);

            // Always read from backup (vanilla source)
            var bytes = File.ReadAllBytes(backupPath);
            var mapName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(emevdPath)); // strips .emevd.dcx
            var patchedBytes = _emevdPatcher.Patch(bytes, globalCloneIdMap, mapName, ctx.Multiplier);
            AtomicWriter.Write(emevdPath, patchedBytes);
            result.EventsPatched++;
        }

        // Step 5: Patch FMG from BACKUP and write to game folder
        Console.WriteLine("  Patching boss names...");
        var menuRel = Path.GetRelativePath(ctx.GameRoot, menuBndPath);
        var menuBackupPath = Path.Combine(ctx.BackupDir, menuRel);
        var menuBytes = File.ReadAllBytes(menuBackupPath);
        var patchedMenuBytes = _fmgPatcher.Patch(menuBytes, globalCloneIdMap);
        AtomicWriter.Write(menuBndPath, patchedMenuBytes);

        return result;
    }
}
