using Source.Common;
using Source;
using Game.Shared;
using System.Numerics;
namespace Game.Client;
[NetworkName("CTEWorldDecal")]
public partial class C_TEWorldDecal : C_BaseTempEntity
{
	[NetworkTable]
	public static readonly NetworkProp[] Props = [
		new(nameof(Origin)) { Bits = 0, Flags = PropFlags.Coord },
		new(nameof(Index)) { Bits = 9, Flags = PropFlags.Unsigned },
	];

	[NetworkVar("m_vecOrigin")]
	public Vector3 Origin;
	[NetworkVar("m_nIndex")]
	public int Index;

	public override void PostDataUpdate(DataUpdateType updateType) {
		if (EffectsClient.r_decals.GetInt() != 0) {
			C_BaseEntity? ent = cl_entitylist.GetEnt(0);
			if (ent != null)
				effects.DecalShoot(Index, 0, ent.GetModel(), ent.GetAbsOrigin(), ent.GetAbsAngles(), Origin, null, 0);
		}

		// todo: RecordWorldDecal(Origin, Index), it needs the tool framework ported first
	}
}

public static partial class TempEnts
{
	public static void TE_WorldDecal(IRecipientFilter filter, float delay, in Vector3 pos, int index) {
		if (EffectsClient.r_decals.GetInt() != 0) {
			C_BaseEntity? ent = cl_entitylist.GetEnt(0);
			if (ent != null)
				effects.DecalShoot(index, 0, ent.GetModel(), ent.GetAbsOrigin(), ent.GetAbsAngles(), pos, null, 0);
		}

		// todo: RecordWorldDecal(pos, index), it needs the tool framework ported first
	}
}
