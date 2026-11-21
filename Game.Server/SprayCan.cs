using Game.Shared;

using Source;
using Source.Common.Formats.BSP;
using Source.Common.Mathematics;

using System.Numerics;

namespace Game.Server;

[LinkEntityToClass("spraycan")]
[PrecacheRegister("spraycan")]
public class SprayCan : PointEntity
{
	public void Spawn(BasePlayer owner) {
		SetLocalOrigin(owner.WorldSpaceCenter() + new Vector3(0, 0, 32));
		SetLocalAngles(owner.EyeAngles());
		SetOwnerEntity(owner);
		SetNextThink(gpGlobals.CurTime);
		EmitSound("SprayCan.Paint");
	}

	public override void Precache() {
		PrecacheScriptSound("SprayCan.Paint");
	}

	public override void Think() {
		BasePlayer? player = ToBasePlayer(GetOwnerEntity());
		if (player != null) {
			int playernum = player.EntIndex();

			MathLib.AngleVectors(GetAbsAngles(), out Vector3 forward);
			Util.TraceLine(GetAbsOrigin(), GetAbsOrigin() + forward * 128, Mask.SolidBrushOnly, player, Source.CollisionGroup.None, out Trace tr);

			Util.PlayerDecalTrace(ref tr, playernum);
		}

		Util.Remove(this);
	}

	public override EntityCapabilities ObjectCaps() => EntityCapabilities.DontSave;
}
