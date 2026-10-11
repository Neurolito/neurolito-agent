using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>Finds and downloads GGUF files on Hugging Face, whose API is meant for programs to download from.</summary>
internal sealed partial class HuggingFaceClient
{
    public const string ClientName = "HuggingFace";
    private const string DefaultQuantisation = "Q4_K_M";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlamaServerOptions _options;

    public HuggingFaceClient(IHttpClientFactory httpClientFactory, IOptions<LlamaServerOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    /// <summary>
    /// A Hugging Face name, as Ollama writes one: <c>hf.co/{owner}/{repo}</c> or <c>hf.co/{owner}/{repo}:{quantisation}</c>.
    /// The quantisation defaults to Q4_K_M, which is what Ollama's library uses for most models.
    /// </summary>
    public static bool TryParse(string name, out string repository, out string quantisation)
    {
        var match = NamePattern().Match(name ?? string.Empty);
        repository = match.Success ? match.Groups["repo"].Value : null;
        quantisation = match.Success && match.Groups["quant"].Success ? match.Groups["quant"].Value : DefaultQuantisation;
        return match.Success;
    }

    /// <summary>The single-file GGUF in <paramref name="repository"/> for <paramref name="quantisation"/>, pinned to the current commit and its digest.</summary>
    /// <summary>Whether the file's name ends in the quantisation, as a whole part: <c>Model-Q6_K.gguf</c>, not <c>Model-Q6_K_L.gguf</c>.</summary>
    internal static bool NamedFor(string fileName, string quantisation)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.EndsWith(quantisation, StringComparison.OrdinalIgnoreCase)
               && (stem.Length == quantisation.Length || stem[^(quantisation.Length + 1)] is '-' or '_' or '.');
    }

    public async Task<ModelSource> ResolveAsync(string repository, string quantisation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);

        using var request = Request(HttpMethod.Get, $"api/models/{repository}?blobs=true");
        using var response = await Client().SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Hugging Face does not have {repository}: {(int)response.StatusCode} {response.ReasonPhrase}.");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var root = document.RootElement;

        var candidates = root.GetProperty("siblings").EnumerateArray()
            .Select(x => (Element: x, File: x.GetProperty("rfilename").GetString()))
            .Where(x => x.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                        && x.File.Contains(quantisation, StringComparison.OrdinalIgnoreCase)
                        && !x.File.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                        && !SplitPattern().IsMatch(x.File))
            .ToArray();
        //NOTE: A file named for exactly the quantisation wins over one that only contains it: Q6_K is also part of Q6_K_L,
        //which bartowski publishes next to it.
        var exact = candidates.Where(x => NamedFor(x.File, quantisation)).ToArray();
        if (exact.Length > 0) candidates = exact;

        if (candidates.Length == 0) throw new InvalidOperationException($"{repository} has no single-file GGUF for {quantisation}.");
        if (candidates.Length > 1) throw new InvalidOperationException($"{repository} has several GGUF files for {quantisation}: {string.Join(", ", candidates.Select(x => x.File))}.");

        var file = candidates[0];
        string sha256 = null;
        long? size = null;
        if (file.Element.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
        {
            sha256 = lfs.GetProperty("sha256").GetString();
            size = lfs.GetProperty("size").GetInt64();
        }

        return new ModelSource
        {
            Repository = repository,
            File = file.File,
            Revision = root.GetProperty("sha").GetString(),
            Sha256 = sha256,
            SizeBytes = size
        };
    }

    /// <summary>
    /// Downloads <paramref name="source"/> to <paramref name="path"/>, yielding the percent done, and checks it against
    /// the source's digest and size. The file appears at <paramref name="path"/> only when it is complete and correct.
    /// </summary>
    public async IAsyncEnumerable<double> DownloadAsync(ModelSource source, string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        var file = string.Join('/', source.File.Split('/').Select(Uri.EscapeDataString));
        using var request = Request(HttpMethod.Get, $"{source.Repository}/resolve/{source.Revision ?? "main"}/{file}");
        using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Could not download {source.Repository}/{source.File}: {(int)response.StatusCode} {response.ReasonPhrase}.");

        var total = response.Content.Headers.ContentLength ?? source.SizeBytes ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        var reported = -1;

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    var percent = total > 0 ? (int)(written * 100 / total) : 0;
                    if (percent > reported && percent < 100)
                    {
                        reported = percent;
                        yield return percent;
                    }
                }
            }

            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (source.Sha256 != null && !string.Equals(digest, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{source.File} does not match its digest: expected {source.Sha256}, got {digest}.");
            if (source.SizeBytes is > 0 && written != source.SizeBytes)
                throw new InvalidDataException($"{source.File} is {written} bytes, expected {source.SizeBytes}.");

            File.Move(partial, path, true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }

        yield return 100;
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(ClientName);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.HuggingFaceToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.HuggingFaceToken);
        return request;
    }

    [GeneratedRegex(@"^(?:https?://)?(?:hf\.co|huggingface\.co)/(?<repo>[\w.\-]+/[\w.\-]+?)(?::(?<quant>[\w.\-]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPattern();
}
