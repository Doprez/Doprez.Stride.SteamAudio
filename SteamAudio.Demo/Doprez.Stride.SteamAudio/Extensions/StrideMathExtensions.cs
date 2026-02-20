using NVector3 = System.Numerics.Vector3;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using SteamAudio;

namespace Doprez.Stride.SteamAudio;
/// <summary>
/// Generic extensions for Stride.Core.Mathematics types.
/// </summary>
public static class IPLMathExtensions
{
	static IPLMathExtensions()
	{
		Debug.Assert(
			Unsafe.SizeOf<Vector3>() == Unsafe.SizeOf<IPL.Vector3>(),
			$"Size mismatch: Stride Vector3 ({Unsafe.SizeOf<Vector3>()}) != IPL.Vector3 ({Unsafe.SizeOf<IPL.Vector3>()})");
		Debug.Assert(
			Unsafe.SizeOf<NVector3>() == Unsafe.SizeOf<IPL.Vector3>(),
			$"Size mismatch: System.Numerics Vector3 ({Unsafe.SizeOf<NVector3>()}) != IPL.Vector3 ({Unsafe.SizeOf<IPL.Vector3>()})");
	}

	public static IPL.Vector3 ToIPL(this Vector3 v) => Unsafe.As<Vector3, IPL.Vector3>(ref v);

	public static IPL.Vector3 ToIPL(this NVector3 v) => Unsafe.As<NVector3, IPL.Vector3>(ref v);
}