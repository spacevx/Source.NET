#if !SWDS
using Source.Common.Commands;

namespace Source.Engine;

public static class ColorCorrectionPanelConVars
{
	public static readonly ConVar mat_colorcorrection = new("mat_colorcorrection", "0", FCvar.Archive);
}
#endif
