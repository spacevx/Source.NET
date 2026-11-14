using Source.Common;
using Source.Common.Formats.Keyvalues;
using Source.Common.MaterialSystem;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Source.MaterialSystem;

public class MaterialSubRect : IMaterialInternal
{
	[Flags]
	enum MaterialSubRectFlags
	{
		IsPrecached = 0x1,
		VarsIsPrecached = 0x2,
		IsManuallyCreated = 0x4,
		UsesUNCFileName = 0x20,
		IsPreloaded = 0x40,
		ArtificialRefCount = 0x80,
	}

	readonly MaterialSystem materials;
	IMaterialInternal? MaterialPage;
	int EnumID;
	readonly string Name;
	readonly string TextureGroupName;
	Vector2 Offset;
	Vector2 Scale;
	Vector2 Size;
	short RefCount;
	MaterialSubRectFlags Local;
	readonly List<IMaterialVar> MaterialVars = [];
	KeyValues? VMTKeyValues;
#if GMOD_DLL
	int MinLightmapPageID;
	int MaxLightmapPageID;
#endif

	public MaterialSubRect(MaterialSystem materials, ReadOnlySpan<char> materialName, ReadOnlySpan<char> textureGroupName, KeyValues? vmtKeyValues, KeyValues? patchKeyValues, bool assumeCreateFromFile) {
		// todo: m_QueueFriendlyVersion, it needs CMaterial_QueueFriendly ported first
		this.materials = materials;

		int len = materialName.Length;
		Span<char> temp = stackalloc char[len];
		materialName.ToLowerInvariant(temp);
		temp = temp[..(len - 4)];
		Name = new(temp);

		MaterialPage = null;
		EnumID = 0;
		TextureGroupName = new(textureGroupName);
		Offset = default;
		Scale = default;
		Size = default;
		RefCount = 0;
		Local = 0;
		MaterialVars.Clear();

		if (temp.Length >= 2 && temp[0] == '/' && temp[1] == '/' && (temp.Length == 2 || temp[2] != '/'))
			Local |= MaterialSubRectFlags.UsesUNCFileName;

		if (!assumeCreateFromFile) {
			VMTKeyValues = vmtKeyValues;
			if (VMTKeyValues != null)
				Local |= MaterialSubRectFlags.IsManuallyCreated;

			Precache();
		}
		else {
			VMTKeyValues = null;
			PrecacheVars(vmtKeyValues, patchKeyValues);
			Precache();
		}

		Assert(MaterialPage != null);
		MaterialPage!.IncrementReferenceCount();
	}

	public static void Destroy(IMaterialInternal? material) {
		if (material is not MaterialSubRect mat)
			return;

		mat.Uncache();
		if (mat.RefCount != 0)
			DevWarning(2, $"Reference Count for Material {mat.GetName()} ({mat.RefCount}) != 0\n");

		if (mat.MaterialPage != null) {
			mat.MaterialPage.DecrementReferenceCount();
			mat.MaterialPage = null;
		}

		mat.VMTKeyValues = null;
	}

	public void SetShaderAndParams(KeyValues? keyValues) {
		Uncache();

		VMTKeyValues = keyValues?.MakeCopy();
		if (VMTKeyValues != null)
			Local |= MaterialSubRectFlags.IsManuallyCreated;

		if (materials.ShaderDevice.IsUsingGraphics())
			Precache();
	}

	public ReadOnlySpan<char> GetName() => Name;
	public ReadOnlySpan<char> GetTextureGroupName() => TextureGroupName;

	public float GetMappingWidth() => (int)Size.X;
	public float GetMappingHeight() => (int)Size.Y;

	public bool InMaterialPage() => true;

	public void GetMaterialOffset(Span<float> offset) {
		offset[0] = Offset.X;
		offset[1] = Offset.Y;
	}

	public void GetMaterialScale(Span<float> scale) {
		scale[0] = Scale.X;
		scale[1] = Scale.Y;
	}

	public IMaterial GetMaterialPage() => MaterialPage!;

	public void IncrementReferenceCount() {
		++RefCount;
#if GMOD_DLL
		if (RefCount >= 32001)
			Error($"SubRect Material '{GetName()}' has insane reference count!\n");
#endif
	}
	public void DecrementReferenceCount() => --RefCount;
	public int GetReferenceCount() => RefCount;

	public bool IsPrecached() => (Local & MaterialSubRectFlags.IsPrecached) != 0;
	public bool IsPrecachedVars() => (Local & MaterialSubRectFlags.VarsIsPrecached) != 0;
	public bool IsManuallyCreated() => (Local & MaterialSubRectFlags.IsManuallyCreated) != 0;
	bool UsesUNCFileName() => (Local & MaterialSubRectFlags.UsesUNCFileName) != 0;

	public void Precache() {
		if (IsPrecached())
			return;

		if (!PrecacheVars())
			return;

		Local |= MaterialSubRectFlags.IsPrecached;
	}

	public bool PrecacheVars(KeyValues? inVmtKeyValues = null, KeyValues? inPatchKeyValues = null, List<FileNameHandle_t>? includes = null, MaterialFindContext findContext = 0) {
		Assert((inVmtKeyValues == null) ? (inPatchKeyValues == null) : (inPatchKeyValues != null));
		if (IsPrecachedVars())
			return true;

		KeyValues? vmtKeyValues;

		if (VMTKeyValues != null) {
			vmtKeyValues = VMTKeyValues;
			Assert((inVmtKeyValues == null) && (inPatchKeyValues == null));
		}
		else if (inVmtKeyValues != null)
			vmtKeyValues = inVmtKeyValues;
		else {
			vmtKeyValues = new KeyValues("vmt");
			KeyValues patchKeyValues = new("vmt_patches");
			if (!Material.LoadVMTFile(materials.FileSystem, ref vmtKeyValues, patchKeyValues, GetName(), UsesUNCFileName(), null)) {
				Warning($"CMaterialSubRect::PrecacheVars: error loading vmt file for {GetName()}\n");
				return false;
			}
		}

		ParseMaterialVars(vmtKeyValues);
		SetupMaterialVars();

		Local |= MaterialSubRectFlags.VarsIsPrecached;
		return true;
	}

	void ParseMaterialVars(KeyValues keyValues) {
		ReadOnlySpan<char> shaderName = keyValues.Name;

		if (shaderName.Equals("Subrect", StringComparison.OrdinalIgnoreCase)) {
			for (KeyValues? var = keyValues.GetFirstSubKey(); var != null; var = var.GetNextKey()) {
				ReadOnlySpan<char> varName = var.Name;
				if (varName.Equals("$Pos", StringComparison.OrdinalIgnoreCase))
					ScanFloats(var.GetString(), ref Offset.X, ref Offset.Y);
				else if (varName.Equals("$Size", StringComparison.OrdinalIgnoreCase))
					ScanFloats(var.GetString(), ref Size.X, ref Size.Y);
				else if (varName.Equals("$Material", StringComparison.OrdinalIgnoreCase)) {
					MaterialPage = (IMaterialInternal)materials.FindMaterial(var.GetString(), MaterialDefines.TEXTURE_GROUP_DECAL, true, default)!;
					MaterialPage = MaterialPage.GetRealTimeVersion();
				}

				IMaterialVar? newVar = Material.CreateMaterialVarFromKeyValue(this, var);
				if (newVar != null)
					MaterialVars.Add(newVar);
			}
		}

#if GMOD_DLL
		if (MaterialPage == null)
			Error($"Fatal: Subrect material '{GetName()}' is missing $material parameter!\n");
#endif
	}

	static void ScanFloats(ReadOnlySpan<char> str, ref float x, ref float y) {
		if (!ScanFloat(ref str, out float first))
			return;
		x = first;

		if (!ScanFloat(ref str, out float second))
			return;
		y = second;
	}

	static bool ScanFloat(ref ReadOnlySpan<char> str, out float value) {
		str = str.TrimStart();

		int length = 0;
		for (int end = 1; end <= str.Length; end++) {
			if (float.TryParse(str[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
				length = end;
			else if (!IsFloatPrefix(str[..end]))
				break;
		}

		if (length == 0) {
			value = 0;
			return false;
		}

		value = float.Parse(str[..length], NumberStyles.Float, CultureInfo.InvariantCulture);
		str = str[length..];
		return true;
	}

	static bool IsFloatPrefix(ReadOnlySpan<char> str) {
		char last = str[^1];
		return last == '+' || last == '-' || last == '.' || last == 'e' || last == 'E';
	}

	void SetupMaterialVars() {
		if (MaterialPage == null) {
			DevWarning(1, "CMaterialSubRect::SetupMaterialVars: Invalid Material Page!\n");
			return;
		}

		int materialPageWidth = (int)MaterialPage.GetMappingWidth();
		int materialPageHeight = (int)MaterialPage.GetMappingHeight();

		float ooWidth = 1.0f / materialPageWidth;
		float ooHeight = 1.0f / materialPageHeight;

		Offset.X += 1.0f;
		Offset.Y += 1.0f;
		Offset.X *= ooWidth;
		Offset.Y *= ooHeight;

		Scale.X = (Size.X - 2.0f) * ooWidth;
		Scale.Y = (Size.Y - 2.0f) * ooHeight;
	}

	static int complainCount = 0;
	public IMaterialVar FindVar(ReadOnlySpan<char> varName, out bool found, bool complain = true) {
		IMaterialVar var = MaterialPage!.FindVar(varName, out found, false);
		if (found)
			return var;

		UtlSymId_t symVar = IMaterialVar.FindSymbol(varName);
		if (symVar != UTL_INVAL_SYMBOL) {
			int varCount = MaterialVars.Count;
			for (int i = 0; i < varCount; ++i) {
				if (MaterialVars[i].GetNameAsSymbol() == symVar) {
					found = true;
					return MaterialVars[i];
				}
			}
		}

		if (complain) {
			if (complainCount < 100) {
				DevWarning(1, $"No such variable \"{varName}\" for material \"{GetName()}\"\n");
				complainCount++;
			}
		}

		return GetDummyMaterialVar();
	}

	public bool TryFindVar(ReadOnlySpan<char> varName, [NotNullWhen(true)] out IMaterialVar? found, bool complain = true) {
		found = FindVar(varName, out bool hasFound, complain);
		return hasFound;
	}

	public IMaterialVar? FindVarFast(ReadOnlySpan<char> varName, ref TokenCache token) {
		IMaterialVar? var = MaterialPage!.FindVarFast(varName, ref token);
		if (var != null)
			return var;

		if (token.Symbol != UTL_INVAL_SYMBOL) {
			int varCount = MaterialVars.Count;
			for (int i = 0; i < varCount; ++i) {
				if (MaterialVars[i].GetNameAsSymbol() == token.Symbol)
					return MaterialVars[i];
			}
		}

		return null;
	}

	static IMaterialVar? dummyVar;
	static IMaterialVar GetDummyMaterialVar() {
		dummyVar ??= new MaterialVar(null!, "$dummyVar", 0);
		return dummyVar;
	}

	public int GetEnumerationID() => EnumID;
	public void SetEnumerationID(int id) => EnumID = id;

	public void Uncache(bool preserveVars = false) {
		if (IsPrecached())
			Local &= ~MaterialSubRectFlags.IsPrecached;

		if (!preserveVars) {
			if (IsPrecachedVars())
				Local &= ~MaterialSubRectFlags.VarsIsPrecached;
		}
	}

	public void AddMaterialVar(IMaterialVar materialVar) => MaterialVars.Add(materialVar);

	public void MarkAsPreloaded(bool set) {
		if (set)
			Local |= MaterialSubRectFlags.IsPreloaded;
		else
			Local &= ~MaterialSubRectFlags.IsPreloaded;
	}

	public bool IsPreloaded() => (Local & MaterialSubRectFlags.IsPreloaded) != 0;

	public void ArtificialAddRef() {
		if ((Local & MaterialSubRectFlags.ArtificialRefCount) != 0)
			return;

		Local |= MaterialSubRectFlags.ArtificialRefCount;
		RefCount++;
	}

	public void ArtificialRelease() {
		if ((Local & MaterialSubRectFlags.ArtificialRefCount) == 0)
			return;

		Local &= ~MaterialSubRectFlags.ArtificialRefCount;
		RefCount--;
	}

	public void DeleteIfUnreferenced() {
		if (RefCount > 0)
			return;

		materials.RemoveMaterialSubRect(this);
	}

	public void GetReflectivity(out Vector3 reflect) => MaterialPage!.GetReflectivity(out reflect);
	public int GetNumAnimationFrames() => MaterialPage!.GetNumAnimationFrames();
	public bool UsesEnvCubemap() => MaterialPage!.UsesEnvCubemap();
	public bool IsSpriteCard() => MaterialPage!.IsSpriteCard();
	public bool NeedsSoftwareSkinning() => MaterialPage!.NeedsSoftwareSkinning();
	public bool NeedsSoftwareLighting() => MaterialPage!.NeedsSoftwareLighting();
	public bool NeedsTangentSpace() => MaterialPage!.NeedsTangentSpace();
	public bool NeedsPowerOfTwoFrameBufferTexture(bool checkSpecificToThisFrame) => MaterialPage!.NeedsPowerOfTwoFrameBufferTexture(checkSpecificToThisFrame);
	public bool NeedsFullFrameBufferTexture(bool checkSpecificToThisFrame) => MaterialPage!.NeedsFullFrameBufferTexture(checkSpecificToThisFrame);
	public bool NeedsLightmapBlendAlpha() => MaterialPage!.NeedsLightmapBlendAlpha();
	public void AlphaModulate(float alpha) => MaterialPage!.AlphaModulate(alpha);
	public void ColorModulate(float r, float g, float b) => MaterialPage!.ColorModulate(r, g, b);
	public float GetAlphaModulation() => MaterialPage!.GetAlphaModulation();
	public void GetColorModulation(out float r, out float g, out float b) => MaterialPage!.GetColorModulation(out r, out g, out b);
	public void SetMaterialVarFlag(MaterialVarFlags flag, bool on) => MaterialPage!.SetMaterialVarFlag(flag, on);
	public bool GetMaterialVarFlag(MaterialVarFlags flag) => MaterialPage!.GetMaterialVarFlag(flag);
	public bool IsTranslucent() => MaterialPage!.IsTranslucent();
	public bool IsAlphaTested() => MaterialPage!.IsAlphaTested();
	public bool IsVertexLit() => MaterialPage!.IsVertexLit();
	public bool GetPropertyFlag(MaterialPropertyTypes type) => MaterialPage!.GetPropertyFlag(type);
	public bool IsTwoSided() => MaterialPage!.IsTwoSided();
	public void DrawMesh(VertexCompressionType vertexCompression) => MaterialPage!.DrawMesh(vertexCompression);
	public void ReloadTextures() => MaterialPage!.ReloadTextures();
#if GMOD_DLL
	public void SetMinLightmapPageID(int pageID) => MinLightmapPageID = pageID;
	public void SetMaxLightmapPageID(int pageID) => MaxLightmapPageID = pageID;
	public int GetMinLightmapPageID() => MinLightmapPageID;
	public int GetMaxLightmapPageID() => MaxLightmapPageID;
#else
	public void SetMinLightmapPageID(int pageID) => MaterialPage!.SetMinLightmapPageID(pageID);
	public void SetMaxLightmapPageID(int pageID) => MaterialPage!.SetMaxLightmapPageID(pageID);
	public int GetMinLightmapPageID() => MaterialPage!.GetMinLightmapPageID();
	public int GetMaxLightmapPageID() => MaterialPage!.GetMaxLightmapPageID();
#endif
	public void SetNeedsWhiteLightmap(bool value) => MaterialPage!.SetNeedsWhiteLightmap(value);
	public bool GetNeedsWhiteLightmap() => MaterialPage!.GetNeedsWhiteLightmap();
	public void CallBindProxy(object? proxyData) => MaterialPage!.CallBindProxy(proxyData);
	public bool HasProxy() => MaterialPage!.HasProxy();
	public string? GetShaderName() => MaterialPage!.GetShaderName();
	public VertexFormat GetVertexFormat() => MaterialPage!.GetVertexFormat();
	public void Refresh() => MaterialPage!.Refresh();
	public void RefreshPreservingMaterialVars() => MaterialPage!.RefreshPreservingMaterialVars();
	public void RecomputeStateSnapshots() => MaterialPage!.RecomputeStateSnapshots();
	public int ShaderParamCount() => MaterialPage!.ShaderParamCount();
	public IMaterialVar[]? GetShaderParams() => MaterialPage!.GetShaderParams();
	public bool IsErrorMaterialInternal() => false;
	public void SetUseFixedFunctionBakedLighting(bool enable) => MaterialPage!.SetUseFixedFunctionBakedLighting(enable);
	public bool IsUsingVertexID() => MaterialPage!.IsUsingVertexID();
	public void ReportVarChanged(IMaterialVar? var) => MaterialPage!.ReportVarChanged(var);
	public uint GetChangeID() => MaterialPage!.GetChangeID();
	public bool IsRealTimeVersion() => true;
	public IMaterialInternal GetRealTimeVersion() => this;
}
