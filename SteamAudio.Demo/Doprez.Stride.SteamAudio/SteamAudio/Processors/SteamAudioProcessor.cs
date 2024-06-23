using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using SteamAudio;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Games;
using Stride.Profiling;
using System.Runtime.CompilerServices;
using static SteamAudio.IPL;

namespace Doprez.Stride.SteamAudio.Processors;
public unsafe class SteamAudioProcessor : EntityProcessor<SteamAudioEmitter>
{
	public List<SteamAudioEmitter> Emitters = [];
	public SteamAudioListener Listener;

	public DebugTextSystem DebugText;

	private OpenAlConfiguration _openAlConfiguration;
	private SteamAudioListenerProcessor _listenerProcessor;

	private IPL.Context _iplContext;

	public SteamAudioProcessor()
	{
		Order = 80001;

		// Steam Audio Initialization
		var contextSettings = new ContextSettings
		{
			Version = IPL.Version,
		};
		ContextCreate(in contextSettings, out _iplContext);
	}

	public override void Update(GameTime time)
	{
		foreach(var emitter in ComponentDatas.Values)
		{
			PlayAudio(emitter);
		}
	}

	protected override void OnSystemAdd()
	{
		_openAlConfiguration = new OpenAlConfiguration();
		_openAlConfiguration.Initialize();

		_listenerProcessor = Services.GetService<SteamAudioListenerProcessor>();
		Listener = _listenerProcessor.Listener;

		DebugText = Services.GetService<DebugTextSystem>();
	}

	protected override void OnSystemRemove()
	{
		_openAlConfiguration.Dispose();
		ContextRelease(ref _iplContext);
	}

	protected override void OnEntityComponentAdding(Entity entity, [NotNull] SteamAudioEmitter component, [NotNull] SteamAudioEmitter data)
	{
		Emitters.Add(component);
		component.Initialize(_iplContext);
	}

	protected override void OnEntityComponentRemoved(Entity entity, [NotNull] SteamAudioEmitter component, [NotNull] SteamAudioEmitter data)
	{
		Emitters.Remove(component);
		component.Dispose(_iplContext);
	}

	private void PlayAudio(SteamAudioEmitter emitter)
	{

		emitter.CurrentStreamPosition = TimeSpan.FromSeconds((int)(emitter.AudioStream.Position / sizeof(float) / emitter.SampleRate));
		//TimeSpan streamLengthTimeSpan = TimeSpan.FromSeconds((int)(emitter.AudioStream.Length / sizeof(float) / emitter.SampleRate));

		// Update streamed audio
		_openAlConfiguration.Al.GetSourceProperty(_openAlConfiguration.sourceId, GetSourceInteger.BuffersProcessed, out int numProcessedBuffers);
		_openAlConfiguration.Al.GetSourceProperty(_openAlConfiguration.sourceId, GetSourceInteger.BuffersQueued, out int numQueuedBuffers);

		int buffersToAdd = OpenAlConfiguration.NumBuffers - numQueuedBuffers + numProcessedBuffers;

		while (buffersToAdd > 0)
		{
			uint bufferId = (uint)buffersToAdd;

			if (numProcessedBuffers > 0)
			{
				_openAlConfiguration.Al.SourceUnqueueBuffers(_openAlConfiguration.sourceId, 1, &bufferId);

				numProcessedBuffers--;
			}

			StreamBuffer(bufferId, emitter);

			_openAlConfiguration.Al.SourceQueueBuffers(_openAlConfiguration.sourceId, 1, &bufferId);

			buffersToAdd--;
		}

		// Start playback whenever it stops
		_openAlConfiguration.Al.GetSourceProperty(_openAlConfiguration.sourceId, GetSourceInteger.SourceState, out int sourceStateInt);

		if ((SourceState)sourceStateInt != SourceState.Playing)
		{
			_openAlConfiguration.Al.SourcePlay(_openAlConfiguration.sourceId);
		}

		CheckALErrors();
	}

	private void StreamBuffer(uint bufferId, SteamAudioEmitter emitter)
	{
		var audioStream = emitter.AudioStream;

		var emitterPosition = emitter.Entity.Transform.WorldMatrix.TranslationVector;
		var listenerPosition = Listener.Entity.Transform.WorldMatrix.TranslationVector;
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

		var binauralEffectParams = new IPL.BinauralEffectParams
		{
			Hrtf = emitter.IplHrtf,
			Direction = iplDir,
			Interpolation = IPL.HrtfInterpolation.Nearest,
			SpatialBlend = 1f,
		};
		
		IPL.BinauralEffectApply(emitter.IplBinauralEffect, ref binauralEffectParams, ref emitter.IplInputBuffer, ref emitter.IplOutputBuffer);

		// Apply distance attenuation
		var volume = IPL.DistanceAttenuationCalculate(_iplContext, emitterPosition.ToIPL(), listenerPosition.ToIPL(), emitter.IplDistanceAttenuationModel);
		var directEffectParams = new IPL.DirectEffectParams
		{
			Flags = IPL.DirectEffectFlags.ApplyDistanceAttenuation,
			DistanceAttenuation = volume,
		};
		IPL.DirectEffectCreate(_iplContext, emitter.IplAudioSettings, emitter.DirectEffectSettings, out var directEffect);
		// once the output buffer is filled, we can apply the direct effect using it as the input for future effects.
		IPL.DirectEffectApply(directEffect, ref directEffectParams, ref emitter.IplOutputBuffer, ref emitter.IplOutputBuffer);

		IPL.AudioBufferInterleave(_iplContext, in emitter.IplOutputBuffer, in Unsafe.AsRef<float>((void*)emitter.InterlacingBuffer));

		_openAlConfiguration.Al.BufferData(bufferId, (BufferFormat)FloatBufferFormat.Stereo, (void*)emitter.InterlacingBuffer, emitter.FrameSizeInBytes * 2, emitter.IplAudioSettings.SamplingRate);

		CheckALErrors();
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
