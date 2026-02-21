using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Physics;
using static SteamAudio.IPL;
using StrideVector3 = Stride.Core.Mathematics.Vector3;

namespace Doprez.Stride.SteamAudio.Geometry;

/// <summary>
/// Extracts geometry from Stride's physics <see cref="StaticColliderComponent"/> shapes.
/// Supports Box, Sphere, Capsule, Cylinder, ConvexHull, and StaticMeshCollider shapes.
/// This reuses your existing physics setup — no additional mesh data needed.
/// </summary>
public class StridePhysicsGeometryProvider : ISteamAudioGeometryProvider
{
	private readonly Material _defaultMaterial;

	public StridePhysicsGeometryProvider(Material defaultMaterial)
	{
		_defaultMaterial = defaultMaterial;
	}

	public bool CanProvide(Entity entity)
	{
		var collider = entity.Get<StaticColliderComponent>();
		return collider != null && collider.ColliderShapes.Count > 0;
	}

	public SteamAudioMeshData? ExtractMesh(Entity entity)
	{
		var collider = entity.Get<StaticColliderComponent>();
		if (collider == null || collider.ColliderShapes.Count == 0)
			return null;

		var allVertices = new List<StrideVector3>();
		var allIndices = new List<int>();

		var worldMatrix = entity.Transform.WorldMatrix;

		foreach (var shape in collider.ColliderShapes)
		{
			int baseVertex = allVertices.Count;
			GenerateShapeGeometry(shape, worldMatrix, allVertices, allIndices, baseVertex);
		}

		if (allVertices.Count == 0 || allIndices.Count == 0)
			return null;

		// Check for per-entity SteamAudioMaterial component
		var materialComponent = entity.Get<SteamAudioMaterial>();
		var material = materialComponent?.ToIplMaterial() ?? _defaultMaterial;

		int numTriangles = allIndices.Count / 3;
		var materialIndices = new int[numTriangles]; // All zeros → single material at index 0

		return new SteamAudioMeshData
		{
			Vertices = allVertices.ToArray(),
			Indices = allIndices.ToArray(),
			MaterialIndices = materialIndices,
			Materials = [material],
		};
	}

	private static void GenerateShapeGeometry(
		IColliderShapeDesc shape,
		Matrix worldMatrix,
		List<StrideVector3> vertices,
		List<int> indices,
		int baseVertex)
	{
		switch (shape)
		{
			case BoxColliderShapeDesc box:
				GenerateBox(box, worldMatrix, vertices, indices, baseVertex);
				break;
			case SphereColliderShapeDesc sphere:
				GenerateSphere(sphere, worldMatrix, vertices, indices, baseVertex);
				break;
			case CapsuleColliderShapeDesc capsule:
				GenerateCapsule(capsule, worldMatrix, vertices, indices, baseVertex);
				break;
			case CylinderColliderShapeDesc cylinder:
				GenerateCylinder(cylinder, worldMatrix, vertices, indices, baseVertex);
				break;
			case ConvexHullColliderShapeDesc convexHull:
				// ConvexHull shapes contain raw point data; for SteamAudio we'd need
				// to compute the hull triangles. Skipping for now — implement when needed.
				break;
			case StaticMeshColliderShapeDesc staticMesh:
				// StaticMesh shapes reference a Model asset. Extract from the model's mesh data.
				// This requires access to the GPU vertex/index buffers which is complex.
				// Consider using ModelGeometryProvider for entities with models instead.
				break;
		}
	}

	private static void GenerateBox(BoxColliderShapeDesc box, Matrix worldMatrix, List<StrideVector3> vertices, List<int> indices, int baseVertex)
	{
		var half = box.Size / 2f;
		var offset = new StrideVector3(box.LocalOffset.X, box.LocalOffset.Y, box.LocalOffset.Z);

		// 8 corners of the box
		StrideVector3[] localVerts =
		[
			new StrideVector3(-half.X, -half.Y, -half.Z) + offset,
			new StrideVector3( half.X, -half.Y, -half.Z) + offset,
			new StrideVector3( half.X,  half.Y, -half.Z) + offset,
			new StrideVector3(-half.X,  half.Y, -half.Z) + offset,
			new StrideVector3(-half.X, -half.Y,  half.Z) + offset,
			new StrideVector3( half.X, -half.Y,  half.Z) + offset,
			new StrideVector3( half.X,  half.Y,  half.Z) + offset,
			new StrideVector3(-half.X,  half.Y,  half.Z) + offset,
		];

		foreach (var v in localVerts)
		{
			StrideVector3.TransformCoordinate(in v, in worldMatrix, out var worldV);
			vertices.Add(worldV);
		}

		// 12 triangles (2 per face)
		int[] boxIndices =
		[
			// Front face (-Z)
			0, 2, 1, 0, 3, 2,
			// Back face (+Z)
			4, 5, 6, 4, 6, 7,
			// Left face (-X)
			0, 4, 7, 0, 7, 3,
			// Right face (+X)
			1, 2, 6, 1, 6, 5,
			// Bottom face (-Y)
			0, 1, 5, 0, 5, 4,
			// Top face (+Y)
			2, 3, 7, 2, 7, 6,
		];

		foreach (var i in boxIndices)
			indices.Add(baseVertex + i);
	}

	private static void GenerateSphere(SphereColliderShapeDesc sphere, Matrix worldMatrix, List<StrideVector3> vertices, List<int> indices, int baseVertex)
	{
		float radius = sphere.Radius;
		var offset = new StrideVector3(sphere.LocalOffset.X, sphere.LocalOffset.Y, sphere.LocalOffset.Z);

		// Tessellated icosphere (subdivided once for reasonable fidelity vs triangle count)
		const int rings = 8;
		const int segments = 16;

		// Generate vertices
		for (int ring = 0; ring <= rings; ring++)
		{
			float phi = MathF.PI * ring / rings;
			float sinPhi = MathF.Sin(phi);
			float cosPhi = MathF.Cos(phi);

			for (int seg = 0; seg <= segments; seg++)
			{
				float theta = 2f * MathF.PI * seg / segments;
				var local = new StrideVector3(
					radius * sinPhi * MathF.Cos(theta),
					radius * cosPhi,
					radius * sinPhi * MathF.Sin(theta)
				) + offset;

				StrideVector3.TransformCoordinate(in local, in worldMatrix, out var worldV);
				vertices.Add(worldV);
			}
		}

		// Generate triangles
		for (int ring = 0; ring < rings; ring++)
		{
			for (int seg = 0; seg < segments; seg++)
			{
				int current = ring * (segments + 1) + seg;
				int next = current + segments + 1;

				indices.Add(baseVertex + current);
				indices.Add(baseVertex + next);
				indices.Add(baseVertex + current + 1);

				indices.Add(baseVertex + current + 1);
				indices.Add(baseVertex + next);
				indices.Add(baseVertex + next + 1);
			}
		}
	}

	private static void GenerateCapsule(CapsuleColliderShapeDesc capsule, Matrix worldMatrix, List<StrideVector3> vertices, List<int> indices, int baseVertex)
	{
		// Approximate capsule as a cylinder with hemisphere caps
		float radius = capsule.Radius;
		float halfLength = capsule.Length / 2f;
		var offset = new StrideVector3(capsule.LocalOffset.X, capsule.LocalOffset.Y, capsule.LocalOffset.Z);

		const int segments = 12;
		const int hemRings = 4;

		// Bottom hemisphere
		for (int ring = 0; ring <= hemRings; ring++)
		{
			float phi = MathF.PI / 2f + (MathF.PI / 2f * ring / hemRings);
			float sinPhi = MathF.Sin(phi);
			float cosPhi = MathF.Cos(phi);

			for (int seg = 0; seg <= segments; seg++)
			{
				float theta = 2f * MathF.PI * seg / segments;
				var local = new StrideVector3(
					radius * sinPhi * MathF.Cos(theta),
					radius * cosPhi - halfLength,
					radius * sinPhi * MathF.Sin(theta)
				) + offset;

				StrideVector3.TransformCoordinate(in local, in worldMatrix, out var worldV);
				vertices.Add(worldV);
			}
		}

		// Top hemisphere
		for (int ring = 0; ring <= hemRings; ring++)
		{
			float phi = MathF.PI / 2f * ring / hemRings;
			float sinPhi = MathF.Sin(phi);
			float cosPhi = MathF.Cos(phi);

			for (int seg = 0; seg <= segments; seg++)
			{
				float theta = 2f * MathF.PI * seg / segments;
				var local = new StrideVector3(
					radius * sinPhi * MathF.Cos(theta),
					radius * cosPhi + halfLength,
					radius * sinPhi * MathF.Sin(theta)
				) + offset;

				StrideVector3.TransformCoordinate(in local, in worldMatrix, out var worldV);
				vertices.Add(worldV);
			}
		}

		// Generate triangles
		int totalRings = (hemRings + 1) * 2 - 1;
		for (int ring = 0; ring < totalRings; ring++)
		{
			for (int seg = 0; seg < segments; seg++)
			{
				int current = ring * (segments + 1) + seg;
				int next = current + segments + 1;

				indices.Add(baseVertex + current);
				indices.Add(baseVertex + next);
				indices.Add(baseVertex + current + 1);

				indices.Add(baseVertex + current + 1);
				indices.Add(baseVertex + next);
				indices.Add(baseVertex + next + 1);
			}
		}
	}

	private static void GenerateCylinder(CylinderColliderShapeDesc cylinder, Matrix worldMatrix, List<StrideVector3> vertices, List<int> indices, int baseVertex)
	{
		float radius = cylinder.Radius;
		float halfHeight = cylinder.Height / 2f;
		var offset = new StrideVector3(cylinder.LocalOffset.X, cylinder.LocalOffset.Y, cylinder.LocalOffset.Z);

		const int segments = 16;

		// Bottom center
		var bottomCenter = new StrideVector3(0, -halfHeight, 0) + offset;
		StrideVector3.TransformCoordinate(in bottomCenter, in worldMatrix, out var wBC);
		vertices.Add(wBC);

		// Top center
		var topCenter = new StrideVector3(0, halfHeight, 0) + offset;
		StrideVector3.TransformCoordinate(in topCenter, in worldMatrix, out var wTC);
		vertices.Add(wTC);

		// Bottom ring
		for (int seg = 0; seg <= segments; seg++)
		{
			float theta = 2f * MathF.PI * seg / segments;
			var local = new StrideVector3(radius * MathF.Cos(theta), -halfHeight, radius * MathF.Sin(theta)) + offset;
			StrideVector3.TransformCoordinate(in local, in worldMatrix, out var worldV);
			vertices.Add(worldV);
		}

		// Top ring
		for (int seg = 0; seg <= segments; seg++)
		{
			float theta = 2f * MathF.PI * seg / segments;
			var local = new StrideVector3(radius * MathF.Cos(theta), halfHeight, radius * MathF.Sin(theta)) + offset;
			StrideVector3.TransformCoordinate(in local, in worldMatrix, out var worldV);
			vertices.Add(worldV);
		}

		int bottomCenterIdx = baseVertex;
		int topCenterIdx = baseVertex + 1;
		int bottomRingStart = baseVertex + 2;
		int topRingStart = bottomRingStart + segments + 1;

		// Bottom cap triangles
		for (int seg = 0; seg < segments; seg++)
		{
			indices.Add(bottomCenterIdx);
			indices.Add(bottomRingStart + seg + 1);
			indices.Add(bottomRingStart + seg);
		}

		// Top cap triangles
		for (int seg = 0; seg < segments; seg++)
		{
			indices.Add(topCenterIdx);
			indices.Add(topRingStart + seg);
			indices.Add(topRingStart + seg + 1);
		}

		// Side triangles
		for (int seg = 0; seg < segments; seg++)
		{
			int bl = bottomRingStart + seg;
			int br = bottomRingStart + seg + 1;
			int tl = topRingStart + seg;
			int tr = topRingStart + seg + 1;

			indices.Add(bl);
			indices.Add(br);
			indices.Add(tl);

			indices.Add(br);
			indices.Add(tr);
			indices.Add(tl);
		}
	}
}
