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

	private readonly Dictionary<int, float> _tileProgression = new();
	private readonly Dictionary<int, float> _minimumItemFloor = new();
	private readonly Dictionary<int, (ObtainabilityDifficulty Difficulty, string Reason)> _materialDifficulty = new();
	private readonly List<int> _allValidItemIds = new();
	private readonly Dictionary<int, List<(DropRateInfo Drop, ConditionGate Gate)>> _openableBags = new();
	private float _wofProg = 7.0f;

	public void Build(Mod mod, BossCatalog catalog)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		_wofProg = catalog.WallOfFleshProgression;
		Items.Clear();
		Semantics.Clear();
		DirectRecipesForItem.Clear();
		EquipmentUpgradesTo.Clear();
		_tileProgression.Clear();
		_minimumItemFloor.Clear();
		_materialDifficulty.Clear();
		_allValidItemIds.Clear();
		_openableBags.Clear();

		InitializeItemBaselineAndSemantics(catalog);
		SeedVanillaProgressionFloors(catalog);
		IndexRecipesAndDirectUpgrades();
		SeedBossLootAndBags(catalog);
		SeedNpcDrops(catalog);
		CollectOpenableBagsAndCrates(catalog);
		SeedNpcShops(catalog);
		SeedGuaranteedBiomeResources(catalog);
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
				SourceSummary = "Tier estimate",
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
			bool unavailable = !stage.IsAvailable;
			bool earlyHidden = !stage.IsVisibleOnChecklist && !stage.IsDowned && stage.Progression < ks;

			foreach (int lootId in stage.LootItemIds)
			{
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

				TryRegisterSource(lootId, itemStage, ItemSourceType.BossDrop, SourceConfidence.Verified,
					$"Drops from {stage.DisplayName}", postWof || itemStage > catalog.WallOfFleshProgression, default,
					diff, reason, 1f);
			}

			foreach (DropRateInfo dr in stage.DropRates)
				RegisterStageDrop(stage, dr, postBossProg, postWof, $"Drops from {stage.DisplayName}", false, ks, catalog);

			foreach (int npcId in stage.NpcIds)
			{
				var rules = Main.ItemDropsDB.GetRulesForNPCID(npcId, includeGlobalDrops: false);
				foreach (DropRateInfo dr in FlattenDropRules(rules))
					RegisterStageDrop(stage, dr, postBossProg, postWof, $"Drops from {stage.DisplayName}", false, ks, catalog);
			}

			if (stage.TreasureBagId > 0)
			{
				var bagRules = Main.ItemDropsDB.GetRulesForItemID(stage.TreasureBagId);
				string bagName = Lang.GetItemNameValue(stage.TreasureBagId);
				foreach (DropRateInfo dr in FlattenDropRules(bagRules))
					RegisterStageDrop(stage, dr, postBossProg, postWof, $"From {bagName} ({stage.DisplayName})", true, ks, catalog);
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
		BossCatalog catalog)
	{
		ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
		float itemStage = Math.Max(postBossProg, gate.MinProgression);
		if (stage.IsEvent && gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item evItem))
			itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

		(ObtainabilityDifficulty diff, string reason) = EvaluateDropDifficulty(dr.dropRate, !stage.IsEvent);
		if (!stage.IsAvailable && diff < ObtainabilityDifficulty.VeryHard)
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

		foreach ((int npcId, NPC npc) in ContentSamples.NpcsByNetId)
		{
			if (npc == null || npc.townNPC || bossNpcSet.Contains(npcId))
				continue;

			var rules = Main.ItemDropsDB.GetRulesForNPCID(npcId, includeGlobalDrops: false);
			if (rules == null || rules.Count == 0)
				continue;

			float npcStatFloor = EstimateNpcMinimumStage(npc, catalog);
			string npcName = Lang.GetNPCNameValue(npcId);
			if (string.IsNullOrWhiteSpace(npcName))
				npcName = npc.TypeName;

			foreach (DropRateInfo dr in FlattenDropRules(rules))
			{
				// Ignore pure easter-egg drops (< 0.1%)
				if (dr.dropRate > 0f && dr.dropRate < 0.001f)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
				float stage = Math.Max(npcStatFloor, gate.MinProgression);
				if (gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
				{
					float rareFloor = EstimateDropRarityFloor(dropItem, catalog);
					stage = Math.Max(stage, rareFloor);
				}

				(ObtainabilityDifficulty diff, string reason) = EvaluateDropDifficulty(dr.dropRate, isBossDrop: false);
				bool reqHardmode = gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;

				TryRegisterSource(dr.itemId, stage, ItemSourceType.NpcDrop, SourceConfidence.Verified,
					$"Drop: {npcName} ({dr.dropRate * 100f:0.#}%)", reqHardmode, gate,
					diff, $"{npcName}: {reason}", dr.dropRate);
			}
		}
	}

	private void CollectOpenableBagsAndCrates(BossCatalog catalog)
	{
		float ks = catalog.GetProgressionForNpc(NPCID.KingSlime, 1f);
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
					"Dungeon / Shadow Key", false, default, ObtainabilityDifficulty.Medium, LootLoc("LockBox"), 1f);
			}
			else if (itemId == ItemID.DungeonFishingCrateHard)
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					"Hardmode Dungeon Crate", true, default, ObtainabilityDifficulty.Hard, LootLoc("DungeonCrate"), 1f);
			}
			else if (itemId == ItemID.DungeonFishingCrate)
			{
				TryRegisterSource(itemId, skel + 0.01f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					"Dungeon Crate", false, default, ObtainabilityDifficulty.Hard, LootLoc("DungeonCrate"), 1f);
			}
			else if (ItemID.Sets.IsFishingCrateHardmode[itemId])
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					"Hardmode Fishing Crate", true, default, ObtainabilityDifficulty.Hard, LootLoc("HardmodeCrate"), 1f);
			}
			else if (itemId is ItemID.WoodenCrate or ItemID.IronCrate)
			{
				TryRegisterSource(itemId, itemId == ItemID.WoodenCrate ? 0.6f : 1.0f, ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					"Fishing Crate", false, default, ObtainabilityDifficulty.Medium, LootLoc("BasicCrate"), 1f);
			}
			else if (ItemID.Sets.IsFishingCrate[itemId])
			{
				TryRegisterSource(itemId, Math.Max(ks + 0.5f, 1.5f), ItemSourceType.BagOrCrate, SourceConfidence.Verified,
					"Biome / Gold Fishing Crate", false, default, ObtainabilityDifficulty.Hard, LootLoc("BiomeCrate"), 1f);
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

			foreach (AbstractNPCShop.Entry entry in shop.ActiveEntries)
			{
				if (entry?.Item == null || entry.Item.IsAir)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateRecipeConditions(entry.Conditions, catalog);
				float stage = Math.Max(npcUnlock, gate.MinProgression);
				if (gate.MinProgression <= 0.01f)
					stage = Math.Max(stage, EstimateDropRarityFloor(entry.Item, catalog));

				bool reqHm = gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;

				ObtainabilityDifficulty diff = ObtainabilityDifficulty.Easy;
				string reason = LootLoc("ShopBuy", npcName);
				if (isTravellingMerchant)
				{
					diff = ObtainabilityDifficulty.Hard;
					reason = LootLoc("TravellingShop");
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
					$"Sold by {npcName}", reqHm, gate, diff, reason, 1f);
			}
		}
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
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float prime = catalog.GetProgressionForNpc(NPCID.SkeletronPrime, wof + 3f);
		float mechAll = Math.Max(mech, prime);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, plantera + 3f);
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1f);

		_tileProgression[TileID.Hellforge] = Math.Max(evil + 0.05f, Math.Min(wof - 0.3f, evil + 0.3f));
		_tileProgression[TileID.MythrilAnvil] = wof + 0.1f;
		_tileProgression[TileID.AdamantiteForge] = wof + 0.1f;
		_tileProgression[TileID.LihzahrdFurnace] = plantera + 0.1f;
		_tileProgression[TileID.LunarCraftingStation] = cultist + 0.1f;
		_tileProgression[TileID.Blendomatic] = wof + 0.2f;
		_tileProgression[TileID.Autohammer] = plantera + 0.05f;
		_tileProgression[TileID.CrystalBall] = wof + 0.05f;
		_tileProgression[TileID.SteampunkBoiler] = mech + 0.1f;

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

		// Hard biome/progression item floors so modded bags/mobs never pull Dungeon, Hellstone, or Hardmode ores early
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
		foreach (int id in new[] { ItemID.CobaltOre, ItemID.CobaltBar, ItemID.PalladiumOre, ItemID.PalladiumBar })
			_minimumItemFloor[id] = hm1;

		float hm2 = wof + 0.10f;
		foreach (int id in new[] { ItemID.MythrilOre, ItemID.MythrilBar, ItemID.OrichalcumOre, ItemID.OrichalcumBar })
			_minimumItemFloor[id] = hm2;

		float hm3 = wof + 0.15f;
		foreach (int id in new[] { ItemID.AdamantiteOre, ItemID.AdamantiteBar, ItemID.TitaniumOre, ItemID.TitaniumBar })
			_minimumItemFloor[id] = hm3;

		_minimumItemFloor[ItemID.HallowedBar] = mech + 0.01f;
		_minimumItemFloor[ItemID.ChlorophyteOre] = mechAll + 0.05f;
		_minimumItemFloor[ItemID.ChlorophyteBar] = mechAll + 0.05f;
		_minimumItemFloor[ItemID.ShroomiteBar] = plantera + 0.05f;
		_minimumItemFloor[ItemID.SpectreBar] = plantera + 0.05f;
		_minimumItemFloor[ItemID.LunarOre] = ml + 0.01f;
		_minimumItemFloor[ItemID.LunarBar] = ml + 0.01f;
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

		// Post-evil-boss biome ores can always be mined cleanly even if an enemy (e.g. Meteor Head 2%) also drops them rarely
		TryRegisterSource(ItemID.Meteorite, evil + 0.01f, ItemSourceType.WorldTier, SourceConfidence.Verified,
			"Meteorite biome (after EoW / BoC)", false, default, ObtainabilityDifficulty.Medium, LootLoc("MeteorBiome"), 1f);
		TryRegisterSource(ItemID.Hellstone, evil + 0.05f, ItemSourceType.WorldTier, SourceConfidence.Verified,
			"Underworld mining (after EoW / BoC)", false, default, ObtainabilityDifficulty.Medium, LootLoc("UnderworldEarly"), 1f);
	}

	private void SeedUncraftedWorldMaterials(BossCatalog catalog)
	{
		// Pre-seed raw world materials (no recipes, no NPC drops, no shops) with a conservative world-tier progression
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
		bool reqHm = est > _wofProg;

		bool isCombatOrEquip = item.damage > 0 || item.defense > 0 || item.accessory || item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0;
		ObtainabilityDifficulty diff = isCombatOrEquip && item.rare >= ItemRarityID.Blue
			? ObtainabilityDifficulty.Medium
			: ObtainabilityDifficulty.Easy;
		string reason = isCombatOrEquip ? LootLoc("WorldChest") : LootLoc("WorldGather");
		(diff, reason) = ApplyMaterialDifficulty(itemId, diff, reason);

		TryRegisterSource(itemId, est, ItemSourceType.WorldTier, SourceConfidence.TierEstimated,
			"World / Chest / Mining", reqHm, default, diff, reason, 1f);
	}

	private void RunRelaxationPasses(BossCatalog catalog, int maxPasses)
	{
		// Map tile-placing items so modded crafting stations pass their unlock progression down to recipes
		var tilePlacers = new Dictionary<int, List<int>>();
		foreach (int itemId in _allValidItemIds)
		{
			Item item = ContentSamples.ItemsByType[itemId];
			if (item.createTile >= TileID.Dirt)
			{
				if (!tilePlacers.TryGetValue(item.createTile, out List<int> list))
				{
					list = new List<int>();
					tilePlacers[item.createTile] = list;
				}
				list.Add(itemId);
			}
		}

		// Fixed-point relaxation across all three DifficultyFilterMode budgets
		for (int pass = 0; pass < maxPasses; pass++)
		{
			bool changed = false;

			foreach ((int tileId, List<int> placers) in tilePlacers)
			{
				if (_tileProgression.ContainsKey(tileId))
					continue;

				float bestPlacer = float.MaxValue;
				foreach (int placerId in placers)
				{
					ItemUnlockPath pPath = Items.TryGetValue(placerId, out ItemUnlockInfo pInfo)
						? pInfo.GetPathFor(DifficultyFilterMode.Comfortable) ?? pInfo.GetPathFor(DifficultyFilterMode.All)
						: null;
					if (pPath != null)
						bestPlacer = Math.Min(bestPlacer, pPath.UnlockProgression);
				}
				if (bestPlacer < float.MaxValue && bestPlacer > 0.5f)
					_tileProgression[tileId] = bestPlacer;
			}

			for (int i = 0; i < Recipe.numRecipes; i++)
			{
				Recipe r = Main.recipe[i];
				if (r == null || r.Disabled || r.createItem == null || r.createItem.IsAir)
					continue;

				int outId = r.createItem.type;
				if (!Items.TryGetValue(outId, out ItemUnlockInfo outInfo))
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateRecipeConditions(r.Conditions, catalog);
				if (gate.Unobtainable)
					continue;

				float craftFloor = gate.MinProgression;
				foreach (int tileId in r.requiredTile)
				{
					if (_tileProgression.TryGetValue(tileId, out float tileProg))
						craftFloor = Math.Max(craftFloor, tileProg);
				}

				// Custom quest/dialogue/mod conditions (e.g. ForgedByCalavia / RepairedByFallen) must not leak high-tier gear at stage 0
				if (gate.HasSpecialCondition && gate.MinProgression <= 0.01f)
					craftFloor = Math.Max(craftFloor, EstimateDropRarityFloor(r.createItem, catalog));

				if ((r.createItem.damage > 0 || r.createItem.defense > 0) && r.createItem.rare >= ItemRarityID.LightRed)
					craftFloor = Math.Max(craftFloor, catalog.WallOfFleshProgression + 0.05f);

				for (int filterIdx = 0; filterIdx < 3; filterIdx++)
				{
					var filterMode = (DifficultyFilterMode)filterIdx;
					float recipeStage = craftFloor;
					bool allKnown = true;
					bool anyHardmode = gate.RequiresHardmode;
					bool anyTierEstimated = false;
					ObtainabilityDifficulty maxDiff = ObtainabilityDifficulty.Easy;
					string hardReason = LootLoc("WorkbenchCraft");

					foreach (Item req in r.requiredItem)
					{
						if (req == null || req.IsAir)
							continue;

						(float ingProg, bool ingHm, bool ingEst, ObtainabilityDifficulty ingDiff, string ingReason) =
							ResolveIngredientProgression(r, req.type, filterMode);
						if (ingProg >= float.MaxValue * 0.5f)
						{
							allKnown = false;
							break;
						}
						recipeStage = Math.Max(recipeStage, ingProg);
						anyHardmode |= ingHm;
						anyTierEstimated |= ingEst;
						if (ingDiff > maxDiff)
						{
							maxDiff = ingDiff;
							hardReason = ingReason;
						}
					}

					if (!allKnown)
						continue;

					if (gate.HasSpecialCondition && maxDiff < ObtainabilityDifficulty.Hard)
					{
						maxDiff = ObtainabilityDifficulty.Hard;
						hardReason = !string.IsNullOrWhiteSpace(gate.SpecialConditionText)
							? gate.SpecialConditionText
							: LootLoc("SpecialCondition");
					}

					recipeStage = ApplyMinimumFloor(outId, recipeStage);
					(maxDiff, hardReason) = ApplyMaterialDifficulty(outId, maxDiff, hardReason);
					if (!IsAllowedForFilter(maxDiff, filterMode))
						continue;

					SourceConfidence conf = anyTierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Crafted;
					bool reqHm = anyHardmode || recipeStage > catalog.WallOfFleshProgression;
					if (RegisterPathIntoBucket(outInfo, filterIdx, recipeStage, ItemSourceType.Recipe, conf,
						FormatRecipeSummary(r), reqHm, gate, maxDiff, hardReason, 1f))
					{
						changed = true;
					}
				}
			}

			// Propagate openable bags/crates/caches once the bag item itself is reachable
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

					var filterMode = (DifficultyFilterMode)filterIdx;
					foreach ((DropRateInfo dr, ConditionGate gate) in drops)
					{
						if (!Items.TryGetValue(dr.itemId, out ItemUnlockInfo dstInfo))
							continue;

						float stage = ApplyMinimumFloor(dr.itemId, Math.Max(bagPath.UnlockProgression + 0.001f, gate.MinProgression));
						if (gate.MinProgression <= 0.01f && !bagPath.RequiresHardmode && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
							stage = Math.Max(stage, EstimateDropRarityFloor(dropItem, catalog));

						(ObtainabilityDifficulty dropDiff, string dropReason) = EvaluateDropDifficulty(dr.dropRate, isBossDrop: false);
						ObtainabilityDifficulty combDiff = bagPath.Difficulty > dropDiff ? bagPath.Difficulty : dropDiff;
						string combReason = bagPath.Difficulty >= dropDiff && !string.IsNullOrEmpty(bagPath.DifficultyReason)
							? bagPath.DifficultyReason
							: $"{bagName}: {dropReason}";
						(combDiff, combReason) = ApplyMaterialDifficulty(dr.itemId, combDiff, combReason);

						if (!IsAllowedForFilter(combDiff, filterMode))
							continue;

						bool reqHm = bagPath.RequiresHardmode || gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;
						SourceConfidence conf = bagPath.Confidence == SourceConfidence.TierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Verified;
						if (RegisterPathIntoBucket(dstInfo, filterIdx, stage, ItemSourceType.BagOrCrate, conf,
							$"Inside {bagName}", reqHm, gate, combDiff, combReason, dr.dropRate))
						{
							changed = true;
						}
					}
				}
			}

			// Propagate Shimmer transforms (Shimmer is at least Difficulty.Hard -> only buckets IncludeHard and All)
			foreach (int itemId in _allValidItemIds)
			{
				if (itemId >= ItemID.Sets.ShimmerTransformToItem.Length)
					continue;

				int shimmerOut = ItemID.Sets.ShimmerTransformToItem[itemId];
				if (shimmerOut <= 0 || !Items.TryGetValue(itemId, out ItemUnlockInfo srcInfo) || !Items.TryGetValue(shimmerOut, out ItemUnlockInfo dstInfo))
					continue;

				for (int filterIdx = 1; filterIdx < 3; filterIdx++)
				{
					ItemUnlockPath srcPath = srcInfo.PathsByFilter[filterIdx];
					if (srcPath == null || srcPath.UnlockProgression >= float.MaxValue * 0.5f)
						continue;

					if (!srcPath.RequiresHardmode && ContentSamples.ItemsByType.TryGetValue(shimmerOut, out Item shOutItem) && shOutItem.rare >= ItemRarityID.LightRed)
						continue;

					float shStage = ApplyMinimumFloor(shimmerOut, Math.Max(srcPath.UnlockProgression, 1.05f));
					ObtainabilityDifficulty shDiff = srcPath.Difficulty > ObtainabilityDifficulty.Hard ? srcPath.Difficulty : ObtainabilityDifficulty.Hard;
					if (!IsAllowedForFilter(shDiff, (DifficultyFilterMode)filterIdx))
						continue;

					if (RegisterPathIntoBucket(dstInfo, filterIdx, shStage, ItemSourceType.Shimmer, srcPath.Confidence,
						$"Shimmer: {Lang.GetItemNameValue(itemId)}", srcPath.RequiresHardmode || shStage > _wofProg,
						default, shDiff, LootLoc("ShimmerTransform"), 1f))
					{
						changed = true;
					}
				}
			}

			if (!changed)
				break;
		}
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
				"Estimated from rarity/stats (source hidden in mod code)", reqHm, default, diff, reason, 1f);
		}
	}

	private (float Prog, bool Hardmode, bool Estimated, ObtainabilityDifficulty Diff, string Reason) ResolveIngredientProgression(
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
					ObtainabilityDifficulty bestDiff = ObtainabilityDifficulty.VeryHard;
					string bestReason = string.Empty;

					foreach (int validId in group.ValidItems)
					{
						if (!Items.TryGetValue(validId, out ItemUnlockInfo gInfo))
							continue;

						ItemUnlockPath path = gInfo.GetPathFor(filterMode);
						if (path != null && (path.UnlockProgression < minGroup || (Math.Abs(path.UnlockProgression - minGroup) < 0.001f && path.Difficulty < bestDiff)))
						{
							minGroup = path.UnlockProgression;
							hm = path.RequiresHardmode;
							est = path.Confidence == SourceConfidence.TierEstimated;
							bestDiff = path.Difficulty;
							bestReason = path.DifficultyReason;
						}
					}
					if (minGroup < float.MaxValue * 0.5f)
						return (minGroup, hm, est, bestDiff, bestReason);
				}
			}
		}

		if (Items.TryGetValue(reqType, out ItemUnlockInfo info))
		{
			ItemUnlockPath path = info.GetPathFor(filterMode);
			if (path != null)
				return (path.UnlockProgression, path.RequiresHardmode, path.Confidence == SourceConfidence.TierEstimated, path.Difficulty, path.DifficultyReason);
		}

		return (float.MaxValue, false, true, ObtainabilityDifficulty.VeryHard, string.Empty);
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
		hardmode = hardmode || gate.RequiresHardmode || stage > _wofProg;

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
		bool better = existing == null
			|| stage + 0.45f < existing.UnlockProgression
			|| (stage + 0.0005f < existing.UnlockProgression && difficulty <= existing.Difficulty)
			|| (Math.Abs(stage - existing.UnlockProgression) <= 0.45f && difficulty < existing.Difficulty)
			|| (existing.SourceType == ItemSourceType.EstimatedTier && sourceType == ItemSourceType.Recipe);

		if (!better)
			return false;

		ContentSamples.ItemsByType.TryGetValue(info.ItemType, out Item item);
		info.PathsByFilter[bucketIdx] = new ItemUnlockPath
		{
			UnlockProgression = stage,
			SourceType = sourceType,
			Confidence = confidence,
			SourceSummary = summary,
			Difficulty = difficulty,
			DifficultyReason = difficultyReason ?? string.Empty,
			BestDropChance = dropChance,
			RequiresHardmode = hardmode || gate.RequiresHardmode,
			ExpertOnly = gate.ExpertOnly || (item?.expert ?? false),
			MasterOnly = gate.MasterOnly || (item?.master ?? false),
			ClassicOnly = gate.ClassicOnly,
			CrimsonOnly = gate.CrimsonOnly,
			CorruptOnly = gate.CorruptOnly
		};
		return true;
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
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);

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
			case NPCID.GoblinSummoner:
			case NPCID.RuneWizard:
			case NPCID.Mummy:
			case NPCID.DarkMummy:
			case NPCID.LightMummy:
			case NPCID.BloodMummy:
			case NPCID.ArmoredSkeleton:
			case NPCID.MossHornet:
			case NPCID.IchorSticker:
			case NPCID.Clinger:
			case NPCID.Corruptor:
			case NPCID.IlluminantBat:
			case NPCID.ChaosElemental:
				return wof + 0.05f;

			case NPCID.Paladin:
			case NPCID.BoneLee:
			case NPCID.TacticalSkeleton:
			case NPCID.SkeletonSniper:
			case NPCID.SkeletonCommando:
			case NPCID.DungeonSpirit:
			case NPCID.Lihzahrd:
			case NPCID.LihzahrdCrawler:
			case NPCID.FlyingSnake:
				return plantera + 0.05f;
		}

		if (npc.rarity >= 4 || npc.lifeMax >= 4000)
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
		float evil = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float qb = catalog.GetProgressionForNpc(NPCID.QueenBee, 4f);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);

		return npcType switch
		{
			NPCID.ArmsDealer => Math.Max(ks + 0.5f, 1.5f),
			NPCID.TravellingMerchant => Math.Max(ks + 0.2f, 1.2f),
			NPCID.Dryad => eoc + 0.01f,
			NPCID.GoblinTinkerer or NPCID.DD2Bartender => Math.Max(eoc + 0.5f, evil - 0.3f),
			NPCID.WitchDoctor => qb + 0.01f,
			NPCID.Clothier or NPCID.Mechanic => skel + 0.01f,
			NPCID.Wizard or NPCID.TaxCollector => wof + 0.05f,
			NPCID.Truffle => wof + 0.1f,
			NPCID.Pirate => wof + 0.5f,
			NPCID.Steampunker or NPCID.SantaClaus => mech + 0.01f,
			NPCID.Cyborg => plantera + 0.01f,
			NPCID.Princess => golem + 0.5f,
			_ => 0.5f
		};
	}

	private static float EstimateDropRarityFloor(Item item, BossCatalog catalog)
	{
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float evil = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, golem + 2f);
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
		float evil = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float wof = catalog.WallOfFleshProgression;
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);
		float golem = catalog.GetProgressionForNpc(NPCID.Golem, plantera + 1f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, golem + 2f);
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
		string text = "Craft: " + string.Join(" + ", ings) + suffix;

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
