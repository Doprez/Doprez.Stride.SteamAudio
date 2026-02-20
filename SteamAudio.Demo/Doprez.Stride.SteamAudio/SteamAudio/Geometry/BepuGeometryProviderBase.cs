using Stride.Engine;

namespace Doprez.Stride.SteamAudio.Geometry;

/// <summary>
/// Stub geometry provider for Bepu Physics integration.
/// 
/// Bepu is not included as a dependency in this library. To use Bepu physics shapes
/// as SteamAudio geometry, create your own class that implements <see cref="ISteamAudioGeometryProvider"/>
/// and register it with the <see cref="Processors.SteamAudioSceneProcessor"/>.
/// 
/// Example implementation:
/// <code>
/// public class BepuGeometryProvider : ISteamAudioGeometryProvider
/// {
///     public bool CanProvide(Entity entity)
///     {
///         // Check for your Bepu collider component
///         return entity.Get&lt;MyBepuStaticBody&gt;() != null;
///     }
///     
///     public SteamAudioMeshData? ExtractMesh(Entity entity)
///     {
///         // Extract triangles from Bepu shape (StaticMesh, Compound, etc.)
///         // Transform vertices to world space
///         // Return SteamAudioMeshData with vertices, indices, materials
///     }
/// }
/// </code>
/// 
/// Then register in your game startup:
/// <code>
/// var sceneProcessor = Services.GetService&lt;SteamAudioSceneProcessor&gt;();
/// sceneProcessor.RegisterProvider(new BepuGeometryProvider());
/// </code>
/// </summary>
public abstract class BepuGeometryProviderBase : ISteamAudioGeometryProvider
{
	public abstract bool CanProvide(Entity entity);
	public abstract SteamAudioMeshData? ExtractMesh(Entity entity);
}
