using System.Globalization;
using DoriDeck;
using DoriDeck.TouchPortal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// TP doesn't show plugin output, so the log file next to the executable is the main diagnostic.
Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
	.WriteTo.File(
		Path.Combine(AppContext.BaseDirectory, "logs", "dorideck-.log"),
		formatProvider: CultureInfo.InvariantCulture,
		rollingInterval: RollingInterval.Day,
		retainedFileCountLimit: 7)
	.CreateLogger();

try
{
	var builder = Host.CreateApplicationBuilder(args);
	builder.Services
		.AddSerilog()
		.AddSingleton(Log.Logger)
		.AddDoriDeck(CreateTouchPortalOptions());

	await builder.Build().RunAsync();
}
catch (Exception ex)
{
	Log.Fatal(ex, "DoriDeck stopped unexpectedly.");
}
finally
{
	await Log.CloseAndFlushAsync();
}

// DORIDECK_TP_PORT lets a fake TP run next to a real one during development.
static TouchPortalOptions CreateTouchPortalOptions() =>
	int.TryParse(Environment.GetEnvironmentVariable("DORIDECK_TP_PORT"), out var port)
		? new TouchPortalOptions { Port = port }
		: new TouchPortalOptions();
