using Source.Common;
using Source.Common.Engine;
using Source;
using Game.Shared;
using System.Numerics;
namespace Game.Server;

[LinkEntityToClass("trigger_playermovement")]
[NetworkName("CTriggerPlayerMovement")]
public partial class TriggerPlayerMovement : BaseTrigger
{
	[NetworkTable]
	public static readonly new NetworkProp[] Props = [
	];

	[Flags]
	public enum SF
	{
		MoveAutoDisable = 0x80,      // disable auto movement
		AutoDuck = 0x800,    // Duck automatically
		AutoWalk = 0x1000,
		DisableJump = 0x2000,
	}

	//-----------------------------------------------------------------------------
	// Purpose: Called when spawning, after keyvalues have been handled.
	//-----------------------------------------------------------------------------
	public override void Spawn() {
		if (HasSpawnFlags(BaseTrigger.SF.OnlyPlayerAllyNPCs)) {
			// @Note (toml 01-07-04): fix up spawn flag collision coding error. Remove at some point once all maps fixed up please!
			DevMsg("*** trigger_playermovement using obsolete spawnflag. Remove and reset with new value for \"Disable auto player movement\"\n");
			RemoveSpawnFlags(BaseTrigger.SF.OnlyPlayerAllyNPCs);
			AddSpawnFlags(SF.MoveAutoDisable);
		}
		base.Spawn();

		InitTrigger();

		if (HasSpawnFlags(SF.AutoDuck) || HasSpawnFlags(SF.AutoWalk) || HasSpawnFlags(SF.DisableJump)) {
			ClientSidePredicted = true;
			SetTransmitState(EdictFlags.Always);
		}
	}

	public override void UpdateOnRemove() {
		EndTouchOnAllTouchers();
		base.UpdateOnRemove();
	}

	// UNDONE: This will not support a player touching more than one of these
	// UNDONE: Do we care?  If so, ref count automovement in the player?
	public override void StartTouch(BaseEntity? other) {
		if (other == null || !PassesTriggerFilters(other))
			return;

		base.StartTouch(other);

		BasePlayer? player = ToBasePlayer(other);

		if (player == null)
			return;

		if (HasSpawnFlags(SF.AutoDuck))
			player.ForceButtons(InButtons.Duck);

		if (HasSpawnFlags(SF.AutoWalk)) {
			player.ForceButtons(InButtons.Walk);
			player.DisableButtons(InButtons.Speed);
		}

		if (HasSpawnFlags(SF.DisableJump))
			player.DisableButtons(InButtons.Jump);

		// UNDONE: Currently this is the only operation this trigger can do
		if (HasSpawnFlags(SF.MoveAutoDisable))
			player.Local.AllowAutoMovement = false;
	}

	public override void EndTouch(BaseEntity? other) {
		base.EndTouch(other);

		if (other == null || !PassesTriggerFilters(other))
			return;

		BasePlayer? player = ToBasePlayer(other);

		if (player == null)
			return;

		if (HasSpawnFlags(SF.AutoDuck))
			player.UnforceButtons(InButtons.Duck);

		if (HasSpawnFlags(SF.AutoWalk)) {
			player.UnforceButtons(InButtons.Walk);
			player.EnableButtons(InButtons.Speed);
		}

		if (HasSpawnFlags(SF.DisableJump))
			player.EnableButtons(InButtons.Jump);

		if (HasSpawnFlags(SF.MoveAutoDisable))
			player.Local.AllowAutoMovement = true;
	}
}
