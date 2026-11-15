using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;
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
		_allValidItemIds.Clear();
		_openableBags.Clear();

		InitializeItemBaselineAndSemantics(catalog);
		SeedVanillaProgressionFloors(catalog);
		IndexRecipesAndDirectUpgrades();
		SeedBossLootAndBags(catalog);
		SeedNpcDrops(catalog);
		CollectOpenableBagsAndCrates(catalog);
		SeedNpcShops(catalog);
		RelaxCraftingAndShimmerGraph(catalog);
		ApplyFallbackTierEstimates(catalog);

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

				bool bothEquip = (ingItem.accessory && outItem.accessory) || (ingItem.damage > 0 && outItem.damage > 0);
				if (!bothEquip)
					continue;

				if (!EquipmentUpgradesTo.TryGetValue(req.type, out HashSet<int> targets))
				{
					targets = new HashSet<int>();
					EquipmentUpgradesTo[req.type] = targets;
				}
				targets.Add(resultId);
			}
		}
	}

	private void SeedBossLootAndBags(BossCatalog catalog)
	{
		foreach (BossStageInfo stage in catalog.AllStages)
		{
			float postBossProg = stage.Progression + 0.001f;
			bool postWof = stage.Progression >= catalog.WallOfFleshProgression;

			foreach (int lootId in stage.LootItemIds)
			{
				float itemStage = postBossProg;
				if (stage.IsEvent && ContentSamples.ItemsByType.TryGetValue(lootId, out Item evItem))
					itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

				TryRegisterSource(lootId, itemStage, ItemSourceType.BossDrop, SourceConfidence.Verified,
					$"Drops from {stage.DisplayName}", postWof || itemStage > catalog.WallOfFleshProgression, default);
			}

			foreach (DropRateInfo dr in stage.DropRates)
			{
				ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
				float itemStage = Math.Max(postBossProg, gate.MinProgression);
				if (stage.IsEvent && gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item evItem))
					itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

				TryRegisterSource(dr.itemId, itemStage, ItemSourceType.BossDrop,
					SourceConfidence.Verified, $"Drops from {stage.DisplayName}", postWof || gate.RequiresHardmode || itemStage > catalog.WallOfFleshProgression, gate);
			}

			foreach (int npcId in stage.NpcIds)
			{
				var rules = Main.ItemDropsDB.GetRulesForNPCID(npcId, includeGlobalDrops: false);
				foreach (DropRateInfo dr in FlattenDropRules(rules))
				{
					ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
					float itemStage = Math.Max(postBossProg, gate.MinProgression);
					if (stage.IsEvent && gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item evItem))
						itemStage = Math.Max(itemStage, EstimateDropRarityFloor(evItem, catalog));

					TryRegisterSource(dr.itemId, itemStage, ItemSourceType.BossDrop,
						SourceConfidence.Verified, $"Drops from {stage.DisplayName}", postWof || gate.RequiresHardmode || itemStage > catalog.WallOfFleshProgression, gate);
				}
			}

			if (stage.TreasureBagId > 0)
			{
				var bagRules = Main.ItemDropsDB.GetRulesForItemID(stage.TreasureBagId);
				foreach (DropRateInfo dr in FlattenDropRules(bagRules))
				{
					ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
					string bagName = Lang.GetItemNameValue(stage.TreasureBagId);
					TryRegisterSource(dr.itemId, Math.Max(postBossProg, gate.MinProgression), ItemSourceType.BossDrop,
						SourceConfidence.Verified, $"From {bagName} ({stage.DisplayName})", postWof || gate.RequiresHardmode, gate with { ExpertOnly = true });
				}
			}
		}
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
				ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
				float stage = Math.Max(npcStatFloor, gate.MinProgression);
				if (gate.MinProgression <= 0.01f && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
				{
					float rareFloor = EstimateDropRarityFloor(dropItem, catalog);
					stage = Math.Max(stage, rareFloor);
				}

				// Ignore ultra-rare (< 0.4%) farm/easter-egg drops (e.g. 0.01% Slime Staff) when assigning minimum unlock
				if (dr.dropRate > 0f && dr.dropRate < 0.004f)
					continue;

				bool reqHardmode = gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;

				TryRegisterSource(dr.itemId, stage, ItemSourceType.NpcDrop, SourceConfidence.Verified,
					$"Drop: {npcName} ({dr.dropRate * 100f:0.#}%)", reqHardmode, gate);
			}
		}
	}

	private void CollectOpenableBagsAndCrates(BossCatalog catalog)
	{
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
				if (dr.dropRate > 0f && dr.dropRate < 0.004f)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateDropConditions(dr.conditions, catalog);
				if (!gate.Unobtainable)
					list.Add((dr, gate));
			}

			if (list.Count > 0)
				_openableBags[itemId] = list;

			// Seed known vanilla fishing crates / lockboxes cleanly
			float wof = catalog.WallOfFleshProgression;
			float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
			if (itemId == ItemID.LockBox || itemId == ItemID.ObsidianLockbox)
			{
				TryRegisterSource(itemId, skel + 0.01f, ItemSourceType.WorldTier, SourceConfidence.Verified, "Dungeon / Shadow Key", false, default);
			}
			else if (itemId == ItemID.DungeonFishingCrateHard)
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified, "Hardmode Dungeon Crate", true, default);
			}
			else if (itemId == ItemID.DungeonFishingCrate)
			{
				TryRegisterSource(itemId, skel + 0.01f, ItemSourceType.BagOrCrate, SourceConfidence.Verified, "Dungeon Crate", false, default);
			}
			else if (ItemID.Sets.IsFishingCrateHardmode[itemId])
			{
				TryRegisterSource(itemId, wof + 0.05f, ItemSourceType.BagOrCrate, SourceConfidence.Verified, "Hardmode Fishing Crate", true, default);
			}
			else if (ItemID.Sets.IsFishingCrate[itemId])
			{
				TryRegisterSource(itemId, 0.25f, ItemSourceType.BagOrCrate, SourceConfidence.Verified, "Fishing Crate", false, default);
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

			foreach (AbstractNPCShop.Entry entry in shop.ActiveEntries)
			{
				if (entry?.Item == null || entry.Item.IsAir)
					continue;

				ConditionGate gate = ConditionProgressionMapper.EvaluateRecipeConditions(entry.Conditions, catalog);
				float stage = Math.Max(npcUnlock, gate.MinProgression);
				if (gate.MinProgression <= 0.01f)
				{
					stage = Math.Max(stage, EstimateDropRarityFloor(entry.Item, catalog));
				}

				bool reqHm = gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;

				TryRegisterSource(entry.Item.type, stage, ItemSourceType.NpcShop, SourceConfidence.Verified,
					$"Sold by {npcName}", reqHm, gate);
			}
		}
	}

	private void SeedVanillaProgressionFloors(BossCatalog catalog)
	{
		float evil = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float prime = catalog.GetProgressionForNpc(NPCID.SkeletronPrime, wof + 3f);
		float mechAll = Math.Max(mech, prime);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);
		float cultist = catalog.GetProgressionForNpc(NPCID.CultistBoss, plantera + 3f);
		float ml = catalog.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1f);

		_tileProgression[TileID.Hellforge] = Math.Min(wof - 0.3f, evil + 0.5f);
		_tileProgression[TileID.MythrilAnvil] = wof + 0.1f;
		_tileProgression[TileID.AdamantiteForge] = wof + 0.1f;
		_tileProgression[TileID.LihzahrdFurnace] = plantera + 0.1f;
		_tileProgression[TileID.LunarCraftingStation] = cultist + 0.1f;
		_tileProgression[TileID.Blendomatic] = wof + 0.2f;
		_tileProgression[TileID.Autohammer] = plantera + 0.05f;
		_tileProgression[TileID.CrystalBall] = wof + 0.05f;
		_tileProgression[TileID.SteampunkBoiler] = mech + 0.1f;

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

	private void RelaxCraftingAndShimmerGraph(BossCatalog catalog)
	{
		// Pre-seed raw world materials (no recipes, no NPC drops, no shops) with a conservative world-tier progression
		foreach (int itemId in _allValidItemIds)
		{
			ItemUnlockInfo info = Items[itemId];
			if (info.UnlockProgression < float.MaxValue)
				continue;

			if (!DirectRecipesForItem.ContainsKey(itemId))
			{
				Item item = ContentSamples.ItemsByType[itemId];
				float est = ApplyMinimumFloor(itemId, EstimateRawWorldItemStage(item, catalog));
				info.UnlockProgression = est;
				info.RequiresHardmode = est > _wofProg;
				info.SourceType = ItemSourceType.WorldTier;
				info.Confidence = SourceConfidence.TierEstimated;
				info.SourceSummary = "World / Chest / Mining";
			}
		}

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

		// Fixed-point relaxation across recipes, crafting tiles, and shimmer
		for (int pass = 0; pass < 14; pass++)
		{
			bool changed = false;

			// Update modded tile unlock progressions from their placer items
			foreach ((int tileId, List<int> placers) in tilePlacers)
			{
				if (_tileProgression.ContainsKey(tileId))
					continue;

				float bestPlacer = float.MaxValue;
				foreach (int placerId in placers)
				{
					if (Items.TryGetValue(placerId, out ItemUnlockInfo pInfo))
						bestPlacer = Math.Min(bestPlacer, pInfo.UnlockProgression);
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

				float recipeStage = gate.MinProgression;
				bool allKnown = true;
				bool anyHardmode = gate.RequiresHardmode;
				bool anyTierEstimated = false;

				foreach (int tileId in r.requiredTile)
				{
					if (_tileProgression.TryGetValue(tileId, out float tileProg))
						recipeStage = Math.Max(recipeStage, tileProg);
				}

				foreach (Item req in r.requiredItem)
				{
					if (req == null || req.IsAir)
						continue;

					(float ingProg, bool ingHm, bool ingEst) = ResolveIngredientProgression(r, req.type);
					if (ingProg >= float.MaxValue * 0.5f)
					{
						allKnown = false;
						break;
					}
					recipeStage = Math.Max(recipeStage, ingProg);
					anyHardmode |= ingHm;
					anyTierEstimated |= ingEst;
				}

				if (!allKnown)
					continue;

				recipeStage = ApplyMinimumFloor(outId, recipeStage);
				if (recipeStage + 0.0005f < outInfo.UnlockProgression)
				{
					outInfo.UnlockProgression = recipeStage;
					outInfo.RequiresHardmode = anyHardmode || recipeStage > catalog.WallOfFleshProgression;
					outInfo.SourceType = ItemSourceType.Recipe;
					outInfo.Confidence = anyTierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Crafted;
					outInfo.SourceSummary = FormatRecipeSummary(r);
					outInfo.ExpertOnly = gate.ExpertOnly;
					outInfo.MasterOnly = gate.MasterOnly;
					outInfo.ClassicOnly = gate.ClassicOnly;
					outInfo.CrimsonOnly = gate.CrimsonOnly;
					outInfo.CorruptOnly = gate.CorruptOnly;
					changed = true;
				}
			}

			// Propagate openable bags/crates/caches once the bag item itself is reachable
			foreach ((int bagId, var drops) in _openableBags)
			{
				if (!Items.TryGetValue(bagId, out ItemUnlockInfo bagInfo) || bagInfo.UnlockProgression >= float.MaxValue * 0.5f)
					continue;

				string bagName = Lang.GetItemNameValue(bagId);
				foreach ((DropRateInfo dr, ConditionGate gate) in drops)
				{
					if (!Items.TryGetValue(dr.itemId, out ItemUnlockInfo dstInfo))
						continue;

					float stage = ApplyMinimumFloor(dr.itemId, Math.Max(bagInfo.UnlockProgression + 0.001f, gate.MinProgression));
					if (gate.MinProgression <= 0.01f && !bagInfo.RequiresHardmode && ContentSamples.ItemsByType.TryGetValue(dr.itemId, out Item dropItem))
						stage = Math.Max(stage, EstimateDropRarityFloor(dropItem, catalog));

					bool reqHm = bagInfo.RequiresHardmode || gate.RequiresHardmode || stage > catalog.WallOfFleshProgression;
					if (stage + 0.0005f < dstInfo.UnlockProgression)
					{
						dstInfo.UnlockProgression = stage;
						dstInfo.RequiresHardmode = reqHm;
						dstInfo.SourceType = ItemSourceType.BagOrCrate;
						dstInfo.Confidence = bagInfo.Confidence == SourceConfidence.TierEstimated ? SourceConfidence.TierEstimated : SourceConfidence.Verified;
						dstInfo.SourceSummary = $"Inside {bagName}";
						dstInfo.ExpertOnly = gate.ExpertOnly;
						dstInfo.MasterOnly = gate.MasterOnly;
						dstInfo.ClassicOnly = gate.ClassicOnly;
						dstInfo.CrimsonOnly = gate.CrimsonOnly;
						dstInfo.CorruptOnly = gate.CorruptOnly;
						changed = true;
					}
				}
			}

			// Propagate Shimmer transforms (only if vanilla item array covers this itemId)
			foreach (int itemId in _allValidItemIds)
			{
				if (itemId >= ItemID.Sets.ShimmerTransformToItem.Length)
					continue;

				int shimmerOut = ItemID.Sets.ShimmerTransformToItem[itemId];
				if (shimmerOut <= 0 || !Items.TryGetValue(itemId, out ItemUnlockInfo srcInfo) || !Items.TryGetValue(shimmerOut, out ItemUnlockInfo dstInfo))
					continue;

				if (!srcInfo.RequiresHardmode && ContentSamples.ItemsByType.TryGetValue(shimmerOut, out Item shOutItem) && shOutItem.rare >= ItemRarityID.LightRed)
					continue;

				float shStage = ApplyMinimumFloor(shimmerOut, srcInfo.UnlockProgression);
				if (shStage + 0.0005f < dstInfo.UnlockProgression)
				{
					dstInfo.UnlockProgression = shStage;
					dstInfo.RequiresHardmode = srcInfo.RequiresHardmode || shStage > _wofProg;
					dstInfo.SourceType = ItemSourceType.Shimmer;
					dstInfo.Confidence = srcInfo.Confidence;
					dstInfo.SourceSummary = $"Shimmer: {Lang.GetItemNameValue(itemId)}";
					changed = true;
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
			if (info.UnlockProgression < float.MaxValue * 0.5f)
				continue;

			Item item = ContentSamples.ItemsByType[itemId];
			float est = ApplyMinimumFloor(itemId, EstimateRawWorldItemStage(item, catalog));
			info.UnlockProgression = est;
			info.RequiresHardmode = est > catalog.WallOfFleshProgression;
			info.SourceType = ItemSourceType.EstimatedTier;
			info.Confidence = SourceConfidence.TierEstimated;
			info.SourceSummary = "Estimated from rarity/stats (source hidden in mod code)";
		}
	}

	private (float Prog, bool Hardmode, bool Estimated) ResolveIngredientProgression(Recipe recipe, int reqType)
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
					foreach (int validId in group.ValidItems)
					{
						if (Items.TryGetValue(validId, out ItemUnlockInfo gInfo) && gInfo.UnlockProgression < minGroup)
						{
							minGroup = gInfo.UnlockProgression;
							hm = gInfo.RequiresHardmode;
							est = gInfo.Confidence == SourceConfidence.TierEstimated;
						}
					}
					if (minGroup < float.MaxValue * 0.5f)
						return (minGroup, hm, est);
				}
			}
		}

		if (Items.TryGetValue(reqType, out ItemUnlockInfo info))
			return (info.UnlockProgression, info.RequiresHardmode, info.Confidence == SourceConfidence.TierEstimated);

		return (float.MaxValue, false, true);
	}

	private void TryRegisterSource(
		int itemId,
		float stage,
		ItemSourceType sourceType,
		SourceConfidence confidence,
		string summary,
		bool hardmode,
		ConditionGate gate)
	{
		if (gate.Unobtainable || !Items.TryGetValue(itemId, out ItemUnlockInfo info))
			return;

		stage = ApplyMinimumFloor(itemId, stage);
		hardmode = hardmode || gate.RequiresHardmode || stage > _wofProg;
		if (stage < info.UnlockProgression)
		{
			info.UnlockProgression = stage;
			info.SourceType = sourceType;
			info.Confidence = confidence;
			info.SourceSummary = summary;
			info.RequiresHardmode = hardmode || gate.RequiresHardmode;
			info.ExpertOnly = gate.ExpertOnly || ContentSamples.ItemsByType[itemId].expert;
			info.MasterOnly = gate.MasterOnly || ContentSamples.ItemsByType[itemId].master;
			info.ClassicOnly = gate.ClassicOnly;
			info.CrimsonOnly = gate.CrimsonOnly;
			info.CorruptOnly = gate.CorruptOnly;
		}
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
		float eoc = catalog.GetProgressionForNpc(NPCID.EyeofCthulhu, 2f);
		float evil = catalog.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3f);
		float skel = catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f);
		float wof = catalog.WallOfFleshProgression;
		float mech = catalog.GetProgressionForNpc(NPCID.TheDestroyer, wof + 2f);
		float plantera = catalog.GetProgressionForNpc(NPCID.Plantera, wof + 4f);

		// Vanilla Dungeon / Underworld / Hardmode / Post-Plantera / Temple NPC biome gates
		switch (npc.type)
		{
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

		return 0f;
	}

	private static float GetTownNpcMinimumStage(int npcType, BossCatalog catalog)
	{
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
			NPCID.ArmsDealer or NPCID.TravellingMerchant => 1.5f,
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
		if (item.rare < ItemRarityID.White)
		{
			if (item.value >= 500000) floor = Math.Max(floor, plantera);
			else if (item.value >= 200000) floor = Math.Max(floor, mech);
			else if (item.value >= 75000) floor = Math.Max(floor, wof + 0.05f);
		}
		if (item.damage >= 180 || item.defense >= 28)
			floor = Math.Max(floor, cultist);
		else if (item.damage >= 90 || item.defense >= 18)
			floor = Math.Max(floor, mech);
		else if (item.damage >= 48 || item.defense >= 11)
			floor = Math.Max(floor, wof + 0.05f);

		return floor;
	}

	private static float EstimateRawWorldItemStage(Item item, BossCatalog catalog)
	{
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
		if (item.damage >= 180 || item.defense >= 28)
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
		return "Craft: " + string.Join(" + ", ings) + suffix;
	}
}
