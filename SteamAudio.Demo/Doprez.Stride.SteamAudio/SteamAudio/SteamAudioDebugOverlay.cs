using Doprez.Stride.SteamAudio.Processors;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Profiling;
using static SteamAudio.IPL;

namespace Doprez.Stride.SteamAudio;

/// <summary>
/// Debug overlay that renders real-time SteamAudio diagnostic information on screen.
/// Attach to any entity in the scene (typically the same entity as the listener/camera).
/// Shows: scene geometry stats, simulator status, per-emitter occlusion/transmission values,
/// and listener/emitter positions.
/// 
/// Toggle visibility at runtime by setting <see cref="Enabled"/> to false.
/// </summary>
public class SteamAudioDebugOverlay : SyncScript
{
	/// <summary>Whether to show debug text on screen.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Screen X position for the debug text (in pixels from left).</summary>
	public int ScreenX { get; set; } = 10;

	/// <summary>Screen Y position for the debug text (in pixels from top).</summary>
	public int ScreenY { get; set; } = 10;

	private DebugTextSystem? _debugText;
	private SteamAudioSceneProcessor? _sceneProcessor;
	private SteamAudioProcessor? _audioProcessor;

	public override void Start()
	{
		_debugText = Services.GetService<DebugTextSystem>();
	}

	public override void Update()
	{
		if (!Enabled || _debugText == null)
			return;

		// Lazily resolve processors
		_sceneProcessor ??= Services.GetService<SteamAudioSceneProcessor>();
		_audioProcessor ??= Services.GetService<SteamAudioProcessor>();

		int y = ScreenY;
		const int lineHeight = 16;
		var red = new Color(255, 80, 80);
		var green = new Color(80, 255, 80);
		var yellow = new Color(255, 255, 80);
		var white = Color.White;

		// ─── Header + FPS ───
		float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
		float fps = dt > 0 ? 1f / dt : 0f;
		float frameMs = dt * 1000f;
		_debugText.Print($"=== SteamAudio Debug ===  FPS: {fps:F0}  Frame: {frameMs:F1}ms", new Int2(ScreenX, y), white);
		y += lineHeight;

		// ─── Errors ───
		string sceneError = _sceneProcessor?.DiagLastError ?? "";
		string procError = _audioProcessor?.DiagLastError ?? "";
		if (!string.IsNullOrEmpty(sceneError))
		{
			_debugText.Print($"SCENE ERROR: {sceneError}", new Int2(ScreenX, y), red);
			y += lineHeight;
		}
		if (!string.IsNullOrEmpty(procError))
		{
			_debugText.Print($"PROC ERROR: {procError}", new Int2(ScreenX, y), red);
			y += lineHeight;
		}

		// ─── Scene Geometry ───
		if (_sceneProcessor != null)
		{
			var sceneReady = _sceneProcessor.IsSceneReady;
			_debugText.Print($"Scene Ready: {sceneReady}", new Int2(ScreenX, y), sceneReady ? green : red);
			y += lineHeight;

			if (sceneReady)
			{
				int meshCount = _sceneProcessor.DiagStaticMeshCount;
				_debugText.Print($"Static Meshes:  {meshCount}", new Int2(ScreenX, y), meshCount > 0 ? green : red);
				y += lineHeight;
				_debugText.Print($"Verts/Tris:     {_sceneProcessor.DiagTotalVertices}/{_sceneProcessor.DiagTotalTriangles}", new Int2(ScreenX, y));
				y += lineHeight;
				_debugText.Print($"Entities: {_sceneProcessor.DiagEntitiesInspected} inspected, {_sceneProcessor.DiagEntitiesWithGeometry} w/geo, {_sceneProcessor.DiagEntitiesSkipped} skipped", new Int2(ScreenX, y));
				y += lineHeight;

				// Geometry AABB bounds
				var gMin = _sceneProcessor.DiagGeometryMin;
				var gMax = _sceneProcessor.DiagGeometryMax;
				_debugText.Print($"Geo AABB: ({gMin.X:F1},{gMin.Y:F1},{gMin.Z:F1}) to ({gMax.X:F1},{gMax.Y:F1},{gMax.Z:F1})", new Int2(ScreenX, y), yellow);
				y += lineHeight;
			}
			else
			{
				_debugText.Print("Scene NOT built - no occlusion possible", new Int2(ScreenX, y), red);
				y += lineHeight;
			}
		}
		else
		{
			_debugText.Print("Scene Processor: NOT FOUND (add SteamAudioScene component!)", new Int2(ScreenX, y), red);
			y += lineHeight;
		}

		y += lineHeight / 2; // spacer

		// ─── Simulator ───
		if (_audioProcessor != null)
		{
			bool simInit = _audioProcessor.SimulatorInitialized;
			_debugText.Print($"Simulator: {(simInit ? "ACTIVE" : "NOT INITIALIZED")}", new Int2(ScreenX, y), simInit ? green : red);
			y += lineHeight;

			// ─── Listener ───
			var listener = _audioProcessor.Listener;
			if (listener != null)
			{
				var lPos = listener.Entity.Transform.WorldMatrix.TranslationVector;
				_debugText.Print($"Listener: ({lPos.X:F2}, {lPos.Y:F2}, {lPos.Z:F2})", new Int2(ScreenX, y));
				y += lineHeight;
			}
			else
			{
				_debugText.Print("Listener: NULL", new Int2(ScreenX, y), red);
				y += lineHeight;
			}

			y += lineHeight / 2; // spacer

			// ─── Per-Emitter ───
			_debugText.Print($"Emitters: {_audioProcessor.Emitters.Count}", new Int2(ScreenX, y));
			y += lineHeight;

			foreach (var emitter in _audioProcessor.Emitters)
			{
				var ePos = emitter.Entity.Transform.WorldMatrix.TranslationVector;
				string name = emitter.Entity.Name ?? "Unnamed";

				// Distance to listener
				float dist = 0f;
				if (listener != null)
				{
					var lPos = listener.Entity.Transform.WorldMatrix.TranslationVector;
					dist = (ePos - lPos).Length();
				}

				_debugText.Print($"--- {name} (dist: {dist:F1}m) ---", new Int2(ScreenX, y), yellow);
				y += lineHeight;
				_debugText.Print($"  Pos: ({ePos.X:F1}, {ePos.Y:F1}, {ePos.Z:F1})", new Int2(ScreenX, y));
				y += lineHeight;
				_debugText.Print($"  IsSimSource: {emitter.IsSimulatorSource}", new Int2(ScreenX, y), emitter.IsSimulatorSource ? green : red);
				y += lineHeight;

				if (emitter.IsSimulatorSource)
				{
					var direct = emitter.CachedSimulationOutputs.Direct;

					// Color-code occlusion: green=unoccluded, red=occluded, yellow=partial
					float occ = direct.Occlusion;
					var occColor = occ > 0.9f ? green : occ < 0.1f ? red : yellow;
					_debugText.Print($"  DistAtten:    {direct.DistanceAttenuation:F4}", new Int2(ScreenX, y));
					y += lineHeight;
					_debugText.Print($"  Occlusion:    {occ:F4}  (1=clear, 0=blocked)", new Int2(ScreenX, y), occColor);
					y += lineHeight;

					unsafe
					{
						bool isFallback = emitter.EnableTransmission &&
							occ < 0.999f &&
							direct.Transmission[0] >= 0.999f &&
							direct.Transmission[1] >= 0.999f &&
							direct.Transmission[2] >= 0.999f;
						var transColor = isFallback ? red : white;
						string fallbackTag = isFallback ? " [FALLBACK]" : "";
						_debugText.Print($"  Transmission: ({direct.Transmission[0]:F4}, {direct.Transmission[1]:F4}, {direct.Transmission[2]:F4}){fallbackTag}", new Int2(ScreenX, y), transColor);
						y += lineHeight;

						// Air absorption
						if (emitter.EnableAirAbsorption)
						{
							_debugText.Print($"  AirAbsorb:    ({direct.AirAbsorption[0]:F4}, {direct.AirAbsorption[1]:F4}, {direct.AirAbsorption[2]:F4})", new Int2(ScreenX, y), yellow);
							y += lineHeight;
						}
					}

					_debugText.Print($"  DirectSimFlags: Occ={emitter.EnableOcclusion} Trans={emitter.EnableTransmission} Air={emitter.EnableAirAbsorption}", new Int2(ScreenX, y));
					y += lineHeight;

					// Reflections status
					if (emitter.EnableReflections)
					{
						var reflColor = emitter.HasReflectionEffect ? green : red;
						_debugText.Print($"  Reflections:  order={emitter.ReflectionAmbisonicsOrder} ch={(emitter.ReflectionAmbisonicsOrder+1)*(emitter.ReflectionAmbisonicsOrder+1)} active={emitter.HasReflectionEffect}", new Int2(ScreenX, y), reflColor);
						y += lineHeight;
					}

					// Pathing status
					if (emitter.EnablePathing)
					{
						var pathColor = emitter.HasPathEffect ? green : red;
						_debugText.Print($"  Pathing:      order={emitter.PathingOrder} active={emitter.HasPathEffect}", new Int2(ScreenX, y), pathColor);
						y += lineHeight;
					}

					// HRTF info
					string hrtfType = string.IsNullOrEmpty(emitter.SofaFilePath) ? "Default" : "SOFA";
					_debugText.Print($"  HRTF: {hrtfType}  Interp={emitter.HrtfInterpolation}  Norm={emitter.HrtfNormType}", new Int2(ScreenX, y));
					y += lineHeight;

					_debugText.Print($"  EffectFlags: {emitter.GetDirectEffectFlags()}  OccType={emitter.OcclusionType} TransRays={emitter.NumTransmissionRays}", new Int2(ScreenX, y));
					y += lineHeight;
					// Show raw DirectFlags int and raw output flags for diagnosing transmission issue
					_debugText.Print($"  RawOutFlags: 0x{(int)direct.Flags:X2}  RawTransType: {(int)direct.TransmissionType}", new Int2(ScreenX, y));
					y += lineHeight;
				}
				else
				{
					_debugText.Print("  NOT a simulator source - using fallback (no occlusion)", new Int2(ScreenX, y), red);
					y += lineHeight;
				}
			}
		}
		else
		{
			_debugText.Print("Audio Processor: NOT FOUND", new Int2(ScreenX, y), red);
			y += lineHeight;
		}

		// ─── Build Log (last 5 lines) ───
		if (_sceneProcessor != null && _sceneProcessor.DiagBuildLog.Count > 0)
		{
			y += lineHeight / 2;
			_debugText.Print("--- Build Log ---", new Int2(ScreenX, y), yellow);
			y += lineHeight;
			int start = Math.Max(0, _sceneProcessor.DiagBuildLog.Count - 8);
			for (int i = start; i < _sceneProcessor.DiagBuildLog.Count; i++)
			{
				_debugText.Print(_sceneProcessor.DiagBuildLog[i], new Int2(ScreenX, y));
				y += lineHeight;
			}
		}
	}
}
