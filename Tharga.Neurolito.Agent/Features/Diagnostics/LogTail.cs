using System.Text;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>Finds the agent's newest log file and reads its last lines while the agent still writes to it.</summary>
internal static class LogTail
{
    /// <summary>The most recently written <c>*.log</c> in any of <paramref name="directories"/>, or null when there is none.</summary>
    public static string Newest(IEnumerable<string> directories)
    {
        return directories
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => Directory.EnumerateFiles(x, "*.log"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>The last <paramref name="lines"/> lines of the file, reading no more than <paramref name="maxBytes"/> from its end.</summary>
    public static string[] Read(string path, int lines, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[length - start];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        var all = Encoding.UTF8.GetString(buffer, 0, read).Split('\n').Select(x => x.TrimEnd('\r')).ToList();

        //NOTE: Reading from the middle of the file starts in the middle of a line, which is dropped.
        if (start > 0 && all.Count > 0) all.RemoveAt(0);
        while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);

        return all.TakeLast(Math.Max(1, lines)).ToArray();
    }
}
