using Stride.Core;
using Stride.Engine;
using static SteamAudio.IPL;

namespace Doprez.Stride.SteamAudio;

/// <summary>
/// Per-entity acoustic material properties for SteamAudio.
/// Attach to any entity that also has geometry (physics collider or model) to 
/// define how sound interacts with its surfaces — absorption, scattering, and transmission
/// across low/mid/high frequency bands.
/// 
/// If this component is absent, the scene-level default material is used.
/// </summary>
[DataContract]
[Display("Steam Audio Material")]
[ComponentCategory("Audio")]
public class SteamAudioMaterial : EntityComponent
{
	/// <summary>Low-frequency absorption coefficient (0 = fully reflective, 1 = fully absorptive).</summary>
	public float AbsorptionLow { get; set; } = 0.10f;

	/// <summary>Mid-frequency absorption coefficient.</summary>
	public float AbsorptionMid { get; set; } = 0.20f;

	/// <summary>High-frequency absorption coefficient.</summary>
	public float AbsorptionHigh { get; set; } = 0.30f;

	/// <summary>Scattering coefficient (0 = specular, 1 = fully diffuse).</summary>
	public float Scattering { get; set; } = 0.05f;

	/// <summary>Low-frequency transmission coefficient (0 = fully blocks, 1 = fully transmits).</summary>
	public float TransmissionLow { get; set; } = 0.10f;

	/// <summary>Mid-frequency transmission coefficient.</summary>
	public float TransmissionMid { get; set; } = 0.05f;

	/// <summary>High-frequency transmission coefficient.</summary>
	public float TransmissionHigh { get; set; } = 0.03f;

	/// <summary>
	/// Converts this component's properties into a SteamAudio <see cref="Material"/> struct.
	/// </summary>
	public unsafe Material ToIplMaterial()
	{
		var material = new Material
		{
			Scattering = Scattering,
		};

		material.Absorption[0] = AbsorptionLow;
		material.Absorption[1] = AbsorptionMid;
		material.Absorption[2] = AbsorptionHigh;

		material.Transmission[0] = TransmissionLow;
		material.Transmission[1] = TransmissionMid;
		material.Transmission[2] = TransmissionHigh;

		return material;
	}

	/// <summary>
	/// Creates a default acoustic material suitable for generic hard surfaces (concrete/plaster).
	/// </summary>
	public static unsafe Material CreateDefault()
	{
		var material = new Material
		{
			Scattering = 0.05f,
		};

		material.Absorption[0] = 0.10f;
		material.Absorption[1] = 0.20f;
		material.Absorption[2] = 0.30f;

		material.Transmission[0] = 0.10f;
		material.Transmission[1] = 0.05f;
		material.Transmission[2] = 0.03f;

		return material;
	}
}
