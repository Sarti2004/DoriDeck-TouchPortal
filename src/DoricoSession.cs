using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ScoreInterface;
using ScoreInterface.Comms;
using ScoreInterface.Enums;
using ScoreInterface.Exceptions;
using ScoreInterface.Responses;
using DoriDeck.Services;
using Lea;
using Serilog;
using System.Text.Json.Nodes;

namespace DoriDeck;

/// Owns the connection to Dorico and the state actions need to read. Registered as a singleton so the integration and
/// every action share one instance instead of reaching through a static singleton.
public sealed class DoricoSession : IAsyncDisposable
{
	private const string ClientName = "DoriDeck";
	private static readonly TimeSpan StatusDebounceDelay = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan DoricoRequestTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan AutoConnectRetryInterval = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan AutoConnectWindow = TimeSpan.FromSeconds(90);

	public const int DefaultFlowSwitchDelay = 150;
	public const string DefaultDoricoVersion = "6";
	private const string ParagraphStyleCollection = "kParagraphStyleCollection";

	private readonly IScoreInterfaceRemote _remote;
	private readonly IEventAggregator _eventAggregator;
	private readonly ISessionTokenStore _tokenStore;
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _connectGate = new(1, 1);
	private readonly SemaphoreSlim _contextVariableGate = new(1, 1);
	private readonly SubscriptionToken _disconnectSubscription;
	private readonly SubscriptionToken _statusSubscription;
	private readonly SubscriptionToken _optionsChangedSubscription;
	private readonly SubscriptionToken _selectionChangedSubscription;
	private readonly object _statusDebounceLock = new();
	private readonly object _scoreInfoLock = new();
	private readonly ScoreOpenLogReader _scoreOpenLogReader;

	// Score info already read, by OCF session id (= openScoreID), so switching between open scores reads neither the
	// log nor the score again. A few KB per score opened in this Dorico run; cleared when Dorico restarts.
	private readonly Dictionary<int, LoadedScore> _loadedScores = new();
	private int? _loadedScoreId;
	private readonly CancellationTokenSource _shutdownCts = new();

	private CancellationTokenSource? _statusDebounceCts;
	private DoricoLaunchWatcher? _launchWatcher;
	private int _autoConnecting;
	private string _doricoVersion = DefaultDoricoVersion;
	private string _customScriptPath = string.Empty;
	private int _flowsCount;

	public DoricoSession(
		IScoreInterfaceRemote remote,
		IEventAggregator eventAggregator,
		ISessionTokenStore tokenStore,
		ILogger logger)
	{
		_remote = remote;
		_eventAggregator = eventAggregator;
		_tokenStore = tokenStore;
		_logger = logger.ForContext<DoricoSession>();
		_scoreOpenLogReader = new ScoreOpenLogReader(logger);

		_disconnectSubscription = _eventAggregator.Subscribe<DisconnectResponse>(OnDoricoDisconnected);
		_statusSubscription = _eventAggregator.Subscribe<StatusResponse>(OnDoricoStatusChanged);
		_optionsChangedSubscription = _eventAggregator.Subscribe<OptionsChangedResponse>(OnDoricoOptionsChanged);
		_selectionChangedSubscription = _eventAggregator.Subscribe<SelectionChangedResponse>(OnDoricoSelectionChanged);
		_remote.RawMessageReceived += OnDoricoRawMessage;
	}

	public bool IsConnected => _remote.IsConnected;

	public IScoreInterfaceRemote Remote => _remote;

	public int FlowsCount => _flowsCount;

	// Follows the version Dorico reports, unless the user chose a folder.
	public string ScriptPath => _customScriptPath.Length > 0 ? _customScriptPath : DefaultScriptPath();

	public string DoricoVersion { get; private set; } = DefaultDoricoVersion;

	// The version Dorico reported once connected, otherwise the setting.
	public string ActiveDoricoVersion => _doricoVersion;

	public int FlowSwitchDelay { get; private set; } = DefaultFlowSwitchDelay;

	public bool HasScore { get; private set; }

	public bool HasSelection { get; private set; }

	public int ActiveOpenScoreId { get; private set; } = -1;

	public string NoteInputMode { get; private set; } = string.Empty;

	public string Accidental { get; private set; } = string.Empty;

	public string WindowModeRaw { get; private set; } = string.Empty;

	public string WindowMode { get; private set; } = string.Empty;

	/// <summary>Dorico's note-input duration, e.g. "kCrotchet", or empty until a status reports one.</summary>
	public string Duration { get; private set; } = string.Empty;

	public int RhythmDots { get; private set; }

	public event Action<int>? RhythmDotsChanged;

	public bool NoteInputActive { get; private set; }

	/// <summary>Flag reported from Dorico</summary>
	public event Action<bool>? NoteInputActiveChanged;

	public bool RestMode { get; private set; }

	public bool InPlayback { get; private set; }

	public bool IsRecording { get; private set; }

	/// <summary>Dorico's rhythmic grid resolution, e.g. "kCrotchet", or empty until a status reports one.</summary>
	public string RhythmicGridResolution { get; private set; } = string.Empty;

	/// <summary>Flag reported from Dorico</summary>
	public event Action<string>? DurationChanged;

	/// <summary>Raised when Dorico sends a confirmed disconnect notification.</summary>
	public event Action? Disconnected;

	/// Raised with the new value whenever the Dorico connection opens or closes.
	public event Action<bool>? ConnectionChanged;

	/// Raised once a new connection has also learned Dorico's version and commands.
	public event Action? ConnectCompleted;

	/// Raised when the active score, or something in it such as its styles, may have changed.
	public event Action? ScoreContentChanged;

	public string CurrentFlowId { get; private set; } = string.Empty;

	public string CurrentFlowName { get; private set; } = string.Empty;

	public IReadOnlyList<CommandInfo> AvailableCommands { get; private set; } = [];

	public string ScoreInfoPath { get; private set; } = string.Empty;

	public string ScoreInfoJson { get; private set; } = string.Empty;

	public event Action<string>? ScoreInfoChanged;

	public ScoreSummary? ScoreSummary { get; private set; }

	public byte[]? ScorePreviewPng { get; private set; }

	public event Action? DisplayStateChanged;

	///list of recent files from FileList.xml</summary>
	public IReadOnlyList<string> RecentFiles { get; internal set; } = [];

	public string RecentFileName(int index)
	{
		var recentFiles = RecentFiles;
		return index < recentFiles.Count ? Path.GetFileNameWithoutExtension(recentFiles[index]) : string.Empty;
	}

	public string DoricoRecentFileListPath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"Steinberg",
			$"Dorico {_doricoVersion}",
			"recentFileList.xml");

	public string DoricoApplicationLogPath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"Steinberg",
			$"Dorico {_doricoVersion}",
			"application.log");

	public async Task InitializeAsync()
	{
		await EnsureConnectedAsync();
		LoadRecentFiles();
	}

	public void ApplySettings(DoriDeckSettings settings)
	{
		var versionChanged = settings.DoricoVersion != DoricoVersion;

		DoricoVersion = settings.DoricoVersion;
		_customScriptPath = settings.ScriptPath;
		FlowSwitchDelay = settings.FlowSwitchDelay;

		// A connected Dorico has reported its real version, so the setting only applies while disconnected.
		if (!IsConnected)
		{
			_doricoVersion = DoricoVersion;
		}

		if (_launchWatcher is null || versionChanged)
		{
			_launchWatcher?.Dispose();
			_launchWatcher = new DoricoLaunchWatcher(DoricoApplicationLogPath, _logger);
			_launchWatcher.Launched += OnDoricoLaunched;
			LoadRecentFiles();
		}

		_logger.Information("Applied settings: {@Settings}", settings);
	}

	private void OnDoricoLaunched()
	{
		ForgetLoadedScores();

		if (IsConnected || Interlocked.Exchange(ref _autoConnecting, 1) == 1)
		{
			return;
		}

		_ = AutoConnectAsync();
	}

	/// Dorico takes a while after launch to open its Remote Control port, so retry for a bounded window.
	private async Task AutoConnectAsync()
	{
		try
		{
			if (string.IsNullOrEmpty(_tokenStore.Read()))
			{
				_logger.Information("Dorico started, but DoriDeck has no saved session; can not connect automatically.");
				return;
			}

			var started = Stopwatch.StartNew();
			while (!IsConnected && started.Elapsed < AutoConnectWindow)
			{
				await Task.Delay(AutoConnectRetryInterval, _shutdownCts.Token);

				// Cheap probe first so attempts made while Dorico is still starting don't each log a failure.
				if (await IsRemoteControlListeningAsync(_shutdownCts.Token) && await EnsureConnectedAsync())
				{
					_logger.Information("Connected to Dorico automatically after it started.");
					return;
				}
			}
		}
		catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to connect to Dorico automatically.");
		}
		finally
		{
			Interlocked.Exchange(ref _autoConnecting, 0);
		}
	}

	private static async Task<bool> IsRemoteControlListeningAsync(CancellationToken cancellationToken)
	{
		try
		{
			var port = new Uri(new ConnectionArguments().Address).Port;
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
			return true;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	private sealed record LoadedScore(string Path, string Json, ScoreSummary Summary);

	/// Shows the info of the active score. Only a score not seen before needs the log, and only the lines Dorico
	/// wrote since the last read; a score already shown, or switched back to, costs a dictionary lookup.
	private void RefreshScoreInfo()
	{
		// Refreshes run on the thread pool; serialize them so the last one to finish reflects the latest active id.
		lock (_scoreInfoLock)
		{
			if (!HasScore)
			{
				return;
			}

			var scoreId = ActiveOpenScoreId;
			if (_loadedScoreId == scoreId)
			{
				return;
			}

			if (!_loadedScores.TryGetValue(scoreId, out var score))
			{
				var scorePath = _scoreOpenLogReader.GetScorePath(scoreId);
				if (scorePath is null)
				{
					_scoreOpenLogReader.ReadNewlyOpenedScores(DoricoApplicationLogPath);
					scorePath = _scoreOpenLogReader.GetScorePath(scoreId);
				}

				// No path means a score that was never saved, so there is nothing to read. Clear the previous score's
				// info rather than showing it for this one, and remember the id so the log isn't searched again.
				score = scorePath is null ? null : LoadScore(scorePath);
				if (score is not null)
				{
					_loadedScores[scoreId] = score;
				}
			}

			_loadedScoreId = scoreId;
			ShowScore(score);
		}
	}

	private LoadedScore? LoadScore(string scorePath)
	{
		try
		{
			var json = ScoreInfoReader.ReadJson(scorePath);
			//_logger.Information("Json.{json}", json);
			return new LoadedScore(scorePath, json, ScoreSummary.FromJson(json, scorePath));
		}
		catch (Exception ex)
		{
			_logger.Information(ex, "Failed to read score info from {Path}.", scorePath);
			return null;
		}
	}

	private void ShowScore(LoadedScore? score)
	{
		ScoreInfoPath = score?.Path ?? string.Empty;
		ScoreInfoJson = score?.Json ?? string.Empty;
		ScoreSummary = score?.Summary;

		// Not cached: at a few hundred KB each, previews of every score opened would add up. One zip entry per switch.
		ScorePreviewPng = score is null ? null : ReadPreview(score.Path);

		ScoreInfoChanged?.Invoke(ScoreInfoJson);
		DisplayStateChanged?.Invoke();
	}

	/// Session ids restart from 0 in every Dorico run, so cached info must not outlive it.
	private void ForgetLoadedScores()
	{
		lock (_scoreInfoLock)
		{
			_scoreOpenLogReader.Reset();
			_loadedScores.Clear();
			_loadedScoreId = null;
		}
	}

	private byte[]? ReadPreview(string scorePath)
	{
		try
		{
			return ScoreInfoReader.ReadPreviewPng(scorePath);
		}
		catch (Exception ex)
		{
			_logger.Information(ex, "Failed to read the score preview from {Path}.", scorePath);
			return null;
		}
	}

	public Task ShutdownAsync()
	{
		_shutdownCts.Cancel();

		lock (_statusDebounceLock)
		{
			_statusDebounceCts?.Cancel();
			_statusDebounceCts?.Dispose();
			_statusDebounceCts = null;
		}

		_launchWatcher?.Dispose();
		_launchWatcher = null;

		return _remote.IsConnected ? _remote.DisconnectAsync() : Task.CompletedTask;
	}

	public async Task<bool> EnsureConnectedAsync()
	{
		if (IsConnected)
		{
			return true;
		}

		if (!await _connectGate.WaitAsync(0))
		{
			await _connectGate.WaitAsync();
			try
			{
				return IsConnected;
			}
			finally
			{
				_connectGate.Release();
			}
		}

		try
		{
			if (IsConnected)
			{
				return true;
			}

			await ConnectAsync();
		}
		finally
		{
			_connectGate.Release();
		}

		return IsConnected;
	}

	public async Task ConnectAsync()
	{
		if (_remote.IsConnected)
		{
			return;
		}

		var savedToken = _tokenStore.Read();
		var connectionArgs = string.IsNullOrEmpty(savedToken)
			? new ConnectionArguments()
			: new ConnectionArguments(savedToken);

		try
		{
			await _remote.ConnectAsync(ClientName, connectionArgs);
			await OnConnectedAsync();
		}
		catch (Exception ex) when (ex.Message.Contains("kClientRejected_ConnectTokenRejected"))
		{
			_logger.Warning("Dorico session token rejected. Reconnecting without a cached session.");
			_tokenStore.Clear();

			try
			{
				await _remote.ConnectAsync(ClientName, new ConnectionArguments());
				await OnConnectedAsync();
			}
			catch (Exception retryEx)
			{
				_logger.Warning(retryEx, "Failed to connect to Dorico on retry.");
			}
		}
		catch (ScoreInterfaceException ex) when (ex.InnerException is System.Net.WebSockets.WebSocketException)
		{
			// Dorico simply isn't running; a stack trace adds nothing.
			_logger.Warning("Failed to connect to Dorico: {Reason}", ex.Message);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to connect to Dorico.");
		}
	}

	public Task RefreshContextAsync(CancellationToken cancellationToken = default) =>
		RefreshContextAsync(status: null, cancellationToken);

	/// Names of the paragraph styles in the active score, or an empty list if no score is open or Dorico
	/// doesn't expose them.
	public async Task<IReadOnlyList<string>> GetParagraphStylesAsync(CancellationToken cancellationToken = default)
	{
		LibraryEntitiesListResponse? paragraphStyles;
		try
		{
			var libraryCollections = await _remote.GetLibraryCollectionsAsync(cancellationToken);
			if (libraryCollections?.Collections.Contains(ParagraphStyleCollection) != true)
			{
				return [];
			}

			paragraphStyles = await _remote.GetLibraryEntitiesAsync(ParagraphStyleCollection, cancellationToken);
		}
		catch (ScoreInterfaceException<Response> ex) when (ex.Response.Detail == "kNoScore")
		{
			// Library collections belong to a score; Dorico refuses the request while only the Hub is open.
			return [];
		}

		return (paragraphStyles?.LibraryEntities ?? [])
			.Select(entity => entity.Name)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private async Task OnConnectedAsync()
	{
		if (!_remote.IsConnected)
		{
			return;
		}

		if (!string.IsNullOrEmpty(_remote.SessionToken))
		{
			_tokenStore.Save(_remote.SessionToken);
		}

		_logger.Information("Connected to Dorico.");
		ConnectionChanged?.Invoke(true);
		await RefreshContextAsync();

		// Picks up a score opened before we connected, e.g. Dorico launched by double-clicking a .dorico file.
		_ = Task.Run(RefreshScoreInfo);

		var appInfo = await _remote.GetAppInfoAsync();
		if (appInfo?.Number is { Length: > 0 } versionNumber)
		{
			var majorVersion = versionNumber.Split('.')[0];
			if (!string.IsNullOrEmpty(majorVersion) && majorVersion != _doricoVersion)
			{
				// The setting named another version, so the recent list came from the wrong folder.
				_doricoVersion = majorVersion;
				LoadRecentFiles();
			}
		}

		try
		{
			AvailableCommands = (await _remote.GetCommandsAsync())
				.Where(c => !string.IsNullOrEmpty(c.Name))
				.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Failed to fetch the Dorico command list.");
		}

		ConnectCompleted?.Invoke();
	}

	private void OnDoricoDisconnected(DisconnectResponse response)
	{
		_logger.Information("Dorico disconnected.");
		ForgetLoadedScores();
		ResetDisconnectedState();
		Disconnected?.Invoke();
		ConnectionChanged?.Invoke(false);
	}

	// Debug: logs every status message exactly as Dorico sent it
	private void OnDoricoRawMessage(string json)
	{
		if (json.Contains("\"status\"", StringComparison.OrdinalIgnoreCase))
		{
			//_logger.Information("Dorico raw status: {Json}", json);
		}
		//_logger.Information("Dorico raw status: {Json}", json);
	}

	private void OnDoricoStatusChanged(StatusResponse status)
	{
		ApplyStatus(status);

		CancellationToken cancellationToken;
		lock (_statusDebounceLock)
		{
			_statusDebounceCts?.Cancel();
			_statusDebounceCts?.Dispose();
			_statusDebounceCts = new CancellationTokenSource();
			cancellationToken = _statusDebounceCts.Token;
		}

		_ = RefreshFlowsDebouncedAsync(status, cancellationToken);
	}

	private void OnDoricoOptionsChanged(OptionsChangedResponse response)
	{
		LoadRecentFiles();

		// Opening a score updates the recent file list, so this is also our cue to look for it in the log.
		_ = Task.Run(RefreshScoreInfo);
		ScoreContentChanged?.Invoke();
	}

	/// Dorico sends selectionchanged when the user switches to another open score, before any status reports it.
	private void OnDoricoSelectionChanged(SelectionChangedResponse response)
	{
		if (response.OpenScoreId == ActiveOpenScoreId)
		{
			return;
		}

		ActiveOpenScoreId = response.OpenScoreId;
		DisplayStateChanged?.Invoke();
		_ = Task.Run(RefreshScoreInfo);
		ScoreContentChanged?.Invoke();
	}

	private void LoadRecentFiles()
	{
		var path = DoricoRecentFileListPath;

		try
		{
			if (!File.Exists(path))
			{
				RecentFiles = [];
				DisplayStateChanged?.Invoke();
				return;
			}

			RecentFiles = System.Xml.Linq.XDocument.Load(path)
				.Descendants("file")
				.Select(file => file.Value.Trim())
				.Where(file => file.Length > 0)
				.ToList();
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to read Dorico recent file list {Path}.", path);
		}

		DisplayStateChanged?.Invoke();
	}

	private async Task RefreshFlowsDebouncedAsync(StatusResponse status, CancellationToken cancellationToken)
	{
		//_logger.Information("Dorico status: {@Status}", status);
		try
		{
			await Task.Delay(StatusDebounceDelay, cancellationToken);
			await RefreshContextAsync(status, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A newer status event restarted the debounce period.
		}
	}

	private async Task RefreshContextAsync(StatusResponse? status, CancellationToken cancellationToken)
	{
		if (!_remote.IsConnected)
		{
			ResetDisconnectedState();
			return;
		}

		if (!await _contextVariableGate.WaitAsync(0, cancellationToken))
		{
			return;
		}

		try
		{
			if (status == null)
			{
				try
				{
					status = await _remote.GetStatusAsync(cancellationToken).WaitAsync(DoricoRequestTimeout, cancellationToken);
					//_logger.Information("Dorico status2: {@Status}", status);
				}
				catch (TimeoutException)
				{
					// Dorico didn't answer; carry on with the values we already have.
				}

				if (status != null)
				{
					ApplyStatus(status);
				}
			}

			if (!HasScore)
			{
				SetFlowContext(string.Empty, string.Empty, 0);
				return;
			}

			try
			{
				var flowsResponse = await _remote.GetFlowsAsync(cancellationToken).WaitAsync(DoricoRequestTimeout, cancellationToken);
				var flows = flowsResponse?.Flows?.ToList() ?? [];

				var activeWindowTitle = ActiveWindowReader.GetActiveDoricoWindowTitle();
				var currentFlow = ResolveCurrentFlow(flows, activeWindowTitle);

				SetFlowContext(
					currentFlow?.FlowID.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
					currentFlow?.FlowName ?? string.Empty,
					flows.Count);
			}
			catch (TimeoutException)
			{
				_logger.Warning("Timed out while refreshing the active Dorico flow.");
			}
		}
		finally
		{
			_contextVariableGate.Release();
		}
	}

	private void SetFlowContext(string flowId, string flowName, int flowsCount)
	{
		if (flowId == CurrentFlowId && flowName == CurrentFlowName && flowsCount == _flowsCount)
		{
			return;
		}

		CurrentFlowId = flowId;
		CurrentFlowName = flowName;
		_flowsCount = flowsCount;
		DisplayStateChanged?.Invoke();
	}

	private static Flow? ResolveCurrentFlow(IReadOnlyList<Flow> flows, string? activeWindowTitle)
	{
		if (flows.Count == 0)
		{
			return null;
		}

		if (flows.Count == 1)
		{
			return flows[0];
		}

		if (string.IsNullOrWhiteSpace(activeWindowTitle))
		{
			return null;
		}

		var expectedFlowTitle = activeWindowTitle.Split(" in ")[0].Trim();

		return flows.FirstOrDefault(flow =>
			flow.FlowName.StartsWith(expectedFlowTitle, StringComparison.OrdinalIgnoreCase));
	}

	// A status only carries some fields, so anything it doesn't mention keeps its current value.
	private void ApplyStatus(StatusResponse status)
	{
		var previousHasScore = HasScore;
		var previousScoreId = ActiveOpenScoreId;

		HasScore = status.HasScore ?? HasScore;
		HasSelection = status.HasSelection ?? HasSelection;
		ActiveOpenScoreId = status.ActiveOpenScoreID ?? ActiveOpenScoreId;

		// Before the fields below, so a value in this same message still wins.
		if (previousHasScore && !HasScore)
		{
			ResetChangeOnlyFields();
		}

		if (HasScore && (!previousHasScore || ActiveOpenScoreId != previousScoreId))
		{
			_ = Task.Run(RefreshScoreInfo);
			ScoreContentChanged?.Invoke();
		}

		if (status.NoteInputMode is { } noteInputMode)
		{
			NoteInputMode = FriendlyDoricoMode(noteInputMode.ToString());
		}

		if (status.Accidental is { } accidental)
		{
			Accidental = accidental.ToString();
		}

		if (status.WindowMode is { } windowMode)
		{
			WindowModeRaw = windowMode.ToString();
			WindowMode = FriendlyDoricoMode(WindowModeRaw);
		}

		InPlayback = status.InPlayback ?? InPlayback;
		IsRecording = status.IsRecording ?? IsRecording;
		RestMode = status.RestMode ?? RestMode;

		if (!string.IsNullOrEmpty(status.RhythmicGridResolutionValue))
		{
			RhythmicGridResolution = status.RhythmicGridResolutionValue;
		}

		if (status.RhythmDots.HasValue)
		{
			SetRhythmDots(status.RhythmDots.Value);
		}

		if (status.NoteInputActive.HasValue)
		{
			SetNoteInputActive(status.NoteInputActive.Value);
		}

		if (!string.IsNullOrEmpty(status.Duration) &&
			!string.Equals(status.Duration, Duration, StringComparison.OrdinalIgnoreCase))
		{
			Duration = status.Duration;
			DurationChanged?.Invoke(Duration);
		}

		// Raised last so listeners see the whole status; the state publisher drops unchanged values.
		DisplayStateChanged?.Invoke();
	}

	private void SetRhythmDots(int active)
	{
		if (active != RhythmDots)
		{
			RhythmDots = active;
			RhythmDotsChanged?.Invoke(active);
		}
	}

	private void SetNoteInputActive(bool active)
	{
		if (active != NoteInputActive)
		{
			NoteInputActive = active;
			NoteInputActiveChanged?.Invoke(active);
		}
	}

	// Dorico reports these only when they change, so they would otherwise outlive the score or connection.
	private void ResetChangeOnlyFields()
	{
		RhythmicGridResolution = string.Empty;
		RestMode = false;
		SetRhythmDots(0);
		SetNoteInputActive(false);

		if (Duration.Length > 0)
		{
			Duration = string.Empty;
			DurationChanged?.Invoke(Duration);
		}
	}

	private void ResetDisconnectedState()
	{
		HasScore = false;
		HasSelection = false;
		ActiveOpenScoreId = -1;
		NoteInputMode = string.Empty;
		Accidental = string.Empty;
		WindowMode = string.Empty;
		WindowModeRaw = string.Empty;
		ResetChangeOnlyFields();
		InPlayback = false;
		IsRecording = false;
		CurrentFlowId = string.Empty;
		CurrentFlowName = string.Empty;
		_flowsCount = 0;
		DisplayStateChanged?.Invoke();
	}

	private static string FriendlyDoricoMode(string rawMode) =>
		rawMode.Trim().TrimStart('k').Replace("Mode", string.Empty);

	private string DefaultScriptPath() =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"Steinberg",
			$"Dorico {_doricoVersion}",
			"Script Plug-ins") + Path.DirectorySeparatorChar;

	public void ClearSessionToken() => _tokenStore.Clear();

	public async ValueTask DisposeAsync()
	{
		_eventAggregator.Unsubscribe<DisconnectResponse>(_disconnectSubscription);
		_eventAggregator.Unsubscribe<StatusResponse>(_statusSubscription);
		_eventAggregator.Unsubscribe<OptionsChangedResponse>(_optionsChangedSubscription);
		_eventAggregator.Unsubscribe<SelectionChangedResponse>(_selectionChangedSubscription);
		_remote.RawMessageReceived -= OnDoricoRawMessage;

		lock (_statusDebounceLock)
		{
			_statusDebounceCts?.Dispose();
		}

		_shutdownCts.Cancel();
		_shutdownCts.Dispose();
		_launchWatcher?.Dispose();
		_connectGate.Dispose();
		_contextVariableGate.Dispose();

		if (_remote.IsConnected)
		{
			await _remote.DisconnectAsync();
		}
	}
}
