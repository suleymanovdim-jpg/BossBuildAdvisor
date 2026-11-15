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

		// If Auto / All classes selected, evaluate all playable classes fairly across top primary, support, armor, and accessories
		if (selectedClass == null)
		{
			BuildRecommendation bestAuto = null;
			float bestAutoScore = float.NegativeInfinity;

			foreach (DamageClass dc in GetAvailablePlayableClasses())
			{
				BuildRecommendation candidate = BuildForSpecificClass(targetStage, dc, true, cutoff, allowHardmode, options);
				if (candidate.PrimaryWeapons.Count == 0 && (dc != DamageClass.Summon || candidate.SupportWeapons.Count == 0))
					continue;

				float primaryScore = candidate.PrimaryWeapons.Count switch
				{
					0 => 0f,
					1 => candidate.PrimaryWeapons[0].Score * 1.85f,
					_ => candidate.PrimaryWeapons[0].Score + candidate.PrimaryWeapons[1].Score
				};
				float supportWeight = dc == DamageClass.Summon ? 0.65f : 0.25f;
				float supportScore = candidate.SupportWeapons.Take(2).Sum(s => s.Score) * supportWeight;
				float armorScore = (candidate.ArmorSet?.Score ?? 0f) * 1.1f;
				float accScore = candidate.Accessories.Take(5).Sum(a => a.Score) * 0.25f;

				float total = primaryScore + supportScore + armorScore + accScore;
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
				RecommendedSlotItem slot = MakeSlot(item.type, role, score, path, BuildWeaponReason(item, isSentry, item.DamageType == DamageClass.Summon && !isWhip && !isSentry));

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
				string reason = L(isSentry ? "ReasonFreeSentry" : "ReasonFreeMinion", item.damage);
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

		// Recommend top matching ammo types across up to 2 distinct ammo users (e.g. bow + gun)
		var distinctAmmoTypes = topPrimary
			.Select(x => x.Item.useAmmo)
			.Where(a => a > AmmoID.None)
			.Distinct()
			.Take(2)
			.ToList();

		if (distinctAmmoTypes.Count > 0)
		{
			int perTypeLimit = distinctAmmoTypes.Count == 1 ? 3 : 2;
			var pickedAmmo = new List<RecommendedSlotItem>();
			foreach (int ammoType in distinctAmmoTypes)
			{
				var ammoScored = available
					.Where(x => x.Item.ammo == ammoType)
					.Select(x => (
						Slot: MakeSlot(
							x.Item.type,
							"Ammo",
							x.Item.damage * 4f + NormalizeRarity(x.Item.rare) * 6f + StageRecencyBonus(x.Path.UnlockProgression, cutoff) + DifficultyBias(x.Path.Difficulty),
							x.Path,
							L("ReasonAmmo", x.Item.damage)),
						x.Item))
					.OrderByDescending(x => x.Slot.Score)
					.ToList();

				foreach ((RecommendedSlotItem ammoSlot, _) in PickTopDistinct(ammoScored, perTypeLimit))
				{
					if (pickedAmmo.Count >= 4)
						break;
					pickedAmmo.Add(ammoSlot);
				}
			}

			foreach (RecommendedSlotItem ammoSlot in pickedAmmo)
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

		RecommendedSlotItem bestHead = PickBestArmorPiece(available, x => x.headSlot >= 0, "Head", cutoff, targetClass);
		RecommendedSlotItem bestBody = PickBestArmorPiece(available, x => x.bodySlot >= 0 && x.headSlot < 0, "Body", cutoff, targetClass);
		// Robes often set both bodySlot and legSlot for draping visuals; keep them out of standalone Legs picks
		RecommendedSlotItem bestLegs = PickBestArmorPiece(available, x => x.legSlot >= 0 && x.bodySlot < 0 && x.headSlot < 0, "Legs", cutoff, targetClass);

		// Keep highest-scoring class helmet variant per distinct (BodyType, LegsType) so one set doesn't hog all 3 armor tabs
		var bestByBodyLegs = new Dictionary<(int Body, int Legs), ArmorSetCandidate>();
		foreach (ArmorSetDefinition set in _armorSets.Sets)
		{
			if (!availLookup.TryGetValue(set.HeadType, out ItemUnlockPath hPath)
				|| !availLookup.TryGetValue(set.BodyType, out ItemUnlockPath bPath))
				continue;

			bool isTwoPieceRobe = set.LegsType == ItemID.None;
			ItemUnlockPath lPath = null;
			RecommendedSlotItem pairedBestLegs = null;
			if (!isTwoPieceRobe)
			{
				if (!availLookup.TryGetValue(set.LegsType, out lPath))
					continue;
			}
			else if (bestLegs != null && availLookup.TryGetValue(bestLegs.ItemType, out ItemUnlockPath bestLegsPath))
			{
				if (IsTwoPieceRobeCompatibleWithLegs(set.HeadType, set.BodyType, bestLegs.ItemType, set.ModName))
				{
					lPath = bestLegsPath;
					pairedBestLegs = bestLegs;
				}
			}

			int actualLegsType = pairedBestLegs?.ItemType ?? (isTwoPieceRobe ? ItemID.None : set.LegsType);
			int actualTotalDef = set.TotalDefense + (pairedBestLegs != null ? ContentSamples.ItemsByType[pairedBestLegs.ItemType].defense : 0);
			float maxUnlock = Math.Max(hPath.UnlockProgression, Math.Max(bPath.UnlockProgression, lPath?.UnlockProgression ?? 0f));
			(ObtainabilityDifficulty setDiff, string diffReason) = CombinePieceDifficulty(hPath, bPath, lPath);
			float setScore = ScoreArmorSet(set, actualTotalDef, actualLegsType, maxUnlock, cutoff, targetClass, setDiff, rec.PrimaryWeapons);

			var candidate = new ArmorSetCandidate
			{
				HeadType = set.HeadType,
				BodyType = set.BodyType,
				LegsType = actualLegsType,
				TotalDefense = actualTotalDef,
				Score = setScore,
				UnlockProgression = maxUnlock,
				IsTrueSet = true,
				SetBonusSummary = set.SetBonusText,
				ModName = set.ModName,
				Difficulty = setDiff,
				DifficultyReason = diffReason,
				HeadSlot = MakeArmorPieceSlot(set.HeadType, "Head", hPath, setScore / (actualLegsType > ItemID.None ? 3f : 2f)),
				BodySlot = MakeArmorPieceSlot(set.BodyType, "Body", bPath, setScore / (actualLegsType > ItemID.None ? 3f : 2f)),
				LegsSlot = actualLegsType > ItemID.None ? MakeArmorPieceSlot(actualLegsType, "Legs", lPath, setScore / 3f) : null
			};

			var key = (set.BodyType, set.LegsType);
			if (!bestByBodyLegs.TryGetValue(key, out ArmorSetCandidate existing) || candidate.Score > existing.Score)
				bestByBodyLegs[key] = candidate;
		}

		var candidates = bestByBodyLegs.Values
			.OrderByDescending(c => c.Score)
			.ToList();

		// Evaluate mixed best-in-slot armor on equal piece-weight footing with true sets
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

				float mixedFairScore = totalDef * defWeight + sumAff * 1.5f + sumGen * 0.85f
					+ NormalizeRarity((int)Math.Round(avgRare)) * 10f
					+ StageRecencyBonus(maxUnlock, cutoff)
					+ DifficultyBias(mixDiff);

				if (candidates.Count == 0 || mixedFairScore > candidates[0].Score * 1.04f)
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
						ModName = L("ModMixed"),
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
			string dedupKey = opt.IsTrueSet
				? (!string.IsNullOrEmpty(opt.SetBonusSummary)
					? $"Set:{opt.ModName}:{opt.SetBonusSummary}"
					: $"Head:{opt.HeadType}")
				: $"Mixed:{opt.HeadType}:{opt.BodyType}:{opt.LegsType}";
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

	private static bool IsTwoPieceRobeCompatibleWithLegs(int headType, int bodyType, int legsType, string modName)
	{
		if (legsType <= ItemID.None || legsType == headType || legsType == bodyType)
			return false;

		if (ContentSamples.ItemsByType.TryGetValue(legsType, out Item lCheck) && (lCheck.bodySlot >= 0 || lCheck.headSlot >= 0))
			return false;

		if (modName == "Terraria")
			return true;

		try
		{
			Item h = ContentSamples.ItemsByType[headType];
			Item b = ContentSamples.ItemsByType[bodyType];
			Item l = ContentSamples.ItemsByType[legsType];
			if (h.ModItem != null) return h.ModItem.IsArmorSet(h, b, l);
			if (b.ModItem != null) return b.ModItem.IsArmorSet(h, b, l);
		}
		catch
		{
		}
		return false;
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

		// Propagate precursor scores across multi-hop BFS (bridges any missing/filtered intermediate recipes)
		var byId = new Dictionary<int, int>();
		for (int i = 0; i < candidates.Count; i++)
			byId.TryAdd(candidates[i].Slot.ItemType, i);

		bool changed = true;
		for (int round = 0; round < 6 && changed; round++)
		{
			changed = false;
			for (int i = 0; i < candidates.Count; i++)
			{
				int srcId = candidates[i].Slot.ItemType;
				float srcScore = candidates[i].Slot.Score;
				ItemSemanticProfile srcProf = candidates[i].Profile;
				float srcAff = srcProf.ClassAffinityScores.GetValueOrDefault(targetClass.Type);

				var visited = new HashSet<int> { srcId };
				var queue = new Queue<(int Id, int Depth)>();
				queue.Enqueue((srcId, 0));

				while (queue.Count > 0)
				{
					(int cur, int depth) = queue.Dequeue();
					if (depth >= 6 || !_graph.EquipmentUpgradesTo.TryGetValue(cur, out HashSet<int> nexts))
						continue;

					foreach (int nextId in nexts)
					{
						if (byId.TryGetValue(nextId, out int j))
						{
							ItemSemanticProfile dstProf = candidates[j].Profile;
							if (!LosesClassIdentityOnUpgrade(srcProf, dstProf, srcAff, targetClass) && candidates[j].Slot.Score <= srcScore + 4f)
							{
								var curSlot = candidates[j].Slot;
								candidates[j] = (new RecommendedSlotItem
								{
									ItemType = curSlot.ItemType,
									RoleTag = curSlot.RoleTag,
									Score = srcScore + 6f,
									UnlockProgression = curSlot.UnlockProgression,
									SourceSummary = curSlot.SourceSummary,
									ReasonSummary = curSlot.ReasonSummary,
									Confidence = curSlot.Confidence,
									SourceType = curSlot.SourceType,
									Difficulty = curSlot.Difficulty,
									DifficultyReason = curSlot.DifficultyReason
								}, dstProf);
								changed = true;
							}
						}

						if (visited.Add(nextId))
							queue.Enqueue((nextId, depth + 1));
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
		PickAccessoryBatch(rec.Accessories, rec.AccessorySlotBudget, 1, 1, 1, 1, sorted, pickedIds);

		// Populate up to 14 total accessories with practical swaps/alternatives
		int altCap = Math.Max(0, 14 - rec.Accessories.Count);
		PickAccessoryBatch(rec.AltAccessories, altCap, 2, 1, 1, 2, sorted, pickedIds);
	}

	private void PickAccessoryBatch(
		List<RecommendedSlotItem> dest,
		int limit,
		int maxWings,
		int maxBoots,
		int maxDash,
		int maxBalloons,
		List<(RecommendedSlotItem Slot, ItemSemanticProfile Profile)> sorted,
		HashSet<int> pickedIds)
	{
		int wings = 0;
		int boots = 0;
		int dash = 0;
		int balloons = 0;

		foreach ((RecommendedSlotItem slot, ItemSemanticProfile prof) in sorted)
		{
			if (dest.Count >= limit)
				break;

			if (IsSupersededByAlreadyPicked(slot.ItemType, pickedIds))
				continue;

			// Enforce category caps independently so multi-tag items cannot bypass any cap
			if (prof.IsWings && wings >= maxWings) continue;
			if (prof.IsMobilityBoots && boots >= maxBoots) continue;
			if (prof.IsDashOrDodge && dash >= maxDash) continue;

			bool isBalloon = IsBalloonFamilyAccessory(slot.ItemType, prof);
			if (isBalloon && balloons >= maxBalloons) continue;

			dest.Add(slot);
			pickedIds.Add(slot.ItemType);

			if (prof.IsWings) wings++;
			if (prof.IsMobilityBoots) boots++;
			if (prof.IsDashOrDodge) dash++;
			if (isBalloon) balloons++;
		}
	}

	private static bool IsBalloonFamilyAccessory(int itemType, ItemSemanticProfile prof)
	{
		if (itemType is ItemID.ShinyRedBalloon or ItemID.CloudinaBalloon or ItemID.BlizzardinaBalloon
			or ItemID.SandstorminaBalloon or ItemID.FartInABalloon or ItemID.BlueHorseshoeBalloon
			or ItemID.WhiteHorseshoeBalloon or ItemID.YellowHorseshoeBalloon or ItemID.BalloonHorseshoeHoney
			or ItemID.BalloonHorseshoeFart or ItemID.BalloonHorseshoeSharkron or ItemID.BalloonPufferfish
			or ItemID.SharkronBalloon or ItemID.HoneyBalloon or ItemID.BundleofBalloons or ItemID.HorseshoeBundle)
			return true;

		string lower = (ContentSamples.ItemsByType[itemType].Name + " " + prof.CombinedTooltipText).ToLowerInvariant();
		return lower.Contains("balloon") || lower.Contains("шарик") || lower.Contains("воздушн");
	}

	private static bool LosesClassIdentityOnUpgrade(
		ItemSemanticProfile srcProf,
		ItemSemanticProfile dstProf,
		float srcAff,
		DamageClass targetClass)
	{
		float dstAff = dstProf.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
		if (dstAff <= 0f && dstProf.ClassAffinityScores.Count > 0 && srcAff > 0f)
			return true;

		// Class Emblems (15% class damage -> Avenger 12% generic damage) are non-strict conversions and stack in Terraria
		if (srcAff > 0f && dstAff + 4f < srcAff && dstProf.ClassAffinityScores.Count == 0)
			return dstProf.GenericBonusScore < srcProf.GenericBonusScore + srcAff * 0.85f;

		return false;
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
						bool offClassLoss = LosesClassIdentityOnUpgrade(srcProf, dstProf, srcAff, targetClass);
						bool lostWings = srcProf.IsWings && !dstProf.IsWings;
						bool lostBoots = srcProf.IsMobilityBoots && !dstProf.IsMobilityBoots;

						if (!offClassLoss && !lostWings && !lostBoots && dstSlot.Difficulty <= srcSlot.Difficulty)
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
			.Select(x => MakeSlot(x.Item.type, "Healing", x.Item.healLife, x.Path, L("ReasonHealing", x.Item.healLife)))
			.FirstOrDefault();

		var buffCandidates = new List<RecommendedSlotItem>();
		var seenBuffGroups = new HashSet<string>();

		foreach ((Item item, ItemUnlockPath path) in available)
		{
			if (item.potion || item.buffType <= 0 || item.buffTime <= 0 || !item.consumable)
				continue;

			if (item.buffType == BuffID.Tipsy && !targetClass.CountsAsClass(DamageClass.Melee))
				continue;

			if (item.buffType == BuffID.Archery && !targetClass.CountsAsClass(DamageClass.Ranged))
				continue;

			if (item.buffType is BuffID.MagicPower or BuffID.ManaRegeneration && !targetClass.CountsAsClass(DamageClass.Magic))
				continue;

			if (item.buffType == BuffID.Summoning && !targetClass.CountsAsClass(DamageClass.Summon))
				continue;

			if (item.buffType < Main.meleeBuff.Length && Main.meleeBuff[item.buffType]
				&& !targetClass.CountsAsClass(DamageClass.Melee) && targetClass != DamageClass.Summon)
				continue;

			ItemSemanticProfile prof = _graph.Semantics.TryGetValue(item.type, out var p)
				? p
				: ItemTooltipAnalyzer.Analyze(item);

			if (!prof.IsCombatBuffPotion)
				continue;

			float classAff = prof.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
			float maxOtherClass = MaxOtherClassAffinity(prof, targetClass);
			if (maxOtherClass >= 15f && classAff <= 0f && !prof.IsSurvivability)
				continue;

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
			string groupKey = GetBuffExclusionGroup(buffType);
			if (!seenBuffGroups.Add(groupKey))
				continue;

			rec.BuffPotions.Add(pot);
		}
	}

	private static string GetBuffExclusionGroup(int buffType)
	{
		if (buffType is BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3)
			return "group:WellFed";
		if (buffType > 0 && buffType < Main.meleeBuff.Length && Main.meleeBuff[buffType])
			return "group:Flask";
		return $"id:{buffType}";
	}

	private static float MaxOtherClassAffinity(ItemSemanticProfile prof, DamageClass targetClass)
	{
		float max = 0f;
		foreach ((int dcType, float val) in prof.ClassAffinityScores)
		{
			if (dcType != targetClass.Type)
				max = Math.Max(max, val);
		}
		return max;
	}

	private static bool IsCombatWeapon(Item item)
	{
		if (item.type == ItemID.CoinGun)
			return true;

		if (item.damage <= 0 || item.ammo > AmmoID.None || item.accessory)
			return false;

		if (item.DamageType == DamageClass.Default)
			return false;

		// Exclude mining/woodcutting/hammer tools (including chainsaws & drills which fire held projectiles)
		if (item.pick > 0 || item.axe > 0 || item.hammer > 0)
			return false;

		if (item.createTile >= TileID.Dirt || item.createWall >= 0)
			return false;

		// Exclude self-damaging / world-destroying explosives while keeping real thrown weapons (Shuriken, Beenade, Javelins)
		if (item.type is ItemID.Grenade or ItemID.BouncyGrenade or ItemID.StickyGrenade or ItemID.PartyGirlGrenade
			or ItemID.Bomb or ItemID.StickyBomb or ItemID.BouncyBomb or ItemID.BombFish or ItemID.ScarabBomb
			or ItemID.Dynamite or ItemID.StickyDynamite or ItemID.BouncyDynamite or ItemID.DryBomb or ItemID.WetBomb
			or ItemID.LavaBomb or ItemID.HoneyBomb or ItemID.ExplosiveBunny)
			return false;

		if (item.consumable && !item.CountsAsClass(DamageClass.Throwing) && !(item.noUseGraphic && item.noMelee && item.damage >= 10) && item.damage < 35)
			return false;

		return true;
	}

	private static bool MatchesWeaponClass(Item item, DamageClass targetClass)
	{
		if (item.DamageType == targetClass)
			return true;

		if (item.CountsAsClass(targetClass))
			return true;

		return false;
	}

	private static int GetEffectiveWeaponDamage(Item item) =>
		item.type == ItemID.CoinGun ? 50 : item.damage;

	private static float EstimateWeaponBaseDps(Item item, bool isSentry, bool isMinion, out int effectiveDmg)
	{
		effectiveDmg = GetEffectiveWeaponDamage(item);
		float shotsPerSec;

		if (isSentry || isMinion)
		{
			// Summoned minions and sentries attack continuously rather than once per summon cast
			shotsPerSec = isSentry ? 2.4f : 3.0f;
		}
		else
		{
			float effectiveUse = item.channel
				? Math.Clamp(item.useTime, 4f, 12f)
				: Math.Max(4f, item.useTime + item.reuseDelay * 0.5f);
			shotsPerSec = 60f / effectiveUse;
			if (!item.channel && item.useAnimation > item.useTime && item.useTime > 0)
			{
				// Burst fire adjustment (e.g. Clockwork Assault Rifle)
				float burstsPerSec = 60f / Math.Max(6f, item.useAnimation + item.reuseDelay);
				int shotsPerBurst = Math.Max(1, item.useAnimation / item.useTime);
				shotsPerSec = burstsPerSec * shotsPerBurst;
			}
		}

		float critMult = (isSentry || isMinion) ? 1f : 1f + Math.Clamp(item.crit, 0, 70) * 0.01f;
		return effectiveDmg * Math.Min(shotsPerSec, 15f) * critMult;
	}

	private static float ScoreWeapon(
		Item item,
		ItemUnlockPath path,
		float cutoff,
		DamageClass targetClass,
		bool isWhip,
		bool isSentry)
	{
		bool isMinion = targetClass == DamageClass.Summon && !isWhip && !isSentry && item.DamageType == DamageClass.Summon;
		float rawDps = EstimateWeaponBaseDps(item, isSentry, isMinion, out int effectiveDmg);

		// Log-compressed DPS + rarity + stage recency keeps modded hyper-fast low-dmg vs slow heavy-hit balanced
		float dpsScore = (float)(Math.Log10(Math.Max(10.0, rawDps)) * 42.0);
		float rarityScore = NormalizeRarity(item.rare) * 11f;
		float valueScore = (float)(Math.Log10(Math.Max(100, item.value)) * 4.5);
		float recency = StageRecencyBonus(path.UnlockProgression, cutoff);

		float bonus = DifficultyBias(path.Difficulty);
		if (item.shoot > ProjectileID.None) bonus += 16f;
		if (item.autoReuse || item.channel) bonus += 6f;
		if (item.channel) bonus += 6f;
		if (item.noMelee && item.shoot > ProjectileID.None) bonus += 5f;
		if (path.SourceType == ItemSourceType.BossDrop) bonus += 8f;
		if (path.Confidence == SourceConfidence.TierEstimated) bonus -= 18f;
		if (effectiveDmg >= 500 && path.Confidence == SourceConfidence.TierEstimated) bonus -= 80f;
		if (item.DamageType == targetClass) bonus += 5f;
		if (isWhip || isSentry) bonus += 6f;

		return dpsScore + rarityScore + valueScore + recency + bonus;
	}

	private float ScoreArmorSet(
		ArmorSetDefinition set,
		int totalDefense,
		int actualLegsType,
		float maxUnlock,
		float cutoff,
		DamageClass targetClass,
		ObtainabilityDifficulty difficulty,
		List<RecommendedSlotItem> primaryWeapons)
	{
		float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
		float score = totalDefense * defWeight;

		float classAff = set.CombinedProfile.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
		float genScore = set.CombinedProfile.GenericBonusScore;

		int rareSum = ContentSamples.ItemsByType[set.HeadType].rare + ContentSamples.ItemsByType[set.BodyType].rare;
		int pieceCount = 2;
		if (actualLegsType > ItemID.None)
		{
			rareSum += ContentSamples.ItemsByType[actualLegsType].rare;
			pieceCount = 3;
			if (set.LegsType == ItemID.None && _graph.Semantics.TryGetValue(actualLegsType, out var legProf))
			{
				classAff += legProf.ClassAffinityScores.GetValueOrDefault(targetClass.Type);
				genScore += legProf.GenericBonusScore;
			}
		}

		// Match mixed-armor piece weights cleanly so sets win on real set bonus cohesion, not piece-multiplier inflation
		score += classAff * 1.5f;
		score += genScore * 0.85f;
		score += StageRecencyBonus(maxUnlock, cutoff);
		score += DifficultyBias(difficulty);
		score += NormalizeRarity((int)Math.Round(rareSum / (float)pieceCount)) * 10f;

		if (set.IsWeaponSpecificBonus)
		{
			bool hasMatchingWeapon = IsMeteorArmorSet(set) && primaryWeapons.Any(w => IsSpaceGunSetWeapon(w.ItemType));
			score += hasMatchingWeapon ? 38f : 8f;
		}
		else if (!string.IsNullOrEmpty(set.SetBonusText))
			score += 28f;
		else
			score += 14f;

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
		RecommendedSlotItem sgSlot = MakeSlot(sgItem.type, "Weapon", boostedScore, sgPath, BuildWeaponReason(sgItem, false, false) + " | " + L("ReasonMeteorSynergy"));

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
			float defWeight = cutoff <= _catalog.WallOfFleshProgression ? 2.4f : 1.5f;
			float score = item.defense * defWeight + classAff * 1.5f + prof.GenericBonusScore * 0.85f
				+ NormalizeRarity(item.rare) * 10f + StageRecencyBonus(path.UnlockProgression, cutoff) + DifficultyBias(path.Difficulty);

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
		string tip = ItemTooltipAnalyzer.GetTooltipText(item);
		string reason = string.IsNullOrWhiteSpace(tip)
			? L("ReasonArmorPieceNoTip", item.defense)
			: L("ReasonArmorPiece", item.defense, tip);
		return MakeSlot(itemType, role, score, path, reason);
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
		float maxOtherClass = MaxOtherClassAffinity(prof, targetClass);

		if (prof.IsPureNonCombatUtility && classAff <= 0f)
		{
			role = "Utility";
			reason = prof.CombinedTooltipText;
			return 0f;
		}

		// Exclude strictly off-class combat accessories (e.g. Ranged scopes/quivers for Mage, Magic cuffs for Melee)
		if (!prof.IsWings && !prof.IsMobilityBoots && !prof.IsDashOrDodge && classAff <= 0f && maxOtherClass >= 15f)
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

		if (maxOtherClass > classAff + 5f)
			score -= (maxOtherClass - classAff) * 1.6f;

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
			string tipLower = (item.Name + " " + prof.CombinedTooltipText).Replace('\u00a0', ' ').ToLowerInvariant();
			if (tipLower.Contains("allows flight") || tipLower.Contains("rocket boots")
				|| tipLower.Contains("позволяет летать") || tipLower.Contains("реактивн"))
				score += 18f;
			if (tipLower.Contains("run super fast") || tipLower.Contains("быстро бег") || tipLower.Contains("сверхбыстр")
				|| tipLower.Contains("movement speed") || tipLower.Contains("скорость движения") || tipLower.Contains("скорость передвижения"))
				score += 8f;
		}
		else if (classAff > 0f)
		{
			role = "ClassBoost";
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
			? (item.defense > 0 ? L("ReasonDefense", item.defense) : role)
			: prof.CombinedTooltipText;

		return score;
	}

	private static (ObtainabilityDifficulty Diff, string Reason) CombinePieceDifficulty(
		ItemUnlockPath h,
		ItemUnlockPath b,
		ItemUnlockPath l)
	{
		ItemUnlockPath hardest = h;
		if (b != null && (b.Difficulty > hardest.Difficulty || (b.Difficulty == hardest.Difficulty && b.UnlockProgression > hardest.UnlockProgression)))
			hardest = b;
		if (l != null && (l.Difficulty > hardest.Difficulty || (l.Difficulty == hardest.Difficulty && l.UnlockProgression > hardest.UnlockProgression)))
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

	private static string BuildWeaponReason(Item item, bool isSentry, bool isMinion)
	{
		float rawDps = EstimateWeaponBaseDps(item, isSentry, isMinion, out int effectiveDmg);
		int approxDps = (int)Math.Round(rawDps);
		string className = ItemTooltipAnalyzer.FormatClassDisplayName(item.DamageType);
		return L("ReasonWeaponDps", effectiveDmg, approxDps, className);
	}

	private static string L(string key, params object[] args) =>
		Terraria.Localization.Language.GetTextValue("Mods.BossBuildAdvisor.Recommender." + key, args);
}
