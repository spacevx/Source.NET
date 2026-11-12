using Source.Common.Commands;
using Source.Common.GUI;
using Source.Common.Launcher;
using Source.GUI.Controls;

namespace Game.Client;

public partial class Input
{
	IVGuiInput? _input;
	ILauncherManager? _launcher;
	IVGuiInput input => _input ??= Singleton<IVGuiInput>();
	ILauncherManager launcher => _launcher ??= Singleton<ILauncherManager>();

	public void ActivateMouse() {
		if (MouseActive)
			return;

		if (MouseInitialized) {
			if (MouseParmsValid) {
#if WIN32
				RestoreSPI = SystemParametersInfo(SPI_SETMOUSE, 0, NewMouseParms, 0);
#endif
			}
			MouseActive = true;
			ResetMouse();
#if !WIN32
			engine.GetMouseDelta(out _, out _, true);
#endif

			AccumulatedMouseXMovement = 0;
			AccumulatedMouseYMovement = 0;

			inputsystem.GetRawMouseAccumulators(out _, out _);
			launcher.SetWindowRelativeMouseMode(true);
		}
	}

	public void ClearStates() {
		if (!MouseActive)
			return;

		AccumulatedMouseXMovement = 0;
		AccumulatedMouseYMovement = 0;

		inputsystem.GetRawMouseAccumulators(out _, out _);
	}

	public void DeactivateMouse() {
		if (!MouseActive)
			return;

		if (MouseInitialized) {
			if (RestoreSPI) {
#if WIN32
				SystemParametersInfo(SPI_SETMOUSE, 0, OrigMouseParms, 0);
#endif
			}
			MouseActive = false;
			Surface.SetCursor(CursorCode.Arrow);
#if !WIN32
			ResetMouse();
#endif

			AccumulatedMouseXMovement = 0;
			AccumulatedMouseYMovement = 0;
#if GMOD_DLL
			MouseWheel = 0;
#endif
			launcher.SetWindowRelativeMouseMode(false);
		}
	}

#if GMOD_DLL
	int MouseWheel;

	public void MouseWheeled(int delta) {
		if (!Surface.IsCursorVisible()) {
			MouseWheel += delta;
			return;
		}

		if (input.GetMouseOver() is Panel panel && panel.IsWorldClicker())
			MouseWheel += delta;
	}
#endif

	ConVar m_filter = new("0", FCvar.Archive, "Mouse filtering (set this to 1 to average the mouse over 2 frames).");
	static readonly ConVar m_rawinput = new("m_rawinput", "0", FCvar.Archive, "Use Raw Input for mouse input.");
	ConVar sensitivity = new("3", FCvar.Archive, "Mouse sensitivity.", 0.0001f, 1000);
	ConVar m_side = new("0.8", FCvar.Archive, "Mouse side factor.", 0.0001f, 1000);
	ConVar m_yaw = new("0.022", FCvar.Archive, "Mouse yaw factor.", 0.0001f, 1000);
	ConVar m_forward = new("1", FCvar.Archive, "Mouse forward factor.", 0.0001f, 1000);
	// TODO: We need server-bounded convars for this later.
	ConVar m_pitch = new("0.022", FCvar.Archive, "Mouse forward factor.", 0.0001f, 1000);

	ConVar m_customaccel = new("0", FCvar.Archive, """
Custom mouse acceleration:
0: custom accelaration disabled
1: mouse_acceleration = min(m_customaccel_max, pow(raw_mouse_delta, m_customaccel_exponent) * m_customaccel_scale + sensitivity)
2: Same as 1, with but x and y sensitivity are scaled by m_pitch and m_yaw respectively.
3: mouse_acceleration = pow(raw_mouse_delta, m_customaccel_exponent - 1) * sensitivity
"""
	);
	ConVar m_customaccel_scale = new("0.04", FCvar.Archive, "Custom mouse acceleration value.", 0, 10);
	ConVar m_customaccel_max = new("0", FCvar.Archive, "Max mouse move scale factor, 0 for no limit");
	ConVar m_customaccel_exponent = new("1", FCvar.Archive, "Mouse move is raised to this power before being scaled by scale factor.", 0.0f, 1.0f);

	ConVar m_mousespeed = new("1", FCvar.Archive, "Windows mouse acceleration (0 to disable, 1 to enable [Windows 2000: enable initial threshold], 2 to enable secondary threshold [Windows 2000 only]).", 0, 2);
	ConVar m_mouseaccel1 = new("0", FCvar.Archive, "Windows mouse acceleration initial threshold (2x movement).", 0, null);
	ConVar m_mouseaccel2 = new("0", FCvar.Archive, "Windows mouse acceleration secondary threshold (4x movement).", 0, null);

	ConVar cl_mouselook = new("1", FCvar.Archive, "Set to 1 to use mouse for look, 0 for keyboard look.");

	const int MOUSE_ACCEL_THRESHHOLD1 = 0;
	const int MOUSE_ACCEL_THRESHHOLD2 = 1;
	const int MOUSE_SPEED_FACTOR = 2;
	const int NUM_MOUSE_PARAMS = 3;

	bool RestoreSPI;
	readonly int[] OrigMouseParms = new int[NUM_MOUSE_PARAMS];
	readonly int[] NewMouseParms = [0, 0, 1];
	readonly bool[] CheckMouseParam = new bool[NUM_MOUSE_PARAMS];
	bool MouseParmsValid;

#if WIN32
	const uint SPI_GETMOUSE = 0x0003;
	const uint SPI_SETMOUSE = 0x0004;

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);
#endif

	public void CheckMouseAcclerationVars() {
		if (!MouseActive || !MouseInitialized || !MouseParmsValid || !RestoreSPI)
			return;

		Span<int> values = [m_mouseaccel1.GetInt(), m_mouseaccel2.GetInt(), m_mousespeed.GetInt()];

		bool dirty = false;

		for (int i = 0; i < NUM_MOUSE_PARAMS; i++) {
			if (!CheckMouseParam[i])
				continue;

			if (values[i] != NewMouseParms[i]) {
				dirty = true;
				NewMouseParms[i] = values[i];

				string name = i switch {
					MOUSE_ACCEL_THRESHHOLD1 => "m_mouseaccel1",
					MOUSE_ACCEL_THRESHHOLD2 => "m_mouseaccel2",
					_ => "m_mousespeed",
				};

				DevMsg($"Mouse parameter '{name}' set to {values[i]}\n");
			}
		}

		if (dirty) {
#if WIN32
			RestoreSPI = SystemParametersInfo(SPI_SETMOUSE, 0, NewMouseParms, 0);
#endif
		}
	}

	public void GetWindowCenter(out int x, out int y) {
		engine.GetScreenSize(out int w, out int h);
		x = w >> 1;
		y = h >> 1;
	}

	public void ResetMouse() {
		GetWindowCenter(out int x, out int y);
		SetMousePos(x, y);
	}

	private void GetMousePos(out int x, out int y) {
		input.GetCursorPos(out x, out y);
	}

	private void SetMousePos(int x, int y) {
		input.SetCursorPos(x, y);
	}
}
