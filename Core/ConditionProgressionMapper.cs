using System;
using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

public readonly struct ConditionGate
{
	public float MinProgression { get; init; }
	public bool RequiresHardmode { get; init; }
	public bool PreHardmodeOnly { get; init; }
	public bool ExpertOnly { get; init; }
	public bool MasterOnly { get; init; }
	public bool ClassicOnly { get; init; }
	public bool CrimsonOnly { get; init; }
	public bool CorruptOnly { get; init; }
	public bool Unobtainable { get; init; }
	public bool IsChallengeCondition { get; init; }
	public bool IsSeasonal { get; init; }
	public bool HasSpecialCondition { get; init; }
	public string SpecialConditionText { get; init; }
}

/// <summary>
/// Translates Terraria Recipe Conditions, Shop Conditions, and ItemDropRule Conditions
/// into progression gates across vanilla and modded bosses.
/// </summary>
public static class ConditionProgressionMapper
{
	private static bool TryResolveKnownConditionKey(string key, BossCatalog catalog, out float progression, out bool hardmodeOverride, out bool preHardmodeOverride)
	{
		hardmodeOverride = false;
		preHardmodeOverride = false;

		float wof = catalog?.WallOfFleshProgression ?? 7.0f;
		float ks = catalog?.GetProgressionForNpc(NPCID.KingSlime, 1.0f) ?? 1.0f;
		float eoc = catalog?.GetProgressionForNpc(NPCID.EyeofCthulhu, 2.0f) ?? 2.0f;
		float evil = Math.Min(
			catalog?.GetProgressionForNpc(NPCID.EaterofWorldsHead, 3.0f) ?? 3.0f,
			catalog?.GetProgressionForNpc(NPCID.BrainofCthulhu, 3.0f) ?? 3.0f);
		float qb = catalog?.GetProgressionForNpc(NPCID.QueenBee, 4.0f) ?? 4.0f;
		float skel = catalog?.GetProgressionForNpc(NPCID.SkeletronHead, 5.0f) ?? 5.0f;
		float deer = catalog?.GetProgressionForNpc(NPCID.Deerclops, 6.0f) ?? 6.0f;
		float qs = catalog?.GetProgressionForNpc(NPCID.QueenSlimeBoss, wof + 1.0f) ?? (wof + 1.0f);
		float dest = catalog?.GetProgressionForNpc(NPCID.TheDestroyer, wof + 3.0f) ?? (wof + 3.0f);
		float twins = catalog?.GetProgressionForNpc(NPCID.Retinazer, wof + 2.0f) ?? (wof + 2.0f);
		float prime = catalog?.GetProgressionForNpc(NPCID.SkeletronPrime, wof + 4.0f) ?? (wof + 4.0f);
		float mechAny = catalog?.GetMechAnyProgression() ?? Math.Min(dest, Math.Min(twins, prime));
		float mechAll = catalog?.GetMechAllProgression() ?? Math.Max(dest, Math.Max(twins, prime));
		float plantera = catalog?.GetProgressionForNpc(NPCID.Plantera, mechAll + 1.0f) ?? (mechAll + 1.0f);
		float golem = catalog?.GetProgressionForNpc(NPCID.Golem, plantera + 1.0f) ?? (plantera + 1.0f);
		float duke = catalog?.GetProgressionForNpc(NPCID.DukeFishron, golem + 1.0f) ?? (golem + 1.0f);
		float empress = catalog?.GetProgressionForNpc(NPCID.HallowBoss, golem + 2.0f) ?? (golem + 2.0f);
		float cultist = catalog?.GetProgressionForNpc(NPCID.CultistBoss, golem + 4.0f) ?? (golem + 4.0f);
		float ml = catalog?.GetProgressionForNpc(NPCID.MoonLordCore, cultist + 1.0f) ?? (cultist + 1.0f);

		if (key.StartsWith("Conditions.NotDowned", StringComparison.Ordinal)
			|| key is "Conditions.NotBloodMoon" or "Conditions.NotSolarEclipse" or "Conditions.NotBloodOrSun"
				or "Conditions.MoonPhasesHalf0OrPreHardmode")
		{
			progression = 0f;
			return true;
		}

		progression = key switch
		{
			"Conditions.DownedKingSlime" => ks + 0.01f,
			"Conditions.DownedEyeOfCthulhu" or "Conditions.DownedEarlygameBoss" => eoc + 0.01f,
			"Conditions.DownedGoblinArmy" => eoc + 0.5f,
			"Conditions.SmashedShadowOrb" => eoc + 0.6f,
			"Conditions.DownedBoss2" or "Conditions.DownedEaterOfWorlds" or "Conditions.DownedBrainOfCthulhu"
				or "Conditions.DownedB2B3HM" => evil + 0.01f,
			"Conditions.DownedQueenBee" => qb + 0.01f,
			"Conditions.DownedSkeletron" or "Conditions.InDungeon" => skel + 0.01f,
			"Conditions.DownedDeerclops" => deer + 0.01f,
			"Conditions.DownedOldOnesArmyT1" or "Conditions.DownedOldOnesArmyAny" => evil + 0.5f,
			"Conditions.InHardmode" => wof + 0.01f,
			"Conditions.BloodMoonOrHardmode" => 1.5f,
			"Conditions.DownedQueenSlime" => qs + 0.01f,
			"Conditions.DownedPirates" or "Conditions.DownedFrostLegion" => wof + 0.5f,
			"Conditions.DownedMechBossAny" => mechAny + 0.01f,
			"Conditions.DownedDestroyer" => dest + 0.01f,
			"Conditions.DownedTwins" => twins + 0.01f,
			"Conditions.DownedSkeletronPrime" => prime + 0.01f,
			"Conditions.DownedMechBossAll" => mechAll + 0.01f,
			"Conditions.DownedOldOnesArmyT2" => mechAny + 0.01f,
			"Conditions.DownedPlantera" or "Conditions.InLihzhardTemple" => plantera + 0.01f,
			"Conditions.DownedGolem" or "Conditions.DownedOldOnesArmyT3" => golem + 0.01f,
			"Conditions.DownedMartians" => golem + 0.76f,
			"Conditions.DownedDukeFishron" => duke + 0.01f,
			"Conditions.DownedMourningWood" or "Conditions.DownedPumpking" or "Conditions.DownedEverscream" or "Conditions.DownedSantaNK1" or "Conditions.DownedIceQueen" => plantera + 1.25f,
			"Conditions.DownedEmpressOfLight" => empress + 0.01f,
			"Conditions.DownedLunaticCultist" => cultist + 0.01f,
			"Conditions.InTowerSolar" or "Conditions.InTowerVortex" or "Conditions.InTowerNebula" or "Conditions.InTowerStardust" => cultist + 0.1f,
			"Conditions.DownedSolarPillar" or "Conditions.DownedVortexPillar" or "Conditions.DownedNebulaPillar" or "Conditions.DownedStardustPillar" => cultist + 0.5f,
			"Conditions.DownedMoonLord" => ml + 0.01f,
			_ => -1f
		};
		hardmodeOverride = progression > wof;
		return progression >= 0f;
	}

	public static ConditionGate EvaluateRecipeConditions(IEnumerable<Condition> conditions, BossCatalog catalog)
	{
		float minProg = 0f;
		bool hardmode = false;
		bool preHardmode = false;
		bool expert = false;
		bool master = false;
		bool classic = false;
		bool crimson = false;
		bool corrupt = false;
		bool unobtainable = false;
		bool seasonal = false;
		bool hasSpecial = false;
		string specialText = null;

		if (conditions == null)
			return default;

		foreach (Condition cond in conditions)
		{
			if (cond == null)
				continue;

			string key = cond.Description?.Key ?? string.Empty;
			string text = cond.Description?.Value ?? string.Empty;

			if (cond == Condition.Hardmode || key == "Conditions.InHardmode")
			{
				hardmode = true;
				minProg = Math.Max(minProg, catalog.WallOfFleshProgression + 0.01f);
			}
			else if (cond == Condition.PreHardmode || key == "Conditions.PreHardmode")
			{
				preHardmode = true;
			}
			else if (cond == Condition.InExpertMode || key == "Conditions.InExpertMode")
			{
				expert = true;
			}
			else if (cond == Condition.InMasterMode || key == "Conditions.InMasterMode")
			{
				master = true;
			}
			else if (cond == Condition.InClassicMode || key == "Conditions.InClassicMode")
			{
				classic = true;
			}
			else if (cond == Condition.CrimsonWorld || key == "Conditions.WorldCrimson")
			{
				crimson = true;
			}
			else if (cond == Condition.CorruptWorld || key == "Conditions.WorldCorrupt")
			{
				corrupt = true;
			}
			else if (TryEvaluateWorldOrSeedRecipeCondition(cond, key, catalog, ref minProg, ref hardmode, ref unobtainable, ref seasonal))
			{
				continue;
			}
			else if (TryResolveKnownConditionKey(key, catalog, out float knownProg, out bool knownHm, out bool knownPreHm))
			{
				minProg = Math.Max(minProg, knownProg);
				if (knownHm)
					hardmode = true;
				if (knownPreHm)
					preHardmode = true;
			}
			else
			{
				float beforeProg = minProg;
				ApplyTextHeuristics(key + " " + text, catalog, ref minProg, ref hardmode, ref preHardmode, ref expert, ref master, ref unobtainable);

				bool isCustomModCond = !string.IsNullOrEmpty(key)
					&& !key.StartsWith("Conditions.", StringComparison.Ordinal)
					&& !key.StartsWith("Terraria.", StringComparison.Ordinal);

				if (isCustomModCond && !IsBenignEnvironmentCondition(key, text))
				{
					hasSpecial = true;
					if (string.IsNullOrWhiteSpace(specialText))
					{
						specialText = !string.IsNullOrWhiteSpace(text) && !text.Contains("Mods.", StringComparison.Ordinal)
							? text
							: TrimConditionKey(key);
					}
				}
				else if (minProg > beforeProg && !string.IsNullOrWhiteSpace(text))
				{
					specialText ??= text;
				}
			}
		}

		if (preHardmode && !hardmode)
			hardmode = false;

		return new ConditionGate
		{
			MinProgression = minProg,
			RequiresHardmode = hardmode,
			PreHardmodeOnly = preHardmode,
			ExpertOnly = expert,
			MasterOnly = master,
			ClassicOnly = classic,
			CrimsonOnly = crimson,
			CorruptOnly = corrupt,
			Unobtainable = unobtainable,
			IsSeasonal = seasonal,
			HasSpecialCondition = hasSpecial,
			SpecialConditionText = specialText ?? string.Empty
		};
	}

	private static bool TryEvaluateWorldOrSeedRecipeCondition(
		Condition cond,
		string key,
		BossCatalog catalog,
		ref float minProg,
		ref bool hardmode,
		ref bool unobtainable,
		ref bool seasonal)
	{
		switch (key)
		{
			case "Conditions.WorldDrunk":
				if (!Main.drunkWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotDrunk":
				if (Main.drunkWorld) unobtainable = true;
				return true;
			case "Conditions.WorldRemix":
				if (!Main.remixWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotRemix":
				if (Main.remixWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotTheBees":
				if (!Main.notTheBeesWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotNotTheBees":
				if (Main.notTheBeesWorld) unobtainable = true;
				return true;
			case "Conditions.WorldForTheWorthy":
				if (!Main.getGoodWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotForTheWorthy":
				if (Main.getGoodWorld) unobtainable = true;
				return true;
			case "Conditions.WorldAnniversary":
				if (!Main.tenthAnniversaryWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotAnniversary":
				if (Main.tenthAnniversaryWorld) unobtainable = true;
				return true;
			case "Conditions.WorldDontStarve":
				if (!Main.dontStarveWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotDontStarve":
				if (Main.dontStarveWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNoTraps":
				if (!Main.noTrapsWorld) unobtainable = true;
				return true;
			case "Conditions.WorldNotNoTraps":
				if (Main.noTrapsWorld) unobtainable = true;
				return true;
			case "Conditions.WorldZenith":
				if (!(Main.remixWorld && Main.getGoodWorld)) unobtainable = true;
				return true;
			case "Conditions.WorldNotZenith":
				if (Main.remixWorld && Main.getGoodWorld) unobtainable = true;
				return true;
			case "Conditions.HardmodeFTW":
				if (!Main.getGoodWorld)
					unobtainable = true;
				else
				{
					hardmode = true;
					minProg = Math.Max(minProg, (catalog?.WallOfFleshProgression ?? 7f) + 0.01f);
				}
				return true;
			case "Conditions.InMultiplayer":
				if (Main.netMode == NetmodeID.SinglePlayer) unobtainable = true;
				return true;
			case "Conditions.Christmas":
				if (!Main.xMas) seasonal = true;
				return true;
			case "Conditions.Halloween":
				if (!Main.halloween) seasonal = true;
				return true;
		}
		return false;
	}

	public static ConditionGate EvaluateDropConditions(IEnumerable<IItemDropRuleCondition> conditions, BossCatalog catalog)
	{
		float minProg = 0f;
		bool hardmode = false;
		bool preHardmode = false;
		bool expert = false;
		bool master = false;
		bool classic = false;
		bool crimson = false;
		bool corrupt = false;
		bool unobtainable = false;
		bool challenge = false;
		bool seasonal = false;
		string specialText = null;

		if (conditions == null)
			return default;

		foreach (IItemDropRuleCondition cond in conditions)
		{
			if (cond == null)
				continue;

			switch (cond)
			{
				case Conditions.NeverTrue:
				case Conditions.NamedNPC:
					unobtainable = true;
					break;
				case Conditions.MechdusaKill:
					if (!(Main.remixWorld && Main.getGoodWorld))
						unobtainable = true;
					else
					{
						hardmode = true;
						minProg = Math.Max(minProg, (catalog?.GetMechAllProgression() ?? (catalog.WallOfFleshProgression + 4f)) + 0.01f);
					}
					break;
				case Conditions.EmpressOfLightIsGenuinelyEnraged:
					challenge = true;
					try
					{
						specialText ??= cond.GetConditionDescription();
					}
					catch
					{
					}
					break;
				case Conditions.RemixSeed:
					if (!Main.remixWorld) unobtainable = true;
					break;
				case Conditions.NotRemixSeed:
					if (Main.remixWorld) unobtainable = true;
					break;
				case Conditions.RemixSeedHardmode:
					if (!Main.remixWorld)
						unobtainable = true;
					else
					{
						hardmode = true;
						minProg = Math.Max(minProg, catalog.WallOfFleshProgression + 0.01f);
					}
					break;
				case Conditions.RemixSeedEasymode:
					if (!Main.remixWorld)
						unobtainable = true;
					else
						preHardmode = true;
					break;
				case Conditions.TenthAnniversaryIsUp:
					if (!Main.tenthAnniversaryWorld) unobtainable = true;
					break;
				case Conditions.TenthAnniversaryIsNotUp:
					if (Main.tenthAnniversaryWorld) unobtainable = true;
					break;
				case Conditions.ZenithSeedIsUp:
					if (!(Main.remixWorld && Main.getGoodWorld)) unobtainable = true;
					break;
				case Conditions.ZenithSeedIsNotUp:
					if (Main.remixWorld && Main.getGoodWorld) unobtainable = true;
					break;
				case Conditions.ForTheWorthyIsUp:
					if (!Main.getGoodWorld) unobtainable = true;
					break;
				case Conditions.ForTheWorthyIsNotUp:
					if (Main.getGoodWorld) unobtainable = true;
					break;
				case Conditions.DrunkWorldIsUp:
					if (!Main.drunkWorld) unobtainable = true;
					break;
				case Conditions.DrunkWorldIsNotUp:
					if (Main.drunkWorld) unobtainable = true;
					break;
				case Conditions.DontStarveIsUp:
					if (!Main.dontStarveWorld) unobtainable = true;
					break;
				case Conditions.DontStarveIsNotUp:
					if (Main.dontStarveWorld) unobtainable = true;
					break;
				case Conditions.BeesSeed:
					if (!Main.notTheBeesWorld) unobtainable = true;
					break;
				case Conditions.NotBeesSeed:
					if (Main.notTheBeesWorld) unobtainable = true;
					break;
				case Conditions.NoTrapsSeed:
					if (!Main.noTrapsWorld) unobtainable = true;
					break;
				case Conditions.NotNoTrapsSeed:
					if (Main.noTrapsWorld) unobtainable = true;
					break;
				case Conditions.HalloweenGoodieBagDrop:
				case Conditions.HalloweenWeapons:
					if (!Main.halloween) seasonal = true;
					break;
				case Conditions.XmasPresentDrop:
				case Conditions.IsChristmas:
					if (!Main.xMas) seasonal = true;
					break;
				case Conditions.IsHardmode:
				case Conditions.SoulOfLight:
				case Conditions.SoulOfNight:
				case Conditions.LivingFlames:
				case Conditions.YoyosAmarok:
				case Conditions.YoyosHelFire:
				case Conditions.PirateMap:
					hardmode = true;
					minProg = Math.Max(minProg, catalog.WallOfFleshProgression + 0.01f);
					break;
				case Conditions.IsPreHardmode:
					preHardmode = true;
					break;
				case Conditions.IsExpert:
				case Conditions.LegacyHack_IsBossAndExpert:
					expert = true;
					break;
				case Conditions.IsMasterMode:
					master = true;
					break;
				case Conditions.NotExpert:
				case Conditions.LegacyHack_IsBossAndNotExpert:
					classic = true;
					break;
				case Conditions.IsCrimson:
				case Conditions.IsCrimsonAndNotExpert:
					crimson = true;
					if (cond is Conditions.IsCrimsonAndNotExpert)
						classic = true;
					break;
				case Conditions.IsCorruption:
				case Conditions.IsCorruptionAndNotExpert:
					corrupt = true;
					if (cond is Conditions.IsCorruptionAndNotExpert)
						classic = true;
					break;
				case Conditions.YoyoCascade:
					preHardmode = true;
					minProg = Math.Max(minProg, catalog.GetProgressionForNpc(NPCID.SkeletronHead, 5f) + 0.01f);
					break;
				case Conditions.BeatAnyMechBoss:
				case Conditions.YoyosYelets:
					hardmode = true;
					minProg = Math.Max(minProg, (catalog?.GetMechAnyProgression() ?? (catalog.WallOfFleshProgression + 2f)) + 0.01f);
					break;
				case Conditions.DownedAllMechBosses:
					hardmode = true;
					minProg = Math.Max(minProg, (catalog?.GetMechAllProgression() ?? (catalog.WallOfFleshProgression + 4f)) + 0.01f);
					break;
				case Conditions.DownedPlantera:
				case Conditions.YoyosKraken:
				case Conditions.HallowKeyCondition:
				case Conditions.JungleKeyCondition:
				case Conditions.CorruptKeyCondition:
				case Conditions.CrimsonKeyCondition:
				case Conditions.FrozenKeyCondition:
				case Conditions.DesertKeyCondition:
					hardmode = true;
					minProg = Math.Max(minProg, catalog.GetProgressionForNpc(NPCID.Plantera, catalog.WallOfFleshProgression + 5f) + 0.01f);
					break;
				case Conditions.IsPumpkinMoon:
				case Conditions.PumpkinMoonDropGatingChance:
				case Conditions.FrostMoonDropGatingChance:
					hardmode = true;
					minProg = Math.Max(minProg, catalog.GetProgressionForNpc(NPCID.Plantera, catalog.WallOfFleshProgression + 5f) + 1.25f);
					break;
				default:
				{
					string typeName = cond.GetType().FullName ?? cond.GetType().Name;
					string desc = string.Empty;
					try
					{
						desc = cond.GetConditionDescription() ?? string.Empty;
					}
					catch
					{
					}
					ApplyTextHeuristics(typeName + " " + desc, catalog, ref minProg, ref hardmode, ref preHardmode, ref expert, ref master, ref unobtainable);
					break;
				}
			}
		}

		if (preHardmode && !hardmode)
			hardmode = false;

		return new ConditionGate
		{
			MinProgression = minProg,
			RequiresHardmode = hardmode,
			PreHardmodeOnly = preHardmode,
			ExpertOnly = expert,
			MasterOnly = master,
			ClassicOnly = classic,
			CrimsonOnly = crimson,
			CorruptOnly = corrupt,
			Unobtainable = unobtainable,
			IsChallengeCondition = challenge,
			IsSeasonal = seasonal,
			HasSpecialCondition = challenge,
			SpecialConditionText = specialText ?? string.Empty
		};
	}

	private static void ApplyTextHeuristics(
		string raw,
		BossCatalog catalog,
		ref float minProg,
		ref bool hardmode,
		ref bool preHardmode,
		ref bool expert,
		ref bool master,
		ref bool unobtainable)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return;

		string text = raw.ToLowerInvariant().Replace('\u00a0', ' ');

		if (text.Contains("before defeating") || text.Contains("not defeated") || text.Contains("notdowned")
			|| text.Contains("not during") || text.Contains("notbloodmoon") || text.Contains("notsolareclipse")
			|| text.Contains("notbloodorsun") || text.Contains("до победы") || text.Contains("не побежд") || text.Contains("не во время"))
		{
			return;
		}

		float wof = catalog?.WallOfFleshProgression ?? 7.0f;
		float skel = catalog?.GetProgressionForNpc(NPCID.SkeletronHead, 5.0f) ?? 5.0f;
		float mech = catalog?.GetMechAnyProgression() ?? (wof + 2.0f);
		float prime = catalog?.GetProgressionForNpc(NPCID.SkeletronPrime, wof + 4.0f) ?? (wof + 4.0f);
		float plantera = catalog?.GetProgressionForNpc(NPCID.Plantera, wof + 5.0f) ?? (wof + 5.0f);
		float golem = catalog?.GetProgressionForNpc(NPCID.Golem, plantera + 1.0f) ?? (plantera + 1.0f);
		float ml = catalog?.GetProgressionForNpc(NPCID.MoonLordCore, golem + 5.0f) ?? (golem + 5.0f);

		if (text.Contains("hardmode") || text.Contains("хардмод") || text.Contains("сложном режиме"))
		{
			if (text.Contains("pre-hardmode") || text.Contains("prehardmode") || text.Contains("before hardmode")
				|| text.Contains("пре-хардмод") || text.Contains("до хардмода"))
			{
				preHardmode = true;
			}
			else
			{
				hardmode = true;
				minProg = Math.Max(minProg, wof + 0.01f);
			}
		}

		if (text.Contains("post-moon") || text.Contains("moon lord") || text.Contains("лунного лорда"))
		{
			hardmode = true;
			minProg = Math.Max(minProg, ml + 0.01f);
		}
		else if (text.Contains("plantera") || text.Contains("плантер"))
		{
			hardmode = true;
			minProg = Math.Max(minProg, plantera + 0.01f);
		}
		else if (text.Contains("golem") || text.Contains("голем"))
		{
			hardmode = true;
			minProg = Math.Max(minProg, golem + 0.01f);
		}
		else if (text.Contains("skeletron") || text.Contains("скелетрон") || text.Contains("dungeon") || text.Contains("темниц"))
		{
			if (!text.Contains("prime") && !text.Contains("прайм"))
				minProg = Math.Max(minProg, skel + 0.01f);
			else
			{
				hardmode = true;
				minProg = Math.Max(minProg, prime + 0.01f);
			}
		}
		else if (text.Contains("mech") || text.Contains("механическ"))
		{
			hardmode = true;
			minProg = Math.Max(minProg, mech + 0.01f);
		}

		if (text.Contains("expert") || text.Contains("эксперт"))
		{
			if (!text.Contains("notexpert") && !text.Contains("not expert") && !text.Contains("не эксперт") && !text.Contains("не в эксперт"))
				expert = true;
		}
		if (text.Contains("master") || text.Contains("мастер"))
		{
			if (!text.Contains("notmaster") && !text.Contains("not master") && !text.Contains("не мастер") && !text.Contains("не в мастер"))
				master = true;
		}

		// Match any modded or vanilla boss name mentioned in progression / forge / quest conditions
		bool looksLikeBossCondition =
			text.Contains("downed") || text.Contains("defeat") || text.Contains("after ") || text.Contains("post")
			|| text.Contains("forged") || text.Contains("repaired") || text.Contains("slain") || text.Contains("kill")
			|| text.Contains("после ") || text.Contains("побед") || text.Contains("убийств") || text.Contains("кован")
			|| text.Contains("выков") || text.Contains("почин") || text.Contains("одол");

		bool isModCondition = text.Contains("mods.") || !IsBenignEnvironmentCondition(raw, raw);

		if ((looksLikeBossCondition || isModCondition) && catalog != null)
		{
			HashSet<string> condTokens = TokenizeIdentifiers(raw);
			foreach (BossStageInfo stage in catalog.AllStages)
			{
				if (MatchesBossStage(text, condTokens, stage))
				{
					minProg = Math.Max(minProg, stage.Progression + 0.01f);
					if (stage.Progression >= catalog.WallOfFleshProgression)
						hardmode = true;
				}
			}
		}
	}

	private static readonly HashSet<string> GenericBossTokens = new(StringComparer.Ordinal)
	{
		"king", "queen", "lord", "slime", "boss", "mini", "event", "spirit", "seed", "wall", "flesh",
		"giant", "dark", "crimson", "corrupt", "corruption", "shadow", "frost", "moon", "torch", "star",
		"void", "iron", "gold", "eye", "brain", "eater", "golem", "emperor", "morning", "fowl", "ancient",
		"duet", "evil", "blood", "goblin", "army", "pirate", "pirates", "martian", "martians", "pumpkin",
		"король", "королева", "лорд", "слиз", "босс", "дух", "семя", "стена", "плоть", "глаз", "мозг",
		"голем", "армия", "луна", "кровав", "император", "утро", "древних"
	};

	private static bool MatchesBossStage(string rawLower, HashSet<string> condTokens, BossStageInfo stage)
	{
		if (!string.IsNullOrWhiteSpace(stage.DisplayName) && stage.DisplayName.Length >= 4)
		{
			string dispLower = stage.DisplayName.ToLowerInvariant();
			if (rawLower.Contains(dispLower))
				return true;

			// Also match distinctive >=5 char stem of localized boss name (handles Russian case endings e.g. "Калавия" -> "Калавией")
			foreach (string word in TokenizeIdentifiers(stage.DisplayName))
			{
				if (word.Length >= 5 && !GenericBossTokens.Contains(word))
				{
					string stem = word[..Math.Min(word.Length - 1, Math.Max(5, word.Length - 2))];
					if (rawLower.Contains(stem))
						return true;
				}
			}
		}

		// Match internal key / NPC class name tokens (e.g. "Redemption Calavia", "Erhan", "EaglecrestGolem")
		foreach (string keyTok in TokenizeIdentifiers(stage.Key))
		{
			if (keyTok.Length < 5 || GenericBossTokens.Contains(keyTok))
				continue;
			if (!string.IsNullOrEmpty(stage.ModSource) && string.Equals(keyTok, stage.ModSource, StringComparison.OrdinalIgnoreCase))
				continue;
			if (condTokens.Contains(keyTok))
				return true;
		}

		foreach (int npcId in stage.NpcIds)
		{
			string internalName = ContentSamples.NpcsByNetId.TryGetValue(npcId, out NPC n) && n?.ModNPC != null
				? n.ModNPC.Name
				: NPCID.Search.TryGetName(npcId, out string vName) ? vName : null;

			if (!string.IsNullOrWhiteSpace(internalName))
			{
				foreach (string npcTok in TokenizeIdentifiers(internalName))
				{
					if (npcTok.Length >= 5 && !GenericBossTokens.Contains(npcTok) && condTokens.Contains(npcTok))
						return true;
				}
			}
		}

		return false;
	}

	private static HashSet<string> TokenizeIdentifiers(string input)
	{
		var tokens = new HashSet<string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(input))
			return tokens;

		var sb = new StringBuilder(32);
		for (int i = 0; i < input.Length; i++)
		{
			char c = input[i];
			if (!char.IsLetterOrDigit(c))
			{
				if (sb.Length > 0)
				{
					tokens.Add(sb.ToString());
					sb.Clear();
				}
				continue;
			}

			if (sb.Length > 0 && char.IsUpper(c) && char.IsLower(input[i - 1]))
			{
				tokens.Add(sb.ToString());
				sb.Clear();
			}
			sb.Append(char.ToLowerInvariant(c));
		}

		if (sb.Length > 0)
			tokens.Add(sb.ToString());

		return tokens;
	}

	private static bool IsBenignEnvironmentCondition(string key, string text)
	{
		string k = (key + " " + text).ToLowerInvariant().Replace('\u00a0', ' ');
		return k.Contains("nearwater") || k.Contains("nearlava") || k.Contains("nearhoney") || k.Contains("nearshimmer")
			|| k.Contains("inwater") || k.Contains("inhoney") || k.Contains("inrain") || k.Contains("timeday")
			|| k.Contains("timenight") || k.Contains("graveyard") || k.Contains("alchemy")
			|| k.Contains("notbloodmoon") || k.Contains("notsolareclipse") || k.Contains("notbloodorsun")
			|| k.Contains("moonphase") || k.Contains("npcis") || k.Contains("bestiary") || k.Contains("golf")
			|| k.Contains("angler") || k.Contains("happy") || k.Contains("windy") || k.Contains("birthday")
			|| k.Contains("lantern") || k.Contains("shimmered") || k.Contains("notdowned")
			|| k.Contains("в воде") || k.Contains("рядом с водой") || k.Contains("рядом с лавой")
			|| k.Contains("рядом с мёдом") || k.Contains("кладбищ") || k.Contains("днём") || k.Contains("ночью")
			|| k.Contains("не во время");
	}

	private static string TrimConditionKey(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
			return string.Empty;
		int lastDot = key.LastIndexOf('.');
		return lastDot >= 0 && lastDot + 1 < key.Length ? key[(lastDot + 1)..] : key;
	}
}
