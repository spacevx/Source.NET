using Source.Common.Commands;

namespace Game.Client.HUD;

public static class CloseCaptionConVars
{
	public static readonly ConVar cc_subtitles = new("cc_subtitles", "0", FCvar.Archive, "If set, don't show sound effect captions, just voice overs (i.e., won't help hearing impaired players).");
}
