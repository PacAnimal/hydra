namespace Hydra.Screen;

public static class ScreenNaming
{
    public static string BuildScreenName(string hostname, int index, int total) =>
        total == 1 ? hostname : $"{hostname}:{index}";
}
