using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using BossBuildAdvisor.Core;
using BossBuildAdvisor.UI;

namespace BossBuildAdvisor.Systems;

public sealed class AdvisorSystem : ModSystem
{
	public static AdvisorSystem Instance { get; private set; }
	public static ModKeybind ToggleAdvisorKeybind { get; private set; }

	public BossCatalog Catalog { get; } = new();
	public ProgressionGraph Graph { get; } = new();
	public ArmorSetAnalyzer ArmorSets { get; } = new();
	public BuildRecommender Recommender { get; private set; }

	private UserInterface _userInterface;
	private AdvisorUIState _uiState;
	private bool _isOpen;
	private bool _dataBuilt;

	public bool IsOpen => _isOpen;

	public override void Load()
	{
		Instance = this;
		ToggleAdvisorKeybind = KeybindLoader.RegisterKeybind(Mod, "ToggleAdvisor", Keys.K);
	}

	public override void Unload()
	{
		Instance = null;
		ToggleAdvisorKeybind = null;
		_userInterface = null;
		_uiState = null;
	}

	public override void PostSetupRecipes()
	{
		RebuildAnalysis();
		LogDiagnosticSummary();
	}

	private void LogDiagnosticSummary()
	{
		if (Recommender == null)
			return;

		var stageCounts = new Dictionary<string, int>();
		foreach (BossStageInfo s in Catalog.AllStages)
			stageCounts[s.ModSource] = stageCounts.TryGetValue(s.ModSource, out int c) ? c + 1 : 1;

		string modsSummary = string.Join(", ", System.Linq.Enumerable.Select(stageCounts, kv => $"{kv.Key}:{kv.Value}"));
		string classesSummary = string.Join(", ", System.Linq.Enumerable.Select(Recommender.GetAvailablePlayableClasses(), c => c.FullName));
		Mod.Logger.Info($"[Summary] Stages({Catalog.AllStages.Count})=[{modsSummary}] Classes=[{classesSummary}] ArmorSets={ArmorSets.Sets.Count}");
		Mod.Logger.Info($"[Milestones] KingSlime={Catalog.GetProgressionForNpc(NPCID.KingSlime, -1):0.##} Skeletron={Catalog.GetProgressionForNpc(NPCID.SkeletronHead, -1):0.##} WoF={Catalog.WallOfFleshProgression:0.##} Destroyer={Catalog.GetProgressionForNpc(NPCID.TheDestroyer, -1):0.##} Plantera={Catalog.GetProgressionForNpc(NPCID.Plantera, -1):0.##} Golem={Catalog.GetProgressionForNpc(NPCID.Golem, -1):0.##} MoonLord={Catalog.GetProgressionForNpc(NPCID.MoonLordCore, -1):0.##}");

		string FmtSlot(RecommendedSlotItem s)
		{
			if (s == null) return "-";
			string src = Graph.Items.TryGetValue(s.ItemType, out ItemUnlockInfo info) ? $"{info.UnlockProgression:0.##}:{info.SourceSummary}" : "?";
			return $"{Lang.GetItemNameValue(s.ItemType)}({s.RoleTag}:{s.Score:0})[{src}]";
		}

		// Log sample recommendation per distinct mod (and key milestones: Skeletron, Wall of Flesh, Plantera, Moon Lord)
		var loggedMods = new HashSet<string>();
		var milestoneStages = new List<BossStageInfo>();
		foreach (BossStageInfo stage in Catalog.AllStages)
		{
			if (stage.IsBoss && loggedMods.Add(stage.ModSource))
				milestoneStages.Add(stage);
			else if (stage.IsBoss && stage.NpcIds.Exists(id => id is NPCID.SkeletronHead or NPCID.WallofFlesh or NPCID.Plantera or NPCID.MoonLordCore))
				milestoneStages.Add(stage);
		}

		foreach (BossStageInfo stage in milestoneStages)
		{
			BuildRecommendation rec = Recommender.Recommend(stage, null, new BuildQueryOptions());
			string w = string.Join(", ", System.Linq.Enumerable.Select(rec.PrimaryWeapons, FmtSlot));
			string a = string.Join(", ", System.Linq.Enumerable.Select(rec.Accessories, FmtSlot));
			string armor = rec.ArmorSet != null
				? $"{Lang.GetItemNameValue(rec.ArmorSet.HeadType)}/{Lang.GetItemNameValue(rec.ArmorSet.BodyType)}/{Lang.GetItemNameValue(rec.ArmorSet.LegsType)} ({rec.ArmorSet.TotalDefense}def{(rec.ArmorSet.IsTrueSet ? " Set" : " Mixed")} @ {rec.ArmorSet.UnlockProgression:0.##})"
				: "-";
			Mod.Logger.Info($"[Sample] {stage.ModSource}/{stage.DisplayName} ({stage.Progression:0.#}) -> {ItemTooltipAnalyzer.FormatClassDisplayName(rec.TargetClass)}: Weapons=[{w}] Armor=[{armor}] Acc=[{a}]");
		}

		// Also verify every custom/vanilla DamageClass produces a valid build at Wall of Flesh
		BossStageInfo wofStage = Catalog.AllStages.Find(s => s.NpcIds.Contains(NPCID.WallofFlesh));
		if (wofStage != null)
		{
			foreach (DamageClass dc in Recommender.GetAvailablePlayableClasses())
			{
				BuildRecommendation dcRec = Recommender.Recommend(wofStage, dc, new BuildQueryOptions());
				string w = string.Join(", ", System.Linq.Enumerable.Select(dcRec.PrimaryWeapons, x => Lang.GetItemNameValue(x.ItemType)));
				Mod.Logger.Info($"[ClassCheck@WoF] {ItemTooltipAnalyzer.FormatClassDisplayName(dc)} ({dc.FullName}): Weapons=[{w}] Armor={(dcRec.ArmorSet?.IsTrueSet == true ? "Set" : "Mix")}:{dcRec.ArmorSet?.TotalDefense ?? 0}def Acc={dcRec.Accessories.Count} Pot={dcRec.BuffPotions.Count}");
			}
		}

		if (_uiState != null)
		{
			Mod.Logger.Info($"[UILayout] {_uiState.GetLayoutCheckSummary()}");
		}
	}

	public void RebuildAnalysis()
	{
		Catalog.Build(Mod);
		Graph.Build(Mod, Catalog);
		ArmorSets.Build(Mod, Graph);
		Recommender = new BuildRecommender(Catalog, Graph, ArmorSets);
		_dataBuilt = true;

		if (!Main.dedServ)
		{
			if (_userInterface == null)
			{
				_userInterface = new UserInterface();
				_uiState = new AdvisorUIState(Catalog, Graph, Recommender);
				_uiState.Activate();
			}
			else if (_isOpen)
			{
				_uiState.RefreshAll();
			}
		}
	}

	public override void OnWorldLoad()
	{
		if (!_dataBuilt)
			RebuildAnalysis();

		SetOpen(false);
	}

	public override void PreSaveAndQuit()
	{
		SetOpen(false);
	}

	public void ToggleOpen() => SetOpen(!_isOpen);

	public void SetOpen(bool open)
	{
		if (Main.dedServ || _userInterface == null)
			return;

		_isOpen = open;
		if (open)
		{
			_uiState.RefreshAll();
			_userInterface.SetState(_uiState);
		}
		else
		{
			_uiState?.OnClosed();
			_userInterface.SetState(null);
		}
	}

	public override void PostUpdateInput()
	{
		if (Main.dedServ || Main.gameMenu)
			return;

		if (ToggleAdvisorKeybind != null && ToggleAdvisorKeybind.JustPressed && !Main.drawingPlayerChat)
		{
			SoundEngine.PlaySound(SoundID.MenuOpen);
			ToggleOpen();
		}
	}

	public override void UpdateUI(GameTime gameTime)
	{
		if (_isOpen && _userInterface?.CurrentState != null)
		{
			_userInterface.Update(gameTime);
		}
	}

	public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
	{
		int inventoryIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Inventory"));
		if (inventoryIndex != -1)
		{
			layers.Insert(inventoryIndex + 1, new LegacyGameInterfaceLayer(
				"BossBuildAdvisor: Inventory Toggle",
				delegate
				{
					if (Main.playerInventory)
						DrawInventoryToggleButton();
					return true;
				},
				InterfaceScaleType.UI));
		}

		int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
		if (mouseTextIndex != -1)
		{
			layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
				"BossBuildAdvisor: Advisor Panel",
				delegate
				{
					if (_isOpen && _userInterface?.CurrentState != null)
						_userInterface.Draw(Main.spriteBatch, Main._drawInterfaceGameTime);
					return true;
				},
				InterfaceScaleType.UI));
		}
	}

	private void DrawInventoryToggleButton()
	{
		// Place a compact clickable pill right above the right edge of the inventory / crafting area
		var rect = new Rectangle(Main.screenWidth - 196, 12, 180, 28);
		bool hovered = rect.Contains(Main.MouseScreen.ToPoint());

		Color bg = _isOpen
			? new Color(75, 112, 175, 235)
			: (hovered ? new Color(62, 78, 122, 235) : new Color(34, 45, 76, 215));
		Color border = hovered || _isOpen ? new Color(255, 220, 105) : new Color(15, 20, 35);

		Utils.DrawInvBG(Main.spriteBatch, rect, bg);

		string label = "Build Advisor [K]";
		Vector2 size = FontAssets.MouseText.Value.MeasureString(label) * 0.78f;
		Vector2 textPos = new Vector2(rect.X + (rect.Width - size.X) * 0.5f, rect.Y + (rect.Height - size.Y) * 0.5f + 2f);
		Utils.DrawBorderStringFourWay(Main.spriteBatch, FontAssets.MouseText.Value, label, textPos.X, textPos.Y, border, Color.Black, Vector2.Zero, 0.78f);

		if (hovered)
		{
			Main.LocalPlayer.mouseInterface = true;
			Main.instance.MouseText(Language.GetTextValue("Mods.BossBuildAdvisor.UI.OpenButtonTooltip"));
			if (Main.mouseLeft && Main.mouseLeftRelease)
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				ToggleOpen();
			}
		}
	}
}
