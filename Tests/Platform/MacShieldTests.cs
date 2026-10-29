using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

// drives the real hydra-shield from the build output, so an Intel runner exercises its x86_64 slice
[TestFixture]
public class MacShieldTests
{
    [OneTimeSetUp]
    public void RequireMacOs()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS-only: hydra-shield");
    }

    [Test]
    public void Binary_IsUniversal()
    {
        var archs = Tool("lipo", "-archs", MacShieldProcess.BinaryPath).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.That(archs, Is.EquivalentTo(["x86_64", "arm64"]));
    }

    [Test]
    public void Binary_RunsOnMacOs13()
    {
        // swiftc stamps the build machine's own macOS version unless told otherwise, locking out every older mac
        var minimums = Regex.Matches(Tool("vtool", "-show-build", MacShieldProcess.BinaryPath), @"minos (\S+)")
            .Select(m => Version.Parse(m.Groups[1].Value))
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(minimums, Has.Count.EqualTo(2));
            Assert.That(minimums, Has.All.LessThanOrEqualTo(new Version(13, 0)));
        }
    }

    [Test]
    public async Task Shield_AcksEachState_AndExitsWhenStdinCloses()
    {
        await using var shield = RunningShield.Start();

        foreach (var state in new[] { "show", "hide" })
            Assert.That(await shield.Ack(state), Is.True, $"no ack for {state}");

        Assert.That(await shield.Exit(), Is.Zero, shield.StderrText);
    }

    [Test]
    public async Task Shield_AcksConcurrentStates_EachBySeq()
    {
        await using var shield = RunningShield.Start();

        string[] states = ["show", "hide", "show"];
        var acks = await Task.WhenAll(states.Select(shield.Ack));

        Assert.That(acks, Is.All.True);
    }

    [Test]
    public async Task Shield_AcceptsEveryMessageHydraSends()
    {
        await using var shield = RunningShield.Start();
        // the old line protocol split on these
        string[] names = ["blåbær|1;2:3.txt", "日本語:ファイル|a;b.png", "🐙 octo;pus:|.bin"];

        // wifi is left out: it would prompt for location access
        await shield.Send("osd", new() { ["text"] = "Hydra → Ødegård 🐙" });
        await shield.Send("transferBegin", new() { ["totalBytes"] = 3L << 30, ["fileCount"] = names.Length, ["isSender"] = false, ["names"] = names });
        await shield.Send("transferFile", new() { ["name"] = names[1] });
        await shield.Send("transferProgress", new() { ["bytes"] = 1L << 30, ["speed"] = 12.5e6 });
        await shield.Send("transferDone");
        await shield.Send("transferError", new() { ["message"] = "disk full: ø|;:" });
        await shield.Send("transferClose");
        // the main queue is serial, so the echo comes back only after all of the above were handled
        Assert.That(await shield.Echo("done"), Is.EqualTo("done"));

        var exitCode = await shield.Exit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(shield.Stderr, Is.Empty);
        }
    }

    // a documentation address off the local network, so the datagram asks macOS for nothing
    private static readonly string[] OffLinkGateway = ["192.0.2.1"];

    // without Local Network access the table reads empty, so this pins the reply, not what is in it
    [Test]
    public async Task Shield_AnswersNeighbours_WithWholeEntries()
    {
        await using var shield = RunningShield.Start();

        var reply = await shield.Reply("neighbours", new() { ["gateways"] = OffLinkGateway });

        Assert.That(reply, Is.Not.Null, shield.StderrText);
        var entries = reply!["entries"] as object?[];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Is.Not.Null, "no entries array");
            Assert.That(MacShieldProcess.ParseNeighbours(reply), Has.Count.EqualTo(entries?.Length ?? -1));
        }
    }

    [Test]
    public async Task Shield_IgnoresUnknownKind()
    {
        await using var shield = RunningShield.Start();

        await shield.Send("nonsense");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await shield.Ack("show"), Is.True, "no ack after the unknown kind");
            Assert.That(await shield.Echo("alive"), Is.EqualTo("alive"));
        }
        var exitCode = await shield.Exit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(shield.Stderr.Count(line => line.Contains("nonsense")), Is.EqualTo(1), shield.StderrText);
        }
    }

    [Test]
    public Task Echo_NilAndBooleans() => AssertEchoes(null, true, false);

    [Test]
    public Task Echo_Integers() => AssertEchoes(
        0, 127, 128, 255, 256, 65535, 65536, 4294967295L, 4294967296L, long.MaxValue,
        -1, -32, -33, -128, -129, -32768, -32769, int.MinValue - 1L, long.MinValue,
        ulong.MaxValue);

    [Test]
    public Task Echo_Doubles() => AssertEchoes(1.5, -273.15);

    [Test]
    public Task Echo_Strings() => AssertEchoes(
        Ascii(0), Ascii(31), Ascii(32), Ascii(255), Ascii(256), Ascii(65535), Ascii(65536), "blåbær 🐙\nnæste linje");

    [Test]
    public Task Echo_Binaries() => AssertEchoes(Bytes(0), Bytes(255), Bytes(256), Bytes(65535), Bytes(65536));

    [Test]
    public Task Echo_Arrays() => AssertEchoes(SmallInts(15), SmallInts(16), SmallInts(65536));

    [Test]
    public Task Echo_Maps() => AssertEchoes(Map(15), Map(16), new Dictionary<string, object?>
    {
        ["list"] = new object?[] { 1, "two", new Dictionary<string, object?> { ["deep"] = true, ["none"] = null } },
        ["name"] = "outer",
    });

    // inside the frame's own map, 64 arrays put the innermost at the 64 levels both decoders allow
    [Test]
    public Task Echo_NestedToTheDepthLimit() => AssertEchoes(Nested(64));

    [Test]
    public async Task Echo_NestedPastTheDepthLimit_IsRefused_AndTheShieldStaysAlive()
    {
        await using var shield = RunningShield.Start();

        // inside the frame's own map, so 65 arrays put the innermost past 64 levels
        await shield.Send("echo", new() { ["payload"] = Nested(65) });

        Assert.That(await shield.Echo("alive"), Is.EqualTo("alive"), "the deep echo was answered, or the shield died");
        var exitCode = await shield.Exit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(shield.Stderr.Count(line => line.Contains("could not be decoded")), Is.EqualTo(1), shield.StderrText);
        }
    }

    // arrays nested depth deep, the innermost empty
    private static object?[] Nested(int depth)
    {
        object?[] value = [];
        for (var i = 1; i < depth; i++) value = [value];
        return value;
    }

    private static async Task AssertEchoes(params object?[] payloads)
    {
        await using var shield = RunningShield.Start();
        foreach (var payload in payloads)
            AssertDecodedAs(payload, await shield.Echo(payload), Label(payload));
    }

    // the shape ShieldChannel decodes: ints as long (ulong only above long.MaxValue), arrays as object?[], maps as dictionaries
    private static void AssertDecodedAs(object? expected, object? actual, string path)
    {
        switch (expected)
        {
            case int or long or ulong:
                Assert.That(actual, Is.TypeOf<long>().Or.TypeOf<ulong>(), path);
                Assert.That(Integer(actual), Is.EqualTo(Integer(expected)), path);
                break;
            case byte[] bytes:
                Assert.That(actual, Is.TypeOf<byte[]>().And.EqualTo(bytes), path);
                break;
            case IReadOnlyDictionary<string, object?> map:
                Assert.That(actual, Is.InstanceOf<IReadOnlyDictionary<string, object?>>(), path);
                var actualMap = (IReadOnlyDictionary<string, object?>)actual!;
                Assert.That(actualMap.Keys, Is.EquivalentTo(map.Keys), path);
                foreach (var pair in map)
                    AssertDecodedAs(pair.Value, actualMap[pair.Key], $"{path}.{pair.Key}");
                break;
            case IList list:
                Assert.That(actual, Is.TypeOf<object[]>(), path);
                var items = (object?[])actual!;
                Assert.That(items, Has.Length.EqualTo(list.Count), path);
                for (var i = 0; i < items.Length; i++)
                    AssertDecodedAs(list[i], items[i], $"{path}[{i}]");
                break;
            case null:
                Assert.That(actual, Is.Null, path);
                break;
            default:
                Assert.That(actual, Is.TypeOf(expected.GetType()).And.EqualTo(expected), path);
                break;
        }
    }

    private static Int128 Integer(object? value) => value switch
    {
        int i => i,
        long l => l,
        ulong u => u,
        _ => throw new ArgumentException($"not an integer: {value}"),
    };

    private static string Label(object? value) => value switch
    {
        null => "nil",
        string s => $"string of {s.Length}",
        byte[] b => $"bin of {b.Length}",
        IReadOnlyDictionary<string, object?> m => $"map of {m.Count}",
        IList l => $"array of {l.Count}",
        _ => $"{value.GetType().Name} {value}",
    };

    private static string Ascii(int length) => string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static object?[] SmallInts(int count) => [.. Enumerable.Range(0, count).Select(i => (object?)(i % 100))];

    private static Dictionary<string, object?> Map(int count) => Enumerable.Range(0, count).ToDictionary(i => $"k{i}", i => (object?)i);

    private static string Tool(string name, params string[] args)
    {
        using var tool = Process.Start(new ProcessStartInfo("xcrun", [name, .. args]) { RedirectStandardOutput = true })!;
        var output = tool.StandardOutput.ReadToEnd();
        tool.WaitForExit();
        Assert.That(tool.ExitCode, Is.Zero, $"{name} failed");
        return output;
    }

    // the binary on redirected stdio with a ShieldChannel over it; every wait shares one TestBound.Bound deadline
    private sealed class RunningShield : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ShieldChannel _channel;
        private readonly CancellationTokenSource _deadline = new(TestBound.Bound);
        private readonly Channel<ShieldMessage> _messages = Channel.CreateUnbounded<ShieldMessage>();
        private readonly List<string> _stderr = [];
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        private RunningShield(Process process)
        {
            _process = process;
            _channel = new ShieldChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, NullLogger.Instance);
            _stdoutPump = PumpMessages();
            _stderrPump = PumpStderr();
        }

        public IReadOnlyList<string> Stderr
        {
            get
            {
                lock (_stderr) return [.. _stderr];
            }
        }

        public string StderrText => string.Join('\n', Stderr);

        // started; what is sent waits in its stdin until it reads
        public static RunningShield Start() =>
            new(Process.Start(new ProcessStartInfo(MacShieldProcess.BinaryPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!);

        // acks are consumed inside ReadAll, which the pump keeps reading
        public Task<bool> Ack(string state) => Bounded(ct => _channel.SendAndAwaitAck(
            "shield", new Dictionary<string, object?> { ["state"] = state }, TestBound.Bound, ct).AsTask(), $"ack for {state}");

        public Task<ShieldMessage?> Reply(string kind, Dictionary<string, object?> fields) =>
            Bounded(ct => _channel.SendAndAwaitReply(kind, fields, TestBound.Bound, ct).AsTask(), $"reply to {kind}");

        public async Task Send(string kind, Dictionary<string, object?>? fields = null) => await Bounded(async ct =>
        {
            await _channel.Send(fields == null ? new ShieldMessage(kind) : new ShieldMessage(kind, fields), ct);
            return true;
        }, $"send of {kind}");

        public async Task<object?> Echo(object? payload)
        {
            await Send("echo", new() { ["payload"] = payload });
            var reply = await Next();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reply.Kind, Is.EqualTo("echo"));
                Assert.That(reply.Fields.ContainsKey("payload"), Is.True, "echo without a payload");
            }
            return reply["payload"];
        }

        public Task<ShieldMessage> Next() => Bounded(async ct =>
        {
            try
            {
                return await _messages.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                throw new AssertionException($"the shield closed its output; stderr: {StderrText}");
            }
        }, "a message");

        // the parent going away closes stdin, and the shield must not outlive it; the channel ends with it
        public async Task<int> Exit()
        {
            _process.StandardInput.Close();
            await Bounded(async ct =>
            {
                await _process.WaitForExitAsync(ct);
                await _stdoutPump.WaitAsync(ct);
                await _stderrPump.WaitAsync(ct);
                return true;
            }, "exit");
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            // a shield left running after "show" keeps absorbing the mouse until the test host exits
            if (!_process.HasExited) _process.Kill();
            await _process.WaitForExitAsync();
            // the pumps end with the process; bounded so a stuck one cannot hang teardown
            await TestBound.Settles(Task.WhenAll(_stdoutPump, _stderrPump));
            _process.Dispose();
            _deadline.Dispose();
        }

        private async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> wait, string what)
        {
            try
            {
                return await wait(_deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new AssertionException($"no {what} within {TestBound.Bound.TotalSeconds}s; stderr: {StderrText}");
            }
        }

        private async Task PumpMessages()
        {
            try
            {
                await foreach (var message in _channel.ReadAll(_deadline.Token))
                    _messages.Writer.TryWrite(message);
            }
            catch (OperationCanceledException)
            {
                // the deadline passed; Next reports it
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        private async Task PumpStderr()
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                lock (_stderr) _stderr.Add(line);
        }
    }
}
