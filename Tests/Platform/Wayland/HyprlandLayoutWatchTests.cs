using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Hydra.Platform.Linux.Wayland;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Platform.Wayland;

[TestFixture]
public class HyprlandLayoutWatchTests
{
    private const string MainSecond = """
        {"mice": [], "keyboards": [
          {"name": "power-button", "layout": "us,ru", "active_layout_index": 0, "active_keymap": "English (US)", "main": false},
          {"name": "at-translated-set-2-keyboard", "layout": "us,ru", "active_layout_index": 1, "active_keymap": "Russian", "main": true}
        ]}
        """;

    private const string NoMain = """
        {"keyboards": [
          {"name": "power-button", "layout": "us,ru", "active_layout_index": 1, "main": false},
          {"name": "video-bus", "layout": "us,ru", "active_layout_index": 0, "main": false}
        ]}
        """;

    [Test]
    public void Devices_TheMainKeyboardsIndex()
    {
        Assert.That(HyprlandLayoutWatch.GroupFromDevices(MainSecond), Is.EqualTo(1u));
    }

    [Test]
    public void Devices_NoMainKeyboard_TheFirstKeyboardsIndex()
    {
        Assert.That(HyprlandLayoutWatch.GroupFromDevices(NoMain), Is.EqualTo(1u));
    }

    [TestCase("""{"mice": [], "keyboards": []}""")]
    [TestCase("""{"mice": []}""")]
    [TestCase("""{"keyboards": [{"name": "kb", "main": true}]}""")]
    [TestCase("""[]""")]
    [TestCase("""{"keyboards": [""")]
    [TestCase("not json")]
    [TestCase("")]
    public void Devices_NoKeyboardOrBadJson_Null(string json)
    {
        Assert.That(HyprlandLayoutWatch.GroupFromDevices(json), Is.Null);
    }

    [TestCase("activelayout>>power-button,Russian", true)]
    [TestCase("activelayout>>at-translated-set-2-keyboard,English (US)", true)]
    [TestCase("activelayoutv2>>keyboard,English (US),1", true)]
    [TestCase("activewindow>>kitty,~", false)]
    [TestCase("workspace>>2", false)]
    [TestCase("", false)]
    [TestCase(" activelayout>>kb,Russian", false)]
    public void LayoutEvent_IsRecognised(string line, bool expected)
    {
        Assert.That(HyprlandLayoutWatch.IsLayoutEvent(line), Is.EqualTo(expected));
    }

    [Test]
    public void NoSockets_NoWatch()
    {
        var dir = TestPaths.FreshFixtureRoot("hyprnone");

        Assert.That(HyprlandLayoutWatch.Start(dir, (_, _) => { }, new CapturingLog<HyprlandLayoutWatchTests>()), Is.Null);
    }

    // a fake Hyprland: the request socket answers j/devices from whatever index is current, the event socket sends a
    // switch when told to
    [Test]
    public async Task FakeHyprland_TheStartGroup_ThenTheSwitchedOne()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix domain sockets: Linux and macOS only");
        // short: a socket path is limited to about 100 bytes
        var dir = TestPaths.FreshFixtureRoot("hypr");
        using var timeout = new CancellationTokenSource(Bound);
        var index = new StrongBox<int>(0);
        var served = new StrongBox<int>(0);
        // the requests served when the activelayout line is sent
        var atEvent = new StrongBox<int>(-1);
        var asked = Channel.CreateUnbounded<int>();
        var switched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var requests = Listen(Path.Combine(dir, ".socket.sock"));
        using var events = Listen(Path.Combine(dir, ".socket2.sock"));
        var requestServer = ServeRequests(requests, () => Volatile.Read(ref index.Value), served, asked.Writer, timeout.Token);
        var eventServer = ServeEvents(events, switched.Task, timeout.Token,
            sending: () => Volatile.Write(ref atEvent.Value, Volatile.Read(ref served.Value)));
        var groups = Channel.CreateUnbounded<uint>();
        var log = new CapturingLog<HyprlandLayoutWatchTests>();

        var watch = HyprlandLayoutWatch.Start(dir, (_, g) => groups.Writer.TryWrite(g), log);
        Assert.That(watch, Is.Not.Null);
        await using (watch)
        {
            var first = await groups.Reader.ReadAsync(timeout.Token);
            // the start's query and the event connection's catch-up, both answered with 0: the 1 can only come from
            // the query the event asks for
            await asked.Reader.ReadAsync(timeout.Token);
            await asked.Reader.ReadAsync(timeout.Token);
            Volatile.Write(ref index.Value, 1);
            switched.SetResult();
            var second = await groups.Reader.ReadAsync(timeout.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.Zero);
                Assert.That(second, Is.EqualTo(1u));
                Assert.That(log.Messages, Has.One.EqualTo("Keyboard layout: following Hyprland's active layout"));
                Assert.That(Volatile.Read(ref atEvent.Value), Is.EqualTo(2), "only the start's query and the catch-up before the event");
                Assert.That(Volatile.Read(ref served.Value), Is.GreaterThan(Volatile.Read(ref atEvent.Value)), "asked again after the event");
            }
        }
        await timeout.CancelAsync();
        await Task.WhenAll(requestServer, eventServer);
    }

    // the answer to the first query is held until the watch is disposed: nothing is reported after that
    [Test]
    public async Task AQueryAnsweredAfterDispose_ReportsNothing()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix domain sockets: Linux and macOS only");
        var dir = TestPaths.FreshFixtureRoot("hypd");
        using var timeout = new CancellationTokenSource(Bound);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = Channel.CreateUnbounded<int>();
        using var requests = Listen(Path.Combine(dir, ".socket.sock"));
        using var events = Listen(Path.Combine(dir, ".socket2.sock"));
        var requestServer = ServeHeldRequests(requests, () => 1, asked.Writer, () => hold.Task, timeout.Token);
        var eventServer = ServeEvents(events, Task.Delay(Timeout.Infinite, timeout.Token), timeout.Token);
        var groups = Channel.CreateUnbounded<uint>();

        var watch = HyprlandLayoutWatch.Start(dir, (_, g) => groups.Writer.TryWrite(g), new CapturingLog<HyprlandLayoutWatchTests>());
        Assert.That(watch, Is.Not.Null);
        await asked.Reader.ReadAsync(timeout.Token);
        await watch!.DisposeAsync();
        hold.SetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Reader.TryRead(out _), Is.False, "no report after Dispose");
            Assert.That(watch.Refresh(after: TimeProvider.System.GetTimestamp()).IsCompleted, Is.True, "a disposed watch asks nothing");
        }
        await timeout.CancelAsync();
        await Task.WhenAll(requestServer, eventServer);
    }

    // a second keyboard on another group raises no event: Refresh asks j/devices once more, and a Refresh for the same
    // time while one is under way joins it rather than asking again
    [Test]
    public async Task Refresh_AsksAgain_AndAConcurrentOneJoinsIt()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix domain sockets: Linux and macOS only");
        var dir = TestPaths.FreshFixtureRoot("hypr");
        using var timeout = new CancellationTokenSource(Bound);
        var time = new FakeTimeProvider();
        var index = new StrongBox<int>(0);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new StrongBox<int>(0);
        var asked = Channel.CreateUnbounded<int>();
        using var requests = Listen(Path.Combine(dir, ".socket.sock"));
        using var events = Listen(Path.Combine(dir, ".socket2.sock"));
        var requestServer = ServeHeldRequests(requests, () => Volatile.Read(ref index.Value), asked.Writer,
            () => Volatile.Read(ref holding.Value) != 0 ? hold.Task : Task.CompletedTask, timeout.Token);
        var eventServer = ServeEvents(events, Task.Delay(Timeout.Infinite, timeout.Token), timeout.Token);
        var groups = Channel.CreateUnbounded<uint>();

        var watch = HyprlandLayoutWatch.Start(dir, (_, g) => groups.Writer.TryWrite(g), new CapturingLog<HyprlandLayoutWatchTests>(), time);
        Assert.That(watch, Is.Not.Null);
        await using (watch)
        {
            var first = await groups.Reader.ReadAsync(timeout.Token);
            // the start's query and the event connection's catch-up
            await asked.Reader.ReadAsync(timeout.Token);
            await asked.Reader.ReadAsync(timeout.Token);
            Volatile.Write(ref index.Value, 1);
            Volatile.Write(ref holding.Value, 1);
            time.Advance(TimeSpan.FromSeconds(1));
            var now = time.GetTimestamp();

            var refresh = watch!.Refresh(after: now);
            await asked.Reader.ReadAsync(timeout.Token);
            var joined = watch.Refresh(after: now);
            hold.SetResult();
            await refresh.WaitAsync(timeout.Token);
            var second = await groups.Reader.ReadAsync(timeout.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.Zero);
                Assert.That(second, Is.EqualTo(1u));
                Assert.That(joined, Is.SameAs(refresh), "the second Refresh joins the one under way");
                Assert.That(asked.Reader.TryRead(out _), Is.False, "one request for both");
                Assert.That(watch.Refresh(after: now).IsCompleted, Is.True, "answered already by the query asked at that time");
            }
        }
        await timeout.CancelAsync();
        await Task.WhenAll(requestServer, eventServer);
    }

    // the first key's Refresh comes after the capture's, which may have asked before the user switched keyboards: one
    // asked before the caller's time is not joined but followed by another
    [Test]
    public async Task Refresh_AfterTheOneUnderWayAsked_AsksAgain()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix domain sockets: Linux and macOS only");
        var dir = TestPaths.FreshFixtureRoot("hypr");
        using var timeout = new CancellationTokenSource(Bound);
        var time = new FakeTimeProvider();
        // the start's query, the event connection's catch-up and the capture's answer from before the switch; the
        // fourth request, after it
        var served = new StrongBox<int>(0);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new StrongBox<int>(0);
        var asked = Channel.CreateUnbounded<int>();
        using var requests = Listen(Path.Combine(dir, ".socket.sock"));
        using var events = Listen(Path.Combine(dir, ".socket2.sock"));
        var requestServer = ServeHeldRequests(requests, () => Interlocked.Increment(ref served.Value) <= 3 ? 0 : 1, asked.Writer,
            () => Volatile.Read(ref holding.Value) != 0 ? hold.Task : Task.CompletedTask, timeout.Token);
        var eventServer = ServeEvents(events, Task.Delay(Timeout.Infinite, timeout.Token), timeout.Token);
        var groups = Channel.CreateUnbounded<uint>();

        var watch = HyprlandLayoutWatch.Start(dir, (_, g) => groups.Writer.TryWrite(g), new CapturingLog<HyprlandLayoutWatchTests>(), time);
        Assert.That(watch, Is.Not.Null);
        await using (watch)
        {
            _ = await groups.Reader.ReadAsync(timeout.Token);
            await asked.Reader.ReadAsync(timeout.Token);
            await asked.Reader.ReadAsync(timeout.Token);
            Volatile.Write(ref holding.Value, 1);
            time.Advance(TimeSpan.FromSeconds(1));

            var atCapture = watch!.Refresh(after: time.GetTimestamp());
            await asked.Reader.ReadAsync(timeout.Token);
            time.Advance(TimeSpan.FromSeconds(1));
            var atKey = watch.Refresh(after: time.GetTimestamp());
            hold.SetResult();
            await atKey.WaitAsync(timeout.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(atKey, Is.Not.SameAs(atCapture), "not joined");
                Assert.That(asked.Reader.TryRead(out _), Is.True, "asked again");
                Assert.That(await groups.Reader.ReadAsync(timeout.Token), Is.EqualTo(1u), "the group after the switch");
            }
        }
        await timeout.CancelAsync();
        await Task.WhenAll(requestServer, eventServer);
    }

    private static Socket Listen(string path)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        return listener;
    }

    private static string Devices(int index) =>
        $$"""{"keyboards": [{"name": "kb", "layout": "us,ru", "active_layout_index": {{index}}, "active_keymap": "x", "main": true}]}""";

    // served counts the requests answered and asked hears of each, both before the answer ends, so the watch sees
    // neither its answer nor its report before them
    private static async Task ServeRequests(Socket listener, Func<int> index, StrongBox<int> served, ChannelWriter<int> asked, CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                using var socket = await listener.AcceptAsync(cancel);
                try
                {
                    var buffer = new byte[256];
                    var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancel);
                    var answer = Encoding.UTF8.GetString(buffer, 0, read) == "j/devices" ? Devices(index()) : "unknown request";
                    await socket.SendAsync(Encoding.UTF8.GetBytes(answer), SocketFlags.None, cancel);
                    Interlocked.Increment(ref served.Value);
                    asked.TryWrite(1);
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // the watch hung up first: a query its dispose cancelled (macOS reports the late send as EPIPE)
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // answers j/devices as ServeRequests does, telling asked of each request; an answer whose hold is pending waits for
    // it and is read after, else it is read before asked hears of it, so a test changing the index then sees no race
    private static async Task ServeHeldRequests(Socket listener, Func<int> index, ChannelWriter<int> asked, Func<Task> hold, CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                using var socket = await listener.AcceptAsync(cancel);
                try
                {
                    var buffer = new byte[256];
                    var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancel);
                    var known = Encoding.UTF8.GetString(buffer, 0, read) == "j/devices";
                    var held = hold();
                    var answer = held.IsCompleted ? Devices(index()) : null;
                    asked.TryWrite(1);
                    await held.WaitAsync(cancel);
                    answer ??= Devices(index());
                    await socket.SendAsync(Encoding.UTF8.GetBytes(known ? answer : "unknown request"), SocketFlags.None, cancel);
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // the watch hung up first: a query its dispose cancelled (macOS reports the late send as EPIPE)
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // sends a workspace line, then the activelayout line once switched completes, calling sending just before it
    private static async Task ServeEvents(Socket listener, Task switched, CancellationToken cancel, Action? sending = null)
    {
        var sent = false;
        try
        {
            using var socket = await listener.AcceptAsync(cancel);
            await socket.SendAsync("workspace>>1\n"u8.ToArray(), SocketFlags.None, cancel);
            await switched.WaitAsync(cancel);
            sending?.Invoke();
            await socket.SendAsync("activelayout>>kb,Russian\n"u8.ToArray(), SocketFlags.None, cancel);
            sent = true;
            await Task.Delay(Timeout.Infinite, cancel);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (sent)
        {
            // the watch hung up first, as its dispose may; before the line, a failure faults the test
        }
    }
}
