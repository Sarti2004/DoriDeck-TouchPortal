using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Respells the selected notes to their enharmonic equivalent (e.g. C# to Db).
internal sealed class RespellNoteAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	private const string RespellCommand =
		"NoteInput.TransposeOrAddNotesToSelection?Definition=c#=db,db=c#,d#=eb,eb=d#,e#=f,f=e#," +
		"f#=gb,gb=f#,g#=ab,ab=g#,a#=bb,bb=a#,b#=c,c=b#,fb=e,e=fb,cb=b,b=cb";

	public string Id => "com.sarti2004.dorideck.action.respell_note";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken) =>
		RunCommandAsync(RespellCommand, applyToAllFlows: false, cancellationToken);
}
