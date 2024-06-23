using NVector3 = System.Numerics.Vector3;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using SteamAudio;

namespace Doprez.Stride.SteamAudio;
/// <summary>
/// Generic extensions for Stride.Core.Mathematics types.
/// </summary>
public static class IPLMathExtensions
{
	public static IPL.Vector3 ToIPL(this Vector3 v) => Unsafe.As<Vector3, IPL.Vector3>(ref v);

	public static IPL.Vector3 ToIPL(this NVector3 v) => Unsafe.As<NVector3, IPL.Vector3>(ref v);
}