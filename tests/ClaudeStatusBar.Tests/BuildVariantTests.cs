using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Inspects the ClaudeStatusBar assembly this test run was built against. The offline build must
/// contain no network code: no reference to any System.Net assembly or type, and neither the
/// credentials file name nor the usage endpoint anywhere in its bytes. The online build must
/// contain them, which shows the checks really detect what they look for.
/// Run both: dotnet test (offline, the default) and dotnet test -p:Offline=false.
/// </summary>
public class BuildVariantTests
{
    private static readonly string AppAssembly = typeof(App).Assembly.Location;
    private static readonly string[] NetworkStrings = [".credentials.json", "api.anthropic.com"];

    private static List<string> NetworkReferences()
    {
        using var stream = File.OpenRead(AppAssembly);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var assemblies = md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name));
        var types = md.TypeReferences
            .Select(h => md.GetTypeReference(h))
            .Select(t => $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}");
        return assemblies.Concat(types)
            .Where(name => name.StartsWith("System.Net", StringComparison.Ordinal))
            .ToList();
    }

    // Either encoding: string literals are UTF-16 in the #US heap, other metadata is UTF-8.
    private static List<string> NetworkStringsFound()
    {
        var bytes = File.ReadAllBytes(AppAssembly);
        return NetworkStrings
            .Where(s => IndexOf(bytes, Encoding.UTF8.GetBytes(s)) >= 0 || IndexOf(bytes, Encoding.Unicode.GetBytes(s)) >= 0)
            .ToList();
    }

    private static int IndexOf(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle);

    private static string Product =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "";

#if OFFLINE
    [Fact]
    public void Offline_build_references_no_networking_assembly_or_type() =>
        Assert.Empty(NetworkReferences());

    [Fact]
    public void Offline_build_contains_neither_the_credentials_file_name_nor_the_endpoint() =>
        Assert.Empty(NetworkStringsFound());

    [Fact]
    public void Offline_build_says_so()
    {
        Assert.Equal("offline", BuildInfo.Variant);
        Assert.Equal("Claude Status Bar (offline build)", Product);
        Assert.Null(typeof(App).Assembly.GetType("ClaudeStatusBar.UsageFetcher"));
    }
#else
    [Fact]
    public void Online_build_references_networking_so_the_check_can_see_it() =>
        Assert.Contains(NetworkReferences(), name => name.StartsWith("System.Net.Http", StringComparison.Ordinal));

    [Fact]
    public void Online_build_contains_the_credentials_file_name_and_the_endpoint_so_the_check_can_see_them() =>
        Assert.Equal(NetworkStrings, NetworkStringsFound());

    [Fact]
    public void Online_build_says_so()
    {
        Assert.Equal("online", BuildInfo.Variant);
        Assert.Equal("Claude Status Bar (online build)", Product);
        Assert.NotNull(typeof(App).Assembly.GetType("ClaudeStatusBar.UsageFetcher"));
    }
#endif
}
