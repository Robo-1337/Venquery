using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Venquery.Tests;

/// <summary>
/// Safety tripwire for the app's "no data leaks" promise (see CLAUDE.md). It scans the shipping
/// source and fails the build if a new *outbound-data primitive* appears — an HTTP write verb
/// (POST/PUT/PATCH/DELETE), a mail/socket client, etc. Today every network path is GET-only from a
/// curated/official URL with no personal data, and this keeps it that way: a future edit that adds a
/// way to send data out trips this test with the exact file and line.
///
/// This is a tripwire, not a sandbox — it reads source text, so it catches *accidental* introduction,
/// not a determined author using reflection or a third-party library. It does not vet definitions.json
/// (curated data governed by the "only real vendor URLs" rule), only code.
///
/// It scans whichever source tree it was built against (via <c>[CallerFilePath]</c>).
///
/// If egress is ever added ON PURPOSE, annotate that single line with <c>// egress-ok: &lt;reason&gt;</c>
/// and this test will treat it as an acknowledged exception.
/// </summary>
public class EgressGuardTests
{
    // Substrings that indicate a new way to send data off the machine. GET verbs (GetAsync,
    // GetStringAsync) and SendAsync (used with an explicit HttpMethod.Get here) are intentionally
    // NOT listed — the mutating HttpMethod.* tokens cover the SendAsync-with-a-write-verb case.
    private static readonly string[] BannedTokens =
    {
        ".PostAsync(", ".PutAsync(", ".PatchAsync(", ".DeleteAsync(",
        "HttpMethod.Post", "HttpMethod.Put", "HttpMethod.Patch", "HttpMethod.Delete",
        "SmtpClient", "WebClient", "TcpClient", "UdpClient",
        "System.Net.Sockets", "System.Net.Mail",
    };


    private const string AcknowledgedException = "egress-ok";

    [Fact]
    public void No_new_outbound_data_primitives_in_source()
    {
        var srcDir = SourceDir();
        var files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(f))
            .ToList();

        // Guard against a silent no-op: if we can't see real source, fail loudly rather than pass.
        Assert.True(files.Count > 0, $"Egress guard found no source files under: {srcDir}");

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains(AcknowledgedException)) continue;   // deliberately allowed
                var code = StripLineComment(line);
                foreach (var token in BannedTokens)
                {
                    if (code.Contains(token))
                        violations.Add($"{Path.GetFileName(file)}:{i + 1}  ->  {token.Trim()}   |  {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "New outbound-data primitive(s) found. Venquery must stay GET-only from curated/official " +
            "URLs with no personal data. If this is intentional, add `// egress-ok: <reason>` to the " +
            "line.\n\n" + string.Join("\n", violations));
    }


    /// <summary>Drops a trailing <c>//</c> line comment so a banned word in a comment doesn't trip the
    /// scan. Not string-literal aware (kept simple) — acceptable for a tripwire.</summary>
    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//");
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/") || p.Contains("/bin/");
    }

    /// <summary>Locate the shipping source tree relative to this test file, so it works from any
    /// checkout/build location. Fails clearly if the expected layout isn't found.</summary>
    private static string SourceDir([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;               // ...\Venquery.Tests
        var candidate = Path.GetFullPath(Path.Combine(testsDir, "..", "Venquery"));
        Assert.True(File.Exists(Path.Combine(candidate, "MainWindow.xaml.cs")),
            $"Egress guard couldn't find the app source (expected MainWindow.xaml.cs under {candidate}).");
        return candidate;
    }

}
