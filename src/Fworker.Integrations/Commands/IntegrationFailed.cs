using Fworker.Contracts.Commands;

namespace Fworker.Integrations.Commands;

/// <summary>
/// The message a command handler sends to the saga when a call was refused for good, or kept failing (docs/design/wolverine-factory.md 4.4): the saga parks the item
/// or retries by its policy. <see cref="CommandName"/> and <see cref="IdempotencyKey"/> identify the command; <see cref="Kind"/> says why it is not retried here.
/// </summary>
public sealed record IntegrationFailed(string CommandName, string IdempotencyKey, FailureKind Kind, string Error)
{
    public static IntegrationFailed From(IntegrationCommand command, CommandResult result) =>
        new(command.Name, command.IdempotencyKey, result.Failure, result.Error);
}
