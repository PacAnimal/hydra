namespace Tests.Setup;

// a fixture whose tests start services, every one of them stopped after each test
public abstract class ServicesFixture
{
    private protected StartedServices Services { get; } = new();

    [TearDown]
    public Task StopServices() => Services.StopAll();
}
