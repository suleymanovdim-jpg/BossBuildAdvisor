using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using BossBuildAdvisor.Systems;

namespace BossBuildAdvisor.Players;

/// <summary>
/// Handles mod hotkeys inside <see cref="ModPlayer.ProcessTriggers"/> as recommended by tModLoader.
/// </summary>
public sealed class AdvisorPlayer : ModPlayer
{
	public override void ProcessTriggers(TriggersSet triggersSet)
	{
		if (Player.whoAmI != Main.myPlayer || AdvisorSystem.IsHotkeyBlocked())
			return;

		ModKeybind keybind = BossBuildAdvisor.ToggleAdvisorKeybind ?? AdvisorSystem.ToggleAdvisorKeybind;
		if (keybind != null && keybind.JustPressed)
		{
			ModContent.GetInstance<AdvisorSystem>().ToggleFromHotkey();
		}
	}
}
