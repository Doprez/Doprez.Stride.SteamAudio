using Doprez.Stride.SteamAudio.Processors;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace Doprez.Stride.SteamAudio;

/// <summary>
/// Defines the geometry source for SteamAudio's acoustic scene.
/// </summary>
public enum GeometrySourceMode
{
	/// <summary>Extract geometry from <see cref="Stride.Physics.StaticColliderComponent"/> shapes.</summary>
	PhysicsColliders,
	/// <summary>Extract geometry from <see cref="Stride.Rendering.ModelComponent"/> meshes.</summary>
	ModelMeshes,
	/// <summary>Use a custom <see cref="Geometry.ISteamAudioGeometryProvider"/> registered at runtime.</summary>
	Custom,
}

/// <summary>
/// Scene-level component that configures SteamAudio's acoustic scene geometry and simulation settings.
/// Place on a single entity in the scene (typically a manager/root entity).
/// </summary>
[DataContract]
[Display("Steam Audio Scene")]
[ComponentCategory("Audio")]
[DefaultEntityComponentProcessor(typeof(SteamAudioSceneProcessor), ExecutionMode = ExecutionMode.Runtime)]
public class SteamAudioScene : EntityComponent
{
	/// <summary>
	/// How to source geometry for the SteamAudio scene.
	/// </summary>
	public GeometrySourceMode GeometrySource { get; set; } = GeometrySourceMode.PhysicsColliders;

	// --- Simulation Settings ---

	/// <summary>Maximum number of occlusion samples per source (higher = more accurate occlusion, more CPU).</summary>
	public int MaxOcclusionSamples { get; set; } = 64;

	/// <summary>Number of rays for reflection simulation (higher = more accurate reflections, more CPU).</summary>
	public int NumRays { get; set; } = 4096;

	/// <summary>Number of bounces for reflection rays.</summary>
	public int NumBounces { get; set; } = 4;

	/// <summary>Number of diffuse samples per bounce for reflection simulation.</summary>
	public int NumDiffuseSamples { get; set; } = 32;

	/// <summary>Maximum impulse response duration in seconds for reflections.</summary>
	public float ReflectionDuration { get; set; } = 1.0f;

	/// <summary>Maximum ambisonics order for reflection IRs (0 = mono, 1 = first-order, 2 = second-order, 3 = third-order).</summary>
	public int AmbisonicsOrder { get; set; } = 1;

	/// <summary>Maximum number of simultaneous audio sources for simulation.</summary>
	public int MaxSources { get; set; } = 32;

	/// <summary>Number of threads for simulation. 0 = auto-detect.</summary>
	public int NumThreads { get; set; } = 0;

	/// <summary>Whether to rebuild the scene geometry when entities change.</summary>
	public bool RebuildOnChange { get; set; } = false;

	/// <summary>Minimum distance (in meters) for irradiance calculation to avoid singularities at very close range.</summary>
	public float IrradianceMinDistance { get; set; } = 1.0f;
}
