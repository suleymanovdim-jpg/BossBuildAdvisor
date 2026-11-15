using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using BossBuildAdvisor.Core;

namespace BossBuildAdvisor.UI;

public sealed class AdvisorUIState : UIState
{
	private BossCatalog _catalog;
	private ProgressionGraph _graph;
	private BuildRecommender _recommender;

	private UIPanel _rootPanel;
	private UIElement _headerBar;
	private UIText _titleText;
	private UIText _subtitleText;
	private bool _dragging;
	private Vector2 _dragOffset;

	private UIPanel _leftCol;
	private UIElement _rightCol;
	private UIPanel _bannerPanel;

	private BossSearchBox _searchBox;
	private UIList _bossList;
	private readonly List<BossStageRow> _bossRows = new();

	private UIElement _classTabsBar;
	private readonly List<(DamageClass Dc, AdvisorButton Btn)> _classButtons = new();

	private AdvisorButton _closeBtn;
	private AdvisorButton _nextBtn;
	private AdvisorButton _includeDropsBtn;
	private AdvisorButton _difficultyBtn;
	private AdvisorButton _includeEstBtn;
	private AdvisorButton _includeMiniBtn;

	private UIText _bossBannerText;
	private UIText _bossMetaText;

	private UIPanel _weaponsPanel;
	private UIPanel _armorPanel;
	private UIPanel _accessoriesPanel;
	private UIPanel _potionsPanel;
	private UIPanel _detailsPanel;

	private UIText _weaponsHeader;
	private UIText _armorBonusText;
	private UIText _accessoriesHeader;
	private readonly AdvisorButton[] _armorOptionButtons = new AdvisorButton[3];

	private readonly List<AdvisorItemSlot> _allSlots = new();

	private BossStageInfo _selectedStage;
	private DamageClass _selectedClass; // null = Auto
	private readonly BuildQueryOptions _options = new();
	private bool _includeMinibossesAndEvents = true;
	private string _searchFilter = string.Empty;

	private BuildRecommendation _currentRec;
	private int _selectedArmorOptionIndex;
	private RecommendedSlotItem _inspectedItem;

	private float _panelW = 1040f;
	private float _panelH = 646f;
	private float _rightColW = 736f;
	private float _topRowH = 160f;
	private float _midRowH = 152f;
	private float _lastViewportW = -1f;
	private float _lastViewportH = -1f;

	public AdvisorUIState(BossCatalog catalog, ProgressionGraph graph, BuildRecommender recommender)
	{
		_catalog = catalog;
		_graph = graph;
		_recommender = recommender;
	}

	public void UpdateDependencies(BossCatalog catalog, ProgressionGraph graph, BuildRecommender recommender)
	{
		_catalog = catalog;
		_graph = graph;
		_recommender = recommender;
	}

	public bool ContainsMouse => _rootPanel != null && _rootPanel.ContainsPoint(Main.MouseScreen);

	public override void OnInitialize()
	{
		ComputeResponsiveMetrics(out _panelW, out _panelH, out float leftW);

		_rootPanel = new UIPanel
		{
			Width = StyleDimension.FromPixels(_panelW),
			Height = StyleDimension.FromPixels(_panelH),
			Left = StyleDimension.FromPixels(Math.Max(8f, (Main.screenWidth - _panelW) * 0.5f)),
			Top = StyleDimension.FromPixels(Math.Max(8f, (Main.screenHeight - _panelH) * 0.5f)),
			BackgroundColor = new Color(25, 33, 58, 240),
			BorderColor = new Color(12, 16, 30, 245),
			OverflowHidden = true
		};
		_rootPanel.SetPadding(10f);
		Append(_rootPanel);

		BuildHeader();
		BuildLeftBossColumn(leftW);
		BuildRightRecommendationColumn(leftW);
		ApplyResponsiveLayout(force: true);
	}

	private static void ComputeResponsiveMetrics(out float panelW, out float panelH, out float leftW)
	{
		panelW = Math.Clamp(Main.screenWidth - 20f, 680f, 1040f);
		panelH = Math.Clamp(Main.screenHeight - 20f, 480f, 646f);
		leftW = Math.Clamp(panelW * 0.265f, 210f, 276f);
	}

	private void BuildHeader()
	{
		_headerBar = new UIElement
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(44f),
			OverflowHidden = true
		};
		_headerBar.OnLeftMouseDown += StartDrag;
		_headerBar.OnLeftMouseUp += EndDrag;
		_rootPanel.Append(_headerBar);

		_titleText = new UIText(Loc("UI.Title"), 1.02f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(2f),
			TextColor = new Color(255, 222, 110)
		};
		_headerBar.Append(_titleText);

		_subtitleText = new UIText(Loc("UI.Subtitle"), 0.70f)
		{
			Left = StyleDimension.FromPixels(6f),
			Top = StyleDimension.FromPixels(24f),
			TextColor = new Color(175, 195, 230)
		};
		_headerBar.Append(_subtitleText);

		_closeBtn = new AdvisorButton("X", () => Systems.AdvisorSystem.Instance.SetOpen(false), 0.85f, new Color(120, 42, 46, 235), new Color(160, 58, 62, 245))
		{
			Width = StyleDimension.FromPixels(32f),
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(_closeBtn);

		_includeMiniBtn = new AdvisorButton(Loc("UI.IncludeMinibossesOn"), ToggleMinibosses, 0.68f)
		{
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_includeMiniBtn.SetActive(_includeMinibossesAndEvents);
		_headerBar.Append(_includeMiniBtn);

		_difficultyBtn = new AdvisorButton(GetDifficultyButtonLabel(_options.MaxDifficulty), CycleDifficultyFilter, 0.68f)
		{
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_difficultyBtn.SetActive(_options.MaxDifficulty != DifficultyFilterMode.Comfortable);
		_headerBar.Append(_difficultyBtn);

		_includeEstBtn = new AdvisorButton(Loc("UI.IncludeEstimatedOn"), ToggleEstimated, 0.68f)
		{
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_includeEstBtn.SetActive(_options.IncludeTierEstimatedItems);
		_headerBar.Append(_includeEstBtn);

		_includeDropsBtn = new AdvisorButton(Loc("UI.IncludeBossDropsOff"), ToggleBossDrops, 0.68f)
		{
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(_includeDropsBtn);

		_nextBtn = new AdvisorButton(Loc("UI.JumpToNextBoss"), JumpToNextBoss, 0.70f, new Color(56, 88, 66, 230), new Color(75, 120, 88, 240))
		{
			Height = StyleDimension.FromPixels(30f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(_nextBtn);
	}

	private void BuildLeftBossColumn(float leftW)
	{
		_leftCol = new UIPanel
		{
			Top = StyleDimension.FromPixels(50f),
			Width = StyleDimension.FromPixels(leftW),
			Height = StyleDimension.FromPixelsAndPercent(-54f, 1f),
			BackgroundColor = new Color(18, 24, 42, 220),
			BorderColor = new Color(12, 16, 30),
			OverflowHidden = true
		};
		_leftCol.SetPadding(6f);
		_rootPanel.Append(_leftCol);

		_searchBox = new BossSearchBox(Loc("UI.SearchPlaceholder"), txt =>
		{
			_searchFilter = txt ?? string.Empty;
			PopulateBossList();
		})
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(30f)
		};
		_leftCol.Append(_searchBox);

		_bossList = new UIList
		{
			Top = StyleDimension.FromPixels(36f),
			Width = StyleDimension.FromPixelsAndPercent(-24f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-38f, 1f),
			ListPadding = 4f,
			ManualSortMethod = _ => { }
		};
		_leftCol.Append(_bossList);

		var scrollbar = new UIScrollbar
		{
			Top = StyleDimension.FromPixels(38f),
			Left = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-42f, 1f)
		};
		scrollbar.SetView(100f, 1000f);
		_leftCol.Append(scrollbar);
		_bossList.SetScrollbar(scrollbar);
	}

	private void BuildRightRecommendationColumn(float leftW)
	{
		_rightCol = new UIElement
		{
			Left = StyleDimension.FromPixels(leftW + 8f),
			Top = StyleDimension.FromPixels(50f),
			Width = StyleDimension.FromPixelsAndPercent(-(leftW + 8f), 1f),
			Height = StyleDimension.FromPixelsAndPercent(-54f, 1f)
		};
		_rootPanel.Append(_rightCol);

		_classTabsBar = new UIElement
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(30f)
		};
		_rightCol.Append(_classTabsBar);

		_bannerPanel = new UIPanel
		{
			Top = StyleDimension.FromPixels(34f),
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(42f),
			BackgroundColor = new Color(32, 42, 72, 225),
			BorderColor = new Color(16, 22, 40),
			OverflowHidden = true
		};
		_bannerPanel.SetPadding(6f);
		_rightCol.Append(_bannerPanel);

		_bossBannerText = new UIText("...", 0.90f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(1f),
			TextColor = new Color(255, 225, 115)
		};
		_bannerPanel.Append(_bossBannerText);

		_bossMetaText = new UIText("...", 0.70f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(21f),
			TextColor = new Color(185, 200, 230)
		};
		_bannerPanel.Append(_bossMetaText);

		// Weapons + Support + Ammo (top-left, 2x7 grid)
		_weaponsPanel = CreateSectionPanel(0f, 80f, 376f, 160f, string.Format(Loc("UI.SectionWeapons"), 0));
		_rightCol.Append(_weaponsPanel);
		_weaponsHeader = _weaponsPanel.Children.OfType<UIText>().FirstOrDefault();

		// Armor Sets (top-right, 3 armor slots + persistent #1/#2/#3 set switcher)
		_armorPanel = CreateSectionPanel(384f, 80f, -384f, 160f, Loc("UI.SectionArmor"), widthPercent: 1f);
		_rightCol.Append(_armorPanel);

		for (int i = 0; i < 3; i++)
		{
			int optionIdx = i;
			var tabBtn = new AdvisorButton(string.Format(Loc("UI.ArmorOptionTab"), i + 1), () =>
			{
				_selectedArmorOptionIndex = optionIdx;
				RenderArmorSection(reselectSlot: true);
				RenderInspectedItemDetails();
			}, 0.68f)
			{
				Width = StyleDimension.FromPixels(34f),
				Height = StyleDimension.FromPixels(20f),
				Top = StyleDimension.FromPixels(-1f)
			};
			_armorOptionButtons[i] = tabBtn;
		}

		_armorBonusText = new UIText("", 0.65f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(78f),
			Width = StyleDimension.FromPixelsAndPercent(-8f, 1f),
			IsWrapped = true,
			TextColor = new Color(180, 225, 195)
		};
		_armorPanel.Append(_armorBonusText);

		// Accessories (mid-left, 2x7 = 14 slots: core + alternatives)
		_accessoriesPanel = CreateSectionPanel(0f, 246f, 424f, 152f, Loc("UI.SectionAccessories"));
		_rightCol.Append(_accessoriesPanel);
		_accessoriesHeader = _accessoriesPanel.Children.OfType<UIText>().FirstOrDefault();

		// Potions & Buffs (mid-right, 2x5 slots)
		_potionsPanel = CreateSectionPanel(432f, 246f, -432f, 152f, Loc("UI.SectionPotions"), widthPercent: 1f);
		_rightCol.Append(_potionsPanel);

		// Selected Item Details (bottom)
		_detailsPanel = CreateSectionPanel(0f, 404f, 0f, -404f, Loc("UI.SectionDetails"), widthPercent: 1f, heightPercent: 1f);
		_rightCol.Append(_detailsPanel);
	}

	private static UIPanel CreateSectionPanel(float left, float top, float widthPx, float heightPx, string header, float widthPercent = 0f, float heightPercent = 0f)
	{
		var panel = new UIPanel
		{
			Left = StyleDimension.FromPixels(left),
			Top = StyleDimension.FromPixels(top),
			Width = StyleDimension.FromPixelsAndPercent(widthPx, widthPercent),
			Height = StyleDimension.FromPixelsAndPercent(heightPx, heightPercent),
			BackgroundColor = new Color(20, 28, 48, 225),
			BorderColor = new Color(14, 18, 34),
			OverflowHidden = true
		};
		panel.SetPadding(7f);

		var label = new UIText(header, 0.78f)
		{
			Left = StyleDimension.FromPixels(2f),
			Top = StyleDimension.FromPixels(0f),
			TextColor = new Color(210, 225, 255)
		};
		panel.Append(label);
		return panel;
	}

	public void RefreshAll()
	{
		ClampPanelToViewport();
		RebuildClassTabs();

		if (_selectedStage == null || !_catalog.AllStages.Contains(_selectedStage) || !_selectedStage.IsVisibleOnChecklist)
			_selectedStage = _catalog.GetNextUndefeatedBoss(_includeMinibossesAndEvents);

		PopulateBossList();
		RebuildRecommendation();
	}

	private void ApplyResponsiveLayout(bool force = false)
	{
		if (_rootPanel == null)
			return;

		if (!force && Math.Abs(_lastViewportW - Main.screenWidth) < 1f && Math.Abs(_lastViewportH - Main.screenHeight) < 1f)
			return;

		_lastViewportW = Main.screenWidth;
		_lastViewportH = Main.screenHeight;

		ComputeResponsiveMetrics(out _panelW, out _panelH, out float leftW);
		_rootPanel.Width.Set(_panelW, 0f);
		_rootPanel.Height.Set(_panelH, 0f);

		// Header button layout (right-aligned, proportionally scaled if viewport is narrow)
		float innerHeaderW = _panelW - 20f;
		float scaleFactor = Math.Clamp((innerHeaderW - 230f) / 736f, 0.72f, 1f);
		float closeW = 32f;
		float miniW = (float)Math.Floor(148f * scaleFactor);
		float diffW = (float)Math.Floor(148f * scaleFactor);
		float estW = (float)Math.Floor(120f * scaleFactor);
		float dropW = (float)Math.Floor(142f * scaleFactor);
		float nextW = (float)Math.Floor(102f * scaleFactor);
		float gap = 5f;

		float rx = 0f;
		void PlaceRight(AdvisorButton btn, float w)
		{
			btn.Width.Set(w, 0f);
			btn.Left.Set(-(rx + w), 1f);
			rx += w + gap;
		}
		PlaceRight(_closeBtn, closeW);
		PlaceRight(_includeMiniBtn, miniW);
		PlaceRight(_difficultyBtn, diffW);
		PlaceRight(_includeEstBtn, estW);
		PlaceRight(_includeDropsBtn, dropW);
		PlaceRight(_nextBtn, nextW);

		_leftCol.Width.Set(leftW, 0f);
		float rightX = leftW + 8f;
		_rightCol.Left.Set(rightX, 0f);
		_rightCol.Width.Set(-rightX, 1f);
		_rightColW = Math.Max(360f, _panelW - 20f - rightX);

		float availRightH = _panelH - 20f - 50f - 80f;
		_topRowH = Math.Clamp((float)Math.Floor(availRightH * 0.32f), 128f, 160f);
		_midRowH = Math.Clamp((float)Math.Floor(availRightH * 0.30f), 124f, 152f);
		float midRowY = 80f + _topRowH + 6f;
		float detRowY = midRowY + _midRowH + 6f;

		float wpnW = (float)Math.Floor((_rightColW - 8f) * 0.515f);
		float accW = (float)Math.Floor((_rightColW - 8f) * 0.58f);

		_weaponsPanel.Top.Set(80f, 0f);
		_weaponsPanel.Width.Set(wpnW, 0f);
		_weaponsPanel.Height.Set(_topRowH, 0f);

		_armorPanel.Left.Set(wpnW + 8f, 0f);
		_armorPanel.Top.Set(80f, 0f);
		_armorPanel.Width.Set(-(wpnW + 8f), 1f);
		_armorPanel.Height.Set(_topRowH, 0f);

		_accessoriesPanel.Top.Set(midRowY, 0f);
		_accessoriesPanel.Width.Set(accW, 0f);
		_accessoriesPanel.Height.Set(_midRowH, 0f);

		_potionsPanel.Left.Set(accW + 8f, 0f);
		_potionsPanel.Top.Set(midRowY, 0f);
		_potionsPanel.Width.Set(-(accW + 8f), 1f);
		_potionsPanel.Height.Set(_midRowH, 0f);

		_detailsPanel.Top.Set(detRowY, 0f);
		_detailsPanel.Height.Set(-detRowY, 1f);

		_rootPanel.Recalculate();
	}

	private void ClampPanelToViewport()
	{
		if (_rootPanel == null)
			return;

		ApplyResponsiveLayout();

		float maxLeft = Math.Max(8f, Main.screenWidth - _panelW - 8f);
		float maxTop = Math.Max(8f, Main.screenHeight - _panelH - 8f);
		_rootPanel.Left.Set(Math.Clamp(_rootPanel.Left.Pixels, 8f, maxLeft), 0f);
		_rootPanel.Top.Set(Math.Clamp(_rootPanel.Top.Pixels, 8f, maxTop), 0f);
		_rootPanel.Recalculate();
	}

	private void RebuildClassTabs()
	{
		_classTabsBar.RemoveAllChildren();
		_classButtons.Clear();

		var classes = new List<DamageClass> { null };
		classes.AddRange(_recommender.GetAvailablePlayableClasses());

		int count = classes.Count;
		float gap = 4f;
		float maxW = _rightColW;
		float totalUsable = Math.Max(120f, maxW - (count - 1) * gap);

		var labels = new List<string>(count);
		var weights = new float[count];
		float weightSum = 0f;
		for (int i = 0; i < count; i++)
		{
			DamageClass dc = classes[i];
			string label = dc == null ? Loc("UI.AllClasses") : ShortenClassName(ItemTooltipAnalyzer.FormatClassDisplayName(dc));
			labels.Add(label);
			float wt = Math.Clamp(label.Length + 3f, 7f, 18f);
			weights[i] = wt;
			weightSum += wt;
		}

		float x = 0f;
		for (int i = 0; i < count; i++)
		{
			DamageClass dc = classes[i];
			float btnW = i == count - 1
				? Math.Max(52f, maxW - x)
				: Math.Clamp((float)Math.Floor(totalUsable * (weights[i] / Math.Max(1f, weightSum))), 52f, 150f);

			var btn = new AdvisorButton(labels[i], () =>
			{
				_selectedClass = dc;
				UpdateClassButtonStates();
				RebuildRecommendation();
			}, 0.72f)
			{
				Left = StyleDimension.FromPixels(x),
				Width = StyleDimension.FromPixels(btnW),
				Height = StyleDimension.FromPixels(30f)
			};
			btn.SetActive(dc == _selectedClass);
			_classTabsBar.Append(btn);
			_classButtons.Add((dc, btn));
			x += btnW + gap;
		}
	}

	private void UpdateClassButtonStates()
	{
		foreach ((DamageClass dc, AdvisorButton btn) in _classButtons)
			btn.SetActive(dc == _selectedClass);
	}

	private void PopulateBossList()
	{
		_bossList.Clear();
		_bossRows.Clear();

		BossStageInfo nextTarget = _catalog.GetNextUndefeatedBoss(_includeMinibossesAndEvents);
		string query = _searchFilter.Trim();

		foreach (BossStageInfo stage in _catalog.AllStages)
		{
			if (!stage.IsVisibleOnChecklist)
				continue;

			if (!_includeMinibossesAndEvents && !stage.IsBoss)
				continue;

			if (!string.IsNullOrEmpty(query)
				&& !stage.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
				&& !stage.ModSource.Contains(query, StringComparison.OrdinalIgnoreCase))
				continue;

			var row = new BossStageRow(stage, stage == nextTarget, s =>
			{
				_selectedStage = s;
				foreach (BossStageRow r in _bossRows)
					r.SetSelected(r.Stage == _selectedStage);
				RebuildRecommendation();
			});
			row.SetSelected(stage == _selectedStage);
			_bossList.Add(row);
			_bossRows.Add(row);
		}
	}

	private void RebuildRecommendation()
	{
		if (_selectedStage == null)
			return;

		_currentRec = _recommender.Recommend(_selectedStage, _selectedClass, _options);
		_selectedArmorOptionIndex = 0;
		_inspectedItem = _currentRec.PrimaryWeapons.FirstOrDefault() ?? _currentRec.HeadSlot;

		RenderRecommendation();
	}

	private void RenderRecommendation()
	{
		if (_currentRec == null)
			return;

		BossStageInfo nextTarget = _catalog.GetNextUndefeatedBoss(_includeMinibossesAndEvents);
		string statusText = _selectedStage.IsDowned
			? Loc("UI.BossDefeated")
			: (_selectedStage == nextTarget ? Loc("UI.BossNextTarget") : Loc("UI.BossUpcoming"));

		string classDisplay = _currentRec.IsAutoClass
			? $"{Loc("UI.AllClasses")} -> {ItemTooltipAnalyzer.FormatClassDisplayName(_currentRec.TargetClass)}"
			: ItemTooltipAnalyzer.FormatClassDisplayName(_currentRec.TargetClass);

		string stagePrefix = string.Format(Loc("UI.StagePrefix"), _selectedStage.Progression);
		_bossBannerText.SetText($"{_selectedStage.DisplayName}  [{_selectedStage.ModSource}]");
		_bossMetaText.SetText($"{stagePrefix}  |  {statusText}  |  {classDisplay}  |  {GetDifficultyButtonLabel(_options.MaxDifficulty)}");

		_allSlots.Clear();
		ClearSlotsExceptHeader(_weaponsPanel);
		ClearSlotsExceptHeader(_accessoriesPanel);
		ClearSlotsExceptHeader(_potionsPanel);

		// Weapons (up to 8) + Support (up to 4) + Ammo (up to 2) in 2x7 grid
		var weaponSlots = new List<RecommendedSlotItem>();
		weaponSlots.AddRange(_currentRec.PrimaryWeapons.Take(8));
		int remaining = 14 - weaponSlots.Count;
		int ammoTake = Math.Min(_currentRec.AmmoOptions.Count, _currentRec.SupportWeapons.Count <= 2 ? 2 : 1);
		int supTake = Math.Min(_currentRec.SupportWeapons.Count, remaining - ammoTake);
		weaponSlots.AddRange(_currentRec.SupportWeapons.Take(supTake));
		weaponSlots.AddRange(_currentRec.AmmoOptions.Take(Math.Min(_currentRec.AmmoOptions.Count, 14 - weaponSlots.Count)));

		float wpnInnerW = Math.Max(200f, _weaponsPanel.GetInnerDimensions().Width);
		float wpnSize = CalcSlotSize(wpnInnerW, 7, 5f, 44f);
		_weaponsHeader?.SetText(string.Format(Loc("UI.SectionWeapons"), weaponSlots.Count));
		PlaceSlotGrid(_weaponsPanel, weaponSlots, startX: 4f, startY: 22f, slotSize: wpnSize, gap: 5f, cols: 7, shortTagFn: ShortRole);

		// Armor Sets (#1 / #2 / #3)
		RenderArmorSection(reselectSlot: false);

		// Accessories (up to 14: core equipped + alternatives)
		var allAccessories = new List<RecommendedSlotItem>();
		allAccessories.AddRange(_currentRec.Accessories);
		allAccessories.AddRange(_currentRec.AltAccessories);
		float accInnerW = Math.Max(200f, _accessoriesPanel.GetInnerDimensions().Width);
		float accSize = CalcSlotSize(accInnerW, 7, 6f, 48f);
		_accessoriesHeader?.SetText(string.Format(Loc("UI.SectionAccessories"), _currentRec.Accessories.Count, _currentRec.AltAccessories.Count));
		PlaceSlotGrid(_accessoriesPanel, allAccessories, startX: 4f, startY: 24f, slotSize: accSize, gap: 6f, cols: 7, shortTagFn: ShortRole,
			isAlternativeFn: idx => idx >= _currentRec.Accessories.Count);

		// Potions & Buffs (1 HP + 8 buffs = up to 9 in 2x5 grid)
		var potionSlots = new List<RecommendedSlotItem>();
		if (_currentRec.HealingPotion != null) potionSlots.Add(_currentRec.HealingPotion);
		potionSlots.AddRange(_currentRec.BuffPotions.Take(8));
		float potInnerW = Math.Max(160f, _potionsPanel.GetInnerDimensions().Width);
		float potSize = CalcSlotSize(potInnerW, 5, 5f, 44f);
		string hpTag = Loc("UI.SlotTagHP");
		PlaceSlotGrid(_potionsPanel, potionSlots, startX: 4f, startY: 24f, slotSize: potSize, gap: 5f, cols: 5, shortTagFn: s => s.RoleTag == "Healing" ? hpTag : "");

		RenderInspectedItemDetails();
	}

	private static float CalcSlotSize(float innerW, int cols, float gap, float maxSize)
	{
		if (innerW <= 0f)
			return maxSize;

		float usable = Math.Max(80f, innerW - 8f - (cols - 1) * gap);
		return Math.Clamp((float)Math.Floor(usable / cols), 30f, maxSize);
	}

	private void RenderArmorSection(bool reselectSlot)
	{
		if (_currentRec == null)
			return;

		_allSlots.RemoveAll(s => s.Parent == _armorPanel);
		ClearSlotsExceptHeader(_armorPanel, keepText: _armorBonusText, keepPersistent: true);

		int optCount = _currentRec.ArmorOptions.Count;
		int idx = optCount > 0 ? Math.Clamp(_selectedArmorOptionIndex, 0, optCount - 1) : 0;
		_selectedArmorOptionIndex = idx;

		for (int i = 0; i < 3; i++)
		{
			AdvisorButton tabBtn = _armorOptionButtons[i];
			if (i < optCount)
			{
				tabBtn.Left.Set(-(optCount - i) * 38f, 1f);
				tabBtn.Top.Set(-1f, 0f);
				tabBtn.SetActive(i == idx);
				if (tabBtn.Parent == null)
					_armorPanel.Append(tabBtn);
				tabBtn.Recalculate();
			}
			else if (tabBtn.Parent != null)
			{
				_armorPanel.RemoveChild(tabBtn);
			}
		}

		ArmorSetCandidate activeSet = optCount > 0 ? _currentRec.ArmorOptions[idx] : _currentRec.ArmorSet;
		var armorSlots = new List<RecommendedSlotItem>();
		RecommendedSlotItem head = activeSet?.HeadSlot ?? _currentRec.HeadSlot;
		RecommendedSlotItem body = activeSet?.BodySlot ?? _currentRec.BodySlot;
		RecommendedSlotItem legs = activeSet?.LegsSlot ?? _currentRec.LegsSlot;
		if (head != null) armorSlots.Add(head);
		if (body != null) armorSlots.Add(body);
		if (legs != null) armorSlots.Add(legs);

		float armorSlotSize = _topRowH >= 150f ? 48f : 42f;
		PlaceSlotGrid(_armorPanel, armorSlots, startX: 4f, startY: 24f, slotSize: armorSlotSize, gap: 8f, cols: 3, shortTagFn: ArmorShortTag);

		_armorBonusText.Top.Set(24f + armorSlotSize + 5f, 0f);
		if (activeSet != null)
		{
			string defLine = string.Format(Loc("UI.TotalDefense"), activeSet.TotalDefense);
			string diffName = GetDifficultyLabel(activeSet.Difficulty);
			string bonusLine = activeSet.IsTrueSet
				? (string.IsNullOrWhiteSpace(activeSet.SetBonusSummary)
					? string.Format(Loc("UI.ArmorSetFromMod"), activeSet.ModName)
					: activeSet.SetBonusSummary)
				: Loc("UI.NoArmorSetBonus");
			_armorBonusText.SetText($"{defLine} | {diffName} | {bonusLine}");
		}
		else
		{
			_armorBonusText.SetText("-");
		}

		if (reselectSlot && head != null)
		{
			_inspectedItem = head;
			foreach (AdvisorItemSlot s in _allSlots)
				s.SetSelected(s.SlotData == _inspectedItem);
		}
	}

	private void PlaceSlotGrid(
		UIPanel container,
		List<RecommendedSlotItem> items,
		float startX,
		float startY,
		float slotSize,
		float gap,
		int cols,
		Func<RecommendedSlotItem, string> shortTagFn,
		Func<int, bool> isAlternativeFn = null)
	{
		for (int i = 0; i < items.Count; i++)
		{
			int col = i % cols;
			int row = i / cols;
			RecommendedSlotItem data = items[i];
			bool isAlt = isAlternativeFn != null && isAlternativeFn(i);

			var slot = new AdvisorItemSlot(data, shortTagFn(data), slotSize, clicked =>
			{
				_inspectedItem = clicked;
				foreach (AdvisorItemSlot s in _allSlots)
					s.SetSelected(s.SlotData == _inspectedItem);
				RenderInspectedItemDetails();
			}, isAlt)
			{
				Left = StyleDimension.FromPixels(startX + col * (slotSize + gap)),
				Top = StyleDimension.FromPixels(startY + row * (slotSize + gap))
			};
			slot.SetSelected(data == _inspectedItem);
			container.Append(slot);
			_allSlots.Add(slot);
		}
	}

	private void RenderInspectedItemDetails()
	{
		ClearSlotsExceptHeader(_detailsPanel);

		if (_inspectedItem == null || !ContentSamples.ItemsByType.TryGetValue(_inspectedItem.ItemType, out Item item))
		{
			_detailsPanel.Append(new UIText(Loc("UI.ClickItemHint"), 0.78f)
			{
				Left = StyleDimension.FromPixels(4f),
				Top = StyleDimension.FromPixels(26f),
				Width = StyleDimension.FromPixelsAndPercent(-10f, 1f),
				IsWrapped = true,
				TextColor = new Color(185, 200, 225)
			});
			return;
		}

		var previewSlot = new AdvisorItemSlot(_inspectedItem, "", 54f, null)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(26f)
		};
		_detailsPanel.Append(previewSlot);

		string modName = item.ModItem?.Mod?.Name ?? "Terraria";
		string confText = _inspectedItem.Confidence switch
		{
			SourceConfidence.Verified => Loc("UI.ConfidenceVerified"),
			SourceConfidence.Crafted => Loc("UI.ConfidenceCrafted"),
			_ => Loc("UI.ConfidenceTierEstimated")
		};
		Color confColor = _inspectedItem.Confidence switch
		{
			SourceConfidence.Verified => new Color(130, 235, 145),
			SourceConfidence.Crafted => new Color(135, 200, 255),
			_ => new Color(255, 200, 115)
		};

		float panelInnerW = _detailsPanel.GetInnerDimensions().Width;
		if (panelInnerW <= 60f)
			panelInnerW = Math.Max(260f, _rightColW - 14f);
		float textWrapW = Math.Max(180f, panelInnerW - 76f);

		float y = 22f;
		y += AppendDetailLine($"{item.Name}  ({string.Format(Loc("UI.ModOrigin"), modName)})", y, 0.86f, new Color(255, 228, 120), textWrapW);

		string roleDisp = FormatDetailRole(_inspectedItem.RoleTag, _currentRec?.TargetClass);
		string stageText = string.Format(Loc("UI.UnlockStage"), _inspectedItem.UnlockProgression, confText);
		string unlockLine = string.IsNullOrEmpty(roleDisp) ? stageText : $"{roleDisp}  |  {stageText}";
		y += AppendDetailLine(unlockLine, y, 0.69f, confColor, textWrapW);

		string diffLabel = GetDifficultyLabel(_inspectedItem.Difficulty);
		string diffReason = string.IsNullOrWhiteSpace(_inspectedItem.DifficultyReason) ? confText : _inspectedItem.DifficultyReason;
		Color diffColor = GetDifficultyColor(_inspectedItem.Difficulty);
		y += AppendDetailLine(string.Format(Loc("UI.DifficultyLine"), diffLabel, diffReason), y, 0.69f, diffColor, textWrapW);

		y += AppendDetailLine(string.Format(Loc("UI.SourcePrefix"), _inspectedItem.SourceSummary ?? "-"), y, 0.70f, Color.White, textWrapW);
		AppendDetailLine(string.Format(Loc("UI.ReasonsPrefix"), _inspectedItem.ReasonSummary ?? "-"), y, 0.67f, new Color(185, 205, 230), textWrapW);
	}

	private float AppendDetailLine(string text, float topY, float scale, Color color, float textWrapW)
	{
		var txt = new UIText(text, scale)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(topY),
			Width = StyleDimension.FromPixelsAndPercent(-74f, 1f),
			IsWrapped = true,
			TextColor = color
		};
		_detailsPanel.Append(txt);

		if (Terraria.GameContent.FontAssets.MouseText?.IsLoaded != true)
			return 22f * scale + 3f;

		int maxCharsPx = (int)Math.Max(120f, textWrapW / scale);
		Utils.WordwrapString(text, Terraria.GameContent.FontAssets.MouseText.Value, maxCharsPx, 12, out int lineAmount);
		int lines = Math.Max(1, lineAmount + 1);
		return lines * (22f * scale) + 2f;
	}

	private static void ClearSlotsExceptHeader(UIPanel panel, UIElement keepText = null, bool keepPersistent = false)
	{
		var toRemove = new List<UIElement>();
		bool firstTextKept = false;
		foreach (UIElement child in panel.Children)
		{
			if (child == keepText || (keepPersistent && child is AdvisorButton))
				continue;

			if (!firstTextKept && child is UIText)
			{
				firstTextKept = true;
				continue;
			}
			toRemove.Add(child);
		}
		foreach (UIElement e in toRemove)
			panel.RemoveChild(e);
	}

	private void JumpToNextBoss()
	{
		_selectedStage = _catalog.GetNextUndefeatedBoss(_includeMinibossesAndEvents);
		PopulateBossList();
		RebuildRecommendation();
	}

	private void ToggleBossDrops()
	{
		_options.IncludeTargetBossDrops = !_options.IncludeTargetBossDrops;
		_includeDropsBtn.SetText(_options.IncludeTargetBossDrops ? Loc("UI.IncludeBossDropsOn") : Loc("UI.IncludeBossDropsOff"));
		_includeDropsBtn.SetActive(_options.IncludeTargetBossDrops);
		RebuildRecommendation();
	}

	private void ToggleEstimated()
	{
		_options.IncludeTierEstimatedItems = !_options.IncludeTierEstimatedItems;
		_includeEstBtn.SetText(_options.IncludeTierEstimatedItems ? Loc("UI.IncludeEstimatedOn") : Loc("UI.IncludeEstimatedOff"));
		_includeEstBtn.SetActive(_options.IncludeTierEstimatedItems);
		RebuildRecommendation();
	}

	private void ToggleMinibosses()
	{
		_includeMinibossesAndEvents = !_includeMinibossesAndEvents;
		_includeMiniBtn.SetText(_includeMinibossesAndEvents ? Loc("UI.IncludeMinibossesOn") : Loc("UI.IncludeMinibossesOff"));
		_includeMiniBtn.SetActive(_includeMinibossesAndEvents);
		PopulateBossList();
		RebuildRecommendation();
	}

	private void CycleDifficultyFilter()
	{
		_options.MaxDifficulty = _options.MaxDifficulty switch
		{
			DifficultyFilterMode.Comfortable => DifficultyFilterMode.IncludeHard,
			DifficultyFilterMode.IncludeHard => DifficultyFilterMode.All,
			_ => DifficultyFilterMode.Comfortable
		};
		_difficultyBtn.SetText(GetDifficultyButtonLabel(_options.MaxDifficulty));
		_difficultyBtn.SetActive(_options.MaxDifficulty != DifficultyFilterMode.Comfortable);
		RebuildRecommendation();
	}

	private static string GetDifficultyButtonLabel(DifficultyFilterMode mode) => mode switch
	{
		DifficultyFilterMode.Comfortable => Loc("UI.DifficultyComfortable"),
		DifficultyFilterMode.IncludeHard => Loc("UI.DifficultyIncludeHard"),
		_ => Loc("UI.DifficultyAll")
	};

	private static string GetDifficultyLabel(ObtainabilityDifficulty diff) => diff switch
	{
		ObtainabilityDifficulty.Easy => Loc("UI.DifficultyEasy"),
		ObtainabilityDifficulty.Medium => Loc("UI.DifficultyMedium"),
		ObtainabilityDifficulty.Hard => Loc("UI.DifficultyHard"),
		_ => Loc("UI.DifficultyVeryHard")
	};

	private static Color GetDifficultyColor(ObtainabilityDifficulty diff) => diff switch
	{
		ObtainabilityDifficulty.Easy => new Color(120, 240, 145),
		ObtainabilityDifficulty.Medium => new Color(135, 210, 255),
		ObtainabilityDifficulty.Hard => new Color(255, 185, 95),
		_ => new Color(255, 115, 115)
	};

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);

		ClampPanelToViewport();

		if (ContainsMouse)
		{
			Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
			Terraria.GameInput.PlayerInput.LockVanillaMouseScroll("BossBuildAdvisor/UI");
		}

		if (_dragging)
		{
			if (!Main.mouseLeft || !Main.hasFocus)
			{
				_dragging = false;
				return;
			}

			Vector2 newPos = Main.MouseScreen - _dragOffset;
			float maxX = Math.Max(8f, Main.screenWidth - _panelW - 8f);
			float maxY = Math.Max(8f, Main.screenHeight - _panelH - 8f);
			_rootPanel.Left.Set(Math.Clamp(newPos.X, 8f, maxX), 0f);
			_rootPanel.Top.Set(Math.Clamp(newPos.Y, 8f, maxY), 0f);
			_rootPanel.Recalculate();
		}
	}

	public override void Draw(SpriteBatch spriteBatch)
	{
		if (ContainsMouse)
		{
			Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
			if (!Main.HoverItem.IsAir)
				Main.HoverItem.TurnToAir();
			Main.hoverItemName = string.Empty;
		}

		base.Draw(spriteBatch);
	}

	private void StartDrag(UIMouseEvent evt, UIElement listeningElement)
	{
		if (evt.Target is AdvisorButton)
			return;

		// Drag only when pressing the header bar background or title/subtitle directly
		if (evt.Target != _headerBar && evt.Target?.Parent != _headerBar)
			return;

		_dragging = true;
		_dragOffset = evt.MousePosition - new Vector2(_rootPanel.Left.Pixels, _rootPanel.Top.Pixels);
	}

	private void EndDrag(UIMouseEvent evt, UIElement listeningElement)
	{
		_dragging = false;
	}

	public void OnClosed()
	{
		_dragging = false;
		_searchBox?.ClearFocus();
	}

	public string GetLayoutCheckSummary()
	{
		if (_rootPanel == null)
			return "UI not initialized";

		RefreshAll();
		_rootPanel.Recalculate();

		CalculatedStyle root = _rootPanel.GetDimensions();
		CalculatedStyle wp = _weaponsPanel.GetDimensions();
		CalculatedStyle ap = _armorPanel.GetDimensions();
		CalculatedStyle ac = _accessoriesPanel.GetDimensions();
		CalculatedStyle pt = _potionsPanel.GetDimensions();
		CalculatedStyle dt = _detailsPanel.GetDimensions();

		bool insideScreen = root.X >= 0 && root.Y >= 0 && (root.X + root.Width) <= Main.screenWidth + 1 && (root.Y + root.Height) <= Main.screenHeight + 1;
		bool childrenInside = dt.Y + dt.Height <= root.Y + root.Height + 1 && pt.X + pt.Width <= root.X + root.Width + 1;
		return $"Screen={Main.screenWidth}x{Main.screenHeight} UIScale={Main.UIScale:0.##} Root=({root.X:0},{root.Y:0},{root.Width:0}x{root.Height:0}) Wpn={wp.Width:0}x{wp.Height:0} Arm={ap.Width:0}x{ap.Height:0} Acc={ac.Width:0}x{ac.Height:0} Pot={pt.Width:0}x{pt.Height:0} Det={dt.Width:0}x{dt.Height:0} BossRows={_bossRows.Count} ClassTabs={_classButtons.Count} InsideScreen={insideScreen} PanelsFit={childrenInside}";
	}

	private static string Loc(string suffix) => Language.GetTextValue($"Mods.BossBuildAdvisor.{suffix}");

	private static string ShortenClassName(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return "Class";

		string s = raw.Trim();
		if (s.Length > 20)
			s = s[..19] + "…";
		return s;
	}

	private static string ArmorShortTag(RecommendedSlotItem slot) => slot.RoleTag switch
	{
		"Head" => Loc("UI.SlotTagHead"),
		"Body" => Loc("UI.SlotTagBody"),
		"Legs" => Loc("UI.SlotTagLegs"),
		_ => slot.RoleTag[..1]
	};

	private static string ShortRole(RecommendedSlotItem slot)
	{
		return slot.RoleTag switch
		{
			"Wings" => Loc("UI.SlotTagWing"),
			"Dash / Dodge" => Loc("UI.SlotTagDash"),
			"Mobility" => Loc("UI.SlotTagBoot"),
			"Whip" => Loc("UI.SlotTagWhip"),
			"Sentry" or "Support Sentry" => Loc("UI.SlotTagSentry"),
			"Free Minion" => Loc("UI.SlotTagPet"),
			"Ammo" => Loc("UI.SlotTagAmmo"),
			"Defense / Regen" => Loc("UI.SlotTagDef"),
			_ => ""
		};
	}

	private static string FormatDetailRole(string roleTag, DamageClass targetClass) => roleTag switch
	{
		"ClassBoost" => string.Format(Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleClassBoost"), ItemTooltipAnalyzer.FormatClassDisplayName(targetClass)),
		"Whip" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleWhip"),
		"Sentry" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleSentry"),
		"Minion" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleMinion"),
		"Weapon" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleWeapon"),
		"Support Sentry" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleSupportSentry"),
		"Free Minion" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleFreeMinion"),
		"Ammo" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleAmmo"),
		"Head" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleHead"),
		"Body" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleBody"),
		"Legs" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleLegs"),
		"Wings" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleWings"),
		"Dash / Dodge" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleDash"),
		"Mobility" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleMobility"),
		"Defense / Regen" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleDefense"),
		"Combat Utility" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleCombatUtility"),
		"Healing" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleHealing"),
		"Buff" => Language.GetTextValue("Mods.BossBuildAdvisor.Recommender.RoleBuff"),
		_ => roleTag ?? string.Empty
	};
}
