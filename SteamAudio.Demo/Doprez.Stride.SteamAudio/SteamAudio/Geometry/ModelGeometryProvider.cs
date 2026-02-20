using Stride.Engine;
using Stride.Rendering;

namespace Doprez.Stride.SteamAudio.Geometry;

/// <summary>
/// Abstract geometry provider for extracting mesh data from Stride's <see cref="ModelComponent"/>.
/// 
/// Reading vertex/index buffers from GPU requires access to the graphics context
/// (CommandList), which is game-specific. Implement this in your game project where
/// you have access to the <c>Game.GraphicsContext.CommandList</c>.
/// 
/// Example implementation:
/// <code>
/// public class MyModelGeometryProvider : ModelGeometryProviderBase
/// {
///     private readonly CommandList _commandList;
///     private readonly IPL.Material _defaultMaterial;
///     
///     public MyModelGeometryProvider(CommandList cmd, IPL.Material defaultMat)
///     {
///         _commandList = cmd;
///         _defaultMaterial = defaultMat;
///     }
///     
///     public override SteamAudioMeshData? ExtractMesh(Entity entity)
///     {
///         var model = entity.Get&lt;ModelComponent&gt;()?.Model;
///         if (model == null) return null;
///         
///         // Read vertex/index buffers using _commandList.GetData&lt;T&gt;()
///         // Transform vertices to world space using entity.Transform.WorldMatrix
///         // Return SteamAudioMeshData with vertices, indices, materials
///     }
/// }
/// </code>
/// </summary>
public abstract class ModelGeometryProviderBase : ISteamAudioGeometryProvider
{
	public virtual bool CanProvide(Entity entity)
	{
		var model = entity.Get<ModelComponent>();
		return model?.Model?.Meshes != null && model.Model.Meshes.Count > 0;
	}

	public abstract SteamAudioMeshData? ExtractMesh(Entity entity);
}
