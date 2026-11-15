using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

/// <summary>
/// Scores weapons, armor sets, accessories, ammo, and buff potions for any boss stage and damage class.
/// Automatically discovers active DamageClasses across all loaded mods and suppresses superseded crafting precursors.
/// </summary>
public sealed class BuildRecommender
{
	private readonly BossCatalog _catalog;
	private readonly ProgressionGraph _graph;
	private readonly ArmorSetAnalyzer _armorSets;

	public BuildRecommender(BossCatalog catalog, ProgressionGraph graph, ArmorSetAnalyzer armorSets)
	{
		_catalog = catalog;
		_graph = graph;
		_armorSets = armorSets;
	}

	public List<DamageClass> GetAvailablePlayableClasses()
	{
		var usedClassCounts = new Dictionary<int, int>();

		foreach ((int itemId, ItemUnlockInfo info) in _graph.Items)
		{
			if (!ContentSamples.ItemsByType.TryGetValue(itemId, out Item item) || !IsCombatWeapon(item))
				continue;

			int dcType = item.DamageType.Type;
			usedClassCounts[dcType] = usedClassCounts.GetValueOrDefault(dcType) + 1;
		}

		var result = new List<DamageClass>();
		var baseClasses = new[]
		{
			DamageClass.Melee,
			DamageClass.Ranged,
			DamageClass.Magic,
			DamageClass.Summon
		};

		foreach (DamageClass dc in baseClasses)
			result.Add(dc);

		// Add custom mod DamageClasses (and Throwing if a mod adds >= 3 throwing weapons)
		for (int i = 0; i < DamageClassLoader.DamageClassCount; i++)
		{
			DamageClass dc = DamageClassLoader.GetDamageClass(i);
			if (dc == null || dc == DamageClass.Default || dc == DamageClass.Generic
				|| dc == DamageClass.Melee || dc == DamageClass.MeleeNoSpeed
				|| dc == DamageClass.Ranged || dc == DamageClass.Magic
				|| dc == DamageClass.Summon || dc == DamageClass.SummonMeleeSpeed
				|| dc == DamageClass.MagicSummonHybrid)
				continue;

			if (usedClassCounts.GetValueOrDefault(dc.Type) >= 2)
				result.Add(dc);
		}

		return result;
	}

	public BuildRecommendation Recommend(BossStageInfo targetStage, DamageClass selectedClass, BuildQueryOptions options)
	{
		options ??= new BuildQueryOptions();
		float cutoff = options.IncludeTargetBossDrops
			? targetStage.Progression + 0.01f
			: targetStage.Progression;

		bool allowHardmode = options.IncludeTargetBossDrops
			? targetStage.Progression >= _catalog.WallOfFleshProgression
			: targetStage.Progression > _catalog.WallOfFleshProgression;

		// If Auto / All classes selected, evaluate all playable classes and pick the strongest overall loadout
		if (selectedClass == null)
		{
			BuildRecommendation bestAuto = null;
			float bestAutoScore = float.NegativeInfinity;

			foreach (DamageClass dc in GetAvailablePlayableClasses())
			{
				BuildRecommendation candidate = BuildForSpecificClass(targetStage, dc, true, cutoff, allowHardmode, options);
				float total = candidate.PrimaryWeapons.Sum(w => w.Score) + (candidate.ArmorSet?.Score ?? 0f);
				if (total > bestAutoScore)
				{
					bestAutoScore = total;
					bestAuto = candidate;
				}
			}

			return bestAuto ?? BuildForSpecificClass(targetStage, DamageClass.Melee, true, cutoff, allowHardmode, options);
		}

		return BuildForSpecificClass(targetStage, selectedClass, false, cutoff, allowHardmode, options);
	}

	private BuildRecommendation BuildForSpecificClass(
		BossStageInfo targetStage,
		DamageClass targetClass,
		bool isAuto,
		float cutoff,
		bool allowHardmode,
		BuildQueryOptions options)
	{
		var rec = new BuildRecommendation
		{
			TargetStage = targetStage,
			TargetClass = targetClass,
			IsAutoClass = isAuto,
			AccessorySlotBudget = ComputeAccessorySlotBudget(targetStage)
		};

		var availableItems = CollectAvailableItems(cutoff, allowHardmode, options);

		SelectWeaponsAndAmmo(rec, availableItems, targetStage, targetClass, cutoff);
		SelectArmor(rec, availableItems, targetStage, targetClass, cutoff, allowHardmode, options);
		SelectAccessories(rec, availableItems, targetStage, targetClass, cutoff);
		SelectPotions(rec, availableItems, targetClass);

		return rec;
	}

	private List<(Item Item, ItemUnlockInfo Info)> CollectAvailableItems(float cutoff, bool allowHardmode, BuildQueryOptions options)
	{
		var list = new List<(Item, ItemUnlockInfo)>();

		foreach ((int itemId, ItemUnlockInfo info) in _graph.Items)
		{
			if (info.Unobtainable || info.UnlockProgression > cutoff)
				continue;

			if (!allowHardmode && info.RequiresHardmode)
				continue;

			if (!options.IncludeTierEstimatedItems && info.Confidence == SourceConfidence.TierEstimated)
				continue;

			if (options.RespectWorldDifficulty)
			{
				if (info.ExpertOnly && !Main.expertMode)
					continue;
				if (info.MasterOnly && !Main.masterMode)
					continue;
				if (info.ClassicOnly && Main.expertMode)
					continue;
			}

			if (options.RespectWorldEvil && !Main.drunkWorld)
			{
				if (info.CrimsonOnly && !WorldGen.crimson)
					continue;
				if (info.CorruptOnly && WorldGen.crimson)
					continue;
			}

			if (ContentSamples.ItemsByType.TryGetValue(itemId, out Item item) && !item.IsAir && !item.vanity)
				list.Add((item, info));
		}

		return list;
	}

	private void SelectWeaponsAndAmmo(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockInfo Info)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff)
	{
		var scoredPrimary = new List<(RecommendedSlotItem Slot, Item Item)>();
		var scoredSupport = new List<(RecommendedSlotItem Slot, Item Item)>();

		foreach ((Item item, ItemUnlockInfo info) in available)
		{
			if (!IsCombatWeapon(item))
				continue;

			if (!MatchesWeaponClass(item, targetClass))
				continue;

			bool isWhip = item.shoot > ProjectileID.None && item.shoot < ProjectileID.Sets.IsAWhip.Length && ProjectileID.Sets.IsAWhip[item.shoot];
			bool isSentry = item.sentry || item.DD2Summon;

			float score = ScoreWeapon(item, info, cutoff, targetClass, isWhip, isSentry);
			string role = isWhip ? "Whip" : isSentry ? "Sentry" : item.DamageType == DamageClass.Summon ? "Minion" : "Weapon";
			string reason = BuildWeaponReason(item, info);

			var slot = new RecommendedSlotItem
			{
				ItemType = item.type,
				RoleTag = role,
				Score = score,
				UnlockProgression = info.UnlockProgression,
				SourceSummary = info.SourceSummary,
				ReasonSummary = reason,
				Confidence = info.Confidence
			};

			if (targetClass == DamageClass.Summon && (isWhip || isSentry))
				scoredSupport.Add((slot, item));
			else
				scoredPrimary.Add((slot, item));
		}

		// Suppress weapons that are direct crafting ingredients of a higher-scoring available weapon
		var topPrimary = PickTopDistinct(scoredPrimary, 3);
		foreach ((RecommendedSlotItem slot, _) in topPrimary)
			rec.PrimaryWeapons.Add(slot);

		if (targetClass == DamageClass.Summon)
		{
			var bestWhip = scoredSupport.Where(x => x.Slot.RoleTag == "Whip").OrderByDescending(x => x.Slot.Score).FirstOrDefault();
			var bestSentry = scoredSupport.Where(x => x.Slot.RoleTag == "Sentry").OrderByDescending(x => x.Slot.Score).FirstOrDefault();
			if (bestWhip.Slot != null) rec.SupportWeapons.Add(bestWhip.Slot);
			if (bestSentry.Slot != null) rec.SupportWeapons.Add(bestSentry.Slot);
		}
		else if (topPrimary.Count > 0)
		{
			// Offer 1 best summoner minion as free extra DPS support for non-summoner classes
			var bestFreeMinion = available
				.Where(x => IsCombatWeapon(x.Item) && x.Item.DamageType == DamageClass.Summon && !x.Item.sentry && x.Item.shoot > ProjectileID.None)
				.Select(x => (
					Slot: new RecommendedSlotItem
					{
						ItemType = x.Item.type,
						RoleTag = "Free Minion",
						Score = ScoreWeapon(x.Item, x.Info, cutoff, DamageClass.Summon, false, false),
						UnlockProgression = x.Info.UnlockProgression,
						SourceSummary = x.Info.SourceSummary,
						ReasonSummary = $"Free extra minion slot ({x.Item.damage} dmg)",
						Confidence = x.Info.Confidence
					},
					Item: x.Item))
				.OrderByDescending(x => x.Slot.Score)
				.FirstOrDefault();

			if (bestFreeMinion.Slot != null)
				rec.SupportWeapons.Add(bestFreeMinion.Slot);
		}

		// Recommend matching ammo if top weapon uses ammo
		Item primaryAmmoUser = topPrimary.Select(x => x.Item).FirstOrDefault(x => x.useAmmo > AmmoID.None);
		if (primaryAmmoUser != null)
		{
			var bestAmmo = available
				.Where(x => x.Item.ammo == primaryAmmoUser.useAmmo)
				.Select(x => new RecommendedSlotItem
				{
					ItemType = x.Item.type,
					RoleTag = "Ammo",
					Score = x.Item.damage * 4f + NormalizeRarity(x.Item.rare) * 6f + StageRecencyBonus(x.Info.UnlockProgression, cutoff),
					UnlockProgression = x.Info.UnlockProgression,
					SourceSummary = x.Info.SourceSummary,
					ReasonSummary = $"{x.Item.damage} dmg ammo",
					Confidence = x.Info.Confidence
				})
				.OrderByDescending(x => x.Score)
				.FirstOrDefault();

			rec.RecommendedAmmo = bestAmmo;
		}
	}

	private void SelectArmor(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockInfo Info)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff,
		bool allowHardmode,
		BuildQueryOptions options)
	{
		var availLookup = new Dictionary<int, ItemUnlockInfo>(available.Count);
		foreach ((Item availItem, ItemUnlockInfo availInfo) in available)
			availLookup.TryAdd(availItem.type, availInfo);

		ArmorSetCandidate bestSet = null;
		foreach (ArmorSetDefinition set in _armorSets.Sets)
		{
			if (!availLookup.TryGetValue(set.HeadType, out ItemUnlockInfo hInfo)
				|| !availLookup.TryGetValue(set.BodyType, out ItemUnlockInfo bInfo)
				|| !availLookup.TryGetValue(set.LegsType, out ItemUnlockInfo lInfo))
				continue;

			float maxUnlock = Math.Max(hInfo.UnlockProgression, Math.Max(bInfo.UnlockProgression, lInfo.UnlockProgression));
			float setScore = ScoreArmorSet(set, maxUnlock, cutoff, targetClass);

			if (bestSet == null || setScore > bestSet.Score)
			{
				bestSet = new ArmorSetCandidate
				{
					HeadType = set.HeadType,
					BodyType = set.BodyType,
					LegsType = set.LegsType,
					TotalDefense = set.TotalDefense,
					Score = setScore,
					UnlockProgression = maxUnlock,
					IsTrueSet = true,
					SetBonusSummary = set.SetBonusText,
					ModName = set.ModName
				};
			}
		}

		// Also compute mixed best-in-slot armor as fallback or comparison (using fair 1x rarity + 1x recency parity)
		RecommendedSlotItem bestHead = PickBestArmorPiece(available, x => x.headSlot >= 0, "Head", cutoff, targetClass);
		RecommendedSlotItem bestBody = PickBestArmorPiece(available, x => x.bodySlot >= 0, "Body", cutoff, targetClass);
		RecommendedSlotItem bestLegs = PickBestArmorPiece(available, x => x.legSlot >= 0, "Legs", cutoff, targetClass);

		float mixedFairScore = 0f;
		if (bestHead != null && bestBody != null && bestLegs != null)
		{
			Item ih = ContentSamples.ItemsByType[bestHead.ItemType];
			Item ib = ContentSamples.ItemsByType[bestBody.ItemType];
			Item il = ContentSamples.ItemsByType[bestLegs.ItemType];
			int totalDef = ih.defense + ib.defense + il.defense;
			float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
			float avgRare = (ih.rare + ib.rare + il.rare) / 3f;
			float maxUnlock = Math.Max(bestHead.UnlockProgression, Math.Max(bestBody.UnlockProgression, bestLegs.UnlockProgression));

			float sumAff = 0f;
			float sumGen = 0f;
			foreach (int id in new[] { ih.type, ib.type, il.type })
			{
				if (_graph.Semantics.TryGetValue(id, out var sp))
				{
					sumAff += sp.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
					sumGen += sp.GenericBonusScore;
				}
			}

			mixedFairScore = totalDef * defWeight + sumAff * 1.4f + sumGen * 0.8f
				+ NormalizeRarity((int)Math.Round(avgRare)) * 10f
				+ StageRecencyBonus(maxUnlock, cutoff);
		}

		if (bestSet != null && (bestHead == null || bestBody == null || bestLegs == null || bestSet.Score >= mixedFairScore * 0.90f))
		{
			rec.ArmorSet = bestSet;
			rec.HeadSlot = MakeArmorPieceSlot(bestSet.HeadType, "Head", availLookup[bestSet.HeadType], bestSet.Score / 3f);
			rec.BodySlot = MakeArmorPieceSlot(bestSet.BodyType, "Body", availLookup[bestSet.BodyType], bestSet.Score / 3f);
			rec.LegsSlot = MakeArmorPieceSlot(bestSet.LegsType, "Legs", availLookup[bestSet.LegsType], bestSet.Score / 3f);
		}
		else if (bestHead != null && bestBody != null && bestLegs != null)
		{
			int def = ContentSamples.ItemsByType[bestHead.ItemType].defense
				+ ContentSamples.ItemsByType[bestBody.ItemType].defense
				+ ContentSamples.ItemsByType[bestLegs.ItemType].defense;

			rec.ArmorSet = new ArmorSetCandidate
			{
				HeadType = bestHead.ItemType,
				BodyType = bestBody.ItemType,
				LegsType = bestLegs.ItemType,
				TotalDefense = def,
				Score = mixedFairScore,
				UnlockProgression = Math.Max(bestHead.UnlockProgression, Math.Max(bestBody.UnlockProgression, bestLegs.UnlockProgression)),
				IsTrueSet = false,
				ModName = "Mixed"
			};
			rec.HeadSlot = bestHead;
			rec.BodySlot = bestBody;
			rec.LegsSlot = bestLegs;
		}
	}

	private void SelectAccessories(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockInfo Info)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff)
	{
		var candidates = new List<(RecommendedSlotItem Slot, ItemSemanticProfile Profile)>();

		foreach ((Item item, ItemUnlockInfo info) in available)
		{
			if (!item.accessory)
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			float score = ScoreAccessory(item, info, prof, cutoff, targetClass, out string role, out string reason);
			if (score <= 5f)
				continue;

			candidates.Add((
				new RecommendedSlotItem
				{
					ItemType = item.type,
					RoleTag = role,
					Score = score,
					UnlockProgression = info.UnlockProgression,
					SourceSummary = info.SourceSummary,
					ReasonSummary = reason,
					Confidence = info.Confidence
				},
				prof));
		}

		// Ensure crafted upgrades always outrank their available precursors so precursor suppression triggers cleanly
		var byId = new Dictionary<int, int>();
		for (int i = 0; i < candidates.Count; i++)
			byId.TryAdd(candidates[i].Slot.ItemType, i);

		for (int pass = 0; pass < 3; pass++)
		{
			for (int i = 0; i < candidates.Count; i++)
			{
				int id = candidates[i].Slot.ItemType;
				if (!_graph.EquipmentUpgradesTo.TryGetValue(id, out HashSet<int> nexts))
					continue;

				float srcScore = candidates[i].Slot.Score;
				foreach (int nextId in nexts)
				{
					if (!byId.TryGetValue(nextId, out int j))
						continue;

					// Only boost upgrade if it isn't penalized off-class relative to precursor
					float srcAff = candidates[i].Profile.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
					float dstAff = candidates[j].Profile.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
					if (dstAff + 4f < srcAff && dstAff <= 0f && candidates[j].Profile.ClassAffinityScores.Count > 0)
						continue;

					if (candidates[j].Slot.Score <= srcScore + 4f)
					{
						var cur = candidates[j].Slot;
						candidates[j] = (new RecommendedSlotItem
						{
							ItemType = cur.ItemType,
							RoleTag = cur.RoleTag,
							Score = srcScore + 6f,
							UnlockProgression = cur.UnlockProgression,
							SourceSummary = cur.SourceSummary,
							ReasonSummary = cur.ReasonSummary,
							Confidence = cur.Confidence
						}, candidates[j].Profile);
					}
				}
			}
		}

		// Sort descending and prune direct recipe precursors when an upgrade is also high-scoring
		var sorted = candidates.OrderByDescending(c => c.Slot.Score).ToList();
		var pickedIds = new HashSet<int>();

		int wingsCount = 0;
		int bootsCount = 0;
		int dashCount = 0;

		foreach ((RecommendedSlotItem slot, ItemSemanticProfile prof) in sorted)
		{
			if (rec.Accessories.Count >= rec.AccessorySlotBudget)
				break;

			if (IsSupersededByAlreadyPicked(slot.ItemType, pickedIds))
				continue;

			if (prof.IsWings && wingsCount >= 1)
				continue;
			if (prof.IsMobilityBoots && !prof.IsWings && bootsCount >= 1)
				continue;
			if (prof.IsDashOrDodge && !prof.IsWings && !prof.IsMobilityBoots && dashCount >= 1)
				continue;

			rec.Accessories.Add(slot);
			pickedIds.Add(slot.ItemType);

			if (prof.IsWings) wingsCount++;
			if (prof.IsMobilityBoots) bootsCount++;
			if (prof.IsDashOrDodge) dashCount++;
		}
	}

	private void SelectPotions(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockInfo Info)> available,
		DamageClass targetClass)
	{
		rec.HealingPotion = available
			.Where(x => x.Item.potion && x.Item.healLife > 0 && x.Item.consumable)
			.OrderByDescending(x => x.Item.healLife)
			.ThenByDescending(x => x.Info.UnlockProgression)
			.Select(x => new RecommendedSlotItem
			{
				ItemType = x.Item.type,
				RoleTag = "Healing",
				Score = x.Item.healLife,
				UnlockProgression = x.Info.UnlockProgression,
				SourceSummary = x.Info.SourceSummary,
				ReasonSummary = $"+{x.Item.healLife} HP",
				Confidence = x.Info.Confidence
			})
			.FirstOrDefault();

		var buffCandidates = new List<RecommendedSlotItem>();
		var seenBuffTypes = new HashSet<int>();

		foreach ((Item item, ItemUnlockInfo info) in available)
		{
			if (item.buffType <= 0 || item.buffTime <= 0 || !item.consumable)
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			if (!prof.IsCombatBuffPotion)
				continue;

			float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			float score = 20f + prof.GenericBonusScore + classAff * 1.4f + (item.buffTime / 3600f);

			if (item.buffType is BuffID.Ironskin or BuffID.Regeneration or BuffID.Endurance or BuffID.Swiftness or BuffID.Lifeforce or BuffID.Rage or BuffID.Wrath)
				score += 22f;

			if (item.buffType is BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3)
				score += (item.buffType == BuffID.WellFed3 ? 28f : item.buffType == BuffID.WellFed2 ? 20f : 12f);

			buffCandidates.Add(new RecommendedSlotItem
			{
				ItemType = item.type,
				RoleTag = "Buff",
				Score = score,
				UnlockProgression = info.UnlockProgression,
				SourceSummary = info.SourceSummary,
				ReasonSummary = prof.CombinedTooltipText,
				Confidence = info.Confidence
			});
		}

		foreach (RecommendedSlotItem pot in buffCandidates.OrderByDescending(p => p.Score))
		{
			if (rec.BuffPotions.Count >= 6)
				break;

			int buffType = ContentSamples.ItemsByType[pot.ItemType].buffType;
			if (!seenBuffTypes.Add(buffType))
				continue;

			rec.BuffPotions.Add(pot);
		}
	}

	private static bool IsCombatWeapon(Item item)
	{
		if (item.damage <= 0 || item.ammo > AmmoID.None || item.accessory)
			return false;

		if (item.DamageType == DamageClass.Default)
			return false;

		// Exclude mining/woodcutting/hammer tools (including chainsaws & drills which fire held projectiles)
		if (item.pick > 0 || item.axe > 0 || item.hammer > 0)
			return false;

		// Exclude single-use bombs/grenades/blocks unless dedicated throwing weapon
		if (item.createTile >= TileID.Dirt || item.createWall >= 0)
			return false;

		if (item.consumable && !item.CountsAsClass(DamageClass.Throwing) && item.damage < 35)
			return false;

		return true;
	}

	private static bool MatchesWeaponClass(Item item, DamageClass targetClass)
	{
		if (item.DamageType == targetClass)
			return true;

		if (item.CountsAsClass(targetClass))
			return true;

		// Treat Throwing weapons under Ranged when user picks Ranged if no custom Throwing tab is used, or keep exact
		return false;
	}

	private static float ScoreWeapon(
		Item item,
		ItemUnlockInfo info,
		float cutoff,
		DamageClass targetClass,
		bool isWhip,
		bool isSentry)
	{
		float effectiveUse = Math.Max(4f, item.useTime + item.reuseDelay * 0.5f);
		float shotsPerSec = 60f / effectiveUse;
		if (item.useAnimation > item.useTime && item.useTime > 0)
		{
			// Burst fire adjustment (e.g. Clockwork Assault Rifle)
			float burstsPerSec = 60f / Math.Max(6f, item.useAnimation + item.reuseDelay);
			int shotsPerBurst = Math.Max(1, item.useAnimation / item.useTime);
			shotsPerSec = burstsPerSec * shotsPerBurst;
		}

		float critMult = 1f + Math.Clamp(item.crit, 0, 70) * 0.01f;
		float rawDps = item.damage * Math.Min(shotsPerSec, 15f) * critMult;

		// Log-compressed DPS + rarity + stage recency keeps modded hyper-fast low-dmg vs slow heavy-hit balanced
		float dpsScore = (float)(Math.Log10(Math.Max(10.0, rawDps)) * 42.0);
		float rarityScore = NormalizeRarity(item.rare) * 11f;
		float valueScore = (float)(Math.Log10(Math.Max(100, item.value)) * 4.5);
		float recency = StageRecencyBonus(info.UnlockProgression, cutoff);

		float bonus = 0f;
		if (item.shoot > ProjectileID.None) bonus += 16f;
		if (item.autoReuse || item.channel) bonus += 6f;
		if (item.noMelee && item.shoot > ProjectileID.None) bonus += 5f;
		if (info.SourceType == ItemSourceType.BossDrop) bonus += 8f;
		if (info.Confidence == SourceConfidence.TierEstimated) bonus -= 18f;
		if (item.damage >= 500 && info.Confidence == SourceConfidence.TierEstimated) bonus -= 80f;
		if (item.DamageType == targetClass) bonus += 5f;
		if (isWhip || isSentry) bonus += 6f;

		return dpsScore + rarityScore + valueScore + recency + bonus;
	}

	private float ScoreArmorSet(ArmorSetDefinition set, float maxUnlock, float cutoff, DamageClass targetClass)
	{
		float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
		float score = set.TotalDefense * defWeight;

		float classAff = set.CombinedProfile.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
		score += classAff * 1.8f;
		score += set.CombinedProfile.GenericBonusScore * 0.9f;
		score += StageRecencyBonus(maxUnlock, cutoff);

		// Average rarity of pieces
		int rHead = ContentSamples.ItemsByType[set.HeadType].rare;
		int rBody = ContentSamples.ItemsByType[set.BodyType].rare;
		int rLegs = ContentSamples.ItemsByType[set.LegsType].rare;
		score += NormalizeRarity((rHead + rBody + rLegs) / 3) * 10f;

		if (!string.IsNullOrEmpty(set.SetBonusText))
			score += 42f;
		else
			score += 24f;

		return score;
	}

	private RecommendedSlotItem PickBestArmorPiece(
		List<(Item Item, ItemUnlockInfo Info)> available,
		Func<Item, bool> slotPredicate,
		string role,
		float cutoff,
		DamageClass targetClass)
	{
		RecommendedSlotItem best = null;
		foreach ((Item item, ItemUnlockInfo info) in available)
		{
			if (!slotPredicate(item))
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.2f : 1.4f;
			float score = item.defense * defWeight + classAff * 1.4f + prof.GenericBonusScore * 0.8f
				+ NormalizeRarity(item.rare) * 8f + StageRecencyBonus(info.UnlockProgression, cutoff);

			if (best == null || score > best.Score)
			{
				best = MakeArmorPieceSlot(item.type, role, info, score);
			}
		}
		return best;
	}

	private static RecommendedSlotItem MakeArmorPieceSlot(int itemType, string role, ItemUnlockInfo info, float score)
	{
		Item item = ContentSamples.ItemsByType[itemType];
		return new RecommendedSlotItem
		{
			ItemType = itemType,
			RoleTag = role,
			Score = score,
			UnlockProgression = info.UnlockProgression,
			SourceSummary = info.SourceSummary,
			ReasonSummary = $"{item.defense} def | {ItemTooltipAnalyzer.GetTooltipText(item)}",
			Confidence = info.Confidence
		};
	}

	private static float ScoreAccessory(
		Item item,
		ItemUnlockInfo info,
		ItemSemanticProfile prof,
		float cutoff,
		DamageClass targetClass,
		out string role,
		out string reason)
	{
		float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
		if (prof.IsPureNonCombatUtility && classAff <= 0f)
		{
			role = "Utility";
			reason = prof.CombinedTooltipText;
			return 0f;
		}

		bool hasCombatValue = classAff > 0f || prof.IsWings || prof.IsMobilityBoots || prof.IsDashOrDodge
			|| prof.IsSurvivability || prof.HasGenericCombat || item.defense >= 2;
		if (!hasCombatValue)
		{
			role = "Utility";
			reason = prof.CombinedTooltipText;
			return 0f;
		}

		float score = NormalizeRarity(item.rare) * 7.5f
			+ (float)Math.Log10(Math.Max(100, item.value)) * 3f
			+ StageRecencyBonus(info.UnlockProgression, cutoff) * 0.7f
			+ item.defense * 2.5f;

		score += classAff * 1.7f;
		score += prof.GenericBonusScore * 1.05f;

		// Check if the item belongs to a different class entirely (penalize off-class emblems/quivers)
		float maxOtherClass = 0f;
		foreach ((int dcType, float val) in prof.ClassAffinityScores)
		{
			if (dcType != targetClass.Type)
				maxOtherClass = Math.Max(maxOtherClass, val);
		}
		if (maxOtherClass > classAff + 5f)
			score -= (maxOtherClass - classAff) * 1.3f;

		if (prof.IsWings)
		{
			role = "Wings";
			score += 55f;
		}
		else if (prof.IsDashOrDodge)
		{
			role = "Dash / Dodge";
			score += 46f;
		}
		else if (prof.IsMobilityBoots)
		{
			role = "Mobility";
			score += 38f;
		}
		else if (classAff > 0f)
		{
			role = $"{ItemTooltipAnalyzer.FormatClassDisplayName(targetClass)} Boost";
		}
		else if (prof.IsSurvivability)
		{
			role = "Defense / Regen";
		}
		else
		{
			role = "Combat Utility";
		}

		if (info.Confidence == SourceConfidence.TierEstimated)
			score -= 8f;

		reason = string.IsNullOrWhiteSpace(prof.CombinedTooltipText)
			? (item.defense > 0 ? $"+{item.defense} defense" : role)
			: prof.CombinedTooltipText;

		return score;
	}

	private List<(RecommendedSlotItem Slot, Item Item)> PickTopDistinct(
		List<(RecommendedSlotItem Slot, Item Item)> candidates,
		int maxCount)
	{
		var picked = new List<(RecommendedSlotItem Slot, Item Item)>();
		var pickedIds = new HashSet<int>();

		foreach (var cand in candidates.OrderByDescending(x => x.Slot.Score))
		{
			if (picked.Count >= maxCount)
				break;

			if (IsSupersededByAlreadyPicked(cand.Item.type, pickedIds))
				continue;

			picked.Add(cand);
			pickedIds.Add(cand.Item.type);
		}
		return picked;
	}

	private bool IsSupersededByAlreadyPicked(int candidateId, HashSet<int> alreadyPicked)
	{
		if (alreadyPicked.Contains(candidateId))
			return true;

		// Multi-hop BFS over equipment-to-equipment crafting upgrades (max depth 6)
		var visited = new HashSet<int> { candidateId };
		var queue = new Queue<(int Id, int Depth)>();
		queue.Enqueue((candidateId, 0));

		while (queue.Count > 0)
		{
			(int cur, int depth) = queue.Dequeue();
			if (depth >= 6 || !_graph.EquipmentUpgradesTo.TryGetValue(cur, out HashSet<int> nexts))
				continue;

			foreach (int nextId in nexts)
			{
				if (alreadyPicked.Contains(nextId))
					return true;

				if (visited.Add(nextId))
					queue.Enqueue((nextId, depth + 1));
			}
		}
		return false;
	}

	private int ComputeAccessorySlotBudget(BossStageInfo targetStage)
	{
		int slots = 5;
		bool pastWof = targetStage.Progression > _catalog.WallOfFleshProgression;
		if (Main.expertMode && pastWof)
			slots = 6;
		if (Main.masterMode)
			slots = pastWof ? 7 : 6;
		return slots;
	}

	private static float NormalizeRarity(int rare)
	{
		if (rare < 0) return 0f;
		if (rare <= 11) return rare;
		return 11f + Math.Min(6f, (rare - 11) * 0.5f);
	}

	private static float StageRecencyBonus(float unlockProg, float cutoff)
	{
		float delta = Math.Max(0f, cutoff - unlockProg);
		if (delta <= 1.2f) return 28f;
		if (delta <= 2.5f) return 18f;
		if (delta <= 4.5f) return 8f;
		return 0f;
	}

	private static string BuildWeaponReason(Item item, ItemUnlockInfo info)
	{
		float useSec = Math.Max(1, item.useTime) / 60f;
		int approxDps = (int)Math.Round(item.damage / useSec);
		return $"{item.damage} dmg | ~{approxDps} base DPS | {item.DamageType.DisplayName.Value.Trim()}";
	}
}
