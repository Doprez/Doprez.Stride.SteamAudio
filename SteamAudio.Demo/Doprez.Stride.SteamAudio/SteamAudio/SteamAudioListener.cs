using Doprez.Stride.SteamAudio.Processors;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace Doprez.Stride.SteamAudio;
[DataContract]
[Display("Steam Audio Listener")]
[ComponentCategory("Audio")]
[DefaultEntityComponentProcessor(typeof(SteamAudioListenerProcessor), ExecutionMode = ExecutionMode.Runtime)]
public class SteamAudioListener : EntityComponent
{
	
}
