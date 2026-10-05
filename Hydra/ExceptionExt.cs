namespace Hydra;

internal static class ExceptionExt
{
    // the inner message where there is one: HttpRequestException's own just says the request failed
    internal static string InnerMessage(this Exception ex) => ex.InnerException?.Message ?? ex.Message;
}
