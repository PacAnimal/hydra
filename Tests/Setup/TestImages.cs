namespace Tests.Setup;

public static class TestImages
{
    // a real 1×1 PNG, which every platform's clipboard decodes; a fresh copy each time, so no test can change another's
    public static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
