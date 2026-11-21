using System.Runtime.InteropServices;

namespace Source.Engine;

public static class CustomFilename
{
	public static string Get(uint crc) {
		Span<char> hex = stackalloc char[16];
		binarytohex(MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in crc)), hex);
		return $"user_custom/{hex[0]}{hex[1]}/{((ReadOnlySpan<char>)hex).SliceNullTerminatedString()}.dat";
	}
}
