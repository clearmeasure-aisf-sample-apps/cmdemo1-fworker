using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Messaging;

// The base types of the message records (docs/design/fworker-wolverine-refactor.md 3). Messages are immutable records named by a stable identity, so a
// renamed class does not break a message already stored or in flight. The messages themselves arrive with the saga tasks (W2 and later).

/// <summary>
/// The stable wire name of a message type (the design's <c>[MessageIdentity]</c>). Contracts takes no package, so this is the project's own attribute;
/// when Wolverine arrives (W2.0) the host maps it to Wolverine's identity of the same name.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class MessageIdentityAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Any message of the gateway.</summary>
public interface IMessage { }

/// <summary>A request to do something: one handler, may be refused.</summary>
public interface ICommand : IMessage { }

/// <summary>A fact that happened: any number of handlers, never refused.</summary>
public interface IEvent : IMessage { }

/// <summary>A message about one work item. The item's qualified id is the saga id's first part, the correlation key and the Wolverine partition key (hook H1).</summary>
public interface IItemMessage : IMessage
{
    ItemId Item { get; }
}

/// <summary>Base of a command about one item.</summary>
public abstract record ItemCommand(ItemId Item) : IItemMessage, ICommand;

/// <summary>Base of an event about one item.</summary>
public abstract record ItemEvent(ItemId Item) : IItemMessage, IEvent;

public static class MessageIdentity
{
    /// <summary>The stable name of a message type: its <see cref="MessageIdentityAttribute"/>, or null when it has none.</summary>
    public static string? NameOf(Type messageType) =>
        (Attribute.GetCustomAttribute(messageType, typeof(MessageIdentityAttribute), false) as MessageIdentityAttribute)?.Name;
}
