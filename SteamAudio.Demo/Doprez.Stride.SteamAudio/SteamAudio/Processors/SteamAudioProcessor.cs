using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using SteamAudio;
using Stride.Core.Annotations;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Profiling;
using System.Runtime.CompilerServices;
using static SteamAudio.IPL;

namespace Doprez.Stride.SteamAudio.Processors;

/// <summary>
/// Main audio processor that drives the SteamAudio pipeline each frame.
/// 
/// When a <see cref="SteamAudioScene"/> component exists in the scene, this processor
/// creates a full SteamAudio Simulator for physics-based audio (occlusion, transmission,
/// air absorption, directivity, reflections). Without a scene component, it falls back
/// to the basic binaural + distance attenuation pipeline.
/// </summary>
public unsafe class SteamAudioProcessor : EntityProcessor<SteamAudioEmitter>
{
	public List<SteamAudioEmitter> Emitters = [];
	public SteamAudioListener? Listener;
	public DebugTextSystem? DebugText;

	private OpenAlConfiguration _openAlConfiguration = null!;
	private SteamAudioListenerProcessor? _listenerProcessor;
	private SteamAudioSceneProcessor? _sceneProcessor;
	private ContentManager _contentManager = null!;

	private IPL.Context _iplContext;
	private IPL.Simulator _iplSimulator;
	private bool _sceneBuilt;

	/// <summary>Whether the SteamAudio simulator has been created and is actively running.</summary>
	public bool SimulatorInitialized { get; private set; }

	/// <summary>Last error message from IPL creation calls, or empty if all OK.</summary>
	public string DiagLastError { get; private set; } = "";

	public SteamAudioProcessor()
	{
		Order = 80001;

		// Steam Audio Initialization
		var contextSettings = new ContextSettings
		{
			Version = IPL.Version,
		};
		var ctxErr = ContextCreate(in contextSettings, out _iplContext);
		if (ctxErr != IPL.Error.Success)
		{
			DiagLastError = $"ContextCreate failed: {ctxErr}";
			Console.WriteLine($"[SteamAudio] {DiagLastError}");
		}
	}

	public override void Update(GameTime time)
	{
		// Lazily resolve listener if it wasn't available at startup
		if (Listener == null)
		{
			_listenerProcessor ??= Services.GetService<SteamAudioListenerProcessor>();
			Listener = _listenerProcessor?.Listener;
		}

		// Detect scene transitions: if the scene was previously built but the scene processor
		// was removed or its scene destroyed (e.g., scene switch), tear down the simulator
		// so it can be rebuilt with the new scene's geometry.
		if (_sceneBuilt)
		{
			_sceneProcessor ??= Services.GetService<SteamAudioSceneProcessor>();
			if (_sceneProcessor == null || !_sceneProcessor.IsSceneReady)
			{
				Console.WriteLine("[SteamAudio] Scene transition detected — releasing simulator for rebuild.");

				// Remove all emitters from the old simulator
				foreach (var emitter in Emitters)
				{
					if (emitter.IsSimulatorSource && SimulatorInitialized)
					{
						emitter.RemoveFromSimulator(_iplSimulator, _iplContext);
					}
				}

				if (SimulatorInitialized)
				{
					SimulatorRelease(ref _iplSimulator);
					SimulatorInitialized = false;
				}

				_sceneBuilt = false;
				_sceneProcessor = null;
			}
		}

		// Lazily resolve scene processor and build scene + simulator
		if (!_sceneBuilt)
		{
			_sceneProcessor ??= Services.GetService<SteamAudioSceneProcessor>();
			TryBuildSceneAndSimulator();
		}

		// Run simulator if available
		if (SimulatorInitialized && Listener != null)
		{
			RunSimulation();
		}

		foreach (var emitter in ComponentDatas.Values)
		{
			PlayAudio(emitter);
		}
	}

	protected override void OnSystemAdd()
	{
		_openAlConfiguration = new OpenAlConfiguration();
		_contentManager = Services.GetService<ContentManager>();

		_listenerProcessor = Services.GetService<SteamAudioListenerProcessor>();
		Listener = _listenerProcessor?.Listener;

		_sceneProcessor = Services.GetService<SteamAudioSceneProcessor>();

		DebugText = Services.GetService<DebugTextSystem>();

		// Register so debug overlay and other scripts can find us
		Services.AddService(this);
	}

	protected override void OnSystemRemove()
	{
		// Clean up all active emitters before destroying shared resources
		foreach (var emitter in Emitters)
		{
			if (emitter.IsSimulatorSource && SimulatorInitialized)
			{
				emitter.RemoveFromSimulator(_iplSimulator, _iplContext);
			}
			emitter.Dispose(_iplContext, _openAlConfiguration);
		}
		Emitters.Clear();

		// Release simulator
		if (SimulatorInitialized)
		{
			SimulatorRelease(ref _iplSimulator);
			SimulatorInitialized = false;
		}

		_openAlConfiguration.Dispose();
		ContextRelease(ref _iplContext);
	}

	protected override void OnEntityComponentAdding(Entity entity, [NotNull] SteamAudioEmitter component, [NotNull] SteamAudioEmitter data)
	{
		Emitters.Add(component);
		var stream = _contentManager.OpenAsStream(component.RawFileSource.Url, StreamFlags.Seekable);
		component.Initialize(_iplContext, _openAlConfiguration, stream);

		// Register with simulator if already initialized
		if (SimulatorInitialized)
		{
			component.InitializeSimulatorSource(_iplSimulator, _iplContext);
			SimulatorCommit(_iplSimulator);
		}
	}

	protected override void OnEntityComponentRemoved(Entity entity, [NotNull] SteamAudioEmitter component, [NotNull] SteamAudioEmitter data)
	{
		Emitters.Remove(component);

		if (component.IsSimulatorSource && SimulatorInitialized)
		{
			component.RemoveFromSimulator(_iplSimulator, _iplContext);
			SimulatorCommit(_iplSimulator);
		}

		component.Dispose(_iplContext, _openAlConfiguration);
	}

	private void TryBuildSceneAndSimulator()
	{
		if (_sceneProcessor?.SceneConfig == null)
			return;

		// The scene processor creates the geometry; we trigger it here since we own the IPL context
		var strideScene = _sceneProcessor.SceneConfig.Entity?.Scene;
		if (strideScene == null)
			return;

		_sceneProcessor.BuildScene(_iplContext, strideScene);

		if (!_sceneProcessor.IsSceneReady)
			return;

		// Create the simulator
		var sceneConfig = _sceneProcessor.SceneConfig;
		var simulationSettings = new SimulationSettings
		{
			Flags = SimulationFlags.Direct | SimulationFlags.Reflections | SimulationFlags.Pathing,
			SceneType = SceneType.Default,
			ReflectionType = ReflectionEffectType.Convolution,
			MaxNumOcclusionSamples = sceneConfig.MaxOcclusionSamples,
			MaxNumRays = sceneConfig.NumRays,
			NumDiffuseSamples = sceneConfig.NumDiffuseSamples,
			MaxDuration = sceneConfig.ReflectionDuration,
			MaxOrder = sceneConfig.AmbisonicsOrder,
			MaxNumSources = sceneConfig.MaxSources,
			NumThreads = sceneConfig.NumThreads > 0 ? sceneConfig.NumThreads : Environment.ProcessorCount,
			SamplingRate = 44100,
			FrameSize = 4096,
		};

		var simErr = SimulatorCreate(_iplContext, in simulationSettings, out _iplSimulator);
		if (simErr != IPL.Error.Success)
		{
			DiagLastError = $"SimulatorCreate failed: {simErr}";
			Console.WriteLine($"[SteamAudio] {DiagLastError}");
			return;
		}
		SimulatorSetScene(_iplSimulator, _sceneProcessor.IplScene);
		SimulatorCommit(_iplSimulator);

		Console.WriteLine($"[SteamAudio] Simulator created. Scene meshes={_sceneProcessor.DiagStaticMeshCount}");

		// Register all existing emitters with the simulator
		foreach (var emitter in Emitters)
		{
			if (!emitter.IsSimulatorSource)
			{
				emitter.InitializeSimulatorSource(_iplSimulator, _iplContext);
				Console.WriteLine($"[SteamAudio] Registered emitter '{emitter.Entity?.Name}' IsSimSource={emitter.IsSimulatorSource}");
			}
		}
		SimulatorCommit(_iplSimulator);

		SimulatorInitialized = true;
		_sceneBuilt = true;
	}

	private void RunSimulation()
	{
		if (Listener == null)
			return;

		var listenerPosition = Listener.Entity.Transform.WorldMatrix.TranslationVector;
		var listenerForward = Listener.Entity.Transform.WorldMatrix.Forward;
		var listenerUp = Listener.Entity.Transform.WorldMatrix.Up;
		var listenerRight = Listener.Entity.Transform.WorldMatrix.Right;

		// Set shared inputs (listener position, ray tracing configuration)
		var sceneConfig = _sceneProcessor?.SceneConfig;
		var sharedInputs = new SimulationSharedInputs
		{
			Listener = new CoordinateSpace3
			{
				Origin = listenerPosition.ToIPL(),
				Ahead = listenerForward.ToIPL(),
				Up = listenerUp.ToIPL(),
				Right = listenerRight.ToIPL(),
			},
			NumRays = sceneConfig?.NumRays ?? 4096,
			NumBounces = sceneConfig?.NumBounces ?? 4,
			Duration = sceneConfig?.ReflectionDuration ?? 1.0f,
			Order = sceneConfig?.AmbisonicsOrder ?? 1,
			IrradianceMinDistance = sceneConfig?.IrradianceMinDistance ?? 1.0f,
		};

		var sharedFlags = SimulationFlags.Direct;
		// Only run reflections/pathing if any emitter wants them
		bool anyReflections = false;
		bool anyPathing = false;
		foreach (var emitter in Emitters)
		{
			if (!emitter.IsSimulatorSource)
				continue;

			var flags = SimulationFlags.Direct;
			if (emitter.EnableReflections)
			{
				flags |= SimulationFlags.Reflections;
				anyReflections = true;
			}
			if (emitter.EnablePathing)
			{
				flags |= SimulationFlags.Pathing;
				anyPathing = true;
			}

			emitter.SetSimulationInputs(flags);
		}

		if (anyReflections)
			sharedFlags |= SimulationFlags.Reflections;
		if (anyPathing)
			sharedFlags |= SimulationFlags.Pathing;

		SimulatorSetSharedInputs(_iplSimulator, sharedFlags, in sharedInputs);

		// Run the simulation
		SimulatorRunDirect(_iplSimulator);

		if (anyReflections)
		{
			SimulatorRunReflections(_iplSimulator);
		}

		if (anyPathing)
		{
			SimulatorRunPathing(_iplSimulator);
		}

		// Read outputs for each emitter
		foreach (var emitter in Emitters)
		{
			if (!emitter.IsSimulatorSource)
				continue;

			var outputFlags = SimulationFlags.Direct;
			if (emitter.EnableReflections)
				outputFlags |= SimulationFlags.Reflections;
			if (emitter.EnablePathing)
				outputFlags |= SimulationFlags.Pathing;

			SourceGetOutputs(emitter.IplSource, outputFlags, out emitter.CachedSimulationOutputs);
		}
	}

	private void PlayAudio(SteamAudioEmitter emitter)
	{
		if (Listener == null) return;

		var al = _openAlConfiguration.Al;

		// Unqueue and refill processed buffers
		al.GetSourceProperty(emitter.AlSourceId, GetSourceInteger.BuffersProcessed, out int numProcessedBuffers);
		al.GetSourceProperty(emitter.AlSourceId, GetSourceInteger.BuffersQueued, out int numQueuedBuffers);

		while (numProcessedBuffers > 0)
		{
			uint bufferId;
			al.SourceUnqueueBuffers(emitter.AlSourceId, 1, &bufferId);
			StreamBuffer(bufferId, emitter);
			al.SourceQueueBuffers(emitter.AlSourceId, 1, &bufferId);
			numProcessedBuffers--;
		}

		// Initial fill: queue any buffers that haven't been queued yet
		for (int i = numQueuedBuffers; i < SteamAudioEmitter.NumBuffers; i++)
		{
			uint bufferId = emitter.AlBufferIds[i];
			StreamBuffer(bufferId, emitter);
			al.SourceQueueBuffers(emitter.AlSourceId, 1, &bufferId);
		}

		// Start playback whenever it stops (initial start or buffer underrun recovery)
		al.GetSourceProperty(emitter.AlSourceId, GetSourceInteger.SourceState, out int sourceStateInt);

		if ((SourceState)sourceStateInt != SourceState.Playing)
		{
			al.SourcePlay(emitter.AlSourceId);
		}

		emitter.CurrentStreamPosition = TimeSpan.FromSeconds((int)(emitter.AudioStream.Position / sizeof(float) / emitter.SampleRate));

		CheckALErrors();
	}

	private void StreamBuffer(uint bufferId, SteamAudioEmitter emitter)
	{
		var audioStream = emitter.AudioStream;

		var emitterPosition = emitter.Entity.Transform.WorldMatrix.TranslationVector;
		var listenerPosition = Listener!.Entity.Transform.WorldMatrix.TranslationVector;
		var listenerForward = Listener.Entity.Transform.WorldMatrix.Forward;
		var listenerUp = Listener.Entity.Transform.WorldMatrix.Up;

		var iplDir = IPL.CalculateRelativeDirection(_iplContext, emitterPosition.ToIPL(), listenerPosition.ToIPL(), listenerForward.ToIPL(), listenerUp.ToIPL());

		float* inputBufferChannelPtr = ((float**)emitter.IplInputBuffer.Data)[0];
		var inputBufferByteSpan = new Span<byte>(inputBufferChannelPtr, emitter.FrameSizeInBytes);

		int bytesRead = audioStream.Read(inputBufferByteSpan);

		// Loop the audio on stream end.
		if (bytesRead < emitter.FrameSizeInBytes)
		{
			audioStream.Position = 0;
			audioStream.Read(inputBufferByteSpan[..(emitter.FrameSizeInBytes - bytesRead)]);
		}

		// Apply HRTF binaural spatialization (mono ? stereo)
		var binauralEffectParams = new IPL.BinauralEffectParams
		{
			Hrtf = emitter.IplHrtf,
			Direction = iplDir,
			Interpolation = emitter.HrtfInterpolation,
			SpatialBlend = 1f,
		};

		IPL.BinauralEffectApply(emitter.IplBinauralEffect, ref binauralEffectParams, ref emitter.IplInputBuffer, ref emitter.IplOutputBuffer);

		// Apply direct effects (distance attenuation + simulation-driven occlusion/transmission/air absorption/directivity)
		if (emitter.IsSimulatorSource)
		{
			// Use simulation outputs � the simulator has already computed all enabled direct effects
			var directParams = emitter.CachedSimulationOutputs.Direct;
			directParams.Flags = emitter.GetDirectEffectFlags();
			directParams.TransmissionType = emitter.TransmissionType;

			// Workaround: The SteamAudio.NET binding has a bool alignment bug that causes
			// NumTransmissionRays to be read as 0 by the native code, so the simulator
			// always returns transmission (1,1,1). We interpolate fallback values based
			// on the occlusion factor so the transition is gradual rather than a hard toggle.
			// When occlusion=1.0 (clear): transmission stays at 1.0 (full pass-through).
			// When occlusion=0.0 (fully blocked): transmission = fallback minimum values.
			unsafe
			{
				bool needsFallback = emitter.EnableTransmission &&
					directParams.Occlusion < 0.999f &&
					directParams.Transmission[0] >= 0.999f &&
					directParams.Transmission[1] >= 0.999f &&
					directParams.Transmission[2] >= 0.999f;

				if (needsFallback)
				{
					// Lerp: at occlusion=1 → transmission=1, at occlusion=0 → transmission=fallback
					float occ = directParams.Occlusion;
					directParams.Transmission[0] = occ + (1f - occ) * emitter.FallbackTransmissionLow;
					directParams.Transmission[1] = occ + (1f - occ) * emitter.FallbackTransmissionMid;
					directParams.Transmission[2] = occ + (1f - occ) * emitter.FallbackTransmissionHigh;
				}

				// One-time diagnostic: log raw simulation output
				if (!emitter.DiagOutputLogged && directParams.Occlusion < 0.999f)
				{
					emitter.DiagOutputLogged = true;
					Console.WriteLine($"[SteamAudio] {emitter.Entity?.Name} FIRST OCCLUDED OUTPUT: Occ={directParams.Occlusion:F4} Trans=({directParams.Transmission[0]:F4},{directParams.Transmission[1]:F4},{directParams.Transmission[2]:F4}) DistAtt={directParams.DistanceAttenuation:F4} Fallback={needsFallback}");
				}
			}

			IPL.DirectEffectApply(emitter.IplDirectEffect, ref directParams, ref emitter.IplOutputBuffer, ref emitter.IplOutputBuffer);
		}
		else
		{
			// Fallback: manual distance attenuation only (no scene ? no occlusion/transmission)
			var volume = IPL.DistanceAttenuationCalculate(_iplContext, emitterPosition.ToIPL(), listenerPosition.ToIPL(), emitter.IplDistanceAttenuationModel);
			var directEffectParams = new IPL.DirectEffectParams
			{
				Flags = IPL.DirectEffectFlags.ApplyDistanceAttenuation,
				DistanceAttenuation = volume,
			};
			IPL.DirectEffectApply(emitter.IplDirectEffect, ref directEffectParams, ref emitter.IplOutputBuffer, ref emitter.IplOutputBuffer);
		}

		// Apply reflection effect if enabled and available
		if (emitter.HasReflectionEffect && emitter.IsSimulatorSource)
		{
			var reflectionParams = emitter.CachedSimulationOutputs.Reflections;

			// Step 1: Convolve mono input with reflection IR → ambisonics buffer
			IPL.ReflectionEffectApply(emitter.IplReflectionEffect, ref reflectionParams, ref emitter.IplInputBuffer, ref emitter.IplReflectionOutputBuffer, default);

			// Step 2: Decode ambisonics → binaural stereo
			var ambiParams = new IPL.AmbisonicsBinauralEffectParams
			{
				Hrtf = emitter.IplHrtf,
				Order = emitter.ReflectionAmbisonicsOrder,
			};
			IPL.AmbisonicsBinauralEffectApply(emitter.IplAmbisonicsBinauralEffect, ref ambiParams, ref emitter.IplReflectionOutputBuffer, ref emitter.IplReflectionStereoBuffer);

			// Step 3: Mix decoded stereo reflections into the output buffer
			MixAudioBuffers(emitter.IplReflectionStereoBuffer, ref emitter.IplOutputBuffer);
		}

		// Apply pathing effect if enabled and available
		if (emitter.HasPathEffect && emitter.IsSimulatorSource)
		{
			var pathParams = emitter.CachedSimulationOutputs.Pathing;
			pathParams.Order = emitter.PathingOrder;
			pathParams.Binaural = true;
			pathParams.Hrtf = emitter.IplHrtf;
			pathParams.Listener = new IPL.CoordinateSpace3
			{
				Origin = listenerPosition.ToIPL(),
				Ahead = Listener!.Entity.Transform.WorldMatrix.Forward.ToIPL(),
				Up = listenerUp.ToIPL(),
				Right = Listener.Entity.Transform.WorldMatrix.Right.ToIPL(),
			};

			IPL.PathEffectApply(emitter.IplPathEffect, ref pathParams, ref emitter.IplInputBuffer, ref emitter.IplPathOutputBuffer);

			// Mix pathing into the output buffer
			MixAudioBuffers(emitter.IplPathOutputBuffer, ref emitter.IplOutputBuffer);
		}

		IPL.AudioBufferInterleave(_iplContext, in emitter.IplOutputBuffer, in Unsafe.AsRef<float>((void*)emitter.InterlacingBuffer));

		_openAlConfiguration.Al.BufferData(bufferId, (BufferFormat)FloatBufferFormat.Stereo, (void*)emitter.InterlacingBuffer, emitter.FrameSizeInBytes * 2, emitter.IplAudioSettings.SamplingRate);

		CheckALErrors();
	}

	/// <summary>
	/// Simple additive mix of source AudioBuffer into destination AudioBuffer.
	/// Both buffers must have the same number of channels and frame size.
	/// </summary>
	private static void MixAudioBuffers(AudioBuffer source, ref AudioBuffer destination)
	{
		int numChannels = Math.Min(source.NumChannels, destination.NumChannels);
		int frameSize = Math.Min(source.NumSamples, destination.NumSamples);

		for (int ch = 0; ch < numChannels; ch++)
		{
			float* srcPtr = ((float**)source.Data)[ch];
			float* dstPtr = ((float**)destination.Data)[ch];

			for (int i = 0; i < frameSize; i++)
			{
				dstPtr[i] += srcPtr[i];
			}
		}
	}

	private void CheckALErrors()
	{
		var error = _openAlConfiguration.Al.GetError();

		if (error != AudioError.NoError)
		{
			throw new Exception($"OpenAL Error: {error}");
		}
	}
}
