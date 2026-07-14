namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// A watchdog for the headless engine's async cloud ops. Every op (open / link / save / trip) resolves
/// via a callback; if the cloud never answers, the Task stays pending forever and the whole run wedges
/// silently (this actually cost a multi-hour hang once). A normal op is sub-second, so a minute-plus
/// stall means it is hung — we throw, Program's catch prints it and exits, and because the pipeline saves
/// its manifest after each unit of work the next run resumes where it left off.
/// </summary>
public static class CloudOp
{
    public const int TimeoutSeconds = 90;

    public static async Task<T> WithTimeout<T>(Task<T> op, string what)
    {
        if (await Task.WhenAny(op, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds))) != op)
            throw new TimeoutException(
                $"{what} did not complete within {TimeoutSeconds}s (cloud stall). Re-run to resume — progress is saved.");
        return await op;   // surface the real result or its exception
    }

    public static async Task WithTimeout(Task op, string what)
    {
        if (await Task.WhenAny(op, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds))) != op)
            throw new TimeoutException(
                $"{what} did not complete within {TimeoutSeconds}s (cloud stall). Re-run to resume — progress is saved.");
        await op;
    }
}
