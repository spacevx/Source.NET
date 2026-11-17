global using static Game.Shared.DecalEmitterSystemGlobals;

using Source.Common.Formats.Keyvalues;
using Source.Common.MaterialSystem;

namespace Game.Shared;


// Also known as "Mat" in some of the LuaEnums stuff
public enum CharTex {
	Antlion = 65,
	BloodyFlesh = 66,
	Concrete = 67,
	Dirt = 68,
	Eggshell = 69,
	Flesh = 70,
	Grate = 71,
	AlienFlesh = 72,
	Clip = 73,
	Snow = 74,
	Plastic = 76,
	Metal = 77,
	Sand = 78,
	Foliage = 79,
	Computer = 80,
	Slosh = 83,
	Tile = 84,
	Grass = 85,
	Vent = 86,
	Wood = 87,
	Default = 88,
	Glass = 89,
	WarpShield = 90,
}

public interface IDecalEmitterSystem
{
	int GetDecalIndexForName(ReadOnlySpan<char> decalname);
	ReadOnlySpan<char> TranslateDecalForGameMaterial(ReadOnlySpan<char> decalName, byte gamematerial);
#if GMOD_DLL
	void AddDecal(ReadOnlySpan<char> name, ReadOnlySpan<char> material);
	void AddDecalEx(ReadOnlySpan<char> name, ReadOnlySpan<string> materials);
	string? GetDecalMaterial(ReadOnlySpan<char> name);
#endif
}

public class DecalEmitterSystem(ReadOnlySpan<char> name) : AutoGameSystem(name), IDecalEmitterSystem
{
	const string DECAL_LIST_FILE = "scripts/decals_subrect.txt";
	const string TRANSLATION_DATA_SECTION = "TranslationData";

	class DecalListEntry
	{
		public string Name = "";
		public int PrecacheIndex = -1;
		public float Weight = 1.0f;
#if GMOD_DLL && CLIENT_DLL
		public IMaterial? ModelMaterial;
#endif
	}

	class DecalEntry
	{
		public readonly List<int> Indices = [];
	}

#if GMOD_DLL
	bool LevelInitialized;
#endif
	readonly List<DecalListEntry> AllDecals = [];
	readonly Dictionary<string, DecalEntry> Decals = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, string> GameMaterialTranslation = new(StringComparer.OrdinalIgnoreCase);

	public int GetDecalIndexForName(ReadOnlySpan<char> decalname) {
		DecalListEntry? item = PickDecal(decalname);
		if (item == null)
			return -1;

		return item.PrecacheIndex;
	}

	DecalListEntry? PickDecal(ReadOnlySpan<char> decalname) {
		if (decalname.IsEmpty)
			return null;

		if (!Decals.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(decalname, out DecalEntry? e))
			return null;

		int count = e.Indices.Count;
		if (count <= 0)
			return null;

		float totalweight = 0.0f;
		int slot = 0;

		for (int i = 0; i < count; i++) {
			int idx = e.Indices[i];
			DecalListEntry item = AllDecals[idx];

			if (totalweight == 0)
				slot = idx;

			totalweight += item.Weight;
			if (totalweight == 0 || random.RandomFloat(0, totalweight) < item.Weight)
				slot = idx;
		}

		return AllDecals[slot];
	}

	public override bool Init() {
#if GMOD_DLL
		LevelInitialized = false;
#endif
		LoadDecalsFromScript(DECAL_LIST_FILE);
		return true;
	}

	public override void LevelInitPreEntity() {
#if GMOD_DLL
		LevelInitialized = true;
#endif
		foreach (DecalListEntry e in AllDecals)
			e.PrecacheIndex = PrecacheDecal(e.Name);
	}

	static int PrecacheDecal(ReadOnlySpan<char> name) {
#if CLIENT_DLL
		return effects.DrawDecalIndexFromName(name);
#else
		return engine.PrecacheDecal(name);
#endif
	}

#if GMOD_DLL
	public override void LevelShutdownPostEntity() {
		LevelInitialized = false;
		Clear();
		LoadDecalsFromScript(DECAL_LIST_FILE);
	}
#endif

	void LoadDecalsFromScript(ReadOnlySpan<char> filename) {
		KeyValues kv = new(filename);
		if (!kv.LoadFromFile(filesystem, filename))
			return;

		KeyValues? translation = null;
		for (KeyValues? p = kv; p != null; p = p.GetNextKey()) {
			if (p.GetFirstSubKey() == null)
				continue;

			ReadOnlySpan<char> keyname = p.Name;

			if (keyname.Equals(TRANSLATION_DATA_SECTION, StringComparison.OrdinalIgnoreCase)) {
				translation = p;
				continue;
			}

			DecalEntry entry = new();

			for (KeyValues? sub = p.GetFirstSubKey(); sub != null; sub = sub.GetNextKey()) {
				DecalListEntry decal = new() {
					Name = sub.Name,
					PrecacheIndex = -1,
					Weight = sub.GetFloat()
				};

				entry.Indices.Add(AllDecals.Count);
				AllDecals.Add(decal);
			}

			Decals.TryAdd(new string(keyname), entry);
		}

		if (translation == null)
			return;

		for (KeyValues? sub = translation.GetFirstSubKey(); sub != null; sub = sub.GetNextKey()) {
			ReadOnlySpan<char> decal = sub.GetString();
			if (decal.IsEmpty)
				continue;

			if (Decals.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(decal, out string? key, out _))
				GameMaterialTranslation.TryAdd(sub.Name, key);
		}
	}

	ReadOnlySpan<char> ImpactDecalForGameMaterial(int gamematerial) {
		Span<char> gm = [(char)gamematerial];

		if (!GameMaterialTranslation.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(gm, out string? decal))
			return null;

		return decal;
	}

	public override void Shutdown() => Clear();

	void Clear() {
		Decals.Clear();
		AllDecals.Clear();
		GameMaterialTranslation.Clear();
	}

	public ReadOnlySpan<char> TranslateDecalForGameMaterial(ReadOnlySpan<char> decalName, byte gamematerial) {
		if (gamematerial == (byte)CharTex.Concrete)
			return decalName;

		if (decalName.Equals("Impact.Concrete", StringComparison.OrdinalIgnoreCase)) {
			if (gamematerial == (byte)'-')
				return "";

			ReadOnlySpan<char> d = ImpactDecalForGameMaterial(gamematerial);
			if (!d.IsEmpty)
				return d;
		}

		return decalName;
	}

#if GMOD_DLL
	public void AddDecal(ReadOnlySpan<char> name, ReadOnlySpan<char> material) {
		if (Decals.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name))
			return;

		DecalEntry entry = new();
		AddDecalMaterial(entry, material);
		Decals.Add(new string(name), entry);
	}

	public void AddDecalEx(ReadOnlySpan<char> name, ReadOnlySpan<string> materials) {
		if (Decals.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name))
			return;

		DecalEntry entry = new();
		foreach (string material in materials)
			AddDecalMaterial(entry, material);

		Decals.Add(new string(name), entry);
	}

	void AddDecalMaterial(DecalEntry entry, ReadOnlySpan<char> material) {
		DecalListEntry decal = new() {
			Name = new string(material),
			Weight = 1.0f,
			PrecacheIndex = LevelInitialized ? PrecacheDecal(material) : -1
		};
#if CLIENT_DLL
		DoModelMaterialPrecache(decal);
#endif

		entry.Indices.Add(AllDecals.Count);
		AllDecals.Add(decal);
	}

	public string? GetDecalMaterial(ReadOnlySpan<char> name) {
		DecalListEntry? item = PickDecal(name);
		if (item == null)
			return null;

		return item.Name;
	}

#if CLIENT_DLL
	static void DoModelMaterialPrecache(DecalListEntry decal) {
		IMaterial material = materials.FindMaterial(decal.Name, Source.MaterialDefines.TEXTURE_GROUP_DECAL, false)!;
		IMaterialVar modelMaterial = material.FindVar("$modelmaterial", out bool found, false);
		if (!found)
			return;

		IMaterial? mm = materials.FindMaterial(modelMaterial.GetStringValue(), Source.MaterialDefines.TEXTURE_GROUP_DECAL, false);
		if (!mm.IsErrorMaterial())
			decal.ModelMaterial = mm;
	}
#endif
#endif
}

public static class DecalEmitterSystemGlobals
{
	static readonly DecalEmitterSystem g_DecalSystem = new("CDecalEmitterSystem");
	public static readonly IDecalEmitterSystem decalsystem = g_DecalSystem;
}
