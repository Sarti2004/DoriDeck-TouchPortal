using DoriDeck.Actions;
using Serilog;

namespace DoriDeck.TouchPortal;

/// Routes TP actions to their handlers. Actions are fire-and-forget: failures are only logged.
internal sealed class ActionDispatcher(IEnumerable<ITouchPortalAction> actions, ILogger logger)
{
	private readonly ILogger _logger = logger.ForContext<ActionDispatcher>();
	private readonly Dictionary<string, ITouchPortalAction> _actions = actions.ToDictionary(action => action.Id, StringComparer.Ordinal);

	public IReadOnlyCollection<string> ActionIds => _actions.Keys;

	public Task DispatchAsync(ActionMessage message, CancellationToken cancellationToken)
	{
		if (!_actions.TryGetValue(message.ActionId, out var action))
		{
			_logger.Warning("No handler for action {ActionId}.", message.ActionId);
			return Task.CompletedTask;
		}

		// Off the read loop, so a slow Dorico call never stalls incoming TP messages.
		return Task.Run(async () =>
		{
			try
			{
				switch (message.Type, action)
				{
					case (MessageTypes.Down, ITouchPortalHoldAction hold):
						await hold.DownAsync(message.Data, cancellationToken);
						break;
					case (MessageTypes.Up, ITouchPortalHoldAction hold):
						await hold.UpAsync(message.Data, cancellationToken);
						break;
					case (MessageTypes.Action, _):
						await action.ExecuteAsync(message.Data, cancellationToken);
						break;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "Action {ActionId} failed.", message.ActionId);
			}
		}, CancellationToken.None);
	}
}
