using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

/// <summary>
/// Builds a cross-mod reachability graph over all loaded items, boss loot, NPC drops,
/// treasure bags/crates, NPC shops, crafting stations, recipes, recipe groups, and shimmer.
/// </summary>
public sealed class ProgressionGraph
{
	public Dictionary<int, ItemUnlockInfo> Items { get; } = new();
	public Dictionary<int, ItemSemanticProfile> Semantics { get; } = new();
	public Dictionary<int, List<int>> DirectRecipesForItem { get; } = new();
	public Dictionary<int, HashSet<int>> EquipmentUpgradesTo { get; } = new();
	public int BuildDurationMs { get; private set; }

	private sealed record TileGateInfo(float Prog, bool RequiresHardmode, ConditionGate Gate, ObtainabilityDifficulty Difficulty, string Reason);

	private readonly Dictionary<int, TileGateInfo[]> _tileGates = new();
	private readonly Dictionary<int, float> _minimumItemFloor = new();
	private readonly Dictionary<int, (ObtainabilityDifficulty Difficulty, string Reason)> _materialDifficulty = new();
	private readonly List<int> _allValidItemIds = new();
	private readonly Dictionary<int, List<(DropRateInfo Drop, ConditionGate Gate)>> _openableBags = new();
	private readonly Dictionary<int, List<int>> _tilePlacers = new();
	private float _wofProg = 7.0f;

	public void Build(Mod mod, BossCatalog catalog)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		_wofProg = catalog.WallOfFleshProgression;
		Items.Clear();
		Semantics.Clear();
		DirectRecipesForItem.Clear();
		EquipmentUpgradesTo.Clear();
		_tileGates.Clear();
		_minimumItemFloor.Clear();
		_materialDifficulty.Clear();
		_allValidItemIds.Clear();
		_openableBags.Clear();
		_tilePlacers.Clear();

		InitializeItemBaselineAndSemantics(catalog);
		BuildTilePlacerMap();
		SeedVanillaProgressionFloors(catalog);
		IndexRecipesAndDirectUpgrades();
		SeedBossLootAndBags(catalog);
		SeedNpcDrops(catalog);
		CollectOpenableBagsAndCrates(catalog);
		SeedNpcShops(catalog);
		SeedGuaranteedBiomeResources(catalog);
		// Propagate bags/crates BEFORE uncrafted fallback so Hardmode crate gems/souls get verified crate sources first.
		PropagateOpenableBags(catalog);
		SeedUncraftedWorldMaterials(catalog);
		RunRelaxationPasses(catalog, 12);
		SeedCyclicAndFlooredMaterials(catalog);
		RunRelaxationPasses(catalog, 10);
		ApplyFallbackTierEstimates(catalog);
		RunRelaxationPasses(catalog, 8);

		foreach (ItemUnlockInfo info in Items.Values)
			info.SyncPrimaryFields(DifficultyFilterMode.Comfortable);

		sw.Stop();
		BuildDurationMs = (int)sw.ElapsedMilliseconds;
		mod.Logger.Info($"[BossBuildAdvisor] Indexed {Items.Count} items across {catalog.AllStages.Count} boss/event stages in {BuildDurationMs} ms (BossChecklist={catalog.UsedBossChecklist}).");
	}

	private void BuildTilePlacerMap()
	{
		foreach (int itemId in _allValidItemIds)
		{
			Item item = ContentSamples.ItemsByType[itemId];
			if (item.createTile >= TileID.Dirt)
			{
				if (!_tilePlacers.TryGetValue(item.createTile, out List<int> list))
				{
					list = new List<int>();
					_tilePlacers[item.createTile] = list;
				}
				list.Add(itemId);
			}
		}
	}

	private void InitializeItemBaselineAndSemantics(BossCatalog catalog)
	{
		foreach ((int itemId, Item item) in ContentSamples.ItemsByType)
		{
			if (item == null || item.IsAir || itemId <= ItemID.None || item.type != itemId)
				continue;

			if (ItemID.Sets.Deprecated[itemId])
				continue;

			_allValidItemIds.Add(itemId);

			string modName = item.ModItem?.Mod?.Name ?? "Terraria";
			Items[itemId] = new ItemUnlockInfo
			{
				ItemType = itemId,
				ModName = modName,
				UnlockProgression = float.MaxValue,
				SourceType = ItemSourceType.EstimatedTier,
				Confidence = SourceConfidence.TierEstimated,
				SourceSummary = LootLoc("SourceTierEstimate"),
				ExpertOnly = item.expert,
				MasterOnly = item.master
			};

			if (IsBuildCandidate(item))
			{
				Semantics[itemId] = ItemTooltipAnalyzer.Analyze(item);
			}
		}
	}

	private static bool IsBuildCandidate(Item item)
	{
		if (item.vanity)
			return false;

		return item.damage > 0
			|| item.defense > 0
			|| item.accessory
			|| item.headSlot >= 0
			|| item.bodySlot >= 0
			|| item.legSlot >= 0
			|| (item.buffType > 0 && item.buffTime > 0 && item.consumable)
			|| (item.healLife > 0 && item.potion)
			|| item.ammo > AmmoID.None;
	}

	private void IndexRecipesAndDirectUpgrades()
	{
		for (int i = 0; i < Recipe.numRecipes; i++)
		{
			Recipe recipe = Main.recipe[i];
			if (recipe == null || recipe.Disabled || recipe.createItem == null || recipe.createItem.IsAir)
				continue;

			int resultId = recipe.createItem.type;
			if (!DirectRecipesForItem.TryGetValue(resultId, out List<int> list))
			{
				list = new List<int>();
				DirectRecipesForItem[resultId] = list;
			}
			list.Add(i);

			foreach (Item req in recipe.requiredItem)
			{
				if (req == null || req.IsAir || req.type == resultId)
					continue;

				if (!ContentSamples.ItemsByType.TryGetValue(req.type, out Item ingItem) || !ContentSamples.ItemsByType.TryGetValue(resultId, out Item outItem))
					continue;

				bool outEquip = outItem.accessory || outItem.damage > 0;
				if (!outEquip)
					continue;

				bool bothEquip = (ingItem.accessory && outItem.accessory) || (ingItem.damage > 0 && outItem.damage > 0);
				if (bothEquip)
					AddEquipmentUpgradeEdge(req.type, resultId);

				if (recipe.acceptedGroups != null)
				{
					foreach (int groupId in recipe.acceptedGroups)
					{
						if (groupId < 0)
							break;

						if (!RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup group) || !group.ContainsItem(req.type))
							continue;

						foreach (int altReqId in group.ValidItems)
						{
							if (altReqId == req.type || altReqId == resultId || !ContentSamples.ItemsByType.TryGetValue(altReqId, out Item altItem))
								continue;

							if ((altItem.accessory && outItem.accessory) || (altItem.damage > 0 && outItem.damage > 0))
								AddEquipmentUpgradeEdge(altReqId, resultId);
						}
					}
				}
			}
		}
	}

	private void AddEquipmentUpgradeEdge(int fromId, int toId)
	{
		if (!EquipmentUpgradesTo.TryGetValue(fromId, out HashSet<int> targets))
		{
			targets = new HashSet<int>();
			EquipmentUpgradesTo[fromId] = targets;
		}
		targets.Add(toId);
	}

	private void SeedBossLootAndBags(BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);

		foreach (BossStageInfo stage in catalog.AllStages)
		{
			float postBossProg = stage.Progression + 0.001f;
			bool postWof = stage.Progression >= catalog.WallOfFleshProgression;
			bool unavailable = !Main.gameMenu && !stage.IsAvailable;
			bool earlyHidden = !stage.IsVisibleOnChecklist && !stage.IsDowned && stage.Progression < ks;
			bool isEoWStage = stage.NpcIds.Contains(NPCID.EaterofWorldsHead);
			bool isBoCStage = stage.NpcIds.Contains(NPCID.BrainofCthulhu);
			var coveredDropIds = new HashSet<int>();
			string bossSummary = LootLoc("SourceBossDrop", stage.DisplayName);

			foreach (int npcId in stage.NpcIds)
			{
				var rules = Main.ItemDropsDB.GetRulesForNPCID(npcId, includeGlobalDrops: false);
				foreach (DropRateInfo dr in FlattenDropRules(rules))
				{
					coveredDropIds.Add(dr.itemId);
					RegisterStageDrop(stage, dr, postBossProg, postWof, bossSummary, false, ks, isEoWStage, isBoCStage, catalog);
				}
			}

			if (stage.TreasureBagId > 0)
			{
				var bagRules = Main.ItemDropsDB.GetRulesForItemID(stage.TreasureBagId);
				string bagName = Lang.GetItemNameValue(stage.TreasureBagId);
				string bagSummary = LootLoc("SourceFromBag", bagName, stage.DisplayName);
				foreach (DropRateInfo dr in FlattenDropRules(bagRules))
				{
					coveredDropIds.Add(dr.itemId);
					RegisterStageDrop(stage, dr, postBossProg, postWof, bagSummary, true, ks, isEoWStage, isBoCStage, catalog);
				}
			}

			foreach (DropRateInfo dr in stage.DropRates)
			{
				if (coveredDropIds.Add(dr.itemId))
					RegisterStageDrop(stage, dr, postBossProg, postWof, bossSummary, false, ks, isEoWStage, isBoCStage, catalog);
			}

			foreach (int lootId in stage.LootItemIds)
			{
				if (coveredDropIds.Contains(lootId))
					continue;

				float itemStage = postBossProg;
				if (stage.IsEvent && ContentSamples.ItemsByType.TryGetValue(lootId, out Item evItem))
					itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

				ObtainabilityDifficulty diff = ObtainabilityDifficulty.Easy;
				string reason = LootLoc("BossNamedDrop", stage.DisplayName);
				if (unavailable)
				{
					diff = ObtainabilityDifficulty.VeryHard;
					reason = LootLoc("UnavailableBoss", stage.DisplayName);
				}
				else if (earlyHidden)
				{
					diff = ObtainabilityDifficulty.Hard;
					reason = LootLoc("HiddenEvent", stage.DisplayName);
				}

				ConditionGate fallbackGate = new ConditionGate
				{
					CorruptOnly = isEoWStage && !isBoCStage,
					CrimsonOnly = isBoCStage && !isEoWStage
				};

				TryRegisterSource(lootId, itemStage, ItemSourceType.BossDrop, SourceConfidence.Verified,
					bossSummary, postWof || itemStage > catalog.WallOfFleshProgression, fallbackGate,
					diff, reason, 1f);
			}
		}
	}

	private void RegisterStageDrop(
		BossStageInfo stage,
		DropRateInfo dr,
		float postBossProg,
		bool postWof,
		string summary,
		bool expertBag,
		float ks,
		bool isEoWStage,
		bool isBoCStage,
		BossCatalog catalog)
	{
		ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
		if (gate.Unobtainable)
			return;

		float itemStage = Math.Max(postBossProg, gate.MinProgression);
		if (stage.IsEvent && gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item evItem))
			itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

		(ObtainabilityDifficulty diff, string reason) = EvaluateDropDifficulty(dr.dropRate, !stage.IsEvent);
		if (gate.IsChallengeCondition)
		{
			diff = ObtainabilityDifficulty.VeryHard;
			reason = !string.IsNullOrWhiteSpace(gate.SpecialConditionText) ? gate.SpecialConditionText : LootLoc("SpecialCondition");
		}
		else if (gate.IsSeasonal && diff < ObtainabilityDifficulty.Hard)
		{
			diff = ObtainabilityDifficulty.Hard;
			reason = LootLoc("SeasonalDrop");
		}

		if (!Main.gameMenu && !stage.IsAvailable && diff < ObtainabilityDifficulty.VeryHard)
		{
			diff = ObtainabilityDifficulty.VeryHard;
			reason = LootLoc("UnavailableBoss", stage.DisplayName);
		}
		else if (!stage.IsVisibleOnChecklist && !stage.IsDowned && stage.Progression < ks && diff < ObtainabilityDifficulty.Hard)
		{
			diff = ObtainabilityDifficulty.Hard;
			reason = LootLoc("HiddenEvent", stage.DisplayName);
		}

		if (expertBag)
			gate = gate with { ExpertOnly = true };
		if (isEoWStage && !isBoCStage && !gate.CrimsonOnly)
			gate = gate with { CorruptOnly = true };
		else if (isBoCStage && !isEoWStage && !gate.CorruptOnly)
			gate = gate with { CrimsonOnly = true };

		TryRegisterSource(dr.itemId, itemStage, ItemSourceType.BossDrop, SourceConfidence.Verified, summary,
			postWof || gate.RequiresHardmode || itemStage > catalog.WallOfFleshProgression, gate,
			diff, reason, dr.dropRate);
	}

	private void SeedNpcDrops(BossCatalog catalog)
	{
		var bossNpcSet = new HashSet<int>();
		foreach (BossStageInfo stage in catalog.AllStages)
		{
			foreach (int npcId in stage.NpcIds)
				bossNpcSet.Add(npcId);
		}

		// Global drop rules (Souls of Light/Night, global yoyos, Biome Keys, Pirate Map, Goodie Bag, Present, Living Flames)
		var globalRules = Main.ItemDropsDB.GetRulesForNPCID(int.MinValue, includeGlobalDrops: true);
		RegisterNpcDropRuleSet(globalRules, 0f, isGlobal: true, npcName: null, catalog);

		foreach ((int npcId, NPC npc) in ContentSamples.NpcsByNetId)
		{
			if (npc == null || bossNpcSet.Contains(npcId))
				continue;

			var rules = Main.ItemDropsDB.GetRulesForNPCID(npcId, includeGlobalDrops: false);
			if (rules == null || rules.Count == 0)
				continue;

			float npcStatFloor = npc.townNPC
				? GetTownNpcMinimumStage(npcId, catalog)
				: EstimateNpcMinimumStage(npc, catalog);
			string npcName = Lang.GetNPCNameValue(npcId);
			if (string.IsNullOrWhiteSpace(npcName))
				npcName = npc.TypeName;

			RegisterNpcDropRuleSet(rules, npcStatFloor, isGlobal: false, npcName, catalog);
		}
	}

	private void RegisterNpcDropRuleSet(List<IItemDropRule> rules, float npcFloor, bool isGlobal, string npcName, BossCatalog catalog)
	{
		if (rules == null || rules.Count == 0)
			return;

		foreach (DropRateInfo dr in FlattenDropRules(rules))
		{
			ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
			if (gate.Unobtainable)
				continue;

			float stage = Math.Max(npcFloor, gate.MinProgression);
			if (gate.MinProgression <= 0.01f && !gate.PreHardmodeOnly && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
			{
				float rareFloor = EstimateDropRarityFloor(dropItem, catalog);
				stage = Math.Max(stage, rareFloor);
			}

			(ObtainabilityDifficulty diff, string reason) = EvaluateDropDifficulty(dr.dropRate, isBossDrop: false);
			if (dr.dropRate > 0f && dr.dropRate < 0.001f)
				diff = ObtainabilityDifficulty.VeryHard;
			if (gate.IsSeasonal && diff < ObtainabilityDifficulty.Hard)
			{
				diff = ObtainabilityDifficulty.Hard;
				reason = LootLoc("SeasonalDrop");
			}

			bool reqHardmode = !gate.PreHardmodeOnly && (gate.RequiresHardmode || stage > catalog.WallOfFleshProgression);
			string summary = isGlobal
				? LootLoc("SourceGlobalDrop", dr.dropRate * 100f)
				: LootLoc("SourceNpcDrop", npcName, dr.dropRate * 100f);
			string fullReason = isGlobal ? reason : $"{npcName}: {reason}";

			TryRegisterSource(dr.itemId, stage, ItemSourceType.NpcDrop, SourceConfidence.Verified,
				summary, reqHardmode, gate, diff, fullReason, dr.dropRate);
		}
	}

	private void CollectOpenableBagsAndCrates(BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);
		float eow = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float boc = catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, eow);
		float evil = Math.Min(eow, boc);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;

		foreach (int itemId in _allValidItemIds)
		{
			if (ItemID.Sets.BossBag[itemId])
				continue;

			var rules = Main.ItemDropsDB.GetRulesForItemID(itemId);
			if (rules == null || rules.Count == 0)
				continue;

			var list = new List<(DropRateInfo, ConditionGate)>();
			foreach (DropRateInfo dr in FlattenDropRules(rules))
			{
				if (dr.dropRate > 0f && dr.dropRate < 0.001f)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
				if (!gate.Unobtainable)
					list.Add((dr, gate));
			}

			if (list.Count > 0)
				_openableBags[itemId] = list;

			if (itemId == ItemID.LockBox || itemId == ItemID.ObsidianLockbox)
			{
				TryRegisterSource(itemId, skel + 0.01f, ItemSourceType.WorldTier, SourceConfidence.Verified,
					LootLoc("SourceLockBox"), false, default, ObtainabilityDifficulty.Medium, LootLoc("LockBox"), 1f);
			}
			else if (itemId == ItemID.DungeonFishingCrateHard)
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceDungeonCrate"), true, default, ObtainabilityDifficulty.Hard, LootLoc("DungeonCrate"), 1f);
			}
			else if (itemId == ItemID.DungeonFishingCrate)
			{
				TryRegisterSource(itemId, skel + 0.01f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceDungeonCrate"), false, default, ObtainabilityDifficulty.Hard, LootLoc("DungeonCrate"), 1f);
			}
			else if (ItemID.Sets.IsFishingCrateHardmode[itemId])
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceHardmodeCrate"), true, default, ObtainabilityDifficulty.Hard, LootLoc("HardmodeCrate"), 1f);
			}
			else if (itemId == ItemID.LavaCrate)
			{
				TryRegisterSource(itemId, Math.Max(evil + 0.05f, 3.05f), ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceLavaCrate"), false, default, ObtainabilityDifficulty.Hard, LootLoc("BiomeCrate"), 1f);
			}
			else if (itemId is ItemID.WoodenCrate or ItemID.IronCrate)
			{
				TryRegisterSource(itemId, itemId == ItemID.WoodenCrate ? 0.6f : 1.0f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceBasicCrate"), false, default, ObtainabilityDifficulty.Medium, LootLoc("BasicCrate"), 1f);
			}
			else if (ItemID.Sets.IsFishingCrate[itemId])
			{
				TryRegisterSource(itemId, Math.Max(ks + 0.5f, 1.5f), ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					LootLoc("SourceBiomeCrate"), false, default, ObtainabilityDifficulty.Hard, LootLoc("BiomeCrate"), 1f);
			}
			else if (itemId == ItemID.GoodieBag)
			{
				TryRegisterSource(itemId, 0.8f, ItemSourceType.WorldTier, SourceConfidence.Verified,
					LootLoc("SourceGoodieBag"), false, default,
					Main.halloween ? ObtainabilityDifficulty.Medium : ObtainabilityDifficulty.Hard, LootLoc("SeasonalDrop"), 1f);
			}
			else if (itemId == ItemID.Present)
			{
				TryRegisterSource(itemId, 0.8f, ItemSourceType.WorldTier, SourceConfidence.Verified,
					LootLoc("SourcePresent"), false, default,
					Main.xMas ? ObtainabilityDifficulty.Medium : ObtainabilityDifficulty.Hard, LootLoc("SeasonalDrop"), 1f);
			}
			else if (itemId is ItemID.HerbBag or ItemID.CanOfWorms or ItemID.Oyster)
			{
				float stage = itemId == ItemID.Oyster ? 1.2f : 0.5f;
				TryRegisterSource(itemId, stage, ItemSourceType.WorldTier, SourceConfidence.Verified,
					LootLoc("SourceFishingBag"), false, default, ObtainabilityDifficulty.Medium, LootLoc("WorldChest"), 1f);
			}
		}
	}

	private void SeedNpcShops(BossCatalog catalog)
	{
		foreach (AbstractNPCShop shop in NPCShopDatabase.AllShops)
		{
			float npcUnlock = GetTownNpcMinimumStage(shop.NpcType, catalog);
			string npcName = Lang.GetNPCNameValue(shop.NpcType);
			if (string.IsNullOrWhiteSpace(npcName))
				npcName = ContentSamples.NpcsByNetId.TryGetValue(shop.NpcType, out NPC n) ? n.TypeName : "Merchant";

			bool isTravellingMerchant = shop.NpcType == NPCID.TravellingMerchant;
			bool isSkeletonMerchant = shop.NpcType == NPCID.SkeletonMerchant;

			foreach (AbstractNPCShop.Entry entry in shop.ActiveEntries)
			{
				if (entry?.Item == null || entry.Item.IsAir)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateRecipeConditions(entry.Conditions, catalog);
				if (gate.Unobtainable)
					continue;

				float stage = Math.Max(npcUnlock, gate.MinProgression);
				if (!gate.PreHardmodeOnly)
					stage = Math.Max(stage, EstimateDropRarityFloor(entry.Item, catalog));

				bool reqHm = !gate.PreHardmodeOnly && (gate.RequiresHardmode || stage > catalog.WallOfFleshProgression);

				ObtainabilityDifficulty diff = ObtainabilityDifficulty.Easy;
				string reason = LootLoc("ShopBuy", npcName);
				if (isTravellingMerchant)
				{
					diff = ObtainabilityDifficulty.Hard;
					reason = LootLoc("TravellingShop");
				}
				else if (isSkeletonMerchant)
				{
					diff = ObtainabilityDifficulty.Medium;
					reason = LootLoc("ShopBuy", npcName);
				}
				else if (gate.IsSeasonal)
				{
					diff = ObtainabilityDifficulty.Hard;
					reason = LootLoc("SeasonalDrop");
				}
				else if (entry.Item.value >= 250000 && stage < 3.5f)
				{
					diff = ObtainabilityDifficulty.Hard;
					reason = LootLoc("ShopExpensiveEarly", npcName);
				}
				else if (entry.Item.value >= 100000 && stage < 2.2f)
				{
					diff = ObtainabilityDifficulty.Medium;
					reason = LootLoc("ShopMidCost", npcName);
				}

				TryRegisterSource(entry.Item.type, stage, ItemSourceType.NpcShop, SourceConfidence.Verified,
					LootLoc("SourceSoldBy", npcName), reqHm, gate, diff, reason, 1f);
			}
		}
	}

	private void SeedTileGateFloor(int tileId, float prog, bool requiresHardmode, ObtainabilityDifficulty diff, string reason)
	{
		if (!_tileGates.TryGetValue(tileId, out TileGateInfo[] arr))
		{
			arr = new TileGateInfo[3];
			_tileGates[tileId] = arr;
		}
		var info = new TileGateInfo(prog, requiresHardmode, default, diff, reason ?? string.Empty);
		for (int b = (int)diff <= (int)ObtainabilityDifficulty.Medium ? 0 : (diff == ObtainabilityDifficulty.Hard ? 1 : 2); b < 3; b++)
			arr[b] = info;
	}

	private TileGateInfo GetTileGate(int tileId, int filterIdx)
	{
		if (!_tileGates.TryGetValue(tileId, out TileGateInfo[] arr))
			return null;
		for (int i = Math.Clamp(filterIdx, 0, 2); i >= 0; i--)
		{
			if (arr[i] != null)
				return arr[i];
		}
		return arr[2] ?? arr[1];
	}

	private void SeedVanillaProgressionFloors(BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float eow = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float boc = catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, eow);
		float evil = Math.Min(eow, boc);
		float qb = catalog.GetProgressionForNpc(NPCID.QueenBee, 4f);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetMechAnyProgression();
		float mechAll = catalog.GetMechAllProgression();
		float twins = catalog.GetProgressionForNpc(NPCID.Retinazer, wof + 2f);
		float dest = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 3f);
		float prime = catalog.GetProgressionForNpc(NPCID.SkeletronPrime, wof + 4f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 5f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, plantera + 5f);
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1f);

		SeedTileGateFloor(TileID.Hellforge, Math.Max(evil + 0.05f, Math.Min(wof - 0.3f, evil + 0.3f)), false, ObtainabilityDifficulty.Medium, LootLoc("UnderworldEarly"));
		SeedTileGateFloor(TileID.AlchemyTable, skel + 0.01f, false, ObtainabilityDifficulty.Medium, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.MythrilAnvil, wof + 0.1f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.AdamantiteForge, wof + 0.1f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.LihzahrdFurnace, plantera + 0.1f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.LunarCraftingStation, cultist + 0.1f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.Blendomatic, wof + 0.2f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.Autohammer, plantera + 0.05f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.CrystalBall, wof + 0.05f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));
		SeedTileGateFloor(TileID.SteampunkBoiler, mech + 0.1f, true, ObtainabilityDifficulty.Easy, LootLoc("WorkbenchCraft"));

		// Pre-Evil / early biome gates so Obsidian, Shadow, Jungle, Fossil, and Hellstone do not leak before King Slime
		float jungleFloor = ks + 0.05f;
		foreach (int id in new[] { ItemID.JungleSpores, ItemID.Stinger, ItemID.Vine })
		{
			_minimumItemFloor[id] = jungleFloor;
			_materialDifficulty[id] = (ObtainabilityDifficulty.Medium, LootLoc("UndergroundJungle"));
		}

		_minimumItemFloor[ItemID.FossilOre] = jungleFloor;
		_materialDifficulty[ItemID.FossilOre] = (ObtainabilityDifficulty.Medium, LootLoc("UndergroundDesert"));

		_minimumItemFloor[ItemID.AshWood] = jungleFloor;
		_materialDifficulty[ItemID.AshWood] = (ObtainabilityDifficulty.Hard, LootLoc("UnderworldEarly"));

		_minimumItemFloor[ItemID.FlinxFur] = 0.8f;
		_materialDifficulty[ItemID.FlinxFur] = (ObtainabilityDifficulty.Medium, LootLoc("UndergroundSnow"));

		_minimumItemFloor[ItemID.PharaohsMask] = Math.Max(ks + 0.5f, 1.5f);
		_materialDifficulty[ItemID.PharaohsMask] = (ObtainabilityDifficulty.Hard, LootLoc("PyramidRare"));
		_minimumItemFloor[ItemID.PharaohsRobe] = Math.Max(ks + 0.5f, 1.5f);
		_materialDifficulty[ItemID.PharaohsRobe] = (ObtainabilityDifficulty.Hard, LootLoc("PyramidRare"));

		foreach (int id in new[] { ItemID.DemoniteOre, ItemID.CrimtaneOre, ItemID.DemoniteBar, ItemID.CrimtaneBar })
			_minimumItemFloor[id] = eoc + 0.01f;

		_minimumItemFloor[ItemID.ShadowScale] = eow + 0.01f;
		_minimumItemFloor[ItemID.TissueSample] = boc + 0.01f;
		_minimumItemFloor[ItemID.Obsidian] = evil + 0.01f;
		_minimumItemFloor[ItemID.Meteorite] = evil + 0.01f;
		_minimumItemFloor[ItemID.MeteoriteBar] = evil + 0.01f;
		_minimumItemFloor[ItemID.Hellstone] = evil + 0.05f;
		_minimumItemFloor[ItemID.HellstoneBar] = evil + 0.05f;
		_minimumItemFloor[ItemID.BeeWax] = qb + 0.01f;
		_minimumItemFloor[ItemID.IllegalGunParts] = Math.Max(ks + 0.5f, 1.5f);
		_minimumItemFloor[ItemID.Cascade] = skel + 0.01f;

		// Hard biome/progression item floors so modded bags/mobs never pull Dungeon, Hellstone, or Hardmode materials early
		float dungeonFloor = skel + 0.01f;
		foreach (int id in new[]
		{
			ItemID.GoldenKey, ItemID.ShadowKey, ItemID.Bone, ItemID.LockBox, ItemID.ObsidianLockbox,
			ItemID.DungeonFishingCrate, ItemID.NecroHelmet, ItemID.NecroBreastplate, ItemID.NecroGreaves,
			ItemID.AncientNecroHelmet, ItemID.CobaltShield, ItemID.Handgun, ItemID.Muramasa,
			ItemID.AquaScepter, ItemID.MagicMissile, ItemID.BlueMoon, ItemID.Valor,
			ItemID.Sunfury, ItemID.Flamelash, ItemID.DarkLance, ItemID.HellwingBow, ItemID.FlowerofFire
		})
			_minimumItemFloor[id] = dungeonFloor;

		float hm1 = wof + 0.05f;
		foreach (int id in new[]
		{
			ItemID.CobaltOre, ItemID.CobaltBar, ItemID.PalladiumOre, ItemID.PalladiumBar,
			ItemID.CrystalShard, ItemID.SoulofLight, ItemID.SoulofNight, ItemID.SoulofFlight,
			ItemID.CursedFlame, ItemID.Ichor, ItemID.PixieDust, ItemID.UnicornHorn,
			ItemID.LightShard, ItemID.DarkShard, ItemID.LivingFireBlock, ItemID.PirateMap,
			ItemID.Amarok, ItemID.HelFire
		})
			_minimumItemFloor[id] = hm1;

		float hm2 = wof + 0.10f;
		foreach (int id in new[] { ItemID.MythrilOre, ItemID.MythrilBar, ItemID.OrichalcumOre, ItemID.OrichalcumBar })
			_minimumItemFloor[id] = hm2;

		float hm3 = wof + 0.15f;
		foreach (int id in new[] { ItemID.AdamantiteOre, ItemID.AdamantiteBar, ItemID.TitaniumOre, ItemID.TitaniumBar })
			_minimumItemFloor[id] = hm3;

		_minimumItemFloor[ItemID.HallowedBar] = mech + 0.01f;
		_minimumItemFloor[ItemID.SoulofSight] = twins + 0.01f;
		_minimumItemFloor[ItemID.SoulofMight] = dest + 0.01f;
		_minimumItemFloor[ItemID.SoulofFright] = prime + 0.01f;
		_minimumItemFloor[ItemID.ChlorophyteOre] = mechAll + 0.05f;
		_minimumItemFloor[ItemID.ChlorophyteBar] = mechAll + 0.05f;
		_minimumItemFloor[ItemID.ShroomiteBar] = plantera + 0.05f;
		_minimumItemFloor[ItemID.SpectreBar] = plantera + 0.05f;
		_minimumItemFloor[ItemID.LunarOre] = ml + 0.01f;
		_minimumItemFloor[ItemID.LunarBar] = ml + 0.01f;
		_minimumItemFloor[ItemID.Clentaminator2] = ml + 0.01f;
		_minimumItemFloor[ItemID.RodOfHarmony] = ml + 0.01f;
		_minimumItemFloor[ItemID.BottomlessShimmerBucket] = ml + 0.01f;
	}

	private float ApplyMinimumFloor(int itemId, float candidateStage)
	{
		return _minimumItemFloor.TryGetValue(itemId, out float floor) ? Math.Max(candidateStage, floor) : candidateStage;
	}

	private (ObtainabilityDifficulty Diff, string Reason) ApplyMaterialDifficulty(int itemId, ObtainabilityDifficulty baseDiff, string baseReason)
	{
		if (_materialDifficulty.TryGetValue(itemId, out (ObtainabilityDifficulty Difficulty, string Reason) mat) && mat.Difficulty > baseDiff)
			return mat;
		return (baseDiff, baseReason);
	}

	private void SeedGuaranteedBiomeResources(BossCatalog catalog)
	{
		float eow = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float boc = catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, eow);
		float evil = Math.Min(eow, boc);

		// Basic Pre-Hardmode ores & gems are always gatherable early even if rare fishing crates or late shops also list them
		int[] earlyOresAndGems =
		{
			ItemID.CopperOre, ItemID.TinOre, ItemID.IronOre, ItemID.LeadOre,
			ItemID.SilverOre, ItemID.TungstenOre, ItemID.GoldOre, ItemID.PlatinumOre,
			ItemID.Amethyst, ItemID.Topaz, ItemID.Sapphire, ItemID.Emerald, ItemID.Ruby, ItemID.Diamond
		};
		for (int i = 0; i < earlyOresAndGems.Length; i++)
		{
			TryRegisterSource(earlyOresAndGems[i], 0f, ItemSourceType.WorldTier, SourceConfidence.Verified,
				LootLoc("SourceWorld"), false, default, ObtainabilityDifficulty.Easy, LootLoc("WorldGather"), 1f);
		}

		TryRegisterSource(ItemID.Amber, 0.5f, ItemSourceType.WorldTier, SourceConfidence.Verified,
			LootLoc("SourceWorld"), false, default, ObtainabilityDifficulty.Medium, LootLoc("WorldGather"), 1f);

		// Common surface / underground / ice / jungle chest staples also appear at low % in crates (which would otherwise mask Comfortable paths)
		(int itemId, float stage)[] worldChestStaples =
		{
			(ItemID.Aglet, 0.3f),
			(ItemID.HermesBoots, 0.8f),
			(ItemID.CloudinaBottle, 0.8f),
			(ItemID.BandofRegeneration, 0.8f),
			(ItemID.FlurryBoots, 1.0f),
			(ItemID.BlizzardinaBottle, 1.0f),
			(ItemID.IceSkates, 1.0f),
			(ItemID.AnkletoftheWind, 1.2f)
		};
		foreach ((int itemId, float stage) in worldChestStaples)
		{
			TryRegisterSource(itemId, stage, ItemSourceType.WorldTier, SourceConfidence.Verified,
				LootLoc("SourceWorld"), false, default, ObtainabilityDifficulty.Medium, LootLoc("WorldChest"), 1f);
		}

		// Post-evil-boss biome ores can always be mined cleanly even if an enemy (e.g. Meteor Head 2%) also drops them rarely
		TryRegisterSource(ItemID.Meteorite, evil + 0.01f, ItemSourceType.WorldTier, SourceConfidence.Verified,
			LootLoc("SourceMeteor"), false, default, ObtainabilityDifficulty.Medium, LootLoc("MeteorBiome"), 1f);
		TryRegisterSource(ItemID.Hellstone, evil + 0.05f, ItemSourceType.WorldTier, SourceConfidence.Verified,
			LootLoc("SourceUnderworld"), false, default, ObtainabilityDifficulty.Medium, LootLoc("UnderworldEarly"), 1f);
	}

	private void SeedUncraftedWorldMaterials(BossCatalog catalog)
	{
		// Pre-seed raw world materials (no recipes, no NPC drops, no shops, no bags) with a conservative world-tier progression
		foreach (int itemId in _allValidItemIds)
		{
			ItemUnlockInfo info = Items[itemId];
			if (info.GetPathFor(DifficultyFilterMode.All) != null)
				continue;

			if (!DirectRecipesForItem.ContainsKey(itemId))
				RegisterRawWorldSeed(itemId, catalog);
		}
	}

	private void SeedCyclicAndFlooredMaterials(BossCatalog catalog)
	{
		// Materials/blocks/ores that only appear in circular recipes (e.g. Wall x4 -> Obsidian/Block) or have explicit floors
		// must be seeded before fallback equipment estimation so items like Obsidian Outlaw inherit their real craft floor.
		foreach (int itemId in _allValidItemIds)
		{
			ItemUnlockInfo info = Items[itemId];
			if (info.GetPathFor(DifficultyFilterMode.All) != null)
				continue;

			Item item = ContentSamples.ItemsByType[itemId];
			if (_minimumItemFloor.ContainsKey(itemId) || !IsBuildCandidate(item))
				RegisterRawWorldSeed(itemId, catalog);
		}
	}

	private void RegisterRawWorldSeed(int itemId, BossCatalog catalog)
	{
		Item item = ContentSamples.ItemsByType[itemId];
		float est = ApplyMinimumFloor(itemId, EstimateRawWorldItemStage(item, catalog));

		if (_openableBags.TryGetValue(itemId, out var bagDrops))
		{
			foreach ((DropRateInfo dr, ConditionGate gate) in bagDrops)
			{
				est = Math.Max(est, gate.MinProgression);
				if (gate.MinProgression <= 0.01f && !gate.PreHardmodeOnly && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item innerItem))
					est = Math.Max(est, EstimateDropRarityFloor(innerItem, catalog));
			}
		}

		bool reqHm = est > _wofProg;
		bool isCombatOrEquip = item.damage > 0 || item.defense > 0 || item.accessory || item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0;
		ObtainabilityDifficulty diff = isCombatOrEquip && item.rare >= ItemRarityID.Blue
			? ObtainabilityDifficulty.Medium
			: ObtainabilityDifficulty.Easy;
		if (_openableBags.ContainsKey(itemId) && diff < ObtainabilityDifficulty.Medium)
			diff = ObtainabilityDifficulty.Medium;
		string reason = isCombatOrEquip ? LootLoc("WorldChest") : LootLoc("WorldGather");
		(diff, reason) = ApplyMaterialDifficulty(itemId, diff, reason);

		TryRegisterSource(itemId, est, ItemSourceType.WorldTier, SourceConfidence.TierEstimated,
			LootLoc("SourceWorld"), reqHm, default, diff, reason, 1f);
	}

	private void RunRelaxationPasses(BossCatalog catalog, int maxPasses)
	{
		for (int pass = 0; pass < maxPasses; pass++)
		{
			bool changed = false;

			UpdateTileGates(catalog);

			for (int i = 0; i < Recipe.numRecipes; i++)
			{
				Recipe r = Main.recipe[i];
				if (r == null || r.Disabled || r.createItem == null || r.createItem.IsAir)
					continue;

				int outId = r.createItem.type;
				if (!Items.TryGetValue(outId, out ItemUnlockInfo outInfo))
					continue;

				ConditionGate recipeGate = ConditionProgressionMapper.EvaluateRecipeConditions(r.Conditions, catalog);
				if (recipeGate.Unobtainable)
					continue;

				float craftFloor = recipeGate.MinProgression;
				if (recipeGate.HasSpecialCondition && recipeGate.MinProgression <= 0.01f)
					craftFloor = Math.Max(craftFloor, EstimateDropRarityFloor(r.createItem, catalog));

				if ((r.createItem.damage > 0 || r.createItem.defense > 0) && r.createItem.rare >= ItemRarityID.LightRed && !recipeGate.PreHardmodeOnly)
					craftFloor = Math.Max(craftFloor, catalog.WallOfFleshProgression + 0.05f);

				for (int filterIdx = 0; filterIdx < 3; filterIdx++)
				{
					var filterMode = (DifficultyFilterMode)filterIdx;
					float recipeStage = craftFloor;
					bool allKnown = true;
					bool anyHardmode = recipeGate.RequiresHardmode;
					bool anyTierEstimated = false;
					ConditionGate combinedGate = recipeGate;
					ObtainabilityDifficulty maxDiff = recipeGate.IsSeasonal ? ObtainabilityDifficulty.Hard : ObtainabilityDifficulty.Easy;
					string hardReason = recipeGate.IsSeasonal ? LootLoc("SeasonalDrop") : LootLoc("WorkbenchCraft");

					foreach (int tileId in r.requiredTile)
					{
						TileGateInfo tg = GetTileGate(tileId, filterIdx);
						if (tg == null)
							continue;

						recipeStage = Math.Max(recipeStage, tg.Prog);
						anyHardmode |= tg.RequiresHardmode;
						combinedGate = CombineAndGates(combinedGate, tg.Gate, out bool tileImpossible);
						if (tileImpossible)
						{
							allKnown = false;
							break;
						}
						if (tg.Difficulty > maxDiff)
						{
							maxDiff = tg.Difficulty;
							hardReason = !string.IsNullOrWhiteSpace(tg.Reason) ? tg.Reason : LootLoc("WorkbenchCraft");
						}
					}

					if (!allKnown)
						continue;

					foreach (Item req in r.requiredItem)
					{
						if (req == null || req.IsAir)
							continue;

						(float ingProg, bool ingHm, bool ingEst, ConditionGate ingGate, ObtainabilityDifficulty ingDiff, string ingReason) =
							ResolveIngredientProgression(r, req.type, filterMode);
						if (ingProg >= float.MaxValue * 0.5f)
						{
							allKnown = false;
							break;
						}
						recipeStage = Math.Max(recipeStage, ingProg);
						anyHardmode |= ingHm;
						anyTierEstimated |= ingEst;
						combinedGate = CombineAndGates(combinedGate, ingGate, out bool ingImpossible);
						if (ingImpossible)
						{
							allKnown = false;
							break;
						}
						if (ingDiff > maxDiff)
						{
							maxDiff = ingDiff;
							hardReason = ingReason;
						}
					}

					if (!allKnown)
						continue;

					if (combinedGate.HasSpecialCondition && maxDiff < ObtainabilityDifficulty.Hard)
					{
						maxDiff = ObtainabilityDifficulty.Hard;
						hardReason = !string.IsNullOrWhiteSpace(combinedGate.SpecialConditionText)
							? combinedGate.SpecialConditionText
							: LootLoc("SpecialCondition");
					}

					recipeStage = ApplyMinimumFloor(outId, recipeStage);
					(maxDiff, hardReason) = ApplyMaterialDifficulty(outId, maxDiff, hardReason);
					if (!IsAllowedForFilter(maxDiff, filterMode))
						continue;

					SourceConfidence conf = anyTierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Crafted;
					bool reqHm = !combinedGate.PreHardmodeOnly && (anyHardmode || recipeStage > catalog.WallOfFleshProgression);
					if (RegisterPathIntoBucket(outInfo, filterIdx, recipeStage, ItemSourceType.Recipe, conf,
						FormatRecipeSummary(r), reqHm, combinedGate, maxDiff, hardReason, 1f))
					{
						changed = true;
					}
				}
			}

			if (PropagateOpenableBags(catalog))
				changed = true;

			if (PropagateShimmerTransforms(catalog))
				changed = true;

			if (!changed)
				break;
		}
	}

	private void UpdateTileGates(BossCatalog catalog)
	{
		foreach ((int tileId, List<int> placers) in _tilePlacers)
		{
			if (!_tileGates.TryGetValue(tileId, out TileGateInfo[] arr))
			{
				arr = new TileGateInfo[3];
				_tileGates[tileId] = arr;
			}

			for (int filterIdx = 0; filterIdx < 3; filterIdx++)
			{
				var mode = (DifficultyFilterMode)filterIdx;
				ItemUnlockPath bestPath = null;
				foreach (int placerId in placers)
				{
					if (!Items.TryGetValue(placerId, out ItemUnlockInfo pInfo))
						continue;

					ItemUnlockPath pPath = pInfo.GetPathFor(mode);
					if (pPath == null || pPath.UnlockProgression >= float.MaxValue * 0.5f)
						continue;

					if (bestPath == null
						|| pPath.UnlockProgression + 0.001f < bestPath.UnlockProgression
						|| (Math.Abs(pPath.UnlockProgression - bestPath.UnlockProgression) <= 0.02f && pPath.Difficulty < bestPath.Difficulty))
					{
						bestPath = pPath;
					}
				}

				if (bestPath == null)
					continue;

				var candidate = new TileGateInfo(
					bestPath.UnlockProgression,
					bestPath.RequiresHardmode,
					GateFromPath(bestPath),
					bestPath.Difficulty,
					bestPath.DifficultyReason);

				TileGateInfo cur = arr[filterIdx];
				if (cur == null
					|| candidate.Prog + 0.001f < cur.Prog
					|| (Math.Abs(candidate.Prog - cur.Prog) <= 0.02f && candidate.Difficulty < cur.Difficulty))
				{
					arr[filterIdx] = candidate;
				}
			}
		}
	}

	private bool PropagateOpenableBags(BossCatalog catalog)
	{
		bool changed = false;
		foreach ((int bagId, var drops) in _openableBags)
		{
			if (!Items.TryGetValue(bagId, out ItemUnlockInfo bagInfo))
				continue;

			string bagName = Lang.GetItemNameValue(bagId);
			for (int filterIdx = 0; filterIdx < 3; filterIdx++)
			{
				ItemUnlockPath bagPath = bagInfo.PathsByFilter[filterIdx];
				if (bagPath == null || bagPath.UnlockProgression >= float.MaxValue * 0.5f)
					continue;

				ConditionGate bagGate = GateFromPath(bagPath);
				var filterMode = (DifficultyFilterMode)filterIdx;

				foreach ((DropRateInfo dr, ConditionGate dropGate) in drops)
				{
					if (!Items.TryGetValue(dr.itemId, out ItemUnlockInfo dstInfo))
						continue;

					ConditionGate combined = CombineAndGates(bagGate, dropGate, out bool impossible);
					if (impossible)
						continue;

					float stage = ApplyMinimumFloor(dr.itemId, Math.Max(bagPath.UnlockProgression + 0.001f, dropGate.MinProgression));
					if (dropGate.MinProgression <= 0.01f && !bagPath.RequiresHardmode && !combined.PreHardmodeOnly
						&& ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
					{
						stage = Math.Max(stage, EstimateDropRarityFloor(dropItem, catalog));
					}

					(ObtainabilityDifficulty dropDiff, string dropReason) = EvaluateDropDifficulty(dr.dropRate, isBossDrop: false);
					if (combined.IsSeasonal && dropDiff < ObtainabilityDifficulty.Hard)
					{
						dropDiff = ObtainabilityDifficulty.Hard;
						dropReason = LootLoc("SeasonalDrop");
					}

					ObtainabilityDifficulty combDiff = bagPath.Difficulty > dropDiff ? bagPath.Difficulty : dropDiff;
					string combReason = bagPath.Difficulty >= dropDiff && !string.IsNullOrEmpty(bagPath.DifficultyReason)
						? bagPath.DifficultyReason
						: $"{bagName}: {dropReason}";
					(combDiff, combReason) = ApplyMaterialDifficulty(dr.itemId, combDiff, combReason);

					if (!IsAllowedForFilter(combDiff, filterMode))
						continue;

					bool reqHm = !combined.PreHardmodeOnly && (bagPath.RequiresHardmode || combined.RequiresHardmode || stage > catalog.WallOfFleshProgression);
					SourceConfidence conf = bagPath.Confidence == SourceConfidence.TierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Verified;
					if (RegisterPathIntoBucket(dstInfo, filterIdx, stage, ItemSourceType.BagOrCrate, conf,
						LootLoc("SourceInsideBag", bagName), reqHm, combined, combDiff, combReason, dr.dropRate))
					{
						changed = true;
					}
				}
			}
		}
		return changed;
	}

	private bool PropagateShimmerTransforms(BossCatalog catalog)
	{
		bool changed = false;
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, 18f);

		foreach (int itemId in _allValidItemIds)
		{
			if (itemId < ItemID.Sets.ShimmerTransformToItem.Length)
			{
				int shimmerOut = ItemID.Sets.ShimmerTransformToItem[itemId];
				if (shimmerOut > 0 && TryPropagateSingleShimmer(itemId, shimmerOut, 1.05f, forceHardmode: false, LootLoc("SourceShimmer", Lang.GetItemNameValue(itemId))))
					changed = true;
			}
		}

		// Vanilla hardcoded Moon-Lord-gated Item.GetShimmered() transforms (not in ShimmerTransformToItem)
		foreach ((int srcId, int dstId) in new (int, int)[]
		{
			(ItemID.RodofDiscord, ItemID.RodOfHarmony),
			(ItemID.Clentaminator, ItemID.Clentaminator2),
			(ItemID.BottomlessBucket, ItemID.BottomlessShimmerBucket),
			(ItemID.BottomlessShimmerBucket, ItemID.BottomlessBucket)
		})
		{
			if (TryPropagateSingleShimmer(srcId, dstId, ml + 0.01f, forceHardmode: true, LootLoc("SourceShimmerML", Lang.GetItemNameValue(srcId))))
				changed = true;
		}

		return changed;
	}

	private bool TryPropagateSingleShimmer(int srcId, int dstId, float minStage, bool forceHardmode, string summary)
	{
		if (!Items.TryGetValue(srcId, out ItemUnlockInfo srcInfo) || !Items.TryGetValue(dstId, out ItemUnlockInfo dstInfo))
			return false;

		bool changed = false;
		for (int filterIdx = 1; filterIdx < 3; filterIdx++)
		{
			ItemUnlockPath srcPath = srcInfo.PathsByFilter[filterIdx];
			if (srcPath == null || srcPath.UnlockProgression >= float.MaxValue * 0.5f)
				continue;

			if (!forceHardmode && !srcPath.RequiresHardmode && ContentSamples.ItemsByType.TryGetValue(dstId, out Item shOutItem) && shOutItem.rare >= ItemRarityID.LightRed)
				continue;

			float shStage = ApplyMinimumFloor(dstId, Math.Max(srcPath.UnlockProgression, minStage));
			ObtainabilityDifficulty shDiff = srcPath.Difficulty > ObtainabilityDifficulty.Hard ? srcPath.Difficulty : ObtainabilityDifficulty.Hard;
			if (!IsAllowedForFilter(shDiff, (DifficultyFilterMode)filterIdx))
				continue;

			ConditionGate gate = GateFromPath(srcPath);
			bool reqHm = forceHardmode || srcPath.RequiresHardmode || (!gate.PreHardmodeOnly && shStage > _wofProg);
			if (RegisterPathIntoBucket(dstInfo, filterIdx, shStage, ItemSourceType.Shimmer, srcPath.Confidence,
				summary, reqHm, gate, shDiff, LootLoc("ShimmerTransform"), 1f))
			{
				changed = true;
			}
		}
		return changed;
	}

	private void ApplyFallbackTierEstimates(BossCatalog catalog)
	{
		foreach (int itemId in _allValidItemIds)
		{
			ItemUnlockInfo info = Items[itemId];
			if (info.GetPathFor(DifficultyFilterMode.All) != null)
				continue;

			Item item = ContentSamples.ItemsByType[itemId];
			float est = ApplyMinimumFloor(itemId, EstimateRawWorldItemStage(item, catalog));
			bool reqHm = est > catalog.WallOfFleshProgression;
			(ObtainabilityDifficulty diff, string reason) = ApplyMaterialDifficulty(
				itemId, ObtainabilityDifficulty.Medium, LootLoc("TierEstimate"));

			TryRegisterSource(itemId, est, ItemSourceType.EstimatedTier, SourceConfidence.TierEstimated,
				LootLoc("SourceTierEstimate"), reqHm, default, diff, reason, 1f);
		}
	}

	private (float Prog, bool Hardmode, bool Estimated, ConditionGate Gate, ObtainabilityDifficulty Diff, string Reason) ResolveIngredientProgression(
		Recipe recipe, int reqType, DifficultyFilterMode filterMode)
	{
		if (recipe.acceptedGroups != null)
		{
			foreach (int groupId in recipe.acceptedGroups)
			{
				if (groupId < 0)
					break;

				if (RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup group) && group.ContainsItem(reqType))
				{
					float minGroup = float.MaxValue;
					bool hm = false;
					bool est = false;
					ConditionGate groupGate = default;
					ObtainabilityDifficulty bestDiff = ObtainabilityDifficulty.VeryHard;
					string bestReason = string.Empty;
					bool foundWorldCompat = false;

					foreach (int validId in group.ValidItems)
					{
						if (!Items.TryGetValue(validId, out ItemUnlockInfo gInfo))
							continue;

						ItemUnlockPath path = gInfo.GetPathFor(filterMode);
						if (path == null)
							continue;

						bool compat = Main.gameMenu || IsPathCompatibleWithActiveWorld(path);
						if (!compat && foundWorldCompat)
							continue;

						bool takeFirstCompat = compat && !foundWorldCompat;
						if (takeFirstCompat
							|| path.UnlockProgression + 0.001f < minGroup
							|| (Math.Abs(path.UnlockProgression - minGroup) <= 0.001f && path.Difficulty < bestDiff))
						{
							foundWorldCompat |= compat;
							minGroup = path.UnlockProgression;
							hm = path.RequiresHardmode;
							est = path.Confidence == SourceConfidence.TierEstimated;
							groupGate = GateFromPath(path);
							bestDiff = path.Difficulty;
							bestReason = path.DifficultyReason;
						}
						else if (Math.Abs(path.UnlockProgression - minGroup) <= 0.25f && path.RequiresHardmode == hm)
						{
							groupGate = CombineOrGates(groupGate, GateFromPath(path));
						}
					}
					if (minGroup < float.MaxValue * 0.5f)
						return (minGroup, hm, est, groupGate, bestDiff, bestReason);
				}
			}
		}

		if (Items.TryGetValue(reqType, out ItemUnlockInfo info))
		{
			ItemUnlockPath path = info.GetPathFor(filterMode);
			if (path != null)
				return (path.UnlockProgression, path.RequiresHardmode, path.Confidence == SourceConfidence.TierEstimated, GateFromPath(path), path.Difficulty, path.DifficultyReason);
		}

		return (float.MaxValue, false, true, default, ObtainabilityDifficulty.VeryHard, string.Empty);
	}

	private static bool IsPathCompatibleWithActiveWorld(ItemUnlockPath path)
	{
		if (path.ExpertOnly && !Main.expertMode) return false;
		if (path.MasterOnly && !Main.masterMode) return false;
		if (path.ClassicOnly && Main.expertMode) return false;
		if (!Main.drunkWorld)
		{
			if (path.CrimsonOnly && !WorldGen.crimson) return false;
			if (path.CorruptOnly && WorldGen.crimson) return false;
		}
		return true;
	}

	private static ConditionGate GateFromPath(ItemUnlockPath p) => new ConditionGate
	{
		MinProgression = p.UnlockProgression,
		RequiresHardmode = p.RequiresHardmode,
		PreHardmodeOnly = p.PreHardmodeOnly,
		ExpertOnly = p.ExpertOnly,
		MasterOnly = p.MasterOnly,
		ClassicOnly = p.ClassicOnly,
		CrimsonOnly = p.CrimsonOnly,
		CorruptOnly = p.CorruptOnly
	};

	private static ConditionGate CombineAndGates(ConditionGate a, ConditionGate b, out bool impossible)
	{
		bool expert = a.ExpertOnly || b.ExpertOnly;
		bool master = a.MasterOnly || b.MasterOnly;
		bool classic = a.ClassicOnly || b.ClassicOnly;
		bool crimson = a.CrimsonOnly || b.CrimsonOnly;
		bool corrupt = a.CorruptOnly || b.CorruptOnly;

		impossible = (classic && (expert || master)) || (!Main.drunkWorld && crimson && corrupt);
		if (impossible && Main.drunkWorld && crimson && corrupt)
			crimson = corrupt = false;

		return new ConditionGate
		{
			MinProgression = Math.Max(a.MinProgression, b.MinProgression),
			RequiresHardmode = a.RequiresHardmode || b.RequiresHardmode,
			PreHardmodeOnly = (a.PreHardmodeOnly || b.PreHardmodeOnly) && !(a.RequiresHardmode || b.RequiresHardmode),
			ExpertOnly = expert,
			MasterOnly = master,
			ClassicOnly = classic,
			CrimsonOnly = crimson,
			CorruptOnly = corrupt,
			Unobtainable = a.Unobtainable || b.Unobtainable || impossible,
			IsChallengeCondition = a.IsChallengeCondition || b.IsChallengeCondition,
			IsSeasonal = a.IsSeasonal || b.IsSeasonal,
			HasSpecialCondition = a.HasSpecialCondition || b.HasSpecialCondition,
			SpecialConditionText = !string.IsNullOrWhiteSpace(a.SpecialConditionText) ? a.SpecialConditionText : b.SpecialConditionText
		};
	}

	private static ConditionGate CombineOrGates(ConditionGate a, ConditionGate b)
	{
		bool aClassic = !a.ExpertOnly && !a.MasterOnly;
		bool aExpert = !a.ClassicOnly && !a.MasterOnly;
		bool aMaster = !a.ClassicOnly;
		bool bClassic = !b.ExpertOnly && !b.MasterOnly;
		bool bExpert = !b.ClassicOnly && !b.MasterOnly;
		bool bMaster = !b.ClassicOnly;

		bool c = aClassic || bClassic;
		bool e = aExpert || bExpert;
		bool m = aMaster || bMaster;

		bool allowCorrupt = !a.CrimsonOnly || !b.CrimsonOnly;
		bool allowCrimson = !a.CorruptOnly || !b.CorruptOnly;

		return a with
		{
			PreHardmodeOnly = a.PreHardmodeOnly && b.PreHardmodeOnly,
			ClassicOnly = c && !e && !m,
			ExpertOnly = !c && e,
			MasterOnly = !c && !e && m,
			CrimsonOnly = !allowCorrupt && allowCrimson,
			CorruptOnly = allowCorrupt && !allowCrimson
		};
	}

	private void TryRegisterSource(
		int itemId,
		float stage,
		ItemSourceType sourceType,
		SourceConfidence confidence,
		string summary,
		bool hardmode,
		ConditionGate gate,
		ObtainabilityDifficulty difficulty,
		string difficultyReason,
		float dropChance)
	{
		if (gate.Unobtainable || !Items.TryGetValue(itemId, out ItemUnlockInfo info))
			return;

		stage = ApplyMinimumFloor(itemId, stage);
		(difficulty, difficultyReason) = ApplyMaterialDifficulty(itemId, difficulty, difficultyReason);
		hardmode = !gate.PreHardmodeOnly && (hardmode || gate.RequiresHardmode || stage > _wofProg);

		int minBucket = difficulty switch
		{
			<= ObtainabilityDifficulty.Medium => 0,
			ObtainabilityDifficulty.Hard => 1,
			_ => 2
		};

		for (int b = minBucket; b < 3; b++)
			RegisterPathIntoBucket(info, b, stage, sourceType, confidence, summary, hardmode, gate, difficulty, difficultyReason, dropChance);
	}

	private static bool RegisterPathIntoBucket(
		ItemUnlockInfo info,
		int bucketIdx,
		float stage,
		ItemSourceType sourceType,
		SourceConfidence confidence,
		string summary,
		bool hardmode,
		ConditionGate gate,
		ObtainabilityDifficulty difficulty,
		string difficultyReason,
		float dropChance)
	{
		ItemUnlockPath existing = info.PathsByFilter[bucketIdx];
		bool effHm = !gate.PreHardmodeOnly && (hardmode || gate.RequiresHardmode);

		if (existing != null && stage > existing.UnlockProgression + 0.0005f && !existing.RequiresHardmode && effHm)
			return false;

		bool better = existing == null
			|| stage + 0.45f < existing.UnlockProgression
			|| (stage + 0.0005f < existing.UnlockProgression && difficulty <= existing.Difficulty)
			|| (stage <= existing.UnlockProgression + 0.02f && effHm == existing.RequiresHardmode && difficulty < existing.Difficulty)
			|| (existing.SourceType == ItemSourceType.EstimatedTier && sourceType == ItemSourceType.Recipe && stage <= existing.UnlockProgression + 0.5f && effHm == existing.RequiresHardmode);

		ContentSamples.ItemsByType.TryGetValue(info.ItemType, out Item item);
		if (!better)
		{
			if (Math.Abs(stage - existing.UnlockProgression) <= 0.5f && effHm == existing.RequiresHardmode && difficulty <= existing.Difficulty)
				return MergePathExclusivity(existing, gate, item);
			return false;
		}

		var next = new ItemUnlockPath
		{
			UnlockProgression = stage,
			SourceType = sourceType,
			Confidence = confidence,
			SourceSummary = summary,
			Difficulty = difficulty,
			DifficultyReason = difficultyReason ?? string.Empty,
			BestDropChance = dropChance,
			RequiresHardmode = effHm,
			PreHardmodeOnly = gate.PreHardmodeOnly && !effHm,
			ExpertOnly = gate.ExpertOnly || (item?.expert ?? false),
			MasterOnly = gate.MasterOnly || (item?.master ?? false),
			ClassicOnly = gate.ClassicOnly,
			CrimsonOnly = gate.CrimsonOnly,
			CorruptOnly = gate.CorruptOnly
		};

		if (existing != null && Math.Abs(stage - existing.UnlockProgression) <= 0.5f && effHm == existing.RequiresHardmode)
			MergePathExclusivity(next, GateFromPath(existing), item);

		info.PathsByFilter[bucketIdx] = next;
		return true;
	}

	private static bool MergePathExclusivity(ItemUnlockPath target, ConditionGate other, Item item)
	{
		bool changed = false;

		if (item == null || (!item.expert && !item.master))
		{
			bool tClassic = !target.ExpertOnly && !target.MasterOnly;
			bool tExpert = !target.ClassicOnly && !target.MasterOnly;
			bool tMaster = !target.ClassicOnly;

			bool oClassic = !other.ExpertOnly && !other.MasterOnly;
			bool oExpert = !other.ClassicOnly && !other.MasterOnly;
			bool oMaster = !other.ClassicOnly;

			bool mClassic = tClassic || oClassic;
			bool mExpert = tExpert || oExpert;
			bool mMaster = tMaster || oMaster;

			bool nextClassic = mClassic && !mExpert && !mMaster;
			bool nextExpert = !mClassic && mExpert;
			bool nextMaster = !mClassic && !mExpert && mMaster;

			if (target.ClassicOnly != nextClassic || target.ExpertOnly != nextExpert || target.MasterOnly != nextMaster)
			{
				target.ClassicOnly = nextClassic;
				target.ExpertOnly = nextExpert;
				target.MasterOnly = nextMaster;
				changed = true;
			}
		}

		bool allowCorrupt = !target.CrimsonOnly || !other.CrimsonOnly;
		bool allowCrimson = !target.CorruptOnly || !other.CorruptOnly;
		bool nextCrimsonOnly = !allowCorrupt && allowCrimson;
		bool nextCorruptOnly = allowCorrupt && !allowCrimson;
		if (target.CrimsonOnly != nextCrimsonOnly || target.CorruptOnly != nextCorruptOnly)
		{
			target.CrimsonOnly = nextCrimsonOnly;
			target.CorruptOnly = nextCorruptOnly;
			changed = true;
		}

		if (target.PreHardmodeOnly && !other.PreHardmodeOnly)
		{
			target.PreHardmodeOnly = false;
			changed = true;
		}

		return changed;
	}

	private static bool IsAllowedForFilter(ObtainabilityDifficulty difficulty, DifficultyFilterMode mode)
	{
		return mode switch
		{
			DifficultyFilterMode.Comfortable => difficulty <= ObtainabilityDifficulty.Medium,
			DifficultyFilterMode.IncludeHard => difficulty <= ObtainabilityDifficulty.Hard,
			_ => true
		};
	}

	private static (ObtainabilityDifficulty Diff, string Reason) EvaluateDropDifficulty(float dropRate, bool isBossDrop)
	{
		if (isBossDrop)
		{
			if (dropRate <= 0f || dropRate >= 0.04f)
				return (ObtainabilityDifficulty.Easy, LootLoc("BossDrop"));
			if (dropRate >= 0.015f)
				return (ObtainabilityDifficulty.Hard, LootLoc("BossRareDrop", dropRate * 100f));
			return (ObtainabilityDifficulty.VeryHard, LootLoc("VeryRareDrop", dropRate * 100f));
		}

		if (dropRate <= 0f)
			return (ObtainabilityDifficulty.Medium, LootLoc("DropGeneric"));
		if (dropRate < 0.015f)
			return (ObtainabilityDifficulty.VeryHard, LootLoc("VeryRareDrop", dropRate * 100f));
		if (dropRate <= 0.06f)
			return (ObtainabilityDifficulty.Hard, LootLoc("RareDrop", dropRate * 100f));
		if (dropRate < 0.20f)
			return (ObtainabilityDifficulty.Medium, LootLoc("ChanceDrop", dropRate * 100f));
		return (ObtainabilityDifficulty.Easy, LootLoc("CommonDrop", dropRate * 100f));
	}

	private static string LootLoc(string key, params object[] args)
	{
		string fullKey = $"Mods.BossBuildAdvisor.Loot.{key}";
		if (args == null || args.Length == 0)
			return Language.GetTextValue(fullKey);
		return Language.GetTextValue(fullKey, args);
	}

	private static List<DropRateInfo> FlattenDropRules(List<IItemDropRule> rules)
	{
		var results = new List<DropRateInfo>();
		if (rules == null)
			return results;

		var feed = new DropRateInfoChainFeed(1f);
		foreach (IItemDropRule rule in rules)
		{
			try
			{
				rule?.ReportDroprates(results, feed);
			}
			catch
			{
			}
		}
		return results;
	}

	private static float EstimateNpcMinimumStage(NPC npc, BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float eow = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float boc = catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, eow);
		float evil = Math.Min(eow, boc);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetMechAnyProgression();
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 5f);

		// Vanilla Boss Minions / Jungle / Desert / Dungeon / Underworld / Hardmode / Post-Plantera biome gates
		switch (npc.type)
		{
			case NPCID.Creeper:
				return boc + 0.01f;

			case NPCID.EaterofWorldsBody:
			case NPCID.EaterofWorldsTail:
				return eow + 0.01f;

			case NPCID.SkeletronHand:
				return skel + 0.01f;

			case NPCID.Hornet:
			case NPCID.HornetFatty:
			case NPCID.HornetHoney:
			case NPCID.HornetLeafy:
			case NPCID.HornetSpikey:
			case NPCID.HornetStingy:
			case NPCID.ManEater:
			case NPCID.JungleBat:
			case NPCID.SpikedJungleSlime:
				return ks + 0.05f;

			case NPCID.Hellbat:
			case NPCID.FireImp:
			case NPCID.Demon:
			case NPCID.VoodooDemon:
			case NPCID.BoneSerpentHead:
			case NPCID.LavaSlime:
				return Math.Min(skel - 0.1f, evil + 0.5f);

			case NPCID.AngryBones:
			case NPCID.AngryBonesBig:
			case NPCID.AngryBonesBigMuscle:
			case NPCID.AngryBonesBigHelmet:
			case NPCID.DarkCaster:
			case NPCID.CursedSkull:
			case NPCID.DungeonSlime:
				return skel + 0.01f;

			case NPCID.SkeletonArcher:
			case NPCID.Mimic:
			case NPCID.Wraith:
			case NPCID.PossessedArmor:
			case NPCID.WyvernHead:
			case NPCID.Lavabat:
			case NPCID.RedDevil:
			case NPCID.BigMimicCorruption:
			case NPCID.BigMimicCrimson:
			case NPCID.BigMimicHallow:
			case NPCID.BigMimicJungle:
			case NPCID.GoblinSummoner:
			case NPCID.RuneWizard:
			case NPCID.Mummy:
			case NPCID.DarkMummy:
			case NPCID.LightMummy:
			case NPCID.BloodMummy:
			case NPCID.ArmoredSkeleton:
			case NPCID.MossHornet:
			case NPCID.AngryTrapper:
			case NPCID.IchorSticker:
			case NPCID.Clinger:
			case NPCID.Corruptor:
			case NPCID.CorruptSlime:
			case NPCID.IlluminantBat:
			case NPCID.IlluminantSlime:
			case NPCID.ChaosElemental:
			case NPCID.Pixie:
			case NPCID.Unicorn:
			case NPCID.SeekerHead:
			case NPCID.FloatyGross:
			case NPCID.Gastropod:
			case NPCID.Herpling:
			case NPCID.Derpling:
			case NPCID.GiantTortoise:
			case NPCID.GiantBat:
			case NPCID.Moth:
			case NPCID.CrimsonAxe:
			case NPCID.EnchantedSword:
			case NPCID.CursedHammer:
			case NPCID.PigronCorruption:
			case NPCID.PigronCrimson:
			case NPCID.PigronHallow:
			case NPCID.DesertGhoul:
			case NPCID.DesertGhoulCorruption:
			case NPCID.DesertGhoulCrimson:
			case NPCID.DesertGhoulHallow:
			case NPCID.SandShark:
			case NPCID.SandsharkCorrupt:
			case NPCID.SandsharkCrimson:
			case NPCID.SandsharkHallow:
			case NPCID.DuneSplicerHead:
				return wof + 0.05f;

			case NPCID.UndeadMiner:
			case NPCID.Tim:
			case NPCID.Pinky:
			case NPCID.Nymph:
			case NPCID.LostGirl:
			case NPCID.DoctorBones:
			case NPCID.Gnome:
			case NPCID.GoldenSlime:
				return Math.Min(ks + 0.2f, 1.2f);

			case NPCID.Paladin:
			case NPCID.BoneLee:
			case NPCID.TacticalSkeleton:
			case NPCID.SkeletonSniper:
			case NPCID.SkeletonCommando:
			case NPCID.DungeonSpirit:
			case NPCID.GiantCursedSkull:
			case NPCID.Lihzahrd:
			case NPCID.LihzahrdCrawler:
			case NPCID.FlyingSnake:
				return plantera + 0.05f;
		}

		// Note: Pre-Hardmode Lifeform Analyzer spawns (Tim, Nymph, Pinky) have npc.rarity 3-4 with low lifeMax
		if (npc.lifeMax >= 4000 || (npc.rarity >= 4 && npc.lifeMax >= 500))
			return Math.Max(wof + 2.0f, mech);

		if (npc.lifeMax >= 1200 || npc.damage >= 75)
			return wof + 0.1f;

		if (npc.lifeMax >= 350 || npc.damage >= 42)
			return Math.Max(eoc, 2.5f);

		if (npc.lifeMax >= 160 || npc.damage >= 30)
			return ks + 0.05f;

		return 0f;
	}

	private static float GetTownNpcMinimumStage(int npcType, BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float evil = Math.Min(
			catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f),
			catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, 3f));
		float qb = catalog.GetProgressionForNpc(NPCID.QueenBee, 4f);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mechAny = catalog.GetMechAnyProgression();
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 5f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);

		return npcType switch
		{
			NPCID.ArmsDealer => Math.Max(ks + 0.5f, 1.5f),
			NPCID.TravellingMerchant or NPCID.SkeletonMerchant => Math.Max(ks + 0.2f, 1.2f),
			NPCID.Dryad => eoc + 0.01f,
			NPCID.GoblinTinkerer or NPCID.DD2Bartender => Math.Max(eoc + 0.5f, evil - 0.3f),
			NPCID.WitchDoctor => qb + 0.01f,
			NPCID.Clothier or NPCID.Mechanic => skel + 0.01f,
			NPCID.Wizard or NPCID.TaxCollector => wof + 0.05f,
			NPCID.Truffle => wof + 0.1f,
			NPCID.SantaClaus => wof + 0.35f,
			NPCID.Pirate => wof + 0.5f,
			NPCID.Steampunker => mechAny + 0.01f,
			NPCID.Cyborg => plantera + 0.01f,
			NPCID.Princess => golem + 0.5f,
			_ => 0.5f
		};
	}

	private static float EstimateDropRarityFloor(Item item, BossCatalog catalog)
	{
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float evil = Math.Min(
			catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f),
			catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, 3f));
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetMechAnyProgression();
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 5f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, golem + 4f);
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1f);

		float floor = item.rare switch
		{
			<= ItemRarityID.Blue => 0f,
			ItemRarityID.Green => Math.Min(1.2f, Math.Max(0.8f, eoc - 0.5f)),
			ItemRarityID.Orange => Math.Max(evil + 0.2f, 3.2f),
			ItemRarityID.LightRed => wof + 0.05f,
			ItemRarityID.Pink => wof + 1.2f,
			ItemRarityID.LightPurple => mech + 0.1f,
			ItemRarityID.Lime => plantera + 0.05f,
			ItemRarityID.Yellow => golem + 0.05f,
			ItemRarityID.Cyan => Math.Max(golem + 1.0f, cultist - 0.5f),
			ItemRarityID.Red => cultist + 0.2f,
			>= ItemRarityID.Purple => ml + 0.1f
		};

		// Also guard weapons/armor with high baseline stats or Expert/Master negative rarity
		bool explicitPreHmRare = item.rare >= ItemRarityID.White && item.rare <= ItemRarityID.Orange;
		if (item.rare < ItemRarityID.White)
		{
			if (item.value >= 500000) floor = Math.Max(floor, plantera);
			else if (item.value >= 200000) floor = Math.Max(floor, mech);
			else if (item.value >= 75000) floor = Math.Max(floor, wof + 0.05f);
		}
		if (explicitPreHmRare)
		{
			if (item.damage >= 55 || item.defense >= 11)
				floor = Math.Max(floor, Math.Min(wof - 0.1f, Math.Max(evil + 0.3f, 3.5f)));
		}
		else if (item.damage >= 180 || item.defense >= 28)
			floor = Math.Max(floor, cultist);
		else if (item.damage >= 90 || item.defense >= 18)
			floor = Math.Max(floor, mech);
		else if (item.damage >= 48 || item.defense >= 11)
			floor = Math.Max(floor, wof + 0.05f);

		return floor;
	}

	private static float EstimateRawWorldItemStage(Item item, BossCatalog catalog)
	{
		float evil = Math.Min(
			catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f),
			catalog.GetProgressionForNpc(NPCID.BrainofCthulhu, 3f));
		float wof = catalog.WallOfFleshProgression;
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float mech = catalog.GetMechAnyProgression();
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 5f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, golem + 4f);
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1f);

		float byRare = item.rare switch
		{
			<= ItemRarityID.White => 0f,
			ItemRarityID.Blue => 0.8f,
			ItemRarityID.Green => 2.2f,
			ItemRarityID.Orange => Math.Max(skel - 0.5f, 4.2f),
			ItemRarityID.LightRed => wof + 0.2f,
			ItemRarityID.Pink => wof + 1.5f,
			ItemRarityID.LightPurple => mech + 0.2f,
			ItemRarityID.Lime => plantera + 0.1f,
			ItemRarityID.Yellow => golem + 0.1f,
			ItemRarityID.Cyan => cultist - 0.4f,
			ItemRarityID.Red => cultist + 0.5f,
			>= ItemRarityID.Purple => ml + 0.2f
		};

		if (item.rare < ItemRarityID.White)
		{
			if (item.value >= 500000) byRare = Math.Max(byRare, plantera);
			else if (item.value >= 200000) byRare = Math.Max(byRare, mech);
			else if (item.value >= 75000) byRare = Math.Max(byRare, wof + 0.2f);
		}

		// Guard against modded items with custom/high rare values or missing rarity
		bool explicitPreHmRare = item.rare >= ItemRarityID.White && item.rare <= ItemRarityID.Orange;
		if (explicitPreHmRare)
		{
			if (item.damage >= 55 || item.defense >= 11)
				byRare = Math.Max(byRare, Math.Min(wof - 0.1f, Math.Max(evil + 0.3f, 3.5f)));
		}
		else if (item.damage >= 180 || item.defense >= 28)
			byRare = Math.Max(byRare, cultist);
		else if (item.damage >= 90 || item.defense >= 18)
			byRare = Math.Max(byRare, mech);
		else if (item.damage >= 45 || item.defense >= 10)
			byRare = Math.Max(byRare, wof + 0.2f);

		return byRare;
	}

	private static string FormatRecipeSummary(Recipe r)
	{
		var ings = r.requiredItem
			.Where(x => x != null && !x.IsAir)
			.Take(4)
			.Select(x => x.stack > 1 ? $"{x.Name} x{x.stack}" : x.Name);
		string suffix = r.requiredItem.Count > 4 ? ", ..." : string.Empty;
		string text = LootLoc("SourceCraft", string.Join(" + ", ings) + suffix);

		if (r.Conditions != null && r.Conditions.Count > 0)
		{
			var condTexts = r.Conditions
				.Select(c => c?.Description?.Value)
				.Where(s => !string.IsNullOrWhiteSpace(s) && !s.Contains("Mods.", StringComparison.Ordinal))
				.Take(2)
				.ToList();
			if (condTexts.Count > 0)
				text += " [" + string.Join(", ", condTexts) + "]";
		}

		return text;
	}
}
