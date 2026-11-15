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
		EnsureArmorSignatureWeapons(rec, availableItems, targetClass, cutoff);
		SelectAccessories(rec, availableItems, targetStage, targetClass, cutoff);
		SelectPotions(rec, availableItems, targetClass);

		return rec;
	}

	private List<(Item Item, ItemUnlockPath Path)> CollectAvailableItems(float cutoff, bool allowHardmode, BuildQueryOptions options)
	{
		var list = new List<(Item, ItemUnlockPath)>();

		foreach ((int itemId, ItemUnlockInfo info) in _graph.Items)
		{
			if (info.Unobtainable)
				continue;

			ItemUnlockPath path = info.GetPathFor(options.MaxDifficulty);
			if (path == null || path.UnlockProgression > cutoff)
				continue;

			if (!allowHardmode && path.RequiresHardmode)
				continue;

			if (!options.IncludeTierEstimatedItems && path.Confidence == SourceConfidence.TierEstimated)
				continue;

			if (options.RespectWorldDifficulty)
			{
				if (path.ExpertOnly && !Main.expertMode)
					continue;
				if (path.MasterOnly && !Main.masterMode)
					continue;
				if (path.ClassicOnly && Main.expertMode)
					continue;
			}

			if (options.RespectWorldEvil && !Main.drunkWorld)
			{
				if (path.CrimsonOnly && !WorldGen.crimson)
					continue;
				if (path.CorruptOnly && WorldGen.crimson)
					continue;
			}

			if (ContentSamples.ItemsByType.TryGetValue(itemId, out Item item) && !item.IsAir && !item.vanity)
				list.Add((item, path));
		}

		return list;
	}

	private static RecommendedSlotItem MakeSlot(int itemType, string role, float score, ItemUnlockPath path, string reason)
	{
		return new RecommendedSlotItem
		{
			ItemType = itemType,
			RoleTag = role,
			Score = score,
			UnlockProgression = path.UnlockProgression,
			SourceSummary = path.SourceSummary,
			ReasonSummary = reason,
			Confidence = path.Confidence,
			SourceType = path.SourceType,
			Difficulty = path.Difficulty,
			DifficultyReason = path.DifficultyReason
		};
	}

	private void SelectWeaponsAndAmmo(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockPath Path)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff)
	{
		var scoredPrimary = new List<(RecommendedSlotItem Slot, Item Item)>();
		var scoredWhips = new List<(RecommendedSlotItem Slot, Item Item)>();
		var scoredSentries = new List<(RecommendedSlotItem Slot, Item Item)>();
		var scoredFreeMinions = new List<(RecommendedSlotItem Slot, Item Item)>();

		foreach ((Item item, ItemUnlockPath path) in available)
		{
			if (!IsCombatWeapon(item))
				continue;

			bool isWhip = item.shoot > ProjectileID.None && item.shoot < ProjectileID.Sets.IsAWhip.Length && ProjectileID.Sets.IsAWhip[item.shoot];
			bool isSentry = item.sentry || item.DD2Summon;

			if (MatchesWeaponClass(item, targetClass))
			{
				float score = ScoreWeapon(item, path, cutoff, targetClass, isWhip, isSentry);
				string role = isWhip ? "Whip" : isSentry ? "Sentry" : item.DamageType == DamageClass.Summon ? "Minion" : "Weapon";
				RecommendedSlotItem slot = MakeSlot(item.type, role, score, path, BuildWeaponReason(item));

				if (targetClass == DamageClass.Summon && isWhip)
					scoredWhips.Add((slot, item));
				else if (targetClass == DamageClass.Summon && isSentry)
					scoredSentries.Add((slot, item));
				else
					scoredPrimary.Add((slot, item));
			}
			else if (targetClass != DamageClass.Summon && item.DamageType == DamageClass.Summon && item.shoot > ProjectileID.None)
			{
				float score = ScoreWeapon(item, path, cutoff, DamageClass.Summon, false, isSentry);
				string role = isSentry ? "Support Sentry" : "Free Minion";
				string reason = isSentry ? $"Free sentry DPS ({item.damage} dmg)" : $"Free minion slot ({item.damage} dmg)";
				RecommendedSlotItem slot = MakeSlot(item.type, role, score, path, reason);

				if (isSentry)
					scoredSentries.Add((slot, item));
				else
					scoredFreeMinions.Add((slot, item));
			}
		}

		var topPrimary = PickTopDistinct(scoredPrimary, 8);
		foreach ((RecommendedSlotItem slot, _) in topPrimary)
			rec.PrimaryWeapons.Add(slot);

		if (targetClass == DamageClass.Summon)
		{
			foreach ((RecommendedSlotItem slot, _) in PickTopDistinct(scoredWhips, 2))
				rec.SupportWeapons.Add(slot);
			foreach ((RecommendedSlotItem slot, _) in PickTopDistinct(scoredSentries, 2))
				rec.SupportWeapons.Add(slot);
		}
		else if (topPrimary.Count > 0)
		{
			foreach ((RecommendedSlotItem slot, _) in PickTopDistinct(scoredFreeMinions, 2))
				rec.SupportWeapons.Add(slot);
			foreach ((RecommendedSlotItem slot, _) in PickTopDistinct(scoredSentries, 2))
				rec.SupportWeapons.Add(slot);
		}

		// Recommend top matching ammo types for primary weapons that consume ammo
		Item primaryAmmoUser = topPrimary.Select(x => x.Item).FirstOrDefault(x => x.useAmmo > AmmoID.None);
		if (primaryAmmoUser != null)
		{
			var ammoScored = available
				.Where(x => x.Item.ammo == primaryAmmoUser.useAmmo)
				.Select(x => (
					Slot: MakeSlot(
						x.Item.type,
						"Ammo",
						x.Item.damage * 4f + NormalizeRarity(x.Item.rare) * 6f + StageRecencyBonus(x.Path.UnlockProgression, cutoff) + DifficultyBias(x.Path.Difficulty),
						x.Path,
						$"{x.Item.damage} dmg ammo"),
					x.Item))
				.OrderByDescending(x => x.Slot.Score)
				.ToList();

			foreach ((RecommendedSlotItem ammoSlot, _) in PickTopDistinct(ammoScored, 3))
				rec.AmmoOptions.Add(ammoSlot);

			rec.RecommendedAmmo = rec.AmmoOptions.FirstOrDefault();
		}
	}

	private void SelectArmor(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockPath Path)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff,
		bool allowHardmode,
		BuildQueryOptions options)
	{
		var availLookup = new Dictionary<int, ItemUnlockPath>(available.Count);
		foreach ((Item availItem, ItemUnlockPath availPath) in available)
			availLookup.TryAdd(availItem.type, availPath);

		// Keep highest-scoring class helmet variant per distinct (BodyType, LegsType) so one set doesn't hog all 3 armor tabs
		var bestByBodyLegs = new Dictionary<(int Body, int Legs), ArmorSetCandidate>();
		foreach (ArmorSetDefinition set in _armorSets.Sets)
		{
			if (!availLookup.TryGetValue(set.HeadType, out ItemUnlockPath hPath)
				|| !availLookup.TryGetValue(set.BodyType, out ItemUnlockPath bPath)
				|| !availLookup.TryGetValue(set.LegsType, out ItemUnlockPath lPath))
				continue;

			float maxUnlock = Math.Max(hPath.UnlockProgression, Math.Max(bPath.UnlockProgression, lPath.UnlockProgression));
			(ObtainabilityDifficulty setDiff, string diffReason) = CombinePieceDifficulty(hPath, bPath, lPath);
			float setScore = ScoreArmorSet(set, maxUnlock, cutoff, targetClass, setDiff, rec.PrimaryWeapons);

			var candidate = new ArmorSetCandidate
			{
				HeadType = set.HeadType,
				BodyType = set.BodyType,
				LegsType = set.LegsType,
				TotalDefense = set.TotalDefense,
				Score = setScore,
				UnlockProgression = maxUnlock,
				IsTrueSet = true,
				SetBonusSummary = set.SetBonusText,
				ModName = set.ModName,
				Difficulty = setDiff,
				DifficultyReason = diffReason,
				HeadSlot = MakeArmorPieceSlot(set.HeadType, "Head", hPath, setScore / 3f),
				BodySlot = MakeArmorPieceSlot(set.BodyType, "Body", bPath, setScore / 3f),
				LegsSlot = MakeArmorPieceSlot(set.LegsType, "Legs", lPath, setScore / 3f)
			};

			var key = (set.BodyType, set.LegsType);
			if (!bestByBodyLegs.TryGetValue(key, out ArmorSetCandidate existing) || candidate.Score > existing.Score)
				bestByBodyLegs[key] = candidate;
		}

		var candidates = bestByBodyLegs.Values
			.OrderByDescending(c => c.Score)
			.ToList();

		// Evaluate mixed best-in-slot armor as an extra candidate if valid and competitive
		RecommendedSlotItem bestHead = PickBestArmorPiece(available, x => x.headSlot >= 0, "Head", cutoff, targetClass);
		RecommendedSlotItem bestBody = PickBestArmorPiece(available, x => x.bodySlot >= 0, "Body", cutoff, targetClass);
		RecommendedSlotItem bestLegs = PickBestArmorPiece(available, x => x.legSlot >= 0, "Legs", cutoff, targetClass);

		if (bestHead != null && bestBody != null && bestLegs != null)
		{
			bool alreadyExactSet = candidates.Any(c =>
				c.HeadType == bestHead.ItemType && c.BodyType == bestBody.ItemType && c.LegsType == bestLegs.ItemType);

			if (!alreadyExactSet)
			{
				Item ih = ContentSamples.ItemsByType[bestHead.ItemType];
				Item ib = ContentSamples.ItemsByType[bestBody.ItemType];
				Item il = ContentSamples.ItemsByType[bestLegs.ItemType];
				int totalDef = ih.defense + ib.defense + il.defense;
				float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
				float avgRare = (ih.rare + ib.rare + il.rare) / 3f;
				float maxUnlock = Math.Max(bestHead.UnlockProgression, Math.Max(bestBody.UnlockProgression, bestLegs.UnlockProgression));
				ItemUnlockPath hP = availLookup[bestHead.ItemType];
				ItemUnlockPath bP = availLookup[bestBody.ItemType];
				ItemUnlockPath lP = availLookup[bestLegs.ItemType];
				(ObtainabilityDifficulty mixDiff, string mixReason) = CombinePieceDifficulty(hP, bP, lP);

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

				float mixedFairScore = totalDef * defWeight + sumAff * 1.4f + sumGen * 0.8f
					+ NormalizeRarity((int)Math.Round(avgRare)) * 10f
					+ StageRecencyBonus(maxUnlock, cutoff)
					+ DifficultyBias(mixDiff);

				if (candidates.Count == 0 || mixedFairScore > candidates[0].Score * 1.10f)
				{
					candidates.Insert(0, new ArmorSetCandidate
					{
						HeadType = bestHead.ItemType,
						BodyType = bestBody.ItemType,
						LegsType = bestLegs.ItemType,
						TotalDefense = totalDef,
						Score = mixedFairScore,
						UnlockProgression = maxUnlock,
						IsTrueSet = false,
						ModName = "Mixed",
						Difficulty = mixDiff,
						DifficultyReason = mixReason,
						HeadSlot = bestHead,
						BodySlot = bestBody,
						LegsSlot = bestLegs
					});
				}
			}
		}

		var seenSetKeys = new HashSet<string>();
		foreach (ArmorSetCandidate opt in candidates)
		{
			string dedupKey = opt.IsTrueSet && !string.IsNullOrEmpty(opt.SetBonusSummary)
				? $"Set:{opt.ModName}:{opt.SetBonusSummary}"
				: $"Head:{opt.HeadType}";
			if (!seenSetKeys.Add(dedupKey))
				continue;

			rec.ArmorOptions.Add(opt);
			if (rec.ArmorOptions.Count >= 3)
				break;
		}

		ArmorSetCandidate primary = rec.ArmorOptions.FirstOrDefault();
		if (primary != null)
		{
			rec.ArmorSet = primary;
			rec.HeadSlot = primary.HeadSlot;
			rec.BodySlot = primary.BodySlot;
			rec.LegsSlot = primary.LegsSlot;
		}
	}

	private void SelectAccessories(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockPath Path)> available,
		BossStageInfo targetStage,
		DamageClass targetClass,
		float cutoff)
	{
		var candidates = new List<(RecommendedSlotItem Slot, ItemSemanticProfile Profile)>();

		foreach ((Item item, ItemUnlockPath path) in available)
		{
			if (!item.accessory)
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			float score = ScoreAccessory(item, path, prof, cutoff, targetClass, out string role, out string reason);
			if (score <= 5f)
				continue;

			candidates.Add((MakeSlot(item.type, role, score, path, reason), prof));
		}

		// Ensure crafted upgrades always outrank their available precursors so precursor suppression triggers cleanly
		var byId = new Dictionary<int, int>();
		for (int i = 0; i < candidates.Count; i++)
			byId.TryAdd(candidates[i].Slot.ItemType, i);

		for (int pass = 0; pass < 6; pass++)
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
							Confidence = cur.Confidence,
							SourceType = cur.SourceType,
							Difficulty = cur.Difficulty,
							DifficultyReason = cur.DifficultyReason
						}, candidates[j].Profile);
					}
				}
			}
		}

		HashSet<int> obsoleteIds = FindStrictlyObsoleteAccessories(candidates, byId, targetClass);
		var sorted = candidates
			.Where(c => !obsoleteIds.Contains(c.Slot.ItemType))
			.OrderByDescending(c => c.Slot.Score)
			.ToList();
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

		// Populate up to 14 total accessories with practical swaps/alternatives
		int altWings = 0;
		int altBoots = 0;
		int altDash = 0;
		foreach ((RecommendedSlotItem slot, ItemSemanticProfile prof) in sorted)
		{
			if (rec.Accessories.Count + rec.AltAccessories.Count >= 14)
				break;

			if (IsSupersededByAlreadyPicked(slot.ItemType, pickedIds))
				continue;

			if (prof.IsWings && altWings >= 2)
				continue;
			if (prof.IsMobilityBoots && !prof.IsWings && altBoots >= 1)
				continue;
			if (prof.IsDashOrDodge && !prof.IsWings && !prof.IsMobilityBoots && altDash >= 1)
				continue;

			rec.AltAccessories.Add(slot);
			pickedIds.Add(slot.ItemType);

			if (prof.IsWings) altWings++;
			if (prof.IsMobilityBoots) altBoots++;
			if (prof.IsDashOrDodge) altDash++;
		}
	}

	private HashSet<int> FindStrictlyObsoleteAccessories(
		List<(RecommendedSlotItem Slot, ItemSemanticProfile Profile)> candidates,
		Dictionary<int, int> byId,
		DamageClass targetClass)
	{
		var obsolete = new HashSet<int>();

		foreach ((RecommendedSlotItem srcSlot, ItemSemanticProfile srcProf) in candidates)
		{
			float srcAff = srcProf.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			var visited = new HashSet<int> { srcSlot.ItemType };
			var queue = new Queue<(int Id, int Depth)>();
			queue.Enqueue((srcSlot.ItemType, 0));

			while (queue.Count > 0)
			{
				(int cur, int depth) = queue.Dequeue();
				if (depth >= 6 || !_graph.EquipmentUpgradesTo.TryGetValue(cur, out HashSet<int> nexts))
					continue;

				foreach (int nextId in nexts)
				{
					if (byId.TryGetValue(nextId, out int j))
					{
						(RecommendedSlotItem dstSlot, ItemSemanticProfile dstProf) = candidates[j];
						float dstAff = dstProf.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
						bool offClassLoss = dstAff + 4f < srcAff && dstAff <= 0f && dstProf.ClassAffinityScores.Count > 0;
						bool lostWings = srcProf.IsWings && !dstProf.IsWings;

						if (!offClassLoss && !lostWings && dstSlot.Difficulty <= srcSlot.Difficulty)
						{
							obsolete.Add(srcSlot.ItemType);
							break;
						}
					}

					if (visited.Add(nextId))
						queue.Enqueue((nextId, depth + 1));
				}

				if (obsolete.Contains(srcSlot.ItemType))
					break;
			}
		}

		return obsolete;
	}

	private void SelectPotions(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockPath Path)> available,
		DamageClass targetClass)
	{
		rec.HealingPotion = available
			.Where(x => x.Item.potion && x.Item.healLife > 0 && x.Item.consumable)
			.OrderByDescending(x => x.Item.healLife)
			.ThenByDescending(x => x.Path.UnlockProgression)
			.Select(x => MakeSlot(x.Item.type, "Healing", x.Item.healLife, x.Path, $"+{x.Item.healLife} HP"))
			.FirstOrDefault();

		var buffCandidates = new List<RecommendedSlotItem>();
		var seenBuffTypes = new HashSet<int>();

		foreach ((Item item, ItemUnlockPath path) in available)
		{
			if (item.buffType <= 0 || item.buffTime <= 0 || !item.consumable)
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			if (!prof.IsCombatBuffPotion)
				continue;

			float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			float score = 20f + prof.GenericBonusScore + classAff * 1.4f + (item.buffTime / 3600f) + DifficultyBias(path.Difficulty) * 0.4f;

			if (item.buffType is BuffID.Ironskin or BuffID.Regeneration or BuffID.Endurance or BuffID.Swiftness or BuffID.Lifeforce or BuffID.Rage or BuffID.Wrath)
				score += 22f;

			if (item.buffType is BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3)
				score += (item.buffType == BuffID.WellFed3 ? 28f : item.buffType == BuffID.WellFed2 ? 20f : 12f);

			buffCandidates.Add(MakeSlot(item.type, "Buff", score, path, prof.CombinedTooltipText));
		}

		foreach (RecommendedSlotItem pot in buffCandidates.OrderByDescending(p => p.Score))
		{
			if (rec.BuffPotions.Count >= 8)
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
		ItemUnlockPath path,
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
		float recency = StageRecencyBonus(path.UnlockProgression, cutoff);

		float bonus = DifficultyBias(path.Difficulty);
		if (item.shoot > ProjectileID.None) bonus += 16f;
		if (item.autoReuse || item.channel) bonus += 6f;
		if (item.noMelee && item.shoot > ProjectileID.None) bonus += 5f;
		if (path.SourceType == ItemSourceType.BossDrop) bonus += 8f;
		if (path.Confidence == SourceConfidence.TierEstimated) bonus -= 18f;
		if (item.damage >= 500 && path.Confidence == SourceConfidence.TierEstimated) bonus -= 80f;
		if (item.DamageType == targetClass) bonus += 5f;
		if (isWhip || isSentry) bonus += 6f;

		return dpsScore + rarityScore + valueScore + recency + bonus;
	}

	private float ScoreArmorSet(
		ArmorSetDefinition set,
		float maxUnlock,
		float cutoff,
		DamageClass targetClass,
		ObtainabilityDifficulty difficulty,
		List<RecommendedSlotItem> primaryWeapons)
	{
		float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
		float score = set.TotalDefense * defWeight;

		float classAff = set.CombinedProfile.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
		score += classAff * 1.8f;
		score += set.CombinedProfile.GenericBonusScore * 0.9f;
		score += StageRecencyBonus(maxUnlock, cutoff);
		score += DifficultyBias(difficulty);

		// Average rarity of pieces
		int rHead = ContentSamples.ItemsByType[set.HeadType].rare;
		int rBody = ContentSamples.ItemsByType[set.BodyType].rare;
		int rLegs = ContentSamples.ItemsByType[set.LegsType].rare;
		score += NormalizeRarity((rHead + rBody + rLegs) / 3) * 10f;

		if (set.IsWeaponSpecificBonus)
		{
			bool hasMatchingWeapon = IsMeteorArmorSet(set) && primaryWeapons.Any(w => IsSpaceGunSetWeapon(w.ItemType));
			score += hasMatchingWeapon ? 48f : 12f;
		}
		else if (!string.IsNullOrEmpty(set.SetBonusText))
			score += 42f;
		else
			score += 24f;

		return score;
	}

	private static bool IsMeteorArmorSet(ArmorSetDefinition set) =>
		set != null && set.HeadType == ItemID.MeteorHelmet && set.BodyType == ItemID.MeteorSuit && set.LegsType == ItemID.MeteorLeggings;

	private static bool IsSpaceGunSetWeapon(int itemType) =>
		itemType > ItemID.None && itemType < ItemID.Sets.IsSpaceGun.Length && ItemID.Sets.IsSpaceGun[itemType];

	private static void EnsureArmorSignatureWeapons(
		BuildRecommendation rec,
		List<(Item Item, ItemUnlockPath Path)> available,
		DamageClass targetClass,
		float cutoff)
	{
		if (targetClass != DamageClass.Magic || rec.ArmorSet == null || rec.ArmorSet.HeadType != ItemID.MeteorHelmet)
			return;

		// Meteor armor's signature synergy is zero-mana IsSpaceGun weapons: guarantee Space Gun (or another IsSpaceGun blaster) is present
		if (rec.PrimaryWeapons.Any(w => IsSpaceGunSetWeapon(w.ItemType)))
			return;

		(Item sgItem, ItemUnlockPath sgPath) = available
			.Where(x => IsSpaceGunSetWeapon(x.Item.type))
			.OrderBy(x => x.Path.Difficulty)
			.ThenByDescending(x => x.Item.type == ItemID.SpaceGun)
			.FirstOrDefault();
		if (sgItem == null || sgPath == null)
			return;

		float boostedScore = ScoreWeapon(sgItem, sgPath, cutoff, targetClass, false, false) + 34f;
		RecommendedSlotItem sgSlot = MakeSlot(sgItem.type, "Weapon", boostedScore, sgPath, BuildWeaponReason(sgItem) + " | 0 mana w/ Meteor Armor");

		if (rec.PrimaryWeapons.Count >= 8)
			rec.PrimaryWeapons[rec.PrimaryWeapons.Count - 1] = sgSlot;
		else
			rec.PrimaryWeapons.Add(sgSlot);

		rec.PrimaryWeapons.Sort((a, b) => b.Score.CompareTo(a.Score));
	}

	private RecommendedSlotItem PickBestArmorPiece(
		List<(Item Item, ItemUnlockPath Path)> available,
		Func<Item, bool> slotPredicate,
		string role,
		float cutoff,
		DamageClass targetClass)
	{
		RecommendedSlotItem best = null;
		foreach ((Item item, ItemUnlockPath path) in available)
		{
			if (!slotPredicate(item))
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.2f : 1.4f;
			float score = item.defense * defWeight + classAff * 1.4f + prof.GenericBonusScore * 0.8f
				+ NormalizeRarity(item.rare) * 8f + StageRecencyBonus(path.UnlockProgression, cutoff) + DifficultyBias(path.Difficulty);

			if (best == null || score > best.Score)
			{
				best = MakeArmorPieceSlot(item.type, role, path, score);
			}
		}
		return best;
	}

	private static RecommendedSlotItem MakeArmorPieceSlot(int itemType, string role, ItemUnlockPath path, float score)
	{
		Item item = ContentSamples.ItemsByType[itemType];
		return MakeSlot(itemType, role, score, path, $"{item.defense} def | {ItemTooltipAnalyzer.GetTooltipText(item)}");
	}

	private static float ScoreAccessory(
		Item item,
		ItemUnlockPath path,
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
			+ StageRecencyBonus(path.UnlockProgression, cutoff) * 0.7f
			+ item.defense * 2.5f
			+ DifficultyBias(path.Difficulty);

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
			score += 74f;
		}
		else if (prof.IsDashOrDodge)
		{
			role = "Dash / Dodge";
			score += 56f;
		}
		else if (prof.IsMobilityBoots)
		{
			role = "Mobility";
			score += 48f;
			string tipLower = (item.Name + " " + prof.CombinedTooltipText).ToLowerInvariant();
			if (tipLower.Contains("allows flight") || tipLower.Contains("rocket boots")
				|| tipLower.Contains("позволяет летать") || tipLower.Contains("реактивн"))
				score += 18f;
			if (tipLower.Contains("movement speed") || tipLower.Contains("скорость движения") || tipLower.Contains("скорость передвижения"))
				score += 8f;
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

		if (path.Confidence == SourceConfidence.TierEstimated)
			score -= 8f;

		reason = string.IsNullOrWhiteSpace(prof.CombinedTooltipText)
			? (item.defense > 0 ? $"+{item.defense} defense" : role)
			: prof.CombinedTooltipText;

		return score;
	}

	private static (ObtainabilityDifficulty Diff, string Reason) CombinePieceDifficulty(
		ItemUnlockPath h,
		ItemUnlockPath b,
		ItemUnlockPath l)
	{
		ItemUnlockPath hardest = h;
		if (b.Difficulty > hardest.Difficulty || (b.Difficulty == hardest.Difficulty && b.UnlockProgression > hardest.UnlockProgression))
			hardest = b;
		if (l.Difficulty > hardest.Difficulty || (l.Difficulty == hardest.Difficulty && l.UnlockProgression > hardest.UnlockProgression))
			hardest = l;
		return (hardest.Difficulty, hardest.DifficultyReason);
	}

	private static float DifficultyBias(ObtainabilityDifficulty difficulty)
	{
		return difficulty switch
		{
			ObtainabilityDifficulty.Easy => 4f,
			ObtainabilityDifficulty.Medium => 1f,
			ObtainabilityDifficulty.Hard => -5f,
			_ => -14f
		};
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

	private static string BuildWeaponReason(Item item)
	{
		float useSec = Math.Max(1, item.useTime) / 60f;
		int approxDps = (int)Math.Round(item.damage / useSec);
		return $"{item.damage} dmg | ~{approxDps} base DPS | {item.DamageType.DisplayName.Value.Trim()}";
	}
}
