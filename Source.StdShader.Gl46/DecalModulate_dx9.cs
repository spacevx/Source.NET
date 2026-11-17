using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

public class DecalModulate_dx9 : BaseVSShader
{
	public static string HelpString = "Help for DecalModulate_dx9";
	public static ShaderFlags Flags = ShaderFlags.None;

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		SetFlags(vars, MaterialVarFlags.NoDebugOverride);

		if (HardwareConfig.HasFastVertexTextures()) {
			SetFlags2(vars, MaterialVarFlags2.UsesVertexID);
			SetFlags2(vars, MaterialVarFlags2.SupportsHardwareSkinning);
		}
	}

	public override string? GetFallbackShader(IMaterialVar[] vars) {
		if (HardwareConfig.GetDXSupportLevel() < 90)
			return "DecalModulate_DX6";

		return null;
	}

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		LoadTexture((int)ShaderMaterialVars.BaseTexture);
	}

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		if (ShaderShadow != null) {
			ShaderShadow.EnableAlphaTest(true);
			ShaderShadow.AlphaFunc(ShaderAlphaFunc.Greater, 0.0f);
			ShaderShadow.EnableDepthWrites(false);
			ShaderShadow.EnablePolyOffset(PolygonOffsetMode.Decal);
			ShaderShadow.EnableTexture(Sampler.Sampler0, true);

			ShaderShadow.EnableAlphaWrites(false);

			ShaderShadow.EnableSRGBRead(Sampler.Sampler0, false);
			ShaderShadow.EnableSRGBWrite(false);

			ShaderShadow.EnableBlending(true);
			ShaderShadow.BlendFunc(ShaderBlendFactor.DstColor, ShaderBlendFactor.SrcColor);
			ShaderShadow.DisableFogGammaCorrection(true);
			FogToGrey();

			if (!HardwareConfig.HasFastVertexTextures()) {
				bool useStaticControlFlow = HardwareConfig.SupportsStaticControlFlow();

				StaticShaderIndex vshIndex = new(ShaderShadow, ShaderType.Vertex, "decalmodulate");
				vshIndex.Set("VERTEXCOLOR", false);
				vshIndex.Set("CUBEMAP", false);
				vshIndex.Set("HALFLAMBERT", false);
				vshIndex.Set("FLASHLIGHT", false);
				vshIndex.Set("SEAMLESS_BASE", false);
				vshIndex.Set("SEAMLESS_DETAIL", false);
				vshIndex.Set("SEPARATE_DETAIL_UVS", false);
				vshIndex.Set("USE_STATIC_CONTROL_FLOW", useStaticControlFlow);
				vshIndex.Set("DONT_GAMMA_CONVERT_VERTEX_COLOR", 0);
				ShaderShadow.SetVertexShader("decalmodulate", vshIndex.GetIndex());

				StaticShaderIndex pshIndex = new(ShaderShadow, ShaderType.Pixel, "decalmodulate");
				pshIndex.Set("CONVERT_TO_SRGB", HardwareConfig.NeedsShaderSRGBConversion());
				ShaderShadow.SetPixelShader("decalmodulate", pshIndex.GetIndex());
			}
			else {
				StaticShaderIndex vshIndex = new(ShaderShadow, ShaderType.Vertex, "decalmodulate");
				vshIndex.Set("VERTEXCOLOR", false);
				vshIndex.Set("CUBEMAP", false);
				vshIndex.Set("HALFLAMBERT", false);
				vshIndex.Set("FLASHLIGHT", false);
				vshIndex.Set("SEAMLESS_BASE", false);
				vshIndex.Set("SEAMLESS_DETAIL", false);
				vshIndex.Set("SEPARATE_DETAIL_UVS", false);
				vshIndex.Set("DECAL", true);
				vshIndex.Set("DONT_GAMMA_CONVERT_VERTEX_COLOR", 0);
				ShaderShadow.SetVertexShader("decalmodulate", vshIndex.GetIndex());

				StaticShaderIndex pshIndex = new(ShaderShadow, ShaderType.Pixel, "decalmodulate");
				pshIndex.Set("CONVERT_TO_SRGB", HardwareConfig.NeedsShaderSRGBConversion());
				ShaderShadow.SetPixelShader("decalmodulate", pshIndex.GetIndex());
			}

			// todo: VERTEX_FORMAT_COMPRESSED
			VertexFormat flags = VertexFormat.Position;
			flags |= HardwareConfig.HasFastVertexTextures() ? VertexFormat.Normal : 0;

			Span<int> texCoordDim = [2, 0, 3];
			int texCoordCount = 1;
			int userDataSize = 0;

			if (HardwareConfig.HasFastVertexTextures())
				texCoordCount = 3;

			ShaderShadow.VertexShaderVertexFormat(flags, texCoordCount, texCoordDim[..texCoordCount], userDataSize);
		}
		else if (ShaderAPI != null) {
			if (ShaderAPI.InFlashlightMode() && r_flashlight_version2.GetInt() == 0) {
				Draw(false);
				return;
			}

			BindTexture(Sampler.Sampler0, (int)ShaderMaterialVars.BaseTexture, (int)ShaderMaterialVars.Frame);

			Span<float> transformation = [1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f];
			ShaderAPI.SetVertexShaderConstant((int)VertexShaderConst.ShaderSpecificConst0, transformation, 2);

			MaterialFogMode fogType = ShaderAPI.GetSceneFogMode();
			int fogIndex = (fogType == MaterialFogMode.LinearBelowFogZ) ? 1 : 0;

			ShaderAPI.SetPixelShaderFogParams((int)PixelShaderConst.FogParams);

			Span<float> eyePos_SpecExponent = [0, 0, 0, 0];
			ShaderAPI.GetWorldSpaceCameraPosition(eyePos_SpecExponent);
			eyePos_SpecExponent[3] = 0.0f;
			ShaderAPI.SetPixelShaderConstant((int)PixelShaderConst.EyePosSpecExponent, eyePos_SpecExponent);

			if (!HardwareConfig.HasFastVertexTextures()) {
				DynamicShaderIndex vshIndex = new(ShaderAPI, ShaderType.Vertex);
				vshIndex.Set("DYNAMIC_LIGHT", 0);
				vshIndex.Set("STATIC_LIGHT", 0);
				vshIndex.Set("DOWATERFOG", fogIndex);
				vshIndex.Set("SKINNING", 0);
				vshIndex.Set("LIGHTING_PREVIEW", 0);
				vshIndex.Set("COMPRESSED_VERTS", (int)vertexCompression);
				vshIndex.Set("NUM_LIGHTS", 0);
				ShaderAPI.SetVertexShaderIndex(vshIndex.GetIndex());

				DynamicShaderIndex pshIndex = new(ShaderAPI, ShaderType.Pixel);
				pshIndex.Set("PIXELFOGTYPE", ShaderAPI.GetPixelFogCombo());
				ShaderAPI.SetPixelShaderIndex(pshIndex.GetIndex());
			}
			else {
				// note: the vertex shader reads SHADER_SPECIFIC_CONST_10/11, Valve and GMod pass 6/7
				SetHWMorphVertexShaderState((int)VertexShaderConst.ShaderSpecificConst6, (int)VertexShaderConst.ShaderSpecificConst7, VertexTextureSampler.Sampler0);

				DynamicShaderIndex vshIndex = new(ShaderAPI, ShaderType.Vertex);
				vshIndex.Set("DYNAMIC_LIGHT", 0);
				vshIndex.Set("STATIC_LIGHT", 0);
				vshIndex.Set("DOWATERFOG", fogIndex);
				vshIndex.Set("SKINNING", ShaderAPI.GetCurrentNumBones() > 0);
				vshIndex.Set("LIGHTING_PREVIEW", 0);
				vshIndex.Set("MORPHING", ShaderAPI.IsHWMorphingEnabled());
				vshIndex.Set("COMPRESSED_VERTS", (int)vertexCompression);
				ShaderAPI.SetVertexShaderIndex(vshIndex.GetIndex());

				DynamicShaderIndex pshIndex = new(ShaderAPI, ShaderType.Pixel);
				pshIndex.Set("PIXELFOGTYPE", ShaderAPI.GetPixelFogCombo());
				ShaderAPI.SetPixelShaderIndex(pshIndex.GetIndex());

				Span<bool> unusedTexCoords = [false, false, !ShaderAPI.IsHWMorphingEnabled()];
				ShaderAPI.MarkUnusedVertexFields(0, unusedTexCoords);
			}
		}
		Draw();
	}
}
