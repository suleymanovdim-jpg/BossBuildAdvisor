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
	private bool _uiNeedsRebuild;
	private bool _worldRefreshPending;
	private bool _analysisRebuildPending;

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
		_isOpen = false;
		_dataBuilt = false;
		_uiNeedsRebuild = false;
		_worldRefreshPending = false;
		_analysisRebuildPending = false;
		_lastToggleTick = 0;
		_bossChecklistReflectionDone = false;
		_bossLogConfigInstance = null;
		_bossLogPosProperty = null;
		_bossLogPosField = null;
	}

	public override void PostSetupRecipes()
	{
		RebuildAnalysisData();
		Mod.Logger.Info($"[BossBuildAdvisor] Ready in {Graph.BuildDurationMs} ms: {Catalog.AllStages.Count} stages, {Graph.Items.Count} items, {ArmorSets.Sets.Count} armor sets.");
	}

	public override void OnLocalizationsLoaded()
	{
		if (!_dataBuilt)
			return;

		// Rebuild semantics & UI strings safely on the main thread when culture changes
		_analysisRebuildPending = true;
		_uiNeedsRebuild = true;
	}

	public void DumpDiagnosticsToLog()
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

		if (_uiState != null)
			Mod.Logger.Info($"[UILayout] {_uiState.GetLayoutCheckSummary()}");
	}

	private void RebuildAnalysisData()
	{
		Catalog.Build(Mod);
		Graph.Build(Mod, Catalog);
		ArmorSets.Build(Mod, Graph);
		Recommender = new BuildRecommender(Catalog, Graph, ArmorSets);
		_dataBuilt = true;
	}

	public void RebuildAnalysis()
	{
		RebuildAnalysisData();
		_uiNeedsRebuild = true;
		EnsureUIInitializedOnMainThread();
	}

	private void EnsureUIInitializedOnMainThread()
	{
		if (Main.dedServ)
			return;

		if (!_dataBuilt || _analysisRebuildPending)
		{
			_analysisRebuildPending = false;
			RebuildAnalysisData();
			_uiNeedsRebuild = true;
		}

		if (_worldRefreshPending)
		{
			_worldRefreshPending = false;
			if (_isOpen)
				_uiState?.RefreshAll();
		}

		if (_userInterface == null || _uiState == null || _uiNeedsRebuild)
		{
			_uiNeedsRebuild = false;
			_userInterface ??= new UserInterface();
			if (_uiState == null)
			{
				_uiState = new AdvisorUIState(Catalog, Graph, Recommender);
				_uiState.Activate();
			}
			else
			{
				_uiState.UpdateDependencies(Catalog, Graph, Recommender);
				if (_isOpen)
					_uiState.RefreshAll();
			}
		}
	}

	public override void OnWorldLoad()
	{
		if (!_dataBuilt)
			RebuildAnalysisData();

		_isOpen = false;
		_worldRefreshPending = true;
	}

	public override void OnWorldUnload()
	{
		_isOpen = false;
		_worldRefreshPending = false;
		if (!Main.dedServ)
		{
			Main.QueueMainThreadAction(() =>
			{
				_uiState?.OnClosed();
				_userInterface?.SetState(null);
			});
		}
	}

	public override void PreSaveAndQuit()
	{
		SetOpen(false);
	}

	public void ToggleOpen() => SetOpen(!_isOpen);

	public void SetOpen(bool open)
	{
		if (Main.dedServ)
			return;

		if (open)
		{
			EnsureUIInitializedOnMainThread();
			if (_userInterface == null || _uiState == null)
				return;

			_isOpen = true;
			Main.playerInventory = false;
			_uiState.RefreshAll();
			_userInterface.SetState(_uiState);
		}
		else
		{
			_isOpen = false;
			_uiState?.OnClosed();
			_userInterface?.SetState(null);
		}
	}

	private static ulong _lastToggleTick;
	private static bool _bossChecklistReflectionDone;
	private static object _bossLogConfigInstance;
	private static PropertyInfo _bossLogPosProperty;
	private static FieldInfo _bossLogPosField;

	public static bool IsHotkeyBlocked()
	{
		return Main.dedServ
			|| Main.gameMenu
			|| !Main.hasFocus
			|| Main.ingameOptionsWindow
			|| Main.drawingPlayerChat
			|| Main.editSign
			|| Main.editChest
			|| Main.inFancyUI
			|| Main.mapFullscreen
			|| Main.CurrentInputTextTakerOverride != null;
	}

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
		// ProcessTriggers does not run while paused (e.g. Autopause + Inventory), so handle paused hotkeys here.
		if (!Main.gamePaused || IsHotkeyBlocked())
			return;

		ModKeybind keybind = BossBuildAdvisor.ToggleAdvisorKeybind ?? ToggleAdvisorKeybind;
		if (keybind != null && keybind.JustPressed)
			ToggleFromHotkey();
	}

	public override void UpdateUI(GameTime gameTime)
	{
		if (Main.dedServ || Main.gameMenu)
			return;

		if (_worldRefreshPending || _analysisRebuildPending || _uiNeedsRebuild)
			EnsureUIInitializedOnMainThread();

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
					if (Main.playerInventory && !Main.gameMenu && !Main.ingameOptionsWindow && !Main.inFancyUI && !Main.mapFullscreen)
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
					if (_isOpen && !Main.gameMenu && !Main.ingameOptionsWindow && !Main.inFancyUI && !Main.mapFullscreen && _userInterface?.CurrentState != null)
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

		// BossChecklist stores BossLogPos as the TOP-LEFT offset from (screenWidth, screenHeight) for its 34x38 icon
		float bookX = Math.Clamp(Main.screenWidth + bcPos.X, 0f, Main.screenWidth - 34f);
		float bookY = Math.Clamp(Main.screenHeight + bcPos.Y, 0f, Main.screenHeight - 38f);
		int y = (int)Math.Clamp(bookY + (38f - h) * 0.5f, 8f, Main.screenHeight - h - 8f);

		float leftX = bookX - gap - w;
		float rightX = bookX + 34f + gap;
		bool chestOrShop = Main.LocalPlayer != null && (Main.LocalPlayer.chest != -1 || Main.npcShop > 0);
		bool leftBlocked = leftX < 8f || (chestOrShop && leftX < 520f && y > 235f && y < 480f);
		float chosenX = leftBlocked && rightX + w <= Main.screenWidth - 8f ? rightX : leftX;
		int x = (int)Math.Clamp(chosenX, 8f, Main.screenWidth - w - 8f);

		var rect = new Rectangle(x, y, w, h);
		bool advisorBlocking = _isOpen && _uiState != null && _uiState.ContainsMouse;
		bool inputBlocking = PlayerInput.IgnoreMouseInterface
			|| advisorBlocking
			|| (Main.LocalPlayer?.mouseInterface ?? false)
			|| Main.blockMouse
			|| Main.drawingPlayerChat
			|| Main.editSign
			|| Main.editChest;
		bool hovered = !inputBlocking && rect.Contains(Main.MouseScreen.ToPoint());

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
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
			Main.HoverItem.TurnToAir();

			string keyLabel = "-";
			List<string> keys = ToggleAdvisorKeybind?.GetAssignedKeys();
			if (keys != null && keys.Count > 0 && !string.IsNullOrWhiteSpace(keys[0]))
				keyLabel = keys[0];

			string hoverTitle = string.Format(Language.GetTextValue("Mods.BossBuildAdvisor.UI.OpenButtonTooltip"), keyLabel);
			Vector2 strSize = FontAssets.MouseText.Value.MeasureString(hoverTitle) * 0.85f;
			float tx = Math.Clamp(rect.Center.X - strSize.X * 0.5f, 10f, Main.screenWidth - strSize.X - 10f);
			float ty = Math.Max(8f, rect.Y - 24f);
			Utils.DrawBorderStringFourWay(Main.spriteBatch, FontAssets.MouseText.Value, hoverTitle, tx, ty, new Color(255, 228, 130), Color.Black, Vector2.Zero, 0.85f);

			if (Main.mouseLeft && Main.mouseLeftRelease)
			{
				Main.mouseLeftRelease = false;
				SoundEngine.PlaySound(_isOpen ? SoundID.MenuClose : SoundID.MenuOpen);
				ToggleOpen();
			}
		}
	}
}
