using Source.Common.MaterialSystem;

namespace Source.StdShader.Gl46;

public class DecalModulate : BaseVSShader
{
	public static string HelpString = "Help for DecalModulate";
	public static ShaderFlags Flags = ShaderFlags.None;

	public override string? GetFallbackShader(IMaterialVar[] vars) => "DecalModulate_DX9";
}
