namespace DS1_Enemy_Multiplier;

public class BackupManager
{
    /// <summary>
    /// Ensures the backup exists. Creates it on first run from game files.
    /// On subsequent runs just validates it exists. Never restores.
    /// Returns true if backup was just created.
    /// </summary>
    public bool EnsureBackup(
        string gameRoot,
        string backupDir,
        IEnumerable<string> relativeFilePaths)
    {
        var paths = relativeFilePaths.ToList();

        if (!Directory.Exists(backupDir))
        {
            // First run — create backup from current game files (must be vanilla)
            Directory.CreateDirectory(backupDir);

            foreach (var rel in paths)
            {
                var sourcePath = Path.Combine(gameRoot, rel);
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException(
                        $"Cannot create backup: required game file not found: {sourcePath}", sourcePath);

                var destPath = Path.Combine(backupDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(sourcePath, destPath);
            }

            return true;
        }
        else
        {
            // Subsequent run — just validate backup is complete
            ValidateBackup(backupDir, paths);
            return false;
        }
    }

    /// <summary>
    /// Used only for the restore-vanilla (multiplier=1) path.
    /// Restores all backup files back to the game folder.
    /// </summary>
    public void RestoreFromBackup(string gameRoot, string backupDir)
    {
        foreach (var backupFile in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, backupFile);
            var destPath = Path.Combine(gameRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(backupFile, destPath, overwrite: true);
        }
    }

    public void ValidateBackup(string backupDir, IEnumerable<string> relativeFilePaths)
    {
        foreach (var rel in relativeFilePaths)
        {
            var backupPath = Path.Combine(backupDir, rel);
            if (!File.Exists(backupPath))
                throw new FileNotFoundException(
                    $"Backup file missing: {backupPath}. " +
                    $"Delete the '{backupDir}' folder and re-run to create a fresh backup.", backupPath);
        }
    }
}
