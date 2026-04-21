using DS1_Enemy_Multiplier;
using DS1_Enemy_Multiplier.Models;

Console.WriteLine("=== DS1 Enemy Multiplier v2 ===");
Console.WriteLine();

// Validate game root
// Use the directory of the exe itself, falling back to current directory
string gameRoot = Path.GetDirectoryName(Environment.ProcessPath ?? "")
    ?? Directory.GetCurrentDirectory();

Console.WriteLine($"Game root: {gameRoot}");
Console.WriteLine();

if (!File.Exists(Path.Combine(gameRoot, "DarkSoulsRemastered.exe")))
{
    Console.Error.WriteLine("Error: DarkSoulsRemastered.exe not found in the current directory.");
    Console.Error.WriteLine($"Make sure you placed this tool in your Dark Souls Remastered game folder.");
    Console.Error.WriteLine($"Current directory: {gameRoot}");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(1);
}

string mapStudioDir = Path.Combine(gameRoot, "map", "MapStudio");
if (!Directory.Exists(mapStudioDir))
{
    Console.Error.WriteLine("Error: map/MapStudio/ directory not found.");
    Console.Error.WriteLine("You must unpack the game first using UnpackDarkSoulsForModding.");
    Console.Error.WriteLine("Download it from: https://www.nexusmods.com/darksouls/mods/1304");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(1);
}

if (!Directory.GetFiles(mapStudioDir, "*.msb").Any())
{
    Console.Error.WriteLine("Error: No .msb files found in map/MapStudio/.");
    Console.Error.WriteLine("Make sure the game has been properly unpacked.");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(1);
}

// Prompt for multiplier
int multiplier = 0;
while (true)
{
    Console.WriteLine("Enter a multiplier (2 or higher to multiply enemies, 1 to restore vanilla files):");
    Console.Write("> ");
    string? input = Console.ReadLine();

    if (InputValidator.TryParseMultiplier(input, out multiplier, out string error))
        break;

    Console.WriteLine($"Invalid input: {error}");
    Console.WriteLine();
}

string backupDir = Path.Combine(gameRoot, "EnemyMultiplierBackup");

// Handle restore-only (multiplier = 1)
if (multiplier == 1)
{
    if (!Directory.Exists(backupDir))
    {
        Console.WriteLine("No backup found. Nothing to restore.");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
        Environment.Exit(0);
    }

    try
    {
        Console.WriteLine("Restoring vanilla files from backup...");
        // Walk everything in the backup folder and copy it back
        foreach (var backupFile in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, backupFile);
            var destPath = Path.Combine(gameRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(backupFile, destPath, overwrite: true);
            Console.WriteLine($"  Restored: {rel}");
        }
        Console.WriteLine();
        Console.WriteLine("Vanilla files restored successfully.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error during restore: {ex.Message}");
    }

    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(0);
}

// Run the full pipeline
Console.WriteLine($"Multiplying all enemies by {multiplier}x...");
Console.WriteLine();

try
{
    var registry = EntityIdRegistry.BuildFromMsbs(
        Directory.GetFiles(mapStudioDir, "*.msb"));

    var ctx = new CloneContext(
        Multiplier: multiplier,
        GameRoot: gameRoot,
        BackupDir: backupDir,
        AllOriginalEntityIds: registry.AllEntityIds);

    var app = new MultiplierApp();
    var result = app.Run(ctx);

    Console.WriteLine();
    Console.WriteLine(result.Summary());
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(1);
}
