using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ModLoader;

namespace BossBuildAdvisor;

public class BossBuildAdvisor : Mod
{
	public static ModKeybind ToggleAdvisorKeybind { get; private set; }

	public override void Load()
	{
		if (!Main.dedServ)
			ToggleAdvisorKeybind = KeybindLoader.RegisterKeybind(this, "ToggleAdvisor", Keys.K);
	}

	public override void Unload()
	{
		ToggleAdvisorKeybind = null;
	}
}
