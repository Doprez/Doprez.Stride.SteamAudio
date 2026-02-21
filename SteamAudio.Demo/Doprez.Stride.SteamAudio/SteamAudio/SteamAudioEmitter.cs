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

	// --- HRTF Customization ---

	/// <summary>HRTF interpolation type. Bilinear is smoother, Nearest is faster.</summary>
	public HrtfInterpolation HrtfInterpolation { get; set; } = HrtfInterpolation.Bilinear;

	/// <summary>Path to a custom SOFA HRTF file. Leave empty to use the built-in default HRTF.</summary>
	public string SofaFilePath { get; set; } = "";

	/// <summary>HRTF normalization type. RMS normalizes the HRTF for consistent volume.</summary>
	public HrtfNormType HrtfNormType { get; set; } = HrtfNormType.None;

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

	/// <summary>Ambisonics order for reflection encoding (0=mono, 1=first-order 4ch, 2=second-order 9ch).</summary>
	public int ReflectionAmbisonicsOrder { get; set; } = 1;

	// --- Pathing options ---

	/// <summary>Enable pathing simulation (sound travels around obstacles via shortest viable paths). Requires probe batches.</summary>
	public bool EnablePathing { get; set; } = false;

	/// <summary>Ambisonics order for pathing spatialization.</summary>
	public int PathingOrder { get; set; } = 1;

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
	public AudioBuffer IplReflectionOutputBuffer; // ambisonics buffer: (order+1)^2 channels
	[DataMemberIgnore]
	public bool HasReflectionEffect;

	// Ambisonics decode for reflections
	[DataMemberIgnore]
	public AmbisonicsBinauralEffect IplAmbisonicsBinauralEffect;
	[DataMemberIgnore]
	public AudioBuffer IplReflectionStereoBuffer; // stereo decoded from ambisonics

	// Pathing effect resources
	[DataMemberIgnore]
	public PathEffect IplPathEffect;
	[DataMemberIgnore]
	public AudioBuffer IplPathOutputBuffer;
	[DataMemberIgnore]
	public bool HasPathEffect;

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
		if (EnablePathing)
			flags |= SimulationFlags.Pathing;

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

		Console.WriteLine($"[SteamAudio] Source created for '{Entity?.Name}': Occ={EnableOcclusion} Trans={EnableTransmission} Refl={EnableReflections} Path={EnablePathing}");

		// Create reflection effect if reflections are enabled
		if (EnableReflections)
		{
			CreateReflectionEffect(iplContext);
		}

		// Create pathing effect if pathing is enabled
		if (EnablePathing)
		{
			CreatePathEffect(iplContext);
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
		int order = ReflectionAmbisonicsOrder;
		int numAmbisonicsChannels = (order + 1) * (order + 1); // order 1 → 4 channels

		var reflectionSettings = new ReflectionEffectSettings
		{
			Type = ReflectionEffectType.Convolution,
			IrSize = (int)(IplAudioSettings.SamplingRate * 1.0f), // 1 second IR
			NumChannels = numAmbisonicsChannels,
		};

		var refErr = ReflectionEffectCreate(iplContext, in IplAudioSettings, in reflectionSettings, out IplReflectionEffect);
		if (refErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] ReflectionEffectCreate failed: {refErr}");
			return;
		}

		// Ambisonics intermediate buffer for reflection IR output
		AudioBufferAllocate(iplContext, numAmbisonicsChannels, IplAudioSettings.FrameSize, ref IplReflectionOutputBuffer);

		// Ambisonics → binaural stereo decoder
		var ambiDecodeSettings = new AmbisonicsBinauralEffectSettings
		{
			Hrtf = IplHrtf,
			MaxOrder = order,
		};

		var ambiErr = AmbisonicsBinauralEffectCreate(iplContext, in IplAudioSettings, in ambiDecodeSettings, out IplAmbisonicsBinauralEffect);
		if (ambiErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] AmbisonicsBinauralEffectCreate failed: {ambiErr}");
			return;
		}

		// Stereo output buffer for decoded reflections
		AudioBufferAllocate(iplContext, 2, IplAudioSettings.FrameSize, ref IplReflectionStereoBuffer);

		HasReflectionEffect = true;
		Console.WriteLine($"[SteamAudio] Reflection pipeline created: order={order} ambiChannels={numAmbisonicsChannels}");
	}

	private void CreatePathEffect(Context iplContext)
	{
		var pathSettings = new PathEffectSettings
		{
			MaxOrder = PathingOrder,
			Spatialize = true,
			Hrtf = IplHrtf,
		};

		var pathErr = PathEffectCreate(iplContext, in IplAudioSettings, in pathSettings, out IplPathEffect);
		if (pathErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] PathEffectCreate failed: {pathErr}");
			return;
		}

		AudioBufferAllocate(iplContext, 2, IplAudioSettings.FrameSize, ref IplPathOutputBuffer);
		HasPathEffect = true;
		Console.WriteLine($"[SteamAudio] PathEffect created: order={PathingOrder}");
	}

	private void PrepareSteamAudio(Context iplContext)
	{
		IplAudioSettings = new AudioSettings
		{
			SamplingRate = SampleRate,
			FrameSize = FrameSize
		};

		// HRTF
		bool useSofa = !string.IsNullOrEmpty(SofaFilePath);
		var hrtfSettings = new HrtfSettings
		{
			Type = useSofa ? HrtfType.Sofa : HrtfType.Default,
			SofaFileName = useSofa ? SofaFilePath : null,
			Volume = Volume,
			NormType = HrtfNormType,
		};

		var hrtfErr = HrtfCreate(iplContext, in IplAudioSettings, in hrtfSettings, out IplHrtf);
		if (hrtfErr != IPL.Error.Success)
		{
			Console.WriteLine($"[SteamAudio] HrtfCreate failed: {hrtfErr} (SOFA={useSofa}, path={SofaFilePath})");
			// Fall back to default HRTF
			hrtfSettings.Type = HrtfType.Default;
			hrtfSettings.SofaFileName = null;
			HrtfCreate(iplContext, in IplAudioSettings, in hrtfSettings, out IplHrtf);
		}
		else if (useSofa)
		{
			Console.WriteLine($"[SteamAudio] SOFA HRTF loaded: {SofaFilePath}");
		}

		// Binaural Effect
		var binauralEffectSettings = new BinauralEffectSettings
		{
			Hrtf = IplHrtf
		};

		BinauralEffectCreate(iplContext, in IplAudioSettings, in binauralEffectSettings, out IplBinauralEffect);

		// Audio Buffers
		// Input is mono, output is stereo.
		var inBufErr = AudioBufferAllocate(iplContext, 1, IplAudioSettings.FrameSize, ref IplInputBuffer);
		if (inBufErr != IPL.Error.Success)
		{
			throw new InvalidOperationException($"[SteamAudio] AudioBufferAllocate (input) failed: {inBufErr}. Check that phonon.dll version matches the SteamAudio.NET wrapper.");
		}
		var outBufErr = AudioBufferAllocate(iplContext, 2, IplAudioSettings.FrameSize, ref IplOutputBuffer);
		if (outBufErr != IPL.Error.Success)
		{
			throw new InvalidOperationException($"[SteamAudio] AudioBufferAllocate (output) failed: {outBufErr}. Check that phonon.dll version matches the SteamAudio.NET wrapper.");
		}

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
			AmbisonicsBinauralEffectRelease(ref IplAmbisonicsBinauralEffect);
			AudioBufferFree(iplContext, ref IplReflectionStereoBuffer);
			HasReflectionEffect = false;
		}

		if (HasPathEffect)
		{
			PathEffectRelease(ref IplPathEffect);
			AudioBufferFree(iplContext, ref IplPathOutputBuffer);
			HasPathEffect = false;
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
			AmbisonicsBinauralEffectRelease(ref IplAmbisonicsBinauralEffect);
			AudioBufferFree(iplContext, ref IplReflectionStereoBuffer);
			HasReflectionEffect = false;
		}

		// Release pathing resources
		if (HasPathEffect)
		{
			PathEffectRelease(ref IplPathEffect);
			AudioBufferFree(iplContext, ref IplPathOutputBuffer);
			HasPathEffect = false;
		}

		// Release Steam Audio resources
		DirectEffectRelease(ref IplDirectEffect);
		AudioBufferFree(iplContext, ref IplInputBuffer);
		AudioBufferFree(iplContext, ref IplOutputBuffer);
		BinauralEffectRelease(ref IplBinauralEffect);
		HrtfRelease(ref IplHrtf);
	}
}
