namespace Common.DTO;

public class RelayLoginResponse
{
    public required bool Authenticated { get; init; }
    public string? Message { get; init; }
}
