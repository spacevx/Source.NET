using Source;
using Source.Common;
using Source.Common.Formats.Keyvalues;
using Source.Common.MaterialSystem;

namespace Game.Client;

[ExposeMaterialProxy(Name = "PlayerLogo")]
public class PlayerLogoProxy : IMaterialProxy
{
	const string DEFAULT_DECAL_NAME = "decals/YBlood1";

	IMaterialVar? BaseTextureVar;
	ITexture? DefaultTexture;

	public bool Init(IMaterial material, KeyValues keyValues) {
		BaseTextureVar = material.FindVar("$basetexture", out bool found);
		if (!found)
			return false;

		DefaultTexture = materials.FindTexture(DEFAULT_DECAL_NAME, MaterialDefines.TEXTURE_GROUP_DECAL);
		if (ITexture.IsError(DefaultTexture))
			return false;

		DefaultTexture.IncrementReferenceCount();
		return true;
	}

	public void OnBind(object? o) { }

	public void Release() {
		DefaultTexture?.DecrementReferenceCount();
	}

	public IMaterial GetMaterial() => BaseTextureVar!.GetOwningMaterial();
}
