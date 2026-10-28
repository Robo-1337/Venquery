using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Venquery.Models;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards for the curated driver database (definitions.json). A new vendor entry has to pass all of these:
/// no conflicting duplicates, HTTPS everywhere, links on the vendor's own site, nothing claiming a generic chip ID,
/// verified + dated entries, no em dashes in text the app shows, and the Portable copy kept identical.</summary>
public class DefinitionsDatabaseTests
{
    private static readonly string[] KnownBuses = { "PCI", "USB", "HDAUDIO", "INTELAUDIO" };

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string AppCopy => Path.Combine(RepoRoot(), "Venquery", "definitions.json");

    private static Manifest Load()
        => JsonSerializer.Deserialize<Manifest>(File.ReadAllText(AppCopy))!;

    private static bool OnDomain(string url, string domain, IEnumerable<string>? more = null)
    {
        var host = new Uri(url).Host.ToLowerInvariant();
        bool On(string d) => host == d || host.EndsWith("." + d, StringComparison.Ordinal);
        return On(domain.ToLowerInvariant()) || (more ?? Array.Empty<string>()).Any(m => On(m.ToLowerInvariant()));
    }

    private static string Key(IEnumerable<string>? list) => list is null || !list.Any() ? "*" : string.Join(",", list.Select(x => x.ToUpperInvariant()).OrderBy(x => x));

    [Fact]
    public void No_two_entries_compete_for_the_same_devices()
    {
        var seen = new Dictionary<string, string>();
        foreach (var d in Load().Devices)
            foreach (var bus in d.EffectiveBuses)
            {
                var key = $"{bus.ToUpperInvariant()}|{d.Ven.ToUpperInvariant()}|{(d.PnpClass ?? "*").ToUpperInvariant()}|{Key(d.Dev)}|{Key(d.Subsys)}";
                Assert.False(seen.ContainsKey(key), $"'{d.Label}' has the same match key as '{(seen.TryGetValue(key, out var o) ? o : "")}': {key}");
                seen[key] = d.Label;
            }
    }

    [Fact]
    public void Ids_and_buses_are_well_formed()
    {
        var hex4 = new Regex("^[0-9A-F]{4}$");
        var m = Load();
        foreach (var d in m.Devices)
        {
            Assert.Matches(hex4, d.Ven);
            Assert.All(d.EffectiveBuses, b => Assert.Contains(b, KnownBuses));
            Assert.All(d.Dev ?? new(), x => Assert.Matches(hex4, x));
            Assert.All(d.Subsys ?? new(), x => Assert.Matches(hex4, x));
        }
        Assert.All(m.OemParts, o => { Assert.Matches(hex4, o.Ven); Assert.Contains(o.Bus, KnownBuses); Assert.False(string.IsNullOrWhiteSpace(o.Maker)); });
        Assert.All(m.GenericIds, g => { Assert.Matches(hex4, g.Ven); Assert.Contains(g.Bus, KnownBuses); });
    }

    [Fact]
    public void Every_link_is_https_and_support_links_stay_on_the_vendors_site()
    {
        var m = Load();
        foreach (var d in m.Devices)
        {
            Assert.True(OnDomain(d.SupportPage, d.Domain, d.CdnHosts), $"{d.Label}: support page isn't on {d.Domain}: {d.SupportPage}");
            Assert.StartsWith("https://", d.SupportPage);
            if (d.DirectDownload is { } dl)
            {
                Assert.StartsWith("https://", dl.Url);
                Assert.True(OnDomain(dl.Url, d.Domain, d.CdnHosts), $"{d.Label}: direct download isn't on {d.Domain}: {dl.Url}");
            }
            // Optional tools may live elsewhere on purpose (the Microsoft Store, a trusted driver archive) but must be HTTPS.
            Assert.All(d.Optional, o => Assert.StartsWith("https://", o.Url));
        }
        foreach (var f in m.VendorFallbacks)
            Assert.True(f.SupportPage.StartsWith("https://") && OnDomain(f.SupportPage, f.Domain), $"fallback {f.Name}: {f.SupportPage}");
        foreach (var s in m.SystemVendors)
            Assert.True(s.SupportPage.StartsWith("https://") && OnDomain(s.SupportPage, s.Domain), $"system vendor {s.Aliases[0]}: {s.SupportPage}");
    }

    [Fact]
    public void Generic_ids_and_pc_maker_parts_never_overlap_a_brand_entry()
    {
        var m = Load();
        var claimed = m.Devices.SelectMany(d => d.EffectiveBuses.Select(b => $"{b}|{d.Ven}".ToUpperInvariant())).ToHashSet();
        foreach (var f in m.VendorFallbacks) claimed.Add($"*|{f.Ven}".ToUpperInvariant());
        foreach (var g in m.GenericIds)
        {
            Assert.DoesNotContain($"{g.Bus}|{g.Ven}".ToUpperInvariant(), claimed);
            Assert.DoesNotContain($"*|{g.Ven}".ToUpperInvariant(), claimed);
        }
        // A PC-maker part may share its vendor with NARROW brand entries (exact device / subsystem / class, e.g. the
        // Killer 1969 Ethernet chips): those win and the rest of the vendor routes to the PC maker. Only a
        // vendor-wide entry would swallow the whole vendor, so only that conflicts.
        var vendorWide = m.Devices.Where(d => d.Dev is not { Count: > 0 } && d.Subsys is not { Count: > 0 } && d.PnpClass is null)
            .SelectMany(d => d.EffectiveBuses.Select(b => $"{b}|{d.Ven}".ToUpperInvariant())).ToHashSet();
        foreach (var f in m.VendorFallbacks) vendorWide.Add($"*|{f.Ven}".ToUpperInvariant());
        foreach (var o in m.OemParts)
        {
            Assert.DoesNotContain($"{o.Bus}|{o.Ven}".ToUpperInvariant(), vendorWide);
            Assert.DoesNotContain($"*|{o.Ven}".ToUpperInvariant(), vendorWide);
        }
        Assert.Empty(m.OemParts.Select(o => $"{o.Bus}|{o.Ven}".ToUpperInvariant())
            .Intersect(m.GenericIds.Select(g => $"{g.Bus}|{g.Ven}".ToUpperInvariant())));
    }

    [Fact]
    public void Every_entry_is_dated_and_says_how_it_was_verified()
    {
        foreach (var d in Load().Devices)
        {
            Assert.True(DateTime.TryParseExact(d.LastVerified, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _),
                $"{d.Label}: lastVerified missing or not yyyy-MM-dd");
            Assert.False(string.IsNullOrWhiteSpace(d.Evidence), $"{d.Label}: evidence missing");
        }
    }

    [Fact]
    public void Text_shown_in_the_app_has_no_em_dashes()
    {
        var m = Load();
        var texts = m.Devices.SelectMany(d => new[] { d.Label, d.DirectDownload?.Label, d.DirectDownload?.Note }
                .Concat(d.Optional.SelectMany(o => new[] { o.Name, o.Note })))
            .Concat(m.OemParts.SelectMany(o => new[] { o.Maker, o.Note }))
            .Concat(m.VendorFallbacks.Select(f => f.Name));
        Assert.All(texts.Where(t => t is not null), t => Assert.DoesNotContain("—", t));
    }

    [Fact]
    public void Class_specific_entries_come_before_the_vendors_general_entry()
    {
        // The match precedence no longer depends on it, but the file stays readable in this order.
        var devs = Load().Devices;
        for (var i = 0; i < devs.Count; i++)
            if (devs[i].PnpClass is null)
                for (var j = i + 1; j < devs.Count; j++)
                    Assert.False(devs[j].PnpClass is not null && devs[j].Ven == devs[i].Ven && devs[j].EffectiveBuses.Intersect(devs[i].EffectiveBuses).Any(),
                        $"'{devs[j].Label}' (class-specific) should come before '{devs[i].Label}'");
    }

    [Fact]
    public void Partner_tools_are_narrow_dated_and_on_the_partners_own_site()
    {
        var gpuChips = new[] { "10DE", "1002", "8086" };
        foreach (var p in Load().PartnerTools)
        {
            Assert.NotEmpty(p.Subsys);
            Assert.NotEmpty(p.Vens);
            Assert.All(p.Vens, v => Assert.Contains(v.ToUpperInvariant(), gpuChips));
            Assert.NotEmpty(p.Tools);
            Assert.All(p.Tools, t => Assert.True(t.Url.StartsWith("https://") && OnDomain(t.Url, p.Domain), $"{p.Maker}: {t.Url}"));
            Assert.All(p.Tools, t => Assert.DoesNotContain("—", t.Name + t.Note));
            Assert.True(DateTime.TryParseExact(p.LastVerified, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _), $"{p.Maker}: lastVerified");
            Assert.False(string.IsNullOrWhiteSpace(p.Evidence), $"{p.Maker}: evidence missing");
        }
    }

    [Fact]
    public void Every_portable_copy_matches_the_app_copy()
    {
        var app = File.ReadAllText(AppCopy);
        var portable = Path.Combine(RepoRoot(), "Portable", "definitions.json");
        if (File.Exists(portable)) Assert.Equal(app, File.ReadAllText(portable));
    }
}
