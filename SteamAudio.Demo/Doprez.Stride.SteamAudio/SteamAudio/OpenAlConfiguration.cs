using Silk.NET.OpenAL;

namespace Doprez.Stride.SteamAudio;
public unsafe class OpenAlConfiguration : IDisposable
{
	public const int NumBuffers = 2;

	public bool SoftwareAL = true;

	public readonly  Device* AlAudioDevice;
	public readonly  Context* AlAudioContext;
	public readonly  AL Al;
	public uint sourceId;
	
	private readonly ALContext AlContext;
	

	public OpenAlConfiguration()
	{
		Al = AL.GetApi(soft: SoftwareAL);
		AlContext = ALContext.GetApi(soft: SoftwareAL);

		AlAudioDevice = AlContext.OpenDevice(null);
		AlAudioContext = AlContext.CreateContext(AlAudioDevice, null);

		if (!AlContext.MakeContextCurrent(AlAudioContext))
		{
			throw new InvalidOperationException("Unable to make context current");
		}

		// Require float32 support.
		const string Float32Extension = "AL_EXT_float32";

		if (!Al.IsExtensionPresent(Float32Extension))
		{
			string extensions = Al.GetStateProperty(StateString.Extensions);

			throw new Exception($"This program requires '{Float32Extension}' OpenAL extension to function.\r\nAvailable extensions: {extensions}");
		}

		CheckALErrors();
	}

	public void Initialize()
	{
		uint* alBuffers = stackalloc uint[NumBuffers];

		Al.GenBuffers(NumBuffers, alBuffers);

		sourceId = Al.GenSource();
	}

	private void CheckALErrors()
	{
		var error = Al.GetError();

		if (error != AudioError.NoError)
		{
			throw new Exception($"OpenAL Error: {error}");
		}
	}

	public void Dispose()
	{
		AlContext.DestroyContext(AlAudioContext);
		AlContext.CloseDevice(AlAudioDevice);
	}
}
