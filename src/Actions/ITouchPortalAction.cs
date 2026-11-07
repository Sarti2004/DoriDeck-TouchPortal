using DoriDeck.TouchPortal;

namespace DoriDeck.Actions;

/// Handler for one action declared in entry.tp; <see cref="Id"/> must match the action id there.
internal interface ITouchPortalAction
{
	string Id { get; }

	Task ExecuteAsync(ActionData data, CancellationToken cancellationToken);
}
