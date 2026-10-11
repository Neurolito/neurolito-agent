using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Tharga.Neurolito.Agent.Features.Greeting;

public class GreetingService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //NOTE: A banner. It must never stop the agent: an exception from a background service stops the host, and an
        //agent installed in Program Files cannot write the cache next to itself.
        try
        {
            await ShowBannerAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ShowVersion(Environment.MachineName);
        }
    }

    private static async Task ShowBannerAsync(CancellationToken stoppingToken)
    {
        var machine = Environment.MachineName;
        var font = "Grafitti";
        var url = $"https://asciified.thelicato.io/api/v2/ascii?text={Uri.EscapeDataString(machine)}&font={Uri.EscapeDataString(font)}";

        var cacheDir = Path.Combine(AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(cacheDir);

        var cacheKey = ComputeSha256Hex($"{machine}|{font}|{url}");
        var cachePath = Path.Combine(cacheDir, $"{cacheKey}.txt");

        TimeSpan? ttl = TimeSpan.FromDays(30);

        if (TryReadCache(cachePath, ttl, out var cached))
        {
            ShowVersion(cached);
            return;
        }

        var lockPath = cachePath + ".lock";
        await using var lockStream = await AcquireFileLockAsync(lockPath, stoppingToken);

        if (TryReadCache(cachePath, ttl, out cached))
        {
            ShowVersion(cached);
            return;
        }

        using var httpClient = new HttpClient();
        var response = await httpClient.GetStringAsync(url, stoppingToken);

        await File.WriteAllTextAsync(cachePath, response, Encoding.UTF8, stoppingToken);
        ShowVersion(response);
    }

    private static void ShowVersion(string machine)
    {
        Console.WriteLine(machine);

        var asm = Assembly.GetEntryAssembly();
        var assemblyName = asm?.GetName();
        Console.WriteLine($"{assemblyName?.Name} {assemblyName?.Version}");

        Console.WriteLine();
    }

    private static bool TryReadCache(string path, TimeSpan? ttl, out string content)
    {
        content = string.Empty;

        if (!File.Exists(path))
        {
            return false;
        }

        if (ttl is not null)
        {
            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > ttl.Value)
            {
                return false;
            }
        }

        content = File.ReadAllText(path, Encoding.UTF8);
        return true;
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<FileStream> AcquireFileLockAsync(string lockPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(200, ct);
            }
        }
    }
}