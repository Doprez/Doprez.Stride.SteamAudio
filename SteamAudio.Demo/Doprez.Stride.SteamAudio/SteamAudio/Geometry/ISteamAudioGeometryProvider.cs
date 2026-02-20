using Stride.Engine;
using static SteamAudio.IPL;
using StrideVector3 = Stride.Core.Mathematics.Vector3;

namespace Doprez.Stride.SteamAudio.Geometry;

/// <summary>
/// Abstraction for extracting triangle mesh geometry from Stride entities for use in SteamAudio scene creation.
/// Implement this interface to provide geometry from any source (physics colliders, model meshes, Bepu shapes, etc.).
/// </summary>
public interface ISteamAudioGeometryProvider
{
	/// <summary>
	/// Determines whether this provider can extract geometry from the given entity.
	/// </summary>
	bool CanProvide(Entity entity);

	/// <summary>
	/// Extracts mesh data from the given entity for SteamAudio scene construction.
	/// Returns null if the entity has no usable geometry.
	/// </summary>
	SteamAudioMeshData? ExtractMesh(Entity entity);
}

/// <summary>
/// Holds the extracted triangle mesh data for a single entity, ready to be fed into SteamAudio's StaticMesh.
/// All positions should be in world space.
/// </summary>
public class SteamAudioMeshData
{
	/// <summary>
	/// World-space vertex positions.
	/// </summary>
	public required StrideVector3[] Vertices { get; init; }

	/// <summary>
	/// Triangle indices (every 3 consecutive ints form a triangle).
	/// Each value indexes into <see cref="Vertices"/>.
	/// </summary>
	public required int[] Indices { get; init; }

	/// <summary>
	/// Per-triangle material index into <see cref="Materials"/>.
	/// Length must equal <c>Indices.Length / 3</c>.
	/// </summary>
	public required int[] MaterialIndices { get; init; }

	/// <summary>
	/// Array of acoustic materials used by the triangles in this mesh.
	/// </summary>
	public required Material[] Materials { get; init; }

	public int NumTriangles => Indices.Length / 3;
	public int NumVertices => Vertices.Length;
}
