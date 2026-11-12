using Source.Common;
using Source.Common.Commands;

using System;

namespace Source.Engine;

[EngineComponent]
public class Log : IGameEventListener2
{
	static readonly ConVar sv_logecho = new("sv_logecho", "1", FCvar.Archive, "Echo log information to the console.");

	public void FireGameEvent(IGameEvent ev) {
		throw new NotImplementedException();
	}

	public bool IsActive() => Active;
	public void SetLoggingState(bool state) => Active = state;

	bool Active;
}
