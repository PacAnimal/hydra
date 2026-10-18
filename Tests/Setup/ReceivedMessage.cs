using Cathedral.Extensions;
using Hydra.Relay;

namespace Tests.Setup;

/// <summary>A payload a test relay client received: who sent it, its kind, and its body as text.</summary>
public sealed record ReceivedMessage(string Source, MessageKind Kind, string Json)
{
    public T As<T>() => Json.FromSaneJson<T>() ?? throw new InvalidOperationException($"{Kind} carried no {typeof(T).Name}");
}
