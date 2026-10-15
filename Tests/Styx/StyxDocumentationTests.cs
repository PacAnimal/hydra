using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cathedral.Styx;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// Styx.md §14 is the whole specification for anyone issuing tokens without our code, so it is executed: its
/// vectors are checked against Cathedral and a real relay, and its Python and JavaScript reference implementations
/// are run against the same vectors. A guide that is wrong, incomplete or stale fails the build.
/// </summary>
[TestFixture]
public class StyxDocumentationTests
{
    private static readonly string Document = File.ReadAllText(Path.Combine(TestLog.SolutionRoot, "Styx.md")).ReplaceLineEndings("\n");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private Vectors _vectors = null!;
    private string _directory = null!;
    private WebApplicationFactory<global::Styx.Program>? _relay;

    [OneTimeSetUp]
    public void Load()
    {
        _vectors = JsonSerializer.Deserialize<Vectors>(Block("vectors"), Json)!;
        _relay = StyxTestServer.Create(configure: services =>
            services.AddSingleton(new StyxAuth([Key], null, null, new HashSet<Guid>(), new HashSet<string>())));
        _ = _relay.Server;
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_relay != null) await _relay.DisposeAsync();
    }

    [SetUp]
    public void FreshDirectory() => _directory = TestPaths.FreshFixtureRoot(nameof(StyxDocumentationTests));

    private byte[] Key => Convert.FromHexString(_vectors.Key);

    [Test]
    public void TheVectorKey_IsTheOneARelayRefuses()
    {
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.ReadKeys(Convert.ToBase64String(Key)));
    }

    [Test]
    public void Cathedral_MintsTheRootAndEveryStep()
    {
        var network = Guid.Parse(_vectors.Network);
        var token = StyxToken.Mint(Key, network);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(network.Version, Is.EqualTo(8));
            Assert.That(token.KeyId, Is.EqualTo(_vectors.Kid));
            Assert.That(token.ToString(), Is.EqualTo(_vectors.Root));
            foreach (var step in _vectors.Steps)
            {
                var parts = step.Caveat.Split('=', 2);
                token = token.Narrow(new StyxCaveat(parts[0], parts[1]));
                Assert.That(token.ToString(), Is.EqualTo(step.Token), step.Caveat);
                Assert.That(Convert.ToHexString(SignatureOf(step.Token)), Is.EqualTo(step.Signature).IgnoreCase, step.Caveat);
            }
        }
    }

    [Test]
    public void Cathedral_WritesAndNarrowsTheBlob_AndRefusesTheBadValues()
    {
        var blob = _vectors.Blob;
        var before = new StyxBlob(blob.StyxServer, blob.EncryptionKey, _vectors.Root).Write();
        var after = StyxBlob.Narrow(before, blob.MaxTokenLength, blob.MaxBlobBytes, blob.NarrowedWith.Select(ParseCaveat));
        var fromCamel = StyxBlob.Narrow(blob.CamelCase, blob.MaxTokenLength, blob.MaxBlobBytes, blob.NarrowedWith.Select(ParseCaveat));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(blob.Before));
            Assert.That(after, Is.EqualTo(blob.After));
            Assert.That(fromCamel, Is.EqualTo(blob.After), "a blob read in another form is written back in the one form");
            foreach (var bad in _vectors.RefusedBlobValues)
                Assert.Throws<StyxTokenException>(() => new StyxBlob("https://s", bad, "a").Write(), bad);
        }
    }

    [Test]
    public async Task ARelay_AcceptsTheVectorTokens_AndRefusesEveryRejectedOne()
    {
        await using (var root = new TestStyxClient())
            Assert.That((await root.Connect(_relay!, _vectors.Root, "anyone")).Authenticated, Is.True);
        await using (var last = new TestStyxClient())
            Assert.That((await last.Connect(_relay!, _vectors.Steps[^1].Token, "player-1")).Authenticated, Is.True);

        foreach (var rejected in _vectors.Rejected)
        {
            await using var client = new TestStyxClient();
            var response = await client.Connect(_relay!, rejected.Token, "player-1");
            Assert.That(response.Authenticated, Is.False, rejected.Why);
        }
    }

    [Test]
    public async Task ThePythonReferenceCode_MatchesTheVectors_AndItsTokensVerifyAtARelay()
    {
        File.WriteAllText(Path.Combine(_directory, "styx_issue.py"), Block("python"));
        File.WriteAllText(Path.Combine(_directory, "driver.py"), PythonDriver);
        var results = await Run(Tool("python3", "python"), "driver.py");
        await AssertMatches(results);
    }

    [Test]
    public async Task TheJavaScriptReferenceCode_MatchesTheVectors_AndItsTokensVerifyAtARelay()
    {
        File.WriteAllText(Path.Combine(_directory, "driver.mjs"), Block("javascript") + "\n" + JavaScriptDriver);
        var results = await Run(Tool("node"), "driver.mjs");
        await AssertMatches(results);
    }

    private async Task AssertMatches(DriverResults results)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Steps, Is.EqualTo(_vectors.Steps.Select(s => s.Token)));
            Assert.That(results.Before, Is.EqualTo(_vectors.Blob.Before));
            Assert.That(results.After, Is.EqualTo(_vectors.Blob.After));
            Assert.That(results.AfterFromCamelCase, Is.EqualTo(_vectors.Blob.After));
            Assert.That(results.Refused, Is.All.True, "every bad blob value is refused");
            Assert.That(results.V1Refused, Is.True, "a v1 token cannot be narrowed");
            Assert.That(results.BadCaveatRefused, Is.True, "a caveat outside the grammar is refused before signing");
        }

        await using var client = new TestStyxClient();
        var response = await client.Connect(_relay!, results.Extra, "from-docs");
        Assert.That(response.Authenticated, Is.True, "a token the reference code narrowed verifies at a relay");
    }

    private async Task<DriverResults> Run(string tool, string script)
    {
        var vectorsPath = Path.Combine(_directory, "vectors.json");
        await File.WriteAllTextAsync(vectorsPath, Block("vectors"));
        using var process = Process.Start(new ProcessStartInfo(tool, [script, vectorsPath])
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.That(process.ExitCode, Is.Zero, await error);
        return JsonSerializer.Deserialize<DriverResults>(await output, Json)!;
    }

    // a missing interpreter fails the test rather than skipping it: a skip would hide that the guide went unchecked
    // the first of the names found on PATH; Windows commonly has only `python`
    private static string Tool(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var path = names.SelectMany(name => dirs.SelectMany(dir => new[] { Path.Combine(dir, name), Path.Combine(dir, name + ".exe") }))
            .FirstOrDefault(File.Exists);
        if (path is null) Assert.Fail($"none of {string.Join(", ", names)} is on PATH; Styx.md's reference code cannot be checked without it");
        return path!;
    }

    private static string Block(string name)
    {
        var match = Regex.Match(Document, $@"<!-- styx-test:{name} -->\s*```\w*\n(.*?)\n```", RegexOptions.Singleline);
        Assert.That(match.Success, Is.True, $"Styx.md has no styx-test:{name} block");
        return match.Groups[1].Value;
    }

    private static StyxCaveat ParseCaveat(string text)
    {
        var parts = text.Split('=', 2);
        return new StyxCaveat(parts[0], parts[1]);
    }

    private static byte[] SignatureOf(string token) => System.Buffers.Text.Base64Url.DecodeFromChars(token.AsSpan(StyxToken.Prefix.Length))[^32..];

    private const string PythonDriver = """
        import base64, json, os, sys
        # some distributions (Windows' embeddable one) leave the script's own directory off the import path
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import styx_issue as s

        v = json.load(open(sys.argv[1]))
        b = v["blob"]
        token, steps = v["root"], []
        for step in v["steps"]:
            token = s.narrow(token, [step["caveat"]])
            steps.append(token)

        def refused(call):
            try:
                call()
                return False
            except ValueError:
                return True

        before = s.make_blob(b["styxServer"], b["encryptionKey"], v["root"])
        print(json.dumps({
            "steps": steps,
            "before": before,
            "after": s.narrow_blob(before, b["narrowedWith"], b["maxTokenLength"], b["maxBlobBytes"]),
            "afterFromCamelCase": s.narrow_blob(b["camelCase"], b["narrowedWith"], b["maxTokenLength"], b["maxBlobBytes"]),
            "refused": [refused(lambda bad=bad: s.make_blob("https://s", bad, "a")) for bad in v["refusedBlobValues"]],
            "v1Refused": refused(lambda: s.narrow(base64.b64encode(bytes(130)).decode(), ["id=x"])),
            "badCaveatRefused": refused(lambda: s.narrow(v["root"], ["sub=a.b"])),
            "extra": s.narrow(v["root"], ["sub=docs", "host=from-docs"]),
        }))
        """;

    private const string JavaScriptDriver = """
        import { readFileSync } from "node:fs";
        const v = JSON.parse(readFileSync(process.argv[2], "utf8"));
        const b = v.blob;
        let token = v.root;
        const steps = [];
        for (const step of v.steps) steps.push(token = await narrow(token, [step.caveat]));
        const refused = async call => { try { await call(); return false; } catch { return true; } };
        const before = makeBlob(b.styxServer, b.encryptionKey, v.root);
        const results = [];
        for (const bad of v.refusedBlobValues) results.push(await refused(() => makeBlob("https://s", bad, "a")));
        console.log(JSON.stringify({
          steps,
          before,
          after: await narrowBlob(before, b.narrowedWith, b.maxTokenLength, b.maxBlobBytes),
          afterFromCamelCase: await narrowBlob(b.camelCase, b.narrowedWith, b.maxTokenLength, b.maxBlobBytes),
          refused: results,
          v1Refused: await refused(() => narrow(btoa("\0".repeat(130)), ["id=x"])),
          badCaveatRefused: await refused(() => narrow(v.root, ["sub=a.b"])),
          extra: await narrow(v.root, ["sub=docs", "host=from-docs"]),
        }));
        """;

    private sealed record Vectors(string Key, int Kid, string Network, string Root, VectorStep[] Steps, RejectedToken[] Rejected, Blob Blob, string[] RefusedBlobValues);

    private sealed record VectorStep(string Caveat, string Signature, string Token);

    private sealed record RejectedToken(string Why, string Token);

    private sealed record Blob(string StyxServer, string EncryptionKey, string Before, string CamelCase, string[] NarrowedWith, int MaxTokenLength, int MaxBlobBytes, string After);

    private sealed record DriverResults(string[] Steps, string Before, string After, string AfterFromCamelCase, bool[] Refused, bool V1Refused, bool BadCaveatRefused, string Extra);
}
