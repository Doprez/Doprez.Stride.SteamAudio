using Doprez.Stride.SteamAudio.Processors;
using SteamAudio;
using Stride.Core;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Engine.Design;
using System.Runtime.InteropServices;
using static SteamAudio.IPL;

namespace Doprez.Stride.SteamAudio;
[DataContract]
[Display("Steam Audio Emitter")]
[ComponentCategory("Audio")]
[DefaultEntityComponentProcessor(typeof(SteamAudioProcessor), ExecutionMode = ExecutionMode.Runtime)]
public class SteamAudioEmitter : EntityComponent
{
	public const int NumBuffers = 2;

	// --- Serialized properties (editor-visible) ---

	public UrlReference RawFileSource { get; set; }

	public int SampleRate { get; set; } = 44100;
	public int FrameSize { get; set; } = 4096;
	public float Volume { get; set; } = 1.0f;

	// --- Direct sound simulation options ---

	/// <summary>Enable occlusion (sound blocked by geometry). Requires a SteamAudioScene in the scene.</summary>
	public bool EnableOcclusion { get; set; } = false;

	/// <summary>Enable transmission (sound passing through walls, frequency-filtered by material).</summary>
	public bool EnableTransmission { get; set; } = false;

	/// <summary>Transmission type when EnableTransmission is true.</summary>
	public TransmissionType TransmissionType { get; set; } = TransmissionType.FrequencyDependent;

	/// <summary>Enable frequency-dependent air absorption (high frequencies attenuate faster over distance).</summary>
	public bool EnableAirAbsorption { get; set; } = false;

	/// <summary>Enable directivity pattern (sound emits directionally like a megaphone).</summary>
	public bool EnableDirectivity { get; set; } = false;

	/// <summary>Dipole weight for directivity (0 = omnidirectional, 1 = full dipole). Only used when EnableDirectivity is true.</summary>
	public float DirectivityDipoleWeight { get; set; } = 0.5f;

	/// <summary>Dipole power for directivity (controls sharpness of the pattern). Only used when EnableDirectivity is true.</summary>
	public float DirectivityDipolePower { get; set; } = 2.0f;

	/// <summary>Occlusion type: Raycast (single ray, fast) or Volumetric (multiple samples, softer transitions). Volumetric is required for accurate transmission computation.</summary>
	public OcclusionType OcclusionType { get; set; } = OcclusionType.Volumetric;

	/// <summary>Number of samples for volumetric occlusion (higher = smoother, more CPU). Used when OcclusionType is Volumetric.</summary>
	public int NumOcclusionSamples { get; set; } = 64;

	/// <summary>Radius for volumetric occlusion. Only used when OcclusionType is Volumetric.</summary>
	public float OcclusionRadius { get; set; } = 1.0f;

	/// <summary>Number of rays for transmission calculation.</summary>
	public int NumTransmissionRays { get; set; } = 16;

	// --- Fallback transmission (used when simulator returns bogus 1,1,1) ---

	/// <summary>Low-frequency fallback transmission (0 = fully blocks, 1 = fully transmits). Used when the simulator fails to compute transmission.</summary>
	public float FallbackTransmissionLow { get; set; } = 0.10f;

	/// <summary>Mid-frequency fallback transmission.</summary>
	public float FallbackTransmissionMid { get; set; } = 0.05f;

	/// <summary>High-frequency fallback transmission.</summary>
	public float FallbackTransmissionHigh { get; set; } = 0.03f;

	// --- Reflection simulation options ---

	/// <summary>Enable reflections (early reflections + late reverb computed from scene geometry).</summary>
	public bool EnableReflections { get; set; } = false;

	// --- Runtime state (not serialized) ---

	[DataMemberIgnore]
	public TimeSpan CurrentStreamPosition { get; set; }
	[DataMemberIgnore]
	public TimeSpan TotalStreamDuration { get; set; }
	[DataMemberIgnore]
	public int FrameSizeInBytes;
	[DataMemberIgnore]
	public Stream AudioStream;
	[DataMemberIgnore]
	public IntPtr InterlacingBuffer = IntPtr.Zero;

	/// <summary>Set to true after first occluded output is logged to trace.</summary>
	[DataMemberIgnore]
	public bool DiagOutputLogged;

	// Per-emitter OpenAL resources
	[DataMemberIgnore]
	public uint AlSourceId;
	[DataMemberIgnore]
	public uint[] AlBufferIds;

	// IPL DSP resources
	[DataMemberIgnore]
	public Hrtf IplHrtf;
	[DataMemberIgnore]
	public BinauralEffect IplBinauralEffect;
	[DataMemberIgnore]
	public AudioBuffer IplInputBuffer;
	[DataMemberIgnore]
	public AudioBuffer IplOutputBuffer;
	[DataMemberIgnore]
	public AudioSettings IplAudioSettings;
	[DataMemberIgnore]
	public DistanceAttenuationModel IplDistanceAttenuationModel;
	[DataMemberIgnore]
	public DirectEffectSettings DirectEffectSettings;
	[DataMemberIgnore]
	public DirectEffect IplDirectEffect;

	// IPL Simulator source (created when simulator is available)
	[DataMemberIgnore]
	public Source IplSource;
	[DataMemberIgnore]
	public bool IsSimulatorSource;

	// Reflection effect resources
	[DataMemberIgnore]
	public ReflectionEffect IplReflectionEffect;
	[DataMemberIgnore]
	public AudioBuffer IplReflectionOutputBuffer;
	[DataMemberIgnore]
	public bool HasReflectionEffect;

	// Cached simulation outputs (updated each frame by the processor)
	[DataMemberIgnore]
	public SimulationOutputs CachedSimulationOutputs;

	public void Initialize(Context iplContext, OpenAlConfiguration openAl, Stream audioStream)
	{
		if (RawFileSource == null)
		{
			throw new InvalidOperationException($"{nameof(RawFileSource)} is not set");
		}

		// Recalculate in case FrameSize was changed in the editor
		FrameSizeInBytes = FrameSize * sizeof(float);

		AudioStream = audioStream;
		InterlacingBuffer = Marshal.AllocHGlobal(FrameSizeInBytes * 2);

		// Create per-emitter OpenAL source and buffers
		AlSourceId = openAl.CreateSource(NumBuffers, out AlBufferIds);

		PrepareSteamAudio(iplContext);
	}

	/// <summary>
	/// Creates the IPL Source for this emitter and registers it with the simulator.
	/// Called by SteamAudioProcessor once the simulator is available.
	/// </summary>
	public void InitializeSimulatorSource(Simulator simulator, Context iplContext)
	{
		var flags = SimulationFlags.Direct;
		if (EnableReflections)
			flags |= SimulationFlags.Reflections;

		var sourceSettings = new SourceSettings
		{
			Flags = flags,
		};

		var srcErr = SourceCreate(simulator, in sourceSettings, out var source);
		if (srcErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] SourceCreate failed: {srcErr} for entity '{Entity?.Name}'");
			return;
		}
		IplSource = source;
		SourceAdd(IplSource, simulator);
		IsSimulatorSource = true;

		Console.WriteLine($"[SteamAudio] Source created for '{Entity?.Name}': Occ={EnableOcclusion} Trans={EnableTransmission}");

		// Create reflection effect if reflections are enabled
		if (EnableReflections)
		{
			CreateReflectionEffect(iplContext);
		}
	}

	[DataMemberIgnore]
	private bool _diagLogged;

	/// <summary>
	/// Sets the simulation inputs for this frame based on emitter/listener state.
	/// </summary>
	/// <remarks>
	/// Note: The SteamAudio.NET binding has a known bool alignment issue where C# bool (1 byte)
	/// maps to native IPLbool (4-byte enum). This affects fields: Baked, EnableValidation,
	/// FindAlternatePaths — causing NumTransmissionRays to be read as 0 by the native code.
	/// The transmission fallback workaround in SteamAudioProcessor.StreamBuffer compensates.
	/// </remarks>
	public void SetSimulationInputs(SimulationFlags flags)
	{
		var directFlags = DirectSimulationFlags.DistanceAttenuation;

		if (EnableOcclusion)
			directFlags |= DirectSimulationFlags.Occlusion;
		if (EnableTransmission)
			directFlags |= DirectSimulationFlags.Transmission;
		if (EnableAirAbsorption)
			directFlags |= DirectSimulationFlags.AirAbsorption;
		if (EnableDirectivity)
			directFlags |= DirectSimulationFlags.Directivity;

		var emitterPosition = Entity.Transform.WorldMatrix.TranslationVector;
		var emitterForward = Entity.Transform.WorldMatrix.Forward;
		var emitterUp = Entity.Transform.WorldMatrix.Up;
		var emitterRight = Entity.Transform.WorldMatrix.Right;

		var inputs = new SimulationInputs
		{
			Flags = flags,
			DirectFlags = directFlags,
			Source = new CoordinateSpace3
			{
				Origin = emitterPosition.ToIPL(),
				Ahead = emitterForward.ToIPL(),
				Up = emitterUp.ToIPL(),
				Right = emitterRight.ToIPL(),
			},
			DistanceAttenuationModel = IplDistanceAttenuationModel,
			AirAbsorptionModel = new AirAbsorptionModel
			{
				Type = AirAbsorptionModelType.Default,
			},
			Directivity = new Directivity
			{
				DipoleWeight = EnableDirectivity ? DirectivityDipoleWeight : 0f,
				DipolePower = EnableDirectivity ? DirectivityDipolePower : 1f,
			},
			OcclusionType = OcclusionType,
			OcclusionRadius = OcclusionRadius,
			NumOcclusionSamples = NumOcclusionSamples,
			NumTransmissionRays = NumTransmissionRays,
			Baked = false,
		};

		// One-time diagnostic log for this emitter
		if (!_diagLogged)
		{
			_diagLogged = true;
			int structSize = Marshal.SizeOf<SimulationInputs>();
			nint managedOffset = Marshal.OffsetOf<SimulationInputs>(nameof(SimulationInputs.NumTransmissionRays));
			Console.WriteLine($"[SteamAudio] {Entity?.Name} SimInputs: sizeof={structSize} NumTransmissionRays.Offset={managedOffset} (native expects 248)");
			Console.WriteLine($"[SteamAudio]   Flags={flags} DirectFlags={directFlags} OccType={OcclusionType} OccSamples={NumOcclusionSamples} TransRays={NumTransmissionRays} OccRadius={OcclusionRadius}");
			if ((nint)managedOffset != 248)
				Console.WriteLine($"[SteamAudio]   WARNING: Bool alignment bug — NumTransmissionRays at offset {managedOffset} instead of 248. Transmission fallback will be used.");
		}

		SourceSetInputs(IplSource, flags, in inputs);
	}

	/// <summary>
	/// Builds the DirectEffectFlags for this emitter's current configuration.
	/// </summary>
	public DirectEffectFlags GetDirectEffectFlags()
	{
		var flags = DirectEffectFlags.ApplyDistanceAttenuation;

		if (EnableOcclusion)
			flags |= DirectEffectFlags.ApplyOcclusion;
		if (EnableTransmission)
			flags |= DirectEffectFlags.ApplyTransmission;
		if (EnableAirAbsorption)
			flags |= DirectEffectFlags.ApplyAirAbsorption;
		if (EnableDirectivity)
			flags |= DirectEffectFlags.ApplyDirectivity;

		return flags;
	}

	private void CreateReflectionEffect(Context iplContext)
	{
		// Ambisonics channels for reflections: (order+1)^2 with order=1 ? 4 channels
		int numChannels = 2; // Stereo output for now

		var reflectionSettings = new ReflectionEffectSettings
		{
			Type = ReflectionEffectType.Convolution,
			IrSize = (int)(IplAudioSettings.SamplingRate * 1.0f), // 1 second IR
			NumChannels = numChannels,
		};

		ReflectionEffectCreate(iplContext, in IplAudioSettings, in reflectionSettings, out IplReflectionEffect);
		AudioBufferAllocate(iplContext, numChannels, IplAudioSettings.FrameSize, ref IplReflectionOutputBuffer);
		HasReflectionEffect = true;
	}

	private void PrepareSteamAudio(Context iplContext)
	{
		IplAudioSettings = new AudioSettings
		{
			SamplingRate = SampleRate,
			FrameSize = FrameSize
		};

		// HRTF
		var hrtfSettings = new HrtfSettings
		{
			Type = HrtfType.Default,
			Volume = Volume,
			NormType = HrtfNormType.None
		};

		HrtfCreate(iplContext, in IplAudioSettings, in hrtfSettings, out IplHrtf);

		// Binaural Effect
		var binauralEffectSettings = new BinauralEffectSettings
		{
			Hrtf = IplHrtf
		};

		BinauralEffectCreate(iplContext, in IplAudioSettings, in binauralEffectSettings, out IplBinauralEffect);

		// Audio Buffers
		// Input is mono, output is stereo.
		AudioBufferAllocate(iplContext, 1, IplAudioSettings.FrameSize, ref IplInputBuffer);
		AudioBufferAllocate(iplContext, 2, IplAudioSettings.FrameSize, ref IplOutputBuffer);

		IplDistanceAttenuationModel = new DistanceAttenuationModel
		{ 
			Type = DistanceAttenuationModelType.Default,
			MinDistance = 0.1f
		};

		DirectEffectSettings = new DirectEffectSettings
		{
			NumChannels = 2,
		};

		// Create the DirectEffect once during initialization, not per frame
		var directErr = DirectEffectCreate(iplContext, IplAudioSettings, DirectEffectSettings, out IplDirectEffect);
		if (directErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] DirectEffectCreate failed: {directErr}");
		}
	}

	public void RemoveFromSimulator(Simulator simulator, Context iplContext)
	{
		if (IsSimulatorSource)
		{
			SourceRemove(IplSource, simulator);
			SourceRelease(ref IplSource);
			IsSimulatorSource = false;
		}

		if (HasReflectionEffect)
		{
			ReflectionEffectRelease(ref IplReflectionEffect);
			AudioBufferFree(iplContext, ref IplReflectionOutputBuffer);
			HasReflectionEffect = false;
		}
	}

	public void Dispose(Context iplContext, OpenAlConfiguration openAl)
	{
		AudioStream?.Dispose();
		AudioStream = null;

		if (InterlacingBuffer != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(InterlacingBuffer);
			InterlacingBuffer = IntPtr.Zero;
		}

		// Release OpenAL resources
		openAl.DestroySource(AlSourceId, AlBufferIds);

		// Release reflection resources
		if (HasReflectionEffect)
		{
			ReflectionEffectRelease(ref IplReflectionEffect);
			AudioBufferFree(iplContext, ref IplReflectionOutputBuffer);
			HasReflectionEffect = false;
		}

		// Release Steam Audio resources
		DirectEffectRelease(ref IplDirectEffect);
		AudioBufferFree(iplContext, ref IplInputBuffer);
		AudioBufferFree(iplContext, ref IplOutputBuffer);
		BinauralEffectRelease(ref IplBinauralEffect);
		HrtfRelease(ref IplHrtf);
	}
}
