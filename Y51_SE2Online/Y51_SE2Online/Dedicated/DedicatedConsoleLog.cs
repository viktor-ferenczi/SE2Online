using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// dedicated-console.jsonl for the runner. Callers only enqueue: appending with an open/close per line blocked the
/// net thread for ~400 ms per status line on Windows (the file is rescanned on every close), which froze every
/// joiner's view of moving grids.
/// </summary>
internal static class DedicatedConsoleLog
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 100_000);
    private static string _path;
    private static Thread _writer;

    internal static void Initialize(string directory)
    {
        _path = Path.Combine(directory, "dedicated-console.jsonl");
        if (_writer != null) return;
        _writer = new Thread(Drain) { IsBackground = true, Name = "Y51_SE2Online-ConsoleLog" };
        _writer.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromSeconds(2));
    }

    internal static void Write(string level, string message)
    {
        if (_path == null) return;
        try
        {
            Queue.TryAdd(JsonSerializer.Serialize(new { TimestampUtc = DateTime.UtcNow, ProcessId = Environment.ProcessId, Level = level, Message = message }));
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Waits until everything queued so far is on disk (shutdown).</summary>
    internal static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    private static void Drain()
    {
        StreamWriter writer = null;
        foreach (string entry in Queue.GetConsumingEnumerable())
        {
            try
            {
                writer ??= new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                writer.Write(entry);
                writer.Write('\n');
                if (Queue.Count == 0) writer.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                writer?.Dispose();
                writer = null;
            }
        }
    }
}
