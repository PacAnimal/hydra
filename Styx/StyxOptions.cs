namespace Styx;

/// <param name="ParallelInvocations">how many of a client's invocations /relay/parallel runs at once</param>
public record StyxOptions(bool DebugMessages, int ParallelInvocations = Constants.DefaultParallelInvocations);
