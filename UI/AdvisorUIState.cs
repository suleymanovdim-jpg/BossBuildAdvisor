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
	private readonly BossCatalog _catalog;
	private readonly ProgressionGraph _graph;
	private readonly BuildRecommender _recommender;

	private UIPanel _rootPanel;
	private UIElement _headerBar;
	private bool _dragging;
	private Vector2 _dragOffset;

	private BossSearchBox _searchBox;
	private UIList _bossList;
	private readonly List<BossStageRow> _bossRows = new();

	private UIElement _classTabsBar;
	private readonly List<(DamageClass Dc, AdvisorButton Btn)> _classButtons = new();

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

	private readonly List<AdvisorItemSlot> _allSlots = new();

	private BossStageInfo _selectedStage;
	private DamageClass _selectedClass; // null = Auto
	private readonly BuildQueryOptions _options = new();
	private bool _includeMinibossesAndEvents = true;
	private string _searchFilter = string.Empty;

	private BuildRecommendation _currentRec;
	private int _selectedArmorOptionIndex;
	private RecommendedSlotItem _inspectedItem;

	public AdvisorUIState(BossCatalog catalog, ProgressionGraph graph, BuildRecommender recommender)
	{
		_catalog = catalog;
		_graph = graph;
		_recommender = recommender;
	}

	public override void OnInitialize()
	{
		const float panelW = 1040f;
		const float panelH = 636f;

		_rootPanel = new UIPanel
		{
			Width = StyleDimension.FromPixels(panelW),
			Height = StyleDimension.FromPixels(panelH),
			Left = StyleDimension.FromPixels(Math.Max(12f, (Main.screenWidth - panelW) * 0.5f)),
			Top = StyleDimension.FromPixels(Math.Max(12f, (Main.screenHeight - panelH) * 0.5f)),
			BackgroundColor = new Color(25, 33, 58, 240),
			BorderColor = new Color(12, 16, 30, 245)
		};
		_rootPanel.SetPadding(10f);
		Append(_rootPanel);

		BuildHeader(panelW);
		BuildLeftBossColumn();
		BuildRightRecommendationColumn();
	}

	private void BuildHeader(float panelW)
	{
		_headerBar = new UIElement
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(44f)
		};
		_headerBar.OnLeftMouseDown += StartDrag;
		_headerBar.OnLeftMouseUp += EndDrag;
		_rootPanel.Append(_headerBar);

		var title = new UIText(Loc("UI.Title"), 1.05f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(2f),
			TextColor = new Color(255, 222, 110)
		};
		_headerBar.Append(title);

		var subtitle = new UIText(Loc("UI.Subtitle"), 0.72f)
		{
			Left = StyleDimension.FromPixels(6f),
			Top = StyleDimension.FromPixels(24f),
			TextColor = new Color(175, 195, 230)
		};
		_headerBar.Append(subtitle);

		float rx = 0f;
		var closeBtn = new AdvisorButton("X", () => Systems.AdvisorSystem.Instance.SetOpen(false), 0.85f, new Color(120, 42, 46, 235), new Color(160, 58, 62, 245))
		{
			Width = StyleDimension.FromPixels(32f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-32f, 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(closeBtn);
		rx += 38f;

		_includeMiniBtn = new AdvisorButton(Loc("UI.IncludeMinibossesOn"), ToggleMinibosses, 0.68f)
		{
			Width = StyleDimension.FromPixels(134f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-(rx + 134f), 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_includeMiniBtn.SetActive(_includeMinibossesAndEvents);
		_headerBar.Append(_includeMiniBtn);
		rx += 140f;

		_difficultyBtn = new AdvisorButton(GetDifficultyButtonLabel(_options.MaxDifficulty), CycleDifficultyFilter, 0.68f)
		{
			Width = StyleDimension.FromPixels(140f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-(rx + 140f), 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_difficultyBtn.SetActive(_options.MaxDifficulty != DifficultyFilterMode.Comfortable);
		_headerBar.Append(_difficultyBtn);
		rx += 146f;

		_includeEstBtn = new AdvisorButton(Loc("UI.IncludeEstimatedOn"), ToggleEstimated, 0.68f)
		{
			Width = StyleDimension.FromPixels(114f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-(rx + 114f), 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_includeEstBtn.SetActive(_options.IncludeTierEstimatedItems);
		_headerBar.Append(_includeEstBtn);
		rx += 120f;

		_includeDropsBtn = new AdvisorButton(Loc("UI.IncludeBossDropsOff"), ToggleBossDrops, 0.68f)
		{
			Width = StyleDimension.FromPixels(122f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-(rx + 122f), 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(_includeDropsBtn);
		rx += 128f;

		var nextBtn = new AdvisorButton(Loc("UI.JumpToNextBoss"), JumpToNextBoss, 0.70f, new Color(56, 88, 66, 230), new Color(75, 120, 88, 240))
		{
			Width = StyleDimension.FromPixels(96f),
			Height = StyleDimension.FromPixels(30f),
			Left = StyleDimension.FromPixelsAndPercent(-(rx + 96f), 1f),
			Top = StyleDimension.FromPixels(4f)
		};
		_headerBar.Append(nextBtn);
	}

	private void BuildLeftBossColumn()
	{
		var leftCol = new UIPanel
		{
			Top = StyleDimension.FromPixels(50f),
			Width = StyleDimension.FromPixels(276f),
			Height = StyleDimension.FromPixelsAndPercent(-54f, 1f),
			BackgroundColor = new Color(18, 24, 42, 220),
			BorderColor = new Color(12, 16, 30)
		};
		leftCol.SetPadding(6f);
		_rootPanel.Append(leftCol);

		_searchBox = new BossSearchBox(Loc("UI.SearchPlaceholder"), txt =>
		{
			_searchFilter = txt ?? string.Empty;
			PopulateBossList();
		})
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(30f)
		};
		leftCol.Append(_searchBox);

		_bossList = new UIList
		{
			Top = StyleDimension.FromPixels(36f),
			Width = StyleDimension.FromPixelsAndPercent(-24f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-38f, 1f),
			ListPadding = 4f,
			ManualSortMethod = _ => { }
		};
		leftCol.Append(_bossList);

		var scrollbar = new UIScrollbar
		{
			Top = StyleDimension.FromPixels(38f),
			Left = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-42f, 1f)
		};
		scrollbar.SetView(100f, 1000f);
		leftCol.Append(scrollbar);
		_bossList.SetScrollbar(scrollbar);
	}

	private void BuildRightRecommendationColumn()
	{
		var rightCol = new UIElement
		{
			Left = StyleDimension.FromPixels(284f),
			Top = StyleDimension.FromPixels(50f),
			Width = StyleDimension.FromPixelsAndPercent(-284f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-54f, 1f)
		};
		_rootPanel.Append(rightCol);

		_classTabsBar = new UIElement
		{
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(30f)
		};
		rightCol.Append(_classTabsBar);

		var banner = new UIPanel
		{
			Top = StyleDimension.FromPixels(34f),
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixels(42f),
			BackgroundColor = new Color(32, 42, 72, 225),
			BorderColor = new Color(16, 22, 40)
		};
		banner.SetPadding(6f);
		rightCol.Append(banner);

		_bossBannerText = new UIText("...", 0.90f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(1f),
			TextColor = new Color(255, 225, 115)
		};
		banner.Append(_bossBannerText);

		_bossMetaText = new UIText("...", 0.70f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(21f),
			TextColor = new Color(185, 200, 230)
		};
		banner.Append(_bossMetaText);

		// Weapons + Support + Ammo (top-left, 2x7 grid)
		_weaponsPanel = CreateSectionPanel(0f, 80f, 376f, 152f, string.Format(Loc("UI.SectionWeapons"), 0));
		rightCol.Append(_weaponsPanel);
		_weaponsHeader = _weaponsPanel.Children.OfType<UIText>().FirstOrDefault();

		// Armor Sets (top-right, 3 armor slots + #1/#2/#3 set switcher)
		_armorPanel = CreateSectionPanel(384f, 80f, -384f, 152f, Loc("UI.SectionArmor"), widthPercent: 1f);
		rightCol.Append(_armorPanel);

		_armorBonusText = new UIText("", 0.66f)
		{
			Left = StyleDimension.FromPixels(4f),
			Top = StyleDimension.FromPixels(78f),
			Width = StyleDimension.FromPixelsAndPercent(-8f, 1f),
			IsWrapped = true,
			TextColor = new Color(180, 225, 195)
		};
		_armorPanel.Append(_armorBonusText);

		// Accessories (mid-left, 2x7 = 14 slots: core + alternatives)
		_accessoriesPanel = CreateSectionPanel(0f, 238f, 424f, 148f, Loc("UI.SectionAccessories"));
		rightCol.Append(_accessoriesPanel);
		_accessoriesHeader = _accessoriesPanel.Children.OfType<UIText>().FirstOrDefault();

		// Potions & Buffs (mid-right, 2x5 slots)
		_potionsPanel = CreateSectionPanel(432f, 238f, -432f, 148f, Loc("UI.SectionPotions"), widthPercent: 1f);
		rightCol.Append(_potionsPanel);

		// Selected Item Details (bottom)
		_detailsPanel = CreateSectionPanel(0f, 392f, 0f, -392f, Loc("UI.SectionDetails"), widthPercent: 1f, heightPercent: 1f);
		rightCol.Append(_detailsPanel);
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
			BorderColor = new Color(14, 18, 34)
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

	private void ClampPanelToViewport()
	{
		if (_rootPanel == null)
			return;

		float w = _rootPanel.Width.Pixels;
		float h = _rootPanel.Height.Pixels;
		float maxLeft = Math.Max(8f, Main.screenWidth - w - 8f);
		float maxTop = Math.Max(8f, Main.screenHeight - h - 8f);
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

		float x = 0f;
		float maxW = 736f;
		int count = classes.Count;
		float btnW = Math.Clamp((maxW - (count - 1) * 4f) / Math.Max(1, count), 70f, 128f);

		foreach (DamageClass dc in classes)
		{
			string label = dc == null ? Loc("UI.AllClasses") : ShortenClassName(ItemTooltipAnalyzer.FormatClassDisplayName(dc));
			var btn = new AdvisorButton(label, () =>
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
			x += btnW + 4f;
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

		_bossBannerText.SetText($"{_selectedStage.DisplayName}  [{_selectedStage.ModSource}]");
		_bossMetaText.SetText($"Stage {_selectedStage.Progression:0.#}  |  {statusText}  |  {classDisplay}  |  {GetDifficultyButtonLabel(_options.MaxDifficulty)}");

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

		_weaponsHeader?.SetText(string.Format(Loc("UI.SectionWeapons"), weaponSlots.Count));
		PlaceSlotGrid(_weaponsPanel, weaponSlots, startX: 4f, startY: 22f, slotSize: 44f, gap: 5f, cols: 7, shortTagFn: ShortRole);

		// Armor Sets (#1 / #2 / #3)
		RenderArmorSection(reselectSlot: false);

		// Accessories (up to 14: core equipped + alternatives)
		var allAccessories = new List<RecommendedSlotItem>();
		allAccessories.AddRange(_currentRec.Accessories);
		allAccessories.AddRange(_currentRec.AltAccessories);
		_accessoriesHeader?.SetText(string.Format(Loc("UI.SectionAccessories"), _currentRec.Accessories.Count, _currentRec.AltAccessories.Count));
		PlaceSlotGrid(_accessoriesPanel, allAccessories, startX: 4f, startY: 24f, slotSize: 48f, gap: 6f, cols: 7, shortTagFn: ShortRole,
			isAlternativeFn: idx => idx >= _currentRec.Accessories.Count);

		// Potions & Buffs (1 HP + 8 buffs = up to 9 in 2x5 grid)
		var potionSlots = new List<RecommendedSlotItem>();
		if (_currentRec.HealingPotion != null) potionSlots.Add(_currentRec.HealingPotion);
		potionSlots.AddRange(_currentRec.BuffPotions.Take(8));
		PlaceSlotGrid(_potionsPanel, potionSlots, startX: 4f, startY: 24f, slotSize: 44f, gap: 5f, cols: 5, shortTagFn: s => s.RoleTag == "Healing" ? "HP" : "");

		RenderInspectedItemDetails();
	}

	private void RenderArmorSection(bool reselectSlot)
	{
		if (_currentRec == null)
			return;

		_allSlots.RemoveAll(s => s.Parent == _armorPanel);
		ClearSlotsExceptHeader(_armorPanel, keepText: _armorBonusText);

		int optCount = _currentRec.ArmorOptions.Count;
		int idx = optCount > 0 ? Math.Clamp(_selectedArmorOptionIndex, 0, optCount - 1) : 0;
		_selectedArmorOptionIndex = idx;

		for (int i = 0; i < optCount; i++)
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
				Left = StyleDimension.FromPixelsAndPercent(-(optCount - i) * 38f, 1f),
				Top = StyleDimension.FromPixels(-1f)
			};
			tabBtn.SetActive(i == idx);
			_armorPanel.Append(tabBtn);
		}

		ArmorSetCandidate activeSet = optCount > 0 ? _currentRec.ArmorOptions[idx] : _currentRec.ArmorSet;
		var armorSlots = new List<RecommendedSlotItem>();
		RecommendedSlotItem head = activeSet?.HeadSlot ?? _currentRec.HeadSlot;
		RecommendedSlotItem body = activeSet?.BodySlot ?? _currentRec.BodySlot;
		RecommendedSlotItem legs = activeSet?.LegsSlot ?? _currentRec.LegsSlot;
		if (head != null) armorSlots.Add(head);
		if (body != null) armorSlots.Add(body);
		if (legs != null) armorSlots.Add(legs);

		PlaceSlotGrid(_armorPanel, armorSlots, startX: 4f, startY: 24f, slotSize: 48f, gap: 8f, cols: 3, shortTagFn: s => s.RoleTag[..1]);

		if (activeSet != null)
		{
			string defLine = string.Format(Loc("UI.TotalDefense"), activeSet.TotalDefense);
			string diffName = GetDifficultyLabel(activeSet.Difficulty);
			string bonusLine = activeSet.IsTrueSet
				? (string.IsNullOrWhiteSpace(activeSet.SetBonusSummary)
					? $"Set [{activeSet.ModName}]"
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

		_detailsPanel.Append(new UIText($"{item.Name}  ({string.Format(Loc("UI.ModOrigin"), modName)})", 0.88f)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(22f),
			TextColor = new Color(255, 228, 120)
		});

		_detailsPanel.Append(new UIText(string.Format(Loc("UI.UnlockStage"), _inspectedItem.UnlockProgression, confText), 0.70f)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(42f),
			TextColor = confColor
		});

		string diffLabel = GetDifficultyLabel(_inspectedItem.Difficulty);
		string diffReason = string.IsNullOrWhiteSpace(_inspectedItem.DifficultyReason) ? confText : _inspectedItem.DifficultyReason;
		Color diffColor = GetDifficultyColor(_inspectedItem.Difficulty);

		_detailsPanel.Append(new UIText(string.Format(Loc("UI.DifficultyLine"), diffLabel, diffReason), 0.70f)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(60f),
			TextColor = diffColor
		});

		_detailsPanel.Append(new UIText(string.Format(Loc("UI.SourcePrefix"), _inspectedItem.SourceSummary ?? "-"), 0.72f)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(80f),
			Width = StyleDimension.FromPixelsAndPercent(-74f, 1f),
			IsWrapped = true,
			TextColor = Color.White
		});

		_detailsPanel.Append(new UIText(string.Format(Loc("UI.ReasonsPrefix"), _inspectedItem.ReasonSummary ?? "-"), 0.68f)
		{
			Left = StyleDimension.FromPixels(66f),
			Top = StyleDimension.FromPixels(106f),
			Width = StyleDimension.FromPixelsAndPercent(-74f, 1f),
			IsWrapped = true,
			TextColor = new Color(185, 205, 230)
		});
	}

	private static void ClearSlotsExceptHeader(UIPanel panel, UIElement keepText = null)
	{
		var toRemove = new List<UIElement>();
		bool firstTextKept = false;
		foreach (UIElement child in panel.Children)
		{
			if (child == keepText)
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

		if (_rootPanel.IsMouseHovering)
			Main.LocalPlayer.mouseInterface = true;

		if (_dragging)
		{
			Vector2 newPos = Main.MouseScreen - _dragOffset;
			float maxX = Math.Max(0f, Main.screenWidth - _rootPanel.Width.Pixels);
			float maxY = Math.Max(0f, Main.screenHeight - _rootPanel.Height.Pixels);
			_rootPanel.Left.Set(Math.Clamp(newPos.X, 0f, maxX), 0f);
			_rootPanel.Top.Set(Math.Clamp(newPos.Y, 0f, maxY), 0f);
			_rootPanel.Recalculate();
		}
	}

	private void StartDrag(UIMouseEvent evt, UIElement listeningElement)
	{
		// Only drag if clicking directly on the header area (not child buttons)
		if (evt.Target != _headerBar && evt.Target is not UIText)
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
		s = s.Replace(" damage", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урон в ", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урон ", "", StringComparison.OrdinalIgnoreCase)
			.Trim();
		if (s.Length > 13)
			s = s[..12] + ".";
		return s;
	}

	private static string ShortRole(RecommendedSlotItem slot)
	{
		return slot.RoleTag switch
		{
			"Wings" => "Wing",
			"Dash / Dodge" => "Dash",
			"Mobility" => "Boot",
			"Whip" => "Whip",
			"Sentry" or "Support Sentry" => "Tur",
			"Free Minion" => "Pet",
			"Ammo" => "Ammo",
			"Defense / Regen" => "Def",
			_ => ""
		};
	}
}
