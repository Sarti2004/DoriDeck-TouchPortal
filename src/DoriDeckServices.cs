using DoriDeck.Actions;
using DoriDeck.Services;
using DoriDeck.States;
using DoriDeck.TouchPortal;
using Lea;
using Microsoft.Extensions.DependencyInjection;
using ScoreInterface;
using ScoreInterface.Comms;

namespace DoriDeck;

internal static class DoriDeckServices
{
	public static IServiceCollection AddDoriDeck(this IServiceCollection services, TouchPortalOptions options)
	{
		services
			.AddSingleton<IEventAggregator, EventAggregator>()
			.AddSingleton<IClientWebSocketWrapper, ClientWebSocketWrapper>()
			.AddSingleton<IScoreInterfaceCommsContext, ScoreInterfaceCommsContext>()
			.AddSingleton<IScoreInterfaceRemote, ScoreInterfaceRemote>()
			.AddSingleton<IKeyboardService, KeyboardService>()
			.AddSingleton<IApplicationFocusService, ApplicationFocusService>()
			.AddSingleton<IClipboardService, ClipboardService>()
			.AddSingleton<ISessionTokenStore, FileSessionTokenStore>()
			.AddSingleton<DoricoSession>();

		services
			.AddSingleton<ITouchPortalAction, ConnectDoricoAction>()
			.AddSingleton<ITouchPortalAction, RunCommandAction>()
			.AddSingleton<ITouchPortalAction, RunPredefinedCommandAction>()
			.AddSingleton<ITouchPortalAction, ClearConnectionTokenAction>()
			.AddSingleton<ITouchPortalAction, OpenRecentFileAction>()
			.AddSingleton<ITouchPortalAction, OpenPreviousFileAction>()
			.AddSingleton<ITouchPortalAction, RunScriptAction>()
			.AddSingleton<ITouchPortalAction, CustomScriptAction>()
			.AddSingleton<ITouchPortalAction, RespellNoteAction>()
			.AddSingleton<ITouchPortalAction, PickupMeasureAction>()
			.AddSingleton<ITouchPortalAction, SectionHeaderAction>()
			.AddSingleton<ITouchPortalAction, ChangeParagraphStyleAction>()
			.AddSingleton<ITouchPortalAction, ChoirReductionAction>()
			.AddSingleton<ITouchPortalAction, InsertLyricsAction>();

		services
			.AddSingleton(options)
			.AddSingleton<ActionDispatcher>()
			.AddSingleton<TouchPortalClient>()
			.AddSingleton<ITouchPortalClient>(sp => sp.GetRequiredService<TouchPortalClient>())
			// Publisher first, so it is subscribed before TP can answer the pair request.
			.AddHostedService<DoricoStatePublisher>()
			.AddHostedService(sp => sp.GetRequiredService<TouchPortalClient>())
			.AddHostedService<PluginIntegration>();

		return services;
	}
}
