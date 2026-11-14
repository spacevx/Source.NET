using Source.Common;
using Source;
using Game.Shared;
using System.Numerics;
namespace Game.Client;
[NetworkName("CTEBSPDecal")]
public partial class C_TEBSPDecal : C_BaseTempEntity
{
	[NetworkTable]
	public static readonly NetworkProp[] Props = [
		new(nameof(Origin)) { Bits = 0, Flags = PropFlags.Coord },
		new(nameof(Entity)) { Bits = 13, Flags = PropFlags.Unsigned },
		new(nameof(Index)) { Bits = 9, Flags = PropFlags.Unsigned },
	];

	[NetworkVar("m_vecOrigin")]
	public Vector3 Origin;
	[NetworkVar("m_nEntity")]
	public int Entity;
	[NetworkVar("m_nIndex")]
	public int Index;

	public override void PostDataUpdate(DataUpdateType updateType) {
		C_BaseEntity? ent = cl_entitylist.GetEnt(Entity);
		if (ent == null) {
			DevMsg(1, $"Decal: entity = {Entity}");
			return;
		}

		if (EffectsClient.r_decals.GetInt() != 0)
			effects.DecalShoot(Index, Entity, ent.GetModel(), ent.GetAbsOrigin(), ent.GetAbsAngles(), Origin, null, (int)Source.Engine.FDecal.Permanent);
	}
}

public static partial class TempEnts
{
	public static void TE_BSPDecal(IRecipientFilter filter, float delay, in Vector3 pos, int entity, int index) {
		C_BaseEntity? ent = cl_entitylist.GetEnt(entity);
		if (ent == null) {
			DevMsg(1, $"Decal: entity = {entity}");
			return;
		}

		if (EffectsClient.r_decals.GetInt() != 0)
			effects.DecalShoot(index, entity, ent.GetModel(), ent.GetAbsOrigin(), ent.GetAbsAngles(), pos, null, (int)Source.Engine.FDecal.Permanent);
	}
}
