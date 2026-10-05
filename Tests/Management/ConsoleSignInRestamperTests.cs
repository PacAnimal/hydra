using Hydra.Management;

namespace Tests.Management;

/// <summary>
/// The session child starts at the logon screen, before anyone signs in, and a sign-in to that same session
/// restarts nothing — so the sidecar is re-stamped when a console user appears, and never while there is none.
/// </summary>
[TestFixture]
public class ConsoleSignInRestamperTests
{
    private string? _user;
    private bool _succeeds;
    private readonly List<string> _stamped = [];
    private readonly List<bool> _retries = [];

    [SetUp]
    public void SetUp()
    {
        _user = null;
        _succeeds = true;
        _stamped.Clear();
        _retries.Clear();
    }

    [Test]
    public async Task NoConsoleUser_DoesNotRestamp()
    {
        var restamper = Restamper();

        await restamper.Check();

        Assert.That(_stamped, Is.Empty, "a stamp without a console user strips the one it had");
    }

    [Test]
    public async Task UserSigningInAfterStart_IsStamped()
    {
        var restamper = Restamper();
        await restamper.Check();

        _user = "S-1-5-21-alice";
        await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice"]));
    }

    [Test]
    public async Task SameUserStillSignedIn_IsStampedOnce()
    {
        _user = "S-1-5-21-alice";
        var restamper = Restamper();

        await restamper.Check();
        await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice"]));
    }

    // a write made while nobody was signed in dropped them, so their return must stamp again
    [Test]
    public async Task UserSigningOutAndBackIn_IsStampedAgain()
    {
        _user = "S-1-5-21-alice";
        var restamper = Restamper();
        await restamper.Check();
        _user = null;
        await restamper.Check();

        _user = "S-1-5-21-alice";
        await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-alice"]));
    }

    [Test]
    public async Task AnotherUserSigningIn_IsStamped()
    {
        _user = "S-1-5-21-alice";
        var restamper = Restamper();
        await restamper.Check();

        _user = "S-1-5-21-bob";
        await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-bob"]));
    }

    // a failed stamp is not taken as done: the user is stamped again once the backoff has passed
    [Test]
    public async Task AFailedStamp_IsRetriedAfterABackoff()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        await restamper.Check();

        _succeeds = true;
        for (var poll = 0; poll < ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();
        Assert.That(_stamped, Has.Count.EqualTo(1), "retried without waiting");

        await restamper.Check();
        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-alice"]));
    }

    [Test]
    public async Task AFaultedStamp_IsRetried()
    {
        _user = "S-1-5-21-alice";
        var restamper = new ConsoleSignInRestamper(() => _user, _ =>
        {
            _stamped.Add(_user!);
            return _stamped.Count == 1 ? Task.FromException<bool>(new IOException("held")) : Task.FromResult(true);
        });
        await restamper.Check();

        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();

        Assert.That(_stamped, Has.Count.EqualTo(2));
    }

    // each failure in a row waits twice as long as the one before, up to the cap
    [Test]
    public async Task RepeatedFailures_BackOffFurther()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        await restamper.Check();
        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();
        Assert.That(_stamped, Has.Count.EqualTo(2));

        for (var poll = 0; poll < ConsoleSignInRestamper.FirstRetryPolls * 2; poll++)
            await restamper.Check();
        Assert.That(_stamped, Has.Count.EqualTo(2), "the second wait was no longer than the first");

        await restamper.Check();
        Assert.That(_stamped, Has.Count.EqualTo(3));
    }

    // a stamp still running is not started twice, and a user change meanwhile is stamped once it ends
    [Test]
    public async Task AStampStillRunning_IsNotStartedAgain()
    {
        _user = "S-1-5-21-alice";
        var running = new TaskCompletionSource<bool>();
        var restamper = new ConsoleSignInRestamper(() => _user, _ =>
        {
            _stamped.Add(_user!);
            return _stamped.Count == 1 ? running.Task : Task.FromResult(true);
        });
        _ = restamper.Check();

        _user = "S-1-5-21-bob";
        _ = restamper.Check();
        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice"]));

        running.SetResult(true);
        await restamper.Check();
        await restamper.Check();
        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-bob"]));
    }

    // the backoff stops doubling at the cap
    [Test]
    public async Task RepeatedFailures_BackOffNoFurtherThanTheCap()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        var stampedAt = new List<int>();
        for (var poll = 0; stampedAt.Count < 10; poll++)
        {
            var before = _stamped.Count;
            await restamper.Check();
            if (_stamped.Count > before) stampedAt.Add(poll);
        }

        var gaps = stampedAt.Zip(stampedAt.Skip(1), (a, b) => b - a - 1).ToArray();
        Assert.That(gaps[^3..], Is.All.EqualTo(ConsoleSignInRestamper.MaxRetryPolls));
    }

    // whoever signs in while another user's stamp is backing off is stamped at once, not after the wait
    [Test]
    public async Task AnotherUserSigningInDuringABackoff_IsStampedAtOnce()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        await restamper.Check();
        await restamper.Check();

        _user = "S-1-5-21-bob";
        await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-bob"]));
    }

    // and starts their own streak, waiting only the first backoff if it fails
    [Test]
    public async Task AnotherUserSigningInDuringABackoff_StartsTheBackoffAgain()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        await restamper.Check();
        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();
        _user = "S-1-5-21-bob";
        await restamper.Check();

        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();

        Assert.That(_stamped, Is.EqualTo(["S-1-5-21-alice", "S-1-5-21-alice", "S-1-5-21-bob", "S-1-5-21-bob"]));
    }

    // only the first failure of a streak is news; a success or another user ends the streak
    [Test]
    public async Task RetriesOfAFailedStamp_AreFlaggedAsRetries()
    {
        _user = "S-1-5-21-alice";
        _succeeds = false;
        var restamper = Restamper();
        await restamper.Check();
        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();
        _user = "S-1-5-21-bob";
        await restamper.Check();
        _succeeds = true;
        for (var poll = 0; poll <= ConsoleSignInRestamper.FirstRetryPolls; poll++)
            await restamper.Check();
        _succeeds = false;
        _user = "S-1-5-21-carol";
        await restamper.Check();

        Assert.That(_retries, Is.EqualTo([false, true, false, true, false]));
    }

    private ConsoleSignInRestamper Restamper() => new(() => _user, async retry =>
    {
        _stamped.Add(_user!);
        _retries.Add(retry);
        await Task.CompletedTask;
        return _succeeds;
    });
}
