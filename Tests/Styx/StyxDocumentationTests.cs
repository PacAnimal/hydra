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
/// vectors are checked against the relay's own token code and a real relay, and its Python, JavaScript, C# and Rust reference
/// implementations are run against the same vectors. A guide that is wrong, incomplete or stale fails the build.
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
            services.AddSingleton(new StyxAuth([Key], null, new HashSet<Guid>(), new HashSet<string>())));
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
    public void TheVectorKeyAndPassphrase_AreOnesARelayRefuses()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.ReadKeys(name => name == Constants.RelaySigningKeyEnvVar ? Convert.ToBase64String(Key) : null));
            Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.ReadKeys(name => name == Constants.RelaySigningPassphraseEnvVar ? _vectors.Passphrase : null));
        }
    }

    [Test]
    public void StyxToken_DerivesThePassphraseKey_AndFollowsEveryPassphraseRule()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToHexString(StyxToken.KeyFromPassphrase(_vectors.Passphrase)), Is.EqualTo(_vectors.PassphraseKey).IgnoreCase);
            foreach (var rule in _vectors.PassphraseRules)
            {
                if (rule.Accepted) Assert.DoesNotThrow(() => StyxToken.KeyFromPassphrase(rule.Passphrase), rule.Why);
                else Assert.Throws<StyxTokenException>(() => StyxToken.KeyFromPassphrase(rule.Passphrase), rule.Why);
            }
        }
    }

    [Test]
    public void StyxToken_MintsTheRootAndEveryStep()
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
    public void StyxBlob_WritesAndNarrowsTheBlob_AndRefusesTheBadValues()
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
        await AssertMatches(await Run(Tool("python3", "python"), ["driver.py"], ScriptTimeout));
    }

    [Test]
    public async Task TheJavaScriptReferenceCode_MatchesTheVectors_AndItsTokensVerifyAtARelay()
    {
        File.WriteAllText(Path.Combine(_directory, "driver.mjs"), Block("javascript") + "\n" + JavaScriptDriver);
        await AssertMatches(await Run(Tool("node"), ["driver.mjs"], ScriptTimeout));
    }

    [Test]
    public async Task TheCSharpReferenceCode_MatchesTheVectors_AndItsTokensVerifyAtARelay()
    {
        File.WriteAllText(Path.Combine(_directory, "Driver.csproj"), CSharpProject);
        File.WriteAllText(Path.Combine(_directory, "StyxIssue.cs"), Block("csharp"));
        File.WriteAllText(Path.Combine(_directory, "Program.cs"), CSharpDriver);
        await AssertMatches(await Run(Tool("dotnet"), ["run", "--project", Path.Combine(_directory, "Driver.csproj"), "--"], CompileTimeout));
    }

    [Test]
    public async Task TheRustReferenceCode_MatchesTheVectors_AndItsTokensVerifyAtARelay()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "src"));
        File.WriteAllText(Path.Combine(_directory, "Cargo.toml"), RustPackage + Block("cargo") + "\n");
        File.WriteAllText(Path.Combine(_directory, "src", "styx_issue.rs"), Block("rust") + "\n");
        File.WriteAllText(Path.Combine(_directory, "src", "main.rs"), RustDriver);
        // the whole dependency graph is pinned, so a crate released upstream cannot change what this test builds
        File.Copy(Path.Combine(TestLog.SolutionRoot, "Tests", "Styx", "StyxDocs.Cargo.lock"), Path.Combine(_directory, "Cargo.lock"));
        // built once per machine and reused, so only the first run compiles the dependencies
        var target = Path.Combine(Path.GetTempPath(), "hydra-styx-docs-rust");
        await AssertMatches(await Run(Tool("cargo"), ["run", "--release", "--quiet", "--locked", "--"], CompileTimeout, ("CARGO_TARGET_DIR", target)));
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
            Assert.That(results.PassphraseKey, Is.EqualTo(_vectors.PassphraseKey).IgnoreCase);
            Assert.That(results.PassphraseAccepted, Is.EqualTo(_vectors.PassphraseRules.Select(r => r.Accepted)), "each passphrase rule, in order");
        }

        await using var client = new TestStyxClient();
        var response = await client.Connect(_relay!, results.Extra, "from-docs");
        Assert.That(response.Authenticated, Is.True, "a token the reference code narrowed verifies at a relay");
    }

    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);
    // a first build on a fresh machine compiles the toolchain's dependencies too
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(10);

    // the driver's last line of output is its results; a build tool may print above it
    private async Task<DriverResults> Run(string tool, string[] arguments, TimeSpan timeout, params (string Name, string Value)[] environment)
    {
        var vectorsPath = Path.Combine(_directory, "vectors.json");
        await File.WriteAllTextAsync(vectorsPath, Block("vectors"));
        var start = new ProcessStartInfo(tool, [.. arguments, vectorsPath])
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.That(process.ExitCode, Is.Zero, await error + await output);
        var last = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];
        return JsonSerializer.Deserialize<DriverResults>(last, Json)!;
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
            "passphraseKey": s.key_from_passphrase(v["passphrase"]).hex(),
            "passphraseAccepted": [not refused(lambda r=r: s.key_from_passphrase(r["passphrase"])) for r in v["passphraseRules"]],
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
        const passphraseAccepted = [];
        for (const rule of v.passphraseRules) passphraseAccepted.push(!await refused(() => keyFromPassphrase(rule.passphrase)));
        console.log(JSON.stringify({
          steps,
          before,
          after: await narrowBlob(before, b.narrowedWith, b.maxTokenLength, b.maxBlobBytes),
          afterFromCamelCase: await narrowBlob(b.camelCase, b.narrowedWith, b.maxTokenLength, b.maxBlobBytes),
          refused: results,
          v1Refused: await refused(() => narrow(btoa("\0".repeat(130)), ["id=x"])),
          badCaveatRefused: await refused(() => narrow(v.root, ["sub=a.b"])),
          extra: await narrow(v.root, ["sub=docs", "host=from-docs"]),
          passphraseKey: Array.from(await keyFromPassphrase(v.passphrase), x => x.toString(16).padStart(2, "0")).join(""),
          passphraseAccepted,
        }));
        """;

    private const string CSharpProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <!-- the guide promises .NET 8, whose compiler is C# 12 -->
            <LangVersion>12</LangVersion>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string CSharpDriver = """
        using System.Text.Json;

        var v = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
        var b = v.GetProperty("blob");
        var root = v.GetProperty("root").GetString()!;
        var narrowedWith = b.GetProperty("narrowedWith").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var (maxToken, maxBlob) = (b.GetProperty("maxTokenLength").GetInt32(), b.GetProperty("maxBlobBytes").GetInt32());
        var token = root;
        var steps = new List<string>();
        foreach (var step in v.GetProperty("steps").EnumerateArray()) steps.Add(token = StyxIssue.Narrow(token, [step.GetProperty("caveat").GetString()!]));
        var before = StyxIssue.MakeBlob(b.GetProperty("styxServer").GetString()!, b.GetProperty("encryptionKey").GetString()!, root);
        Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["steps"] = steps,
            ["before"] = before,
            ["after"] = StyxIssue.NarrowBlob(before, narrowedWith, maxToken, maxBlob),
            ["afterFromCamelCase"] = StyxIssue.NarrowBlob(b.GetProperty("camelCase").GetString()!, narrowedWith, maxToken, maxBlob),
            ["refused"] = v.GetProperty("refusedBlobValues").EnumerateArray().Select(bad => Refused(() => StyxIssue.MakeBlob("https://s", bad.GetString()!, "a"))).ToArray(),
            ["v1Refused"] = Refused(() => StyxIssue.Narrow(Convert.ToBase64String(new byte[130]), ["id=x"])),
            ["badCaveatRefused"] = Refused(() => StyxIssue.Narrow(root, ["sub=a.b"])),
            ["extra"] = StyxIssue.Narrow(root, ["sub=docs", "host=from-docs"]),
            ["passphraseKey"] = Convert.ToHexString(StyxIssue.KeyFromPassphrase(v.GetProperty("passphrase").GetString()!)),
            ["passphraseAccepted"] = v.GetProperty("passphraseRules").EnumerateArray().Select(r => !Refused(() => StyxIssue.KeyFromPassphrase(r.GetProperty("passphrase").GetString()!))).ToArray(),
        }));

        static bool Refused(Action call)
        {
            try { call(); return false; }
            catch (FormatException) { return true; }
        }
        """;

    private const string RustPackage = """
        [package]
        name = "styx-docs"
        version = "0.1.0"
        edition = "2021"

        """;

    private const string RustDriver = """
        mod styx_issue;

        use serde_json::{json, Value};
        use styx_issue::{key_from_passphrase, make_blob, narrow, narrow_blob};

        fn text(value: &Value) -> &str {
            value.as_str().expect("a string")
        }

        fn main() {
            let path = std::env::args().nth(1).expect("the vectors' path");
            let v: Value = serde_json::from_str(&std::fs::read_to_string(path).expect("the vectors")).expect("json");
            let b = &v["blob"];
            let root = text(&v["root"]);
            let narrowed: Vec<&str> = b["narrowedWith"].as_array().unwrap().iter().map(text).collect();
            let (max_token, max_blob) = (b["maxTokenLength"].as_u64().map(|n| n as usize), b["maxBlobBytes"].as_u64().map(|n| n as usize));
            let mut token = root.to_string();
            let mut steps = Vec::new();
            for step in v["steps"].as_array().unwrap() {
                token = narrow(&token, &[text(&step["caveat"])], None).unwrap();
                steps.push(token.clone());
            }
            let before = make_blob(text(&b["styxServer"]), text(&b["encryptionKey"]), root).unwrap();
            let refused: Vec<bool> = v["refusedBlobValues"].as_array().unwrap().iter().map(|bad| make_blob("https://s", text(bad), "a").is_err()).collect();
            let key: String = key_from_passphrase(text(&v["passphrase"])).unwrap().iter().map(|x| format!("{x:02x}")).collect();
            let accepted: Vec<bool> = v["passphraseRules"].as_array().unwrap().iter().map(|r| key_from_passphrase(text(&r["passphrase"])).is_ok()).collect();
            println!("{}", json!({
                "steps": steps,
                "before": before,
                "after": narrow_blob(&before, &narrowed, max_token, max_blob).unwrap(),
                "afterFromCamelCase": narrow_blob(text(&b["camelCase"]), &narrowed, max_token, max_blob).unwrap(),
                "refused": refused,
                "v1Refused": narrow(&"A".repeat(176), &["id=x"], None).is_err(),
                "badCaveatRefused": narrow(root, &["sub=a.b"], None).is_err(),
                "extra": narrow(root, &["sub=docs", "host=from-docs"], None).unwrap(),
                "passphraseKey": key,
                "passphraseAccepted": accepted,
            }));
        }
        """;

    private sealed record Vectors(string Key, int Kid, string Network, string Root, VectorStep[] Steps, RejectedToken[] Rejected, Blob Blob, string Passphrase,
        string PassphraseKey, PassphraseRule[] PassphraseRules, string[] RefusedBlobValues);

    private sealed record PassphraseRule(string Why, string Passphrase, bool Accepted);

    private sealed record VectorStep(string Caveat, string Signature, string Token);

    private sealed record RejectedToken(string Why, string Token);

    private sealed record Blob(string StyxServer, string EncryptionKey, string Before, string CamelCase, string[] NarrowedWith, int MaxTokenLength, int MaxBlobBytes, string After);

    private sealed record DriverResults(string[] Steps, string Before, string After, string AfterFromCamelCase, bool[] Refused, bool V1Refused, bool BadCaveatRefused, string Extra,
        string PassphraseKey, bool[] PassphraseAccepted);
}
