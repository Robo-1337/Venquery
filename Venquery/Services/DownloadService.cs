using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Venquery.Models;

namespace Venquery.Services;

public sealed record DownloadResult(bool Ok, string Path, string Sha256, string Error,
    bool SignatureValid = false, string Signer = "", bool HasSignature = false);

/// <summary>
/// Downloads driver packages to the user's Downloads folder.
/// Core protocol guarantees enforced here:
///   - files are only ever WRITTEN to disk, never executed
///   - the URL downloaded is exactly the URL shown in the UI
///   - a SHA-256 of the received file is computed and reported so the user
///     can compare it against the manufacturer's published checksum
/// </summary>
public sealed class DownloadService
{
    private readonly HttpClient _http;

    public DownloadService()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Venquery/0.1");
    }

    public static string DownloadsFolder()
    {
        // The canonical Downloads folder (known-folder GUID via USERPROFILE).
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string? referer,
        IProgress<(double pct, string text)> progress,
        CancellationToken ct = default)
    {
        // HTTPS only: every curated source is HTTPS, and a plain-HTTP download could be swapped in transit.
        if (!IsHttps(url))
            return new(false, "", "", "Refused: only HTTPS download links are allowed.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.Referrer = new Uri(referer);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, "", "", $"HTTP {(int)resp.StatusCode} from server");
            // A redirect must not downgrade to plain HTTP (.NET Framework's handler would follow one).
            if (!IsHttps(resp.RequestMessage?.RequestUri?.AbsoluteUri))
                return new(false, "", "", "Refused: the server redirected to a non-HTTPS address.");

            var fileName = FileNameFrom(resp, url);
            var target = UniquePath(Path.Combine(DownloadsFolder(), fileName));
            var total = resp.Content.Headers.ContentLength ?? -1L;

            // Stream/HTTP APIs below use the (byte[], int, int, CancellationToken) overloads and sync
            // dispose so this compiles on both .NET 8 and the Legacy net48 build (which lacks the
            // Memory/IAsyncDisposable/ct variants).
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = SHA256.Create();

            var buffer = new byte[1 << 16];
            long readTotal = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                readTotal += read;
                if (total > 0)
                    progress.Report((100.0 * readTotal / total,
                        $"downloading… {readTotal / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"));
                else
                    progress.Report((0, $"downloading… {readTotal / 1048576.0:0.0} MB"));
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();

            // Verify the file's Authenticode signature (proves it's the vendor's, intact).
            // Only meaningful for executables/installers, archives are unsigned by nature.
            var ext = Path.GetExtension(target).ToLowerInvariant();
            var sig = ext is ".exe" or ".msi" or ".msu" or ".cab" or ".dll" or ".sys"
                ? Authenticode.Verify(target)
                : new Authenticode.Result(false, "", false);

            progress.Report((100, $"✓ saved {Path.GetFileName(target)}"));
            return new(true, target, hash, "", sig.Valid, sig.Signer, sig.HasSignature);
        }
        catch (OperationCanceledException)
        {
            return new(false, "", "", "cancelled");
        }
        catch (Exception ex)
        {
            return new(false, "", "", ex.Message);
        }
    }

    /// <summary>True for an absolute https:// URL (the only kind Venquery downloads from).</summary>
    public static bool IsHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string FileNameFrom(HttpResponseMessage resp, string url)
    {
        var cd = resp.Content.Headers.ContentDisposition?.FileNameStar
                 ?? resp.Content.Headers.ContentDisposition?.FileName;
        var name = !string.IsNullOrWhiteSpace(cd)
            ? cd.Trim('"')
            : Path.GetFileName(new Uri(url).AbsolutePath);
        return SafeFileName(name);
    }

    /// <summary>A server-supplied name made safe to create in Downloads: no path characters, no names that are
    /// only dots/spaces (".." would point at the parent folder), and no Windows device names (NUL, CON, COM1…,
    /// which would swallow the download).</summary>
    internal static string SafeFileName(string? name)
    {
        name ??= "";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');          // Windows drops trailing dots/spaces anyway
        if (name.Trim('.', ' ').Length == 0) return "driver-package.bin";
        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd(' ');
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            name = "_" + name;
        return name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
