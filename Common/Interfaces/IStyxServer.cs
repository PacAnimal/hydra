using Common.DTO;

namespace Common.Interfaces;

public interface IStyxServer
{
    Task<RelayLoginResponse> Authenticate(RelayLogin login);
    Task<bool> Ping();
    Task Send(string[] targetHosts, byte[] payload);
}
