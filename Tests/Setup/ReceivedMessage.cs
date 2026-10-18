using Hydra.Relay;

namespace Tests.Setup;

/// <summary>A payload a test relay client received: who sent it, its kind, and its body as text.</summary>
public sealed record ReceivedMessage(string Source, MessageKind Kind, string Json);
