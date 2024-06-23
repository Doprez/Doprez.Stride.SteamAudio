using Stride.Core.Annotations;
using Stride.Engine;

namespace Doprez.Stride.SteamAudio.Processors;
public class SteamAudioListenerProcessor : EntityProcessor<SteamAudioListener>
{
	public SteamAudioListener Listener;

	public SteamAudioListenerProcessor()
	{
		Order = 80000;
	}

	protected override void OnSystemAdd()
	{
		Services.AddService(this);
	}

	protected override void OnEntityComponentAdding(Entity entity, [NotNull] SteamAudioListener component, [NotNull] SteamAudioListener data)
	{
		Listener = component;
	}
}
