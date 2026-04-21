namespace DS1_Enemy_Multiplier;

public static class AtomicWriter
{
    public static void Write(string targetPath, byte[] bytes)
    {
        string tempPath = targetPath + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
