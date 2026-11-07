using System.Threading.Channels;
using DoriDeck.Actions;
using DoriDeck.Services;
using DoriDeck.TouchPortal;
using Microsoft.Extensions.Hosting;
using ScoreInterface.Responses;
using Serilog;

namespace DoriDeck;

/// Starts and stops the Dorico session alongside the plugin, and feeds it the TP settings.
internal sealed class PluginIntegration : BackgroundService
{
	private static readonly TimeSpan ParagraphStylesTimeout = TimeSpan.FromSeconds(5);

	private readonly DoricoSession _session;
	private readonly ITouchPortalClient _client;
	private readonly ILogger _logger;
	private readonly Channel<IReadOnlyDictionary<string, string>> _settings =
		Channel.CreateUnbounded<IReadOnlyDictionary<string, string>>(new UnboundedChannelOptions { SingleReader = true });
	private readonly Lock _scriptListLock = new();
	private IReadOnlyList<string>? _scriptNames;
	private readonly SemaphoreSlim _paragraphStylesGate = new(1, 1);
	private IReadOnlyList<string> _paragraphStyles = [];
	private readonly Lock _commandListLock = new();
	private IReadOnlyList<CommandInfo>? _bundledCommands;
	private IReadOnlyList<string>? _commandChoices;

	public PluginIntegration(DoricoSession session, ITouchPortalClient client, ILogger logger)
	{
		_session = session;
		_client = client;
		_logger = logger.ForContext<PluginIntegration>();

		// In the constructor, so it is subscribed before any hosted service starts.
		_client.SettingsReceived += OnSettingsReceived;
		_session.ConnectCompleted += OnConnectCompleted;
		_session.ScoreContentChanged += OnScoreContentChanged;
	}

	private void OnSettingsReceived(IReadOnlyDictionary<string, string> values) => _settings.Writer.TryWrite(values);

	// Connecting can change the default scripts folder, once Dorico has reported its version.
	private void OnConnectCompleted() => _ = Task.Run(() =>
	{
		RefreshScriptList();
		RefreshCommandList();
	});

	// Off the Dorico receive thread, which also has to deliver the replies to these requests.
	private void OnScoreContentChanged() => _ = Task.Run(RefreshParagraphStylesAsync);

	private async Task RefreshParagraphStylesAsync()
	{
		await _paragraphStylesGate.WaitAsync();
		try
		{
			var styles = await _session.GetParagraphStylesAsync().WaitAsync(ParagraphStylesTimeout);

			// No styles means no open score; keep the last list so buttons can still be edited.
			if (styles.Count > 0 && !_paragraphStyles.SequenceEqual(styles))
			{
				_paragraphStyles = styles;
				_client.UpdateChoices(ChangeParagraphStyleAction.StyleData, styles);
			}
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to load the Dorico paragraph styles.");
		}
		finally
		{
			_paragraphStylesGate.Release();
		}
	}

	private void RefreshScriptList()
	{
		lock (_scriptListLock)
		{
			var scriptNames = ScriptCatalog.DiscoverScriptNames(_session.ScriptPath, _logger);
			if (_scriptNames is null || !_scriptNames.SequenceEqual(scriptNames))
			{
				_scriptNames = scriptNames;
				_client.UpdateChoices(RunScriptAction.ScriptNameData, scriptNames);
			}
		}
	}

	// The bundled list until Dorico has connected and reported the commands of the installed version.
	private void RefreshCommandList()
	{
		lock (_commandListLock)
		{
			var commands = _session.AvailableCommands.Count > 0 ? _session.AvailableCommands : BundledCommands();
			var choices = RunPredefinedCommandAction.BuildChoices(commands);
			if (_commandChoices is null || !_commandChoices.SequenceEqual(choices))
			{
				_commandChoices = choices;
				_client.UpdateChoices(RunPredefinedCommandAction.CommandData, choices);
			}
		}
	}

	private IReadOnlyList<CommandInfo> BundledCommands()
	{
		if (_bundledCommands is null)
		{
			try
			{
				_bundledCommands = RunPredefinedCommandAction.LoadBundledCommands(
					Path.Combine(AppContext.BaseDirectory, RunPredefinedCommandAction.BundledCommandsFile));
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "Failed to load the bundled Dorico command list.");
				_bundledCommands = [];
			}
		}

		return _bundledCommands;
	}

	// Queued so saves apply in order, and a first connect waiting on Dorico's approval can't hold them up.
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var initialized = false;

		await foreach (var values in _settings.Reader.ReadAllAsync(stoppingToken))
		{
			_session.ApplySettings(DoriDeckSettings.Parse(values, _logger));
			RefreshScriptList();
			RefreshCommandList();

			if (!initialized)
			{
				initialized = true;
				_ = InitializeSessionAsync();
			}
		}
	}

	private async Task InitializeSessionAsync()
	{
		try
		{
			await _session.InitializeAsync();
			_logger.Information("Initialized.");
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Failed to initialize the Dorico session.");
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		_client.SettingsReceived -= OnSettingsReceived;
		_session.ConnectCompleted -= OnConnectCompleted;
		_session.ScoreContentChanged -= OnScoreContentChanged;
		await base.StopAsync(cancellationToken);
		await _session.ShutdownAsync();
	}
}
