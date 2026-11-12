using Source.Common.Commands;

namespace Game.Client;

public partial class Input
{
	ConVar joy_name = new("joystick", FCvar.Archive);
	ConVar joy_pitchsensitivity = new("1", FCvar.Archive | FCvar.ArchiveXbox);
	ConVar joy_yawsensitivity = new("-1", FCvar.Archive | FCvar.ArchiveXbox);
	ConVar joy_inverty_default = new("0", FCvar.ArchiveXbox);
	ConVar joy_movement_stick_default = new("0", FCvar.ArchiveXbox);
}
