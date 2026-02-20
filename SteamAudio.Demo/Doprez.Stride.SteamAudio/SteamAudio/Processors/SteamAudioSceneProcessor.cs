using Doprez.Stride.SteamAudio.Geometry;
using SteamAudio;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using System.Runtime.InteropServices;
using static SteamAudio.IPL;
using StrideScene = Stride.Engine.Scene;

namespace Doprez.Stride.SteamAudio.Processors;

/// <summary>
/// Processes <see cref="SteamAudioScene"/> components — builds and manages the SteamAudio
/// acoustic scene (geometry + materials) from the Stride scene graph.
/// 
/// Runs before all other SteamAudio processors (Order = 79999) so that the scene
/// is ready when the emitter processor initializes its simulator.
/// </summary>
public unsafe class SteamAudioSceneProcessor : EntityProcessor<SteamAudioScene>
{
	/// <summary>The SteamAudio scene handle, available for other processors.</summary>
	public IPL.Scene IplScene { get; private set; }

	/// <summary>The scene configuration component.</summary>
	public SteamAudioScene? SceneConfig { get; private set; }

	/// <summary>Whether the scene has been built and committed.</summary>
	public bool IsSceneReady { get; private set; }

	// --- Diagnostic counters ---

	/// <summary>Number of IPL static meshes created during scene build.</summary>
	public int DiagStaticMeshCount { get; private set; }

	/// <summary>Total number of vertices across all static meshes.</summary>
	public int DiagTotalVertices { get; private set; }

	/// <summary>Total number of triangles across all static meshes.</summary>
	public int DiagTotalTriangles { get; private set; }

	/// <summary>Number of entities inspected during scene build.</summary>
	public int DiagEntitiesInspected { get; private set; }

	/// <summary>Number of entities that had geometry extracted.</summary>
	public int DiagEntitiesWithGeometry { get; private set; }

	/// <summary>Number of entities skipped (emitter/listener).</summary>
	public int DiagEntitiesSkipped { get; private set; }

	/// <summary>Minimum world-space vertex position across all geometry (AABB min).</summary>
	public global::Stride.Core.Mathematics.Vector3 DiagGeometryMin { get; private set; }

	/// <summary>Maximum world-space vertex position across all geometry (AABB max).</summary>
	public global::Stride.Core.Mathematics.Vector3 DiagGeometryMax { get; private set; }

	/// <summary>Per-entity build log for debugging.</summary>
	public List<string> DiagBuildLog { get; } = [];

	/// <summary>Last error from an IPL creation call, or empty if all succeeded.</summary>
	public string DiagLastError { get; private set; } = "";

	private readonly List<ISteamAudioGeometryProvider> _providers = [];
	private readonly List<IPL.StaticMesh> _staticMeshes = [];
	private IPL.Context _iplContext;

	public SteamAudioSceneProcessor()
	{
		Order = 79999; // Before listener (80000) and emitter (80001) processors
	}

	/// <summary>
	/// Register a custom geometry provider. Providers are tried in order; first one where
	/// <see cref="ISteamAudioGeometryProvider.CanProvide"/> returns true wins.
	/// </summary>
	public void RegisterProvider(ISteamAudioGeometryProvider provider)
	{
		_providers.Add(provider);
	}

	protected override void OnSystemAdd()
	{
		Services.AddService(this);
	}

	protected override void OnEntityComponentAdding(Entity entity, [NotNull] SteamAudioScene component, [NotNull] SteamAudioScene data)
	{
		SceneConfig = component;
	}

	protected override void OnEntityComponentRemoved(Entity entity, [NotNull] SteamAudioScene component, [NotNull] SteamAudioScene data)
	{
		DestroyScene();
		SceneConfig = null;
	}

	protected override void OnSystemRemove()
	{
		DestroyScene();
	}

	/// <summary>
	/// Build the SteamAudio scene from the Stride scene graph.
	/// Called by <see cref="SteamAudioProcessor"/> once the IPL context is available.
	/// </summary>
	public void BuildScene(IPL.Context iplContext, StrideScene strideScene)
	{
		if (IsSceneReady)
			return;

		_iplContext = iplContext;

		if (SceneConfig == null)
			return;

		// Set up providers based on configuration
		if (_providers.Count == 0)
		{
			var defaultMaterial = SteamAudioMaterial.CreateDefault();

			switch (SceneConfig.GeometrySource)
			{
				case GeometrySourceMode.PhysicsColliders:
					_providers.Add(new StridePhysicsGeometryProvider(defaultMaterial));
					break;
				case GeometrySourceMode.ModelMeshes:
					// ModelGeometryProviderBase is abstract — register your own implementation
					// that has access to the graphics context for GPU buffer readback.
					// See ModelGeometryProviderBase documentation for example.
					break;
				case GeometrySourceMode.Custom:
					// User must register providers manually
					break;
			}
		}

		// Create IPL scene
		var sceneSettings = new SceneSettings
		{
			Type = SceneType.Default,
		};
		var sceneErr = SceneCreate(iplContext, in sceneSettings, out var scene);
		IplScene = scene;
		if (sceneErr != IPL.Error.Success)
		{
			DiagLastError = $"SceneCreate failed: {sceneErr}";
			DiagBuildLog.Add(DiagLastError);
			Console.WriteLine($"[SteamAudio] {DiagLastError}");
			return;
		}

		// Reset diagnostic counters
		DiagStaticMeshCount = 0;
		DiagTotalVertices = 0;
		DiagTotalTriangles = 0;
		DiagEntitiesInspected = 0;
		DiagEntitiesWithGeometry = 0;
		DiagEntitiesSkipped = 0;
		DiagGeometryMin = new global::Stride.Core.Mathematics.Vector3(float.MaxValue);
		DiagGeometryMax = new global::Stride.Core.Mathematics.Vector3(float.MinValue);
		DiagLastError = "";
		DiagBuildLog.Clear();

		DiagBuildLog.Add($"Scene build started. Provider count: {_providers.Count}, Root entities: {strideScene.Entities.Count}");

		// Walk the Stride scene and extract geometry
		CollectGeometryRecursive(strideScene.Entities);

		// Commit the scene so SteamAudio builds its acceleration structures
		SceneCommit(IplScene);
		IsSceneReady = true;

		DiagBuildLog.Add($"Scene committed. Meshes={DiagStaticMeshCount} Verts={DiagTotalVertices} Tris={DiagTotalTriangles}");
		DiagBuildLog.Add($"Geometry AABB: ({DiagGeometryMin.X:F1},{DiagGeometryMin.Y:F1},{DiagGeometryMin.Z:F1}) to ({DiagGeometryMax.X:F1},{DiagGeometryMax.Y:F1},{DiagGeometryMax.Z:F1})");

		// Log to console for easy debugging
		foreach (var line in DiagBuildLog)
			Console.WriteLine($"[SteamAudio] {line}");
	}

	/// <summary>
	/// Rebuild the scene (e.g., after dynamic geometry changes).
	/// </summary>
	public void RebuildScene(IPL.Context iplContext, StrideScene strideScene)
	{
		DestroyScene();
		BuildScene(iplContext, strideScene);
	}

	private void CollectGeometryRecursive(IList<Entity> entities)
	{
		foreach (var entity in entities)
		{
			// Skip entities that are SteamAudio emitters or listeners — we only want geometry
			if (entity.Get<SteamAudioEmitter>() != null || entity.Get<SteamAudioListener>() != null)
			{
				DiagEntitiesSkipped++;
				// Still check children
				if (entity.Transform.Children.Count > 0)
				{
					foreach (var child in entity.Transform.Children)
					{
						if (child.Entity != null)
							CollectGeometryFromEntity(child.Entity);
					}
				}
				continue;
			}

			CollectGeometryFromEntity(entity);
		}
	}

	private void CollectGeometryFromEntity(Entity entity)
	{
		DiagEntitiesInspected++;

		foreach (var provider in _providers)
		{
			if (!provider.CanProvide(entity))
				continue;

			var meshData = provider.ExtractMesh(entity);
			if (meshData == null || meshData.NumVertices == 0 || meshData.NumTriangles == 0)
			{
				DiagBuildLog.Add($"  {entity.Name ?? "?"}: provider matched but no mesh data");
				continue;
			}

			DiagEntitiesWithGeometry++;
			DiagTotalVertices += meshData.NumVertices;
			DiagTotalTriangles += meshData.NumTriangles;

			// Track AABB bounds
			var min = DiagGeometryMin;
			var max = DiagGeometryMax;
			foreach (var v in meshData.Vertices)
			{
				if (v.X < min.X) min.X = v.X;
				if (v.Y < min.Y) min.Y = v.Y;
				if (v.Z < min.Z) min.Z = v.Z;
				if (v.X > max.X) max.X = v.X;
				if (v.Y > max.Y) max.Y = v.Y;
				if (v.Z > max.Z) max.Z = v.Z;
			}
			DiagGeometryMin = min;
			DiagGeometryMax = max;

			DiagBuildLog.Add($"  {entity.Name ?? "?"}: {meshData.NumVertices}v {meshData.NumTriangles}t");

			CreateStaticMesh(meshData);
			break; // First provider that can handle this entity wins
		}

		// Recurse into children
		foreach (var child in entity.Transform.Children)
		{
			if (child.Entity != null)
				CollectGeometryFromEntity(child.Entity);
		}
	}

	private void CreateStaticMesh(SteamAudioMeshData meshData)
	{
		// Pin managed arrays and copy to native memory for SteamAudio
		// SteamAudio needs: Vector3[] vertices, Triangle[] triangles, int[] materialIndices, Material[] materials

		// Vertices: convert Stride Vector3 to IPL.Vector3 (same layout due to Unsafe.As compatibility)
		var iplVertices = new IPL.Vector3[meshData.NumVertices];
		for (int i = 0; i < meshData.NumVertices; i++)
		{
			iplVertices[i] = meshData.Vertices[i].ToIPL();
		}

		// Triangles: convert flat index array to IPL.Triangle structs (3 ints each)
		var iplTriangles = new IPL.Triangle[meshData.NumTriangles];
		for (int i = 0; i < meshData.NumTriangles; i++)
		{
			iplTriangles[i].Indices[0] = meshData.Indices[i * 3 + 0];
			iplTriangles[i].Indices[1] = meshData.Indices[i * 3 + 1];
			iplTriangles[i].Indices[2] = meshData.Indices[i * 3 + 2];
		}

		// Allocate native memory
		var verticesPtr = Marshal.AllocHGlobal(iplVertices.Length * sizeof(IPL.Vector3));
		var trianglesPtr = Marshal.AllocHGlobal(iplTriangles.Length * sizeof(IPL.Triangle));
		var materialIndicesPtr = Marshal.AllocHGlobal(meshData.MaterialIndices.Length * sizeof(int));
		var materialsPtr = Marshal.AllocHGlobal(meshData.Materials.Length * sizeof(IPL.Material));

		// Copy data
		fixed (IPL.Vector3* src = iplVertices)
			System.Buffer.MemoryCopy(src, (void*)verticesPtr, iplVertices.Length * sizeof(IPL.Vector3), iplVertices.Length * sizeof(IPL.Vector3));

		fixed (IPL.Triangle* src = iplTriangles)
			System.Buffer.MemoryCopy(src, (void*)trianglesPtr, iplTriangles.Length * sizeof(IPL.Triangle), iplTriangles.Length * sizeof(IPL.Triangle));

		fixed (int* src = meshData.MaterialIndices)
			System.Buffer.MemoryCopy(src, (void*)materialIndicesPtr, meshData.MaterialIndices.Length * sizeof(int), meshData.MaterialIndices.Length * sizeof(int));

		fixed (IPL.Material* src = meshData.Materials)
			System.Buffer.MemoryCopy(src, (void*)materialsPtr, meshData.Materials.Length * sizeof(IPL.Material), meshData.Materials.Length * sizeof(IPL.Material));

		// Verify material data in native memory (read back raw floats to diagnose transmission issues)
		for (int m = 0; m < meshData.Materials.Length; m++)
		{
			var matFloats = (float*)((byte*)materialsPtr + m * sizeof(IPL.Material));
			var matLine = $"  Mat[{m}] sizeof={sizeof(IPL.Material)} Abs=({matFloats[0]:F3},{matFloats[1]:F3},{matFloats[2]:F3}) Scat={matFloats[3]:F3} Trans=({matFloats[4]:F3},{matFloats[5]:F3},{matFloats[6]:F3})";
			DiagBuildLog.Add(matLine);
			Console.WriteLine($"[SteamAudio] {matLine}");
		}

		var settings = new StaticMeshSettings
		{
			NumVertices = meshData.NumVertices,
			NumTriangles = meshData.NumTriangles,
			NumMaterials = meshData.Materials.Length,
			Vertices = verticesPtr,
			Triangles = trianglesPtr,
			MaterialIndices = materialIndicesPtr,
			Materials = materialsPtr,
		};

		var meshErr = StaticMeshCreate(IplScene, in settings, out var staticMesh);
		if (meshErr != IPL.Error.Success)
		{
			DiagLastError = $"StaticMeshCreate failed: {meshErr} (verts={meshData.NumVertices} tris={meshData.NumTriangles})";
			DiagBuildLog.Add(DiagLastError);
			Console.WriteLine($"[SteamAudio] {DiagLastError}");
			Marshal.FreeHGlobal(verticesPtr);
			Marshal.FreeHGlobal(trianglesPtr);
			Marshal.FreeHGlobal(materialIndicesPtr);
			Marshal.FreeHGlobal(materialsPtr);
			return;
		}
		StaticMeshAdd(staticMesh, IplScene);
		_staticMeshes.Add(staticMesh);
		DiagStaticMeshCount++;

		// Free native memory — SteamAudio copies the data during StaticMeshCreate
		Marshal.FreeHGlobal(verticesPtr);
		Marshal.FreeHGlobal(trianglesPtr);
		Marshal.FreeHGlobal(materialIndicesPtr);
		Marshal.FreeHGlobal(materialsPtr);
	}

	private void DestroyScene()
	{
		foreach (var staticMesh in _staticMeshes)
		{
			var mesh = staticMesh;
			StaticMeshRemove(mesh, IplScene);
			StaticMeshRelease(ref mesh);
		}
		_staticMeshes.Clear();

		if (IplScene.Handle != IntPtr.Zero)
		{
			var scene = IplScene;
			SceneRelease(ref scene);
			IplScene = default;
		}

		IsSceneReady = false;
	}
}
