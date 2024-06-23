using Doprez.Stride.SteamAudio.Processors;
using SteamAudio;
using Stride.Core;
using Stride.Core.IO;
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
public class SteamAudioEmitter : StartupScript
{

	public UrlReference RawFileSource { get; set; }

	public int SampleRate { get; set; } = 44100;
	public int FrameSize { get; set; } = 4096;
	public float Volume { get; set; } = 1.0f;


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

	public SteamAudioEmitter()
	{
		FrameSizeInBytes = FrameSize * sizeof(float);
	}

	public override void Start()
	{
		AudioStream = OpenStream();
	}

	public void Initialize(Context iplContext)
	{
		if (RawFileSource == null)
		{
			throw new InvalidOperationException($"{nameof(RawFileSource)} is not set");
		}

		InterlacingBuffer = Marshal.AllocHGlobal(FrameSizeInBytes * 2);
		PrepareSteamAudio(iplContext);
	}

	public Stream OpenStream()
	{
		var stream = Content.OpenAsStream(RawFileSource, StreamFlags.Seekable);
		return stream;
	}

	public void ReadAudioData()
	{

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
	}

	public void Dispose(Context iplContext)
	{
		Marshal.FreeHGlobal(InterlacingBuffer);

		AudioBufferFree(iplContext, ref IplInputBuffer);
		AudioBufferFree(iplContext, ref IplOutputBuffer);
		BinauralEffectRelease(ref IplBinauralEffect);
		HrtfRelease(ref IplHrtf);
	}
}
