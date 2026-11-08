namespace MyBot.Persistence;

public sealed class ProcessLock : IDisposable
{
    private readonly FileStream stream;

    private ProcessLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static ProcessLock Acquire(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        string fullPath = Path.GetFullPath(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return new ProcessLock(new FileStream(fullPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None));
    }

    public void Dispose()
    {
        stream.Dispose();
    }
}
