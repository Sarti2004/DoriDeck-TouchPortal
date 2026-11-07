using System.Globalization;
using DoriDeck.Services;
using ScoreInterface;
using ScoreInterface.Commands;
using ScoreInterface.Enums;
using Serilog;

namespace DoriDeck.Actions;

// Base actions class
internal abstract class DoriDeckActionBase(DoricoSession session, ILogger logger)
{
	// A button press shouldn't sit waiting on a Dorico that isn't answering; the connect itself carries on.
	private static readonly TimeSpan ConnectWaitLimit = TimeSpan.FromSeconds(2);

	protected DoricoSession Session { get; } = session;

	private ILogger? _logger;

	protected ILogger Logger => _logger ??= logger.ForContext(GetType());

	/// Runs one command (with parameters after '?'), optionally once in every flow.
	protected async Task RunCommandAsync(string commandName, bool applyToAllFlows, CancellationToken cancellationToken)
	{
		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped {Command}.", commandName);
			return;
		}

		await ForEachFlowAsync(
			dorico,
			applyToAllFlows,
			() => RunWithModeSwitchAsync(dorico, commandName, cancellationToken),
			cancellationToken);
	}

	// CGEventPost drops keystrokes silently without permission, so stop before leaving Dorico half-way.
	protected bool EnsureKeyboardAccess(IKeyboardService keyboard)
	{
		if (keyboard.RequestAccess())
		{
			return true;
		}

		Logger.Warning("Keystrokes need Accessibility permission for Touch Portal: System Settings > Privacy & Security > Accessibility. Then restart Touch Portal.");
		return false;
	}

	protected async Task ForEachFlowAsync(
		IScoreInterfaceRemote dorico,
		bool applyToAllFlows,
		Func<Task> run,
		CancellationToken cancellationToken,
		bool continueOnFlowError = false)
	{
		if (!applyToAllFlows || Session.FlowsCount <= 1)
		{
			await run();
			return;
		}

		for (var flowId = 0; flowId < Session.FlowsCount; flowId++)
		{
			try
			{
				await dorico.SendRequestAsync(
					new Command("Edit.GoToFlow", new CommandParameter("FlowID", flowId.ToString(CultureInfo.InvariantCulture))),
					cancellationToken);
				await Task.Delay(Session.FlowSwitchDelay, cancellationToken);
				await run();
			}
			catch (Exception ex) when (continueOnFlowError && ex is not OperationCanceledException)
			{
				Logger.Warning(ex, "Failed in flow {FlowId}; continuing with the next flow.", flowId);
			}
		}
	}

	protected static async Task<IScoreInterfaceRemote?> GetConnectedRemoteAsync(
		DoricoSession session,
		CancellationToken cancellationToken)
	{
		if (session.IsConnected)
		{
			return session.Remote;
		}

		try
		{
			// The connect attempt keeps running in the background if this times out; the session's gate dedupes it.
			return await session.EnsureConnectedAsync().WaitAsync(ConnectWaitLimit, cancellationToken)
				? session.Remote
				: null;
		}
		catch (TimeoutException)
		{
			return null;
		}
	}

	protected static (string ActionName, List<CommandParameter> Parameters) ParseCommand(string commandName)
	{
		var parts = commandName.Split('?', 2);
		var actionName = parts[0];
		var parameters = new List<CommandParameter>();

		if (parts.Length > 1)
		{
			foreach (var param in parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				var keyValue = param.Split('=', 2);
				if (keyValue.Length == 2)
				{
					parameters.Add(new CommandParameter(keyValue[0], keyValue[1]));
				}
			}
		}

		return (actionName, parameters);
	}

	// Dorico separates command parameters with commas, so one inside a value has to be escaped.
	internal static string EscapeCommas(string value) => value.Replace(",", "\\\\,");

	protected static WindowMode GetTargetMode(string actionName) => actionName switch
	{
		_ when actionName.StartsWith("Setup.", StringComparison.Ordinal) => WindowMode.kSetupMode,
		_ when actionName.StartsWith("Project.", StringComparison.Ordinal) => WindowMode.kSetupMode,
		_ when actionName.StartsWith("NoteInput.", StringComparison.Ordinal) => WindowMode.kWriteMode,
		_ when actionName.StartsWith("EventEdit.", StringComparison.Ordinal) => WindowMode.kWriteMode,
		_ when actionName.StartsWith("Page.", StringComparison.Ordinal) => WindowMode.kEngraveMode,
		_ when actionName.StartsWith("Print.", StringComparison.Ordinal) => WindowMode.kPrintMode,
		_ => WindowMode.Undefined,
	};

	protected static async Task RunWithModeSwitchAsync(
		IScoreInterfaceRemote dorico,
		string commandName,
		CancellationToken cancellationToken)
	{
		var (actionName, parameters) = ParseCommand(EscapeCommas(commandName));

		var originalStatus = await dorico.GetStatusAsync(cancellationToken).WaitAsync(cancellationToken);
		var originalMode = originalStatus?.WindowMode ?? WindowMode.Undefined;
		var targetMode = GetTargetMode(actionName);

		try
		{
			if (targetMode != WindowMode.Undefined && targetMode != originalMode)
			{
				await dorico.SendRequestAsync(
					new Command("Window.SwitchMode", new CommandParameter("WindowMode", targetMode.ToString())),
					cancellationToken);
			}

			await dorico.SendRequestAsync(new Command(actionName, parameters.ToArray()), cancellationToken);
		}
		finally
		{
			if (originalMode != WindowMode.Undefined && targetMode != WindowMode.Undefined && originalMode != targetMode)
			{
				await dorico.SendRequestAsync(
					new Command("Window.SwitchMode", new CommandParameter("WindowMode", originalMode.ToString())),
					cancellationToken);
			}
		}
	}

	protected static void EnsureHasSelection(DoricoSession session)
	{
		if (!session.HasScore)
		{
			throw new InvalidOperationException("Dorico does not have an active score.");
		}

		if (!session.HasSelection)
		{
			throw new InvalidOperationException("Select the first note before running this action.");
		}
	}
}
