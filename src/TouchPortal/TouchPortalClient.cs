using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DoriDeck.TouchPortal;

internal interface ITouchPortalClient
{
	/// Raised when TP answers the pair request; anything sent before that is queued.
	event Action? Paired;

	/// Raised with all current settings: on pairing, and whenever the user saves them.
	event Action<IReadOnlyDictionary<string, string>>? SettingsReceived;

	void UpdateState(string id, string value);

	void UpdateChoices(string id, IReadOnlyList<string> values);
}

internal sealed record TouchPortalOptions
{
	public const string DefaultPluginId = "com.sarti2004.dorideck";

	public string PluginId { get; init; } = DefaultPluginId;

	public string Host { get; init; } = "127.0.0.1";

	public int Port { get; init; } = 12136;

	public int ConnectAttempts { get; init; } = 10;

	public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// Owns the socket to Touch Portal. The plugin lives only as long as this connection does.
internal sealed class TouchPortalClient(
	TouchPortalOptions options,
	ActionDispatcher dispatcher,
	IHostApplicationLifetime lifetime,
	ILogger logger) : BackgroundService, ITouchPortalClient
{
	private readonly ILogger _logger = logger.ForContext<TouchPortalClient>();
	private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

	public event Action? Paired;

	public event Action<IReadOnlyDictionary<string, string>>? SettingsReceived;

	public void UpdateState(string id, string value) => Send(new StateUpdateMessage(id, value));

	public void UpdateChoices(string id, IReadOnlyList<string> values) => Send(new ChoiceUpdateMessage(id, values));

	private void Send<T>(T message) => _outgoing.Writer.TryWrite(MessageSerializer.Serialize(message));

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			using var tcp = await ConnectAsync(stoppingToken);
			if (tcp is null)
			{
				_logger.Error("Could not reach Touch Portal at {Host}:{Port}.", options.Host, options.Port);
				return;
			}

			await using var stream = tcp.GetStream();
			await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
			using var reader = new StreamReader(stream, Encoding.UTF8);

			// Pair goes out first, directly; everything else waits in the queue behind it.
			await writer.WriteLineAsync(MessageSerializer.Serialize(new PairMessage(options.PluginId)).AsMemory(), stoppingToken);

			var finished = await Task.WhenAny(ReadLoopAsync(reader, stoppingToken), WriteLoopAsync(writer, stoppingToken));
			await finished;
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		catch (Exception ex) when (ex is IOException or SocketException)
		{
			_logger.Warning(ex, "Lost the connection to Touch Portal.");
		}
		finally
		{
			lifetime.StopApplication();
		}
	}

	private async Task<TcpClient?> ConnectAsync(CancellationToken cancellationToken)
	{
		for (var attempt = 1; attempt <= options.ConnectAttempts; attempt++)
		{
			var tcp = new TcpClient { NoDelay = true };
			try
			{
				await tcp.ConnectAsync(options.Host, options.Port, cancellationToken);
				return tcp;
			}
			catch (SocketException)
			{
				tcp.Dispose();
				await Task.Delay(options.ConnectRetryDelay, cancellationToken);
			}
		}

		return null;
	}

	private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		while (await reader.ReadLineAsync(cancellationToken) is { } line)
		{
			if (line.Length > 0 && !HandleMessage(line, cancellationToken))
			{
				return;
			}
		}

		_logger.Information("Touch Portal closed the connection.");
	}

	private async Task WriteLoopAsync(StreamWriter writer, CancellationToken cancellationToken)
	{
		await foreach (var line in _outgoing.Reader.ReadAllAsync(cancellationToken))
		{
			await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
		}
	}

	/// Returns false when TP asks the plugin to close.
	private bool HandleMessage(string line, CancellationToken cancellationToken)
	{
		try
		{
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

			switch (type)
			{
				case MessageTypes.Info:
					_logger.Information(
						"Paired with Touch Portal {Version}.",
						root.TryGetProperty("tpVersionString", out var version) ? version.GetString() : "?");
					SettingsReceived?.Invoke(MessageSerializer.ParseSettings(root, "settings"));
					Paired?.Invoke();
					break;
				case MessageTypes.Settings:
					SettingsReceived?.Invoke(MessageSerializer.ParseSettings(root, "values"));
					break;
				case MessageTypes.Action:
				case MessageTypes.Down:
				case MessageTypes.Up:
					_ = dispatcher.DispatchAsync(MessageSerializer.ParseAction(root), cancellationToken);
					break;
				case MessageTypes.ClosePlugin:
					_logger.Information("Touch Portal asked the plugin to close.");
					return false;
				default:
					_logger.Debug("Ignoring Touch Portal message {Type}.", type);
					break;
			}
		}
		catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			_logger.Warning(ex, "Malformed message from Touch Portal: {Line}", line);
		}

		return true;
	}
}
