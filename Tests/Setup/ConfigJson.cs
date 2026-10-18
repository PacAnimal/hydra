namespace Tests.Setup;

internal static class ConfigJson
{
    // a hydra.conf holding these profiles
    public static string AsFile(string profilesJson) => $$"""{"profiles":{{profilesJson}}}""";
}
