using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Content;
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
	public static ModKeybind ToggleAdvisorKeybind => BossBuildAdvisor.ToggleAdvisorKeybind;

	public BossCatalog Catalog { get; } = new();
	public ProgressionGraph Graph { get; } = new();
	public ArmorSetAnalyzer ArmorSets { get; } = new();
	public BuildRecommender Recommender { get; private set; }

	private UserInterface _userInterface;
	private AdvisorUIState _uiState;
	private Asset<Texture2D> _buttonTexture;
	private bool _isOpen;
	private bool _dataBuilt;

	public bool IsOpen => _isOpen;

	public override void Load()
	{
		Instance = this;
		if (!Main.dedServ)
			_buttonTexture = ModContent.Request<Texture2D>("BossBuildAdvisor/Assets/UI/AdvisorButton", AssetRequestMode.AsyncLoad);
	}

	public override void Unload()
	{
		Instance = null;
		_buttonTexture = null;
		_userInterface = null;
		_uiState = null;
		_bossChecklistReflectionDone = false;
		_bossLogConfigInstance = null;
		_bossLogPosProperty = null;
		_bossLogPosField = null;
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
		int visibleCount = Catalog.AllStages.FindAll(s => s.IsVisibleOnChecklist).Count;
		int hiddenCount = Catalog.AllStages.Count - visibleCount;
		Mod.Logger.Info($"[Summary] Stages({Catalog.AllStages.Count}, Visible={visibleCount}, Hidden={hiddenCount})=[{modsSummary}] Classes=[{classesSummary}] ArmorSets={ArmorSets.Sets.Count}");
		Mod.Logger.Info($"[Milestones] KingSlime={Catalog.GetProgressionForNpc(NPCID.KingSlime, -1):0.##} Skeletron={Catalog.GetProgressionForNpc(NPCID.SkeletronHead, -1):0.##} WoF={Catalog.WallOfFleshProgression:0.##} Destroyer={Catalog.GetProgressionForNpc(NPCID.TheDestroyer, -1):0.##} Plantera={Catalog.GetProgressionForNpc(NPCID.Plantera, -1):0.##} Golem={Catalog.GetProgressionForNpc(NPCID.Golem, -1):0.##} MoonLord={Catalog.GetProgressionForNpc(NPCID.MoonLordCore, -1):0.##}");

		string visPreview = string.Join(" -> ", System.Linq.Enumerable.Take(
			System.Linq.Enumerable.Select(
				System.Linq.Enumerable.Where(Catalog.AllStages, s => s.IsVisibleOnChecklist),
				s => $"{s.DisplayName}({s.Progression:0.#})"),
			16));
		string hidPreview = string.Join(", ", System.Linq.Enumerable.Select(
			System.Linq.Enumerable.Where(Catalog.AllStages, s => !s.IsVisibleOnChecklist),
			s => $"{s.DisplayName}({s.Key},{s.Progression:0.#})"));
		Mod.Logger.Info($"[ChecklistOrder] VisibleTop16=[{visPreview}] Hidden=[{hidPreview}]");

		string FmtSlot(RecommendedSlotItem s)
		{
			if (s == null) return "-";
			return $"{Lang.GetItemNameValue(s.ItemType)}({s.RoleTag}:{s.Difficulty}@{s.UnlockProgression:0.##}:{s.SourceSummary})";
		}

		// Log sample recommendation per distinct mod + key milestones (King Slime, Thorn, Skeletron, WoF, Plantera, Moon Lord)
		var loggedMods = new HashSet<string>();
		var milestoneStages = new List<BossStageInfo>();
		foreach (BossStageInfo stage in Catalog.AllStages)
		{
			if (stage.IsBoss && loggedMods.Add(stage.ModSource))
				milestoneStages.Add(stage);
			else if (stage.IsBoss && stage.NpcIds.Exists(id => id is NPCID.KingSlime or NPCID.SkeletronHead or NPCID.WallofFlesh or NPCID.Plantera or NPCID.MoonLordCore))
				milestoneStages.Add(stage);
		}

		foreach (BossStageInfo stage in milestoneStages)
		{
			BuildRecommendation rec = Recommender.Recommend(stage, null, new BuildQueryOptions { MaxDifficulty = DifficultyFilterMode.Comfortable });
			string w = string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(rec.PrimaryWeapons, 5), FmtSlot));
			string a = string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(rec.Accessories, 5), FmtSlot));
			string armors = string.Join(" | ", System.Linq.Enumerable.Select(rec.ArmorOptions, opt =>
				$"{Lang.GetItemNameValue(opt.HeadType)}/{Lang.GetItemNameValue(opt.BodyType)}/{Lang.GetItemNameValue(opt.LegsType)}({opt.TotalDefense}def,{opt.Difficulty}@{opt.UnlockProgression:0.##})"));
			Mod.Logger.Info($"[Sample/Comfort] {stage.ModSource}/{stage.DisplayName} ({stage.Progression:0.#}) -> {ItemTooltipAnalyzer.FormatClassDisplayName(rec.TargetClass)}: Counts(W={rec.PrimaryWeapons.Count},Sup={rec.SupportWeapons.Count},Ammo={rec.AmmoOptions.Count},Arm={rec.ArmorOptions.Count},Acc={rec.Accessories.Count}+{rec.AltAccessories.Count},Pot={rec.BuffPotions.Count}) Wpn=[{w}] Arm=[{armors}] Acc=[{a}]");
		}

		// Also verify difficulty modes at King Slime + every DamageClass at Wall of Flesh
		BossStageInfo ksStage = Catalog.AllStages.Find(s => s.NpcIds.Contains(NPCID.KingSlime));
		if (ksStage != null)
		{
			foreach (DifficultyFilterMode mode in new[] { DifficultyFilterMode.Comfortable, DifficultyFilterMode.IncludeHard, DifficultyFilterMode.All })
			{
				BuildRecommendation ksRec = Recommender.Recommend(ksStage, null, new BuildQueryOptions { MaxDifficulty = mode });
				string arm = string.Join(" | ", System.Linq.Enumerable.Select(ksRec.ArmorOptions, opt =>
					$"{Lang.GetItemNameValue(opt.HeadType)}({opt.TotalDefense}def,{opt.Difficulty})"));
				string acc = string.Join(", ", System.Linq.Enumerable.Select(
					System.Linq.Enumerable.Take(System.Linq.Enumerable.Concat(ksRec.Accessories, ksRec.AltAccessories), 8),
					s => $"{Lang.GetItemNameValue(s.ItemType)}({s.Difficulty})"));
				Mod.Logger.Info($"[DiffCheck@KS:{mode}] Class={ItemTooltipAnalyzer.FormatClassDisplayName(ksRec.TargetClass)} W={ksRec.PrimaryWeapons.Count} ArmOpts=[{arm}] Acc({ksRec.Accessories.Count}+{ksRec.AltAccessories.Count})=[{acc}]");
			}
		}

		BossStageInfo wofStage = Catalog.AllStages.Find(s => s.NpcIds.Contains(NPCID.WallofFlesh));
		if (wofStage != null)
		{
			foreach (DamageClass dc in Recommender.GetAvailablePlayableClasses())
			{
				BuildRecommendation dcRec = Recommender.Recommend(wofStage, dc, new BuildQueryOptions());
				string w = string.Join(", ", System.Linq.Enumerable.Select(dcRec.PrimaryWeapons, x => Lang.GetItemNameValue(x.ItemType)));
				Mod.Logger.Info($"[ClassCheck@WoF] {ItemTooltipAnalyzer.FormatClassDisplayName(dc)} ({dc.FullName}): Weapons=[{w}] Armor={(dcRec.ArmorSet?.IsTrueSet == true ? "Set" : "Mix")}:{dcRec.ArmorSet?.TotalDefense ?? 0}def Acc={dcRec.Accessories.Count}+{dcRec.AltAccessories.Count} Pot={dcRec.BuffPotions.Count}");
			}
		}

		if (_uiState != null)
		{
			Mod.Logger.Info($"[UILayout] {_uiState.GetLayoutCheckSummary()}");
		}

		string InspectItem(int id)
		{
			if (!Graph.Items.TryGetValue(id, out ItemUnlockInfo i)) return $"{id}:missing";
			ItemUnlockPath c = i.GetPathFor(DifficultyFilterMode.Comfortable);
			ItemUnlockPath a = i.GetPathFor(DifficultyFilterMode.All);
			string cTxt = c == null ? "null" : $"{c.Difficulty}@{c.UnlockProgression:0.##}({c.SourceType}:{c.SourceSummary})";
			string aTxt = a == null ? "null" : $"{a.Difficulty}@{a.UnlockProgression:0.##}({a.SourceType}:{a.SourceSummary})";
			return $"{Lang.GetItemNameValue(id)}[C={cTxt}|A={aTxt}]";
		}

		Mod.Logger.Info($"[ItemInspect] {InspectItem(ItemID.Obsidian)} ; {InspectItem(ItemID.ObsidianHelm)} ; {InspectItem(ItemID.AncientShadowHelmet)} ; {InspectItem(ItemID.Zenith)} ; {InspectItem(ItemID.SailfishBoots)}");
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

		if (!Main.dedServ)
		{
			Vector2 bcPos = GetBossChecklistButtonPos();
			int vis = Catalog.AllStages.FindAll(s => s.IsVisibleOnChecklist).Count;
			string vis12 = string.Join(" -> ", System.Linq.Enumerable.Take(
				System.Linq.Enumerable.Select(
					System.Linq.Enumerable.Where(Catalog.AllStages, s => s.IsVisibleOnChecklist),
					s => $"{(s.IsDowned ? "[v]" : "[ ]")}{s.DisplayName}({s.Progression:0.#})"),
				12));
			Mod.Logger.Info($"[WorldCheck] Visible={vis}/{Catalog.AllStages.Count} Top12=[{vis12}] BossLogPos=({bcPos.X:0},{bcPos.Y:0}) BtnLoaded={_buttonTexture?.IsLoaded == true} Keybind={ToggleAdvisorKeybind != null}");
		}
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

	private static ulong _lastToggleTick;
	private static bool _bossChecklistReflectionDone;
	private static object _bossLogConfigInstance;
	private static PropertyInfo _bossLogPosProperty;
	private static FieldInfo _bossLogPosField;

	public void ToggleFromHotkey()
	{
		if (_lastToggleTick == Main.GameUpdateCount)
			return;
		_lastToggleTick = Main.GameUpdateCount;

		bool opening = !_isOpen;
		SoundEngine.PlaySound(opening ? SoundID.MenuOpen : SoundID.MenuClose);
		ToggleOpen();
	}

	public override void PostUpdateInput()
	{
		if (Main.dedServ || Main.gameMenu)
			return;

		// ProcessTriggers does not run while paused (e.g. Autopause + Inventory), so handle paused hotkeys here.
		if (!Main.gamePaused)
			return;

		if (Main.drawingPlayerChat || Main.editSign || Main.editChest)
			return;

		if (Main.CurrentInputTextTakerOverride is UI.BossSearchBox)
			return;

		ModKeybind keybind = BossBuildAdvisor.ToggleAdvisorKeybind ?? ToggleAdvisorKeybind;
		if (keybind != null && keybind.JustPressed)
			ToggleFromHotkey();
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

	private Vector2 GetBossChecklistButtonPos()
	{
		if (!_bossChecklistReflectionDone)
		{
			_bossChecklistReflectionDone = true;
			if (ModLoader.TryGetMod("BossChecklist", out Mod bcMod))
			{
				Type bcType = bcMod.GetType();
				FieldInfo cfgField = bcType.GetField("BossLogConfig", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				_bossLogConfigInstance = cfgField?.GetValue(null);
				if (_bossLogConfigInstance != null)
				{
					Type cfgType = _bossLogConfigInstance.GetType();
					_bossLogPosProperty = cfgType.GetProperty("BossLogPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					_bossLogPosField = cfgType.GetField("BossLogPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				}
			}
		}

		if (_bossLogConfigInstance != null)
		{
			object val = _bossLogPosProperty != null
				? _bossLogPosProperty.GetValue(_bossLogConfigInstance)
				: _bossLogPosField?.GetValue(_bossLogConfigInstance);
			if (val is Vector2 pos)
				return pos;
		}

		// BossChecklist default BossLogPos (-270, -50) from bottom-right
		return new Vector2(-270f, -50f);
	}

	private void DrawInventoryToggleButton()
	{
		Vector2 bcPos = GetBossChecklistButtonPos();
		const int w = 32;
		const int h = 34;
		const int gap = 8;

		// BossChecklist centers its ~34x34 book icon at (screenWidth + bcPos.X, screenHeight + bcPos.Y)
		int x = (int)Math.Clamp(Main.screenWidth + bcPos.X - 17f - w - gap, 12f, Main.screenWidth - w - 12f);
		int y = (int)Math.Clamp(Main.screenHeight + bcPos.Y - h * 0.5f, 12f, Main.screenHeight - h - 12f);

		var rect = new Rectangle(x, y, w, h);
		bool hovered = rect.Contains(Main.MouseScreen.ToPoint());

		if (_buttonTexture != null && _buttonTexture.IsLoaded)
		{
			float alpha = hovered || _isOpen ? 1f : 0.78f;
			Main.spriteBatch.Draw(_buttonTexture.Value, rect, Color.White * alpha);
		}
		else
		{
			Color bg = _isOpen
				? new Color(75, 112, 175, 235)
				: (hovered ? new Color(62, 78, 122, 235) : new Color(34, 45, 76, 215));
			Utils.DrawInvBG(Main.spriteBatch, rect, bg);
		}

		if (hovered || _isOpen)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color border = _isOpen ? new Color(110, 220, 255) : new Color(255, 215, 90);
			Main.spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, 2), border);
			Main.spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Bottom - 1, rect.Width + 2, 2), border);
			Main.spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Y - 1, 2, rect.Height + 2), border);
			Main.spriteBatch.Draw(pixel, new Rectangle(rect.Right - 1, rect.Y - 1, 2, rect.Height + 2), border);
		}

		if (hovered)
		{
			Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;

			string keyLabel = "K";
			List<string> keys = ToggleAdvisorKeybind?.GetAssignedKeys();
			if (keys != null && keys.Count > 0 && !string.IsNullOrWhiteSpace(keys[0]))
				keyLabel = keys[0];

			string hoverTitle = Language.GetTextValue("Mods.BossBuildAdvisor.UI.Title") + $" [{keyLabel}]";
			Vector2 strSize = FontAssets.MouseText.Value.MeasureString(hoverTitle) * 0.85f;
			float tx = Math.Clamp(rect.Center.X - strSize.X * 0.5f, 10f, Main.screenWidth - strSize.X - 10f);
			float ty = Math.Max(8f, rect.Y - 24f);
			Utils.DrawBorderStringFourWay(Main.spriteBatch, FontAssets.MouseText.Value, hoverTitle, tx, ty, new Color(255, 228, 130), Color.Black, Vector2.Zero, 0.85f);

			if (Main.mouseLeft && Main.mouseLeftRelease)
			{
				SoundEngine.PlaySound(_isOpen ? SoundID.MenuClose : SoundID.MenuOpen);
				ToggleOpen();
			}
		}
	}
}
