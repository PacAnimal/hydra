using System.Text.Json.Nodes;

namespace Hydra.Management;

internal static class RemoteConnectivityGuard
{
    internal static IReadOnlyList<string> FindRiskyChanges(string currentJson, string candidateJson)
    {
        var current = JsonNode.Parse(currentJson) as JsonObject
            ?? throw new InvalidOperationException("Current configuration root must be an object.");
        var candidate = JsonNode.Parse(candidateJson) as JsonObject
            ?? throw new InvalidOperationException("Candidate configuration root must be an object.");
        var changed = new List<string>();

        Compare(current, candidate, "name", "machine name", changed);
        Compare(current, candidate, "profile", "forced profile", changed);

        var currentProfiles = current.GetIgnoreCase("profiles") as JsonArray ?? [];
        var candidateProfiles = candidate.GetIgnoreCase("profiles") as JsonArray ?? [];
        if (currentProfiles.Count != candidateProfiles.Count)
        {
            changed.Add("profile collection");
            return changed;
        }

        for (var i = 0; i < currentProfiles.Count; i++)
        {
            var before = currentProfiles[i] as JsonObject ?? [];
            var after = candidateProfiles[i] as JsonObject ?? [];
            Compare(before, after, "profileName", $"profile {i + 1} name", changed);
            Compare(before, after, "mode", $"profile {i + 1} mode", changed);
            Compare(before, after, "networkConfig", $"profile {i + 1} networkConfig", changed);
            Compare(before, after, "embeddedStyx", $"profile {i + 1} embedded relay client", changed);
            Compare(before, after, "embeddedStyxServer", $"profile {i + 1} embedded relay server", changed);
            Compare(before, after, "conditions", $"profile {i + 1} activation conditions", changed);
        }
        return changed;
    }

    private static void Compare(JsonObject before, JsonObject after, string property, string label, List<string> changed)
    {
        if (!JsonNode.DeepEquals(before.GetIgnoreCase(property), after.GetIgnoreCase(property))) changed.Add(label);
    }
}
