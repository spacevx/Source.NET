using System;
using System.Collections.Generic;
using System.Text;

namespace Source.Engine;

public partial class SV {
	public static int ModelIndex(ReadOnlySpan<char> name) => sv.LookupModelIndex(name);
	public static int FindOrAddModel(ReadOnlySpan<char> name, bool preload){
		Res flags = Res.FatalIfMissing;
		if (preload) 
			flags |= Res.Preload;

		return sv.PrecacheModel(name, flags);
	}

	public static int DecalIndex(ReadOnlySpan<char> name) => sv.LookupDecalIndex(name);

	public static int FindOrAddDecal(ReadOnlySpan<char> name, bool preload) {
		Res flags = Res.FatalIfMissing;
		if (preload)
			flags |= Res.Preload;

		return sv.PrecacheDecal(name, flags);
	}
}
