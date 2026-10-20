namespace Styx;

// what the relay says on the wire: a refused login's message and Kicked's reason (Styx.md §5.1, §6.3)
public static class RelayReasons
{
    public const string InvalidAuthorization = "Invalid authorization";
    public const string AuthorizationExpired = "Authorization expired";
    public const string HostNameNotAllowed = "Authorization does not allow this hostname";
    public const string IncompleteLogin = "Authorization and hostName are both required";
    public const string ConnectionAborted = "Connection aborted";

    public const string KickedDuplicateHostname = "duplicate hostname";
    public const string KickedExpired = "authorization expired";
}
