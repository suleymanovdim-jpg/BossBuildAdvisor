using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using BossBuildAdvisor.Systems;
using BossBuildAdvisor.UI;

namespace BossBuildAdvisor.Players;

/// <summary>
/// Handles mod hotkeys inside <see cref="ModPlayer.ProcessTriggers"/> as recommended by tModLoader.
/// </summary>
public sealed class AdvisorPlayer : ModPlayer
{
	public override void ProcessTriggers(TriggersSet triggersSet)
	{
		if (Main.dedServ || Main.gameMenu || Player.whoAmI != Main.myPlayer)
			return;

		if (Main.drawingPlayerChat || Main.editSign || Main.editChest)
			return;

		if (Main.CurrentInputTextTakerOverride is BossSearchBox)
			return;

		ModKeybind keybind = BossBuildAdvisor.ToggleAdvisorKeybind ?? AdvisorSystem.ToggleAdvisorKeybind;
		if (keybind != null && keybind.JustPressed)
		{
			ModContent.GetInstance<AdvisorSystem>().ToggleFromHotkey();
		}
	}
}
