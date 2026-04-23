namespace Styx.Services;

public interface IStyxPasswordProvider
{
    string Password { get; }
}

// reads password from RELAY_PASSWORD environment variable — used by the standalone Styx server
public class EnvironmentStyxPasswordProvider : IStyxPasswordProvider
{
    public string Password => Environment.GetEnvironmentVariable(Constants.RelayPasswordEnvVar)
        ?? throw new InvalidOperationException("RELAY_PASSWORD environment variable is not set");
}

// uses a password supplied directly — used by the embedded Styx server inside Hydra
public class InlineStyxPasswordProvider(string password) : IStyxPasswordProvider
{
    public string Password => password;
}
