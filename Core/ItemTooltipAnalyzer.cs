using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

/// <summary>
/// Extracts bilingual (EN/RU) semantic combat signals from item tooltips and properties
/// so accessories, armor pieces, and potions from arbitrary mods can be categorized and scored.
/// </summary>
public static class ItemTooltipAnalyzer
{
	private static readonly Regex PercentRegex = new(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

	private static readonly string[] WingsKeywords =
	[
		"allows flight", "flight time", "wings", "wing",
		"позволяет летать", "время полёта", "время полета", "крылья"
	];

	private static readonly string[] BootsKeywords =
	[
		"rocket boots", "sprint", "run speed", "movement speed", "fast on", "ice skates", "water walking", "lava",
		"скорость передвижения", "быстро бегать", "спринт", "реактивн", "сапог", "хождение по воде"
	];

	private static readonly string[] DashKeywords =
	[
		"dash", "double tap", "dodge", "immune", "invincibility", "shadow dodge",
		"рывок", "двойное нажатие", "уклон", "неуязвим"
	];

	private static readonly string[] SurvivabilityKeywords =
	[
		"defense", "damage reduction", "reduces damage taken", "life regeneration", "max life", "endurance", "shield", "potion cooldown",
		"защит", "уменьшает получаемый урон", "снижает получаемый урон", "регенерац", "восстановление здоровья", "максимальное здоровье", "щит", "перезарядку зел"
	];

	private static readonly string[] ManaKeywords =
	[
		"mana", "magic", "мана", "маны", "ману", "мане", "маной", "магич"
	];

	private static readonly string[] MeleeKeywords =
	[
		"melee", "whip", "true melee", "ближн", "кнут", "хлыст"
	];

	private static readonly string[] RangedKeywords =
	[
		"ranged", "ammo", "arrow", "bullet", "bow", "gun",
		"дальнего боя", "дальний бой", "стрелков", "стрелами", "стрельб", "арбалет", "пули", "пуль", "патрон", "боеприпас"
	];

	private static readonly string[] MagicKeywords =
	[
		"magic", "mana", "spell", "магич", "мана", "маны", "ману", "мане", "маной", "заклинан"
	];

	private static readonly string[] SummonKeywords =
	[
		"summon", "minion", "sentry", "whip", "призыв", "миньон", "страж", "турел", "хлыст"
	];

	private static readonly string[] GenericCombatKeywords =
	[
		"all damage", "damage", "critical strike", "attack speed", "armor penetration",
		"весь урон", "урон", "критическ", "скорость атаки", "пробивание брони"
	];

	private static readonly string[] NonCombatUtilityKeywords =
	[
		"tile placement", "block placement", "placement speed", "tile range", "block range",
		"mining speed", "pickaxe speed", "fishing power", "fishing skill", "bait", "critters",
		"pickup range", "grab range", "wall placement", "wire", "paint",
		"displays ", "shows ", "depth", "moon phase", "weather", "dps", "rare creatures", "treasured",
		"скорость размещения", "установки блоков", "размещения блоков", "скорость строительства",
		"скорость копания", "скорость добычи", "мощность рыбалки", "навык рыбалки", "наживк",
		"дальность подбора", "дальность сбора", "живност", "покраск", "стен",
		"показывает ", "отображает ", "глубин", "фаз", "погод", "редких существ"
	];

	public static string GetTooltipText(Item item)
	{
		if (item == null || item.IsAir)
			return string.Empty;

		var parts = new List<string>();
		if (item.ToolTip != null && item.ToolTip.Lines > 0)
		{
			for (int i = 0; i < item.ToolTip.Lines; i++)
			{
				string line = item.ToolTip.GetLine(i);
				if (!string.IsNullOrWhiteSpace(line))
					parts.Add(line);
			}
		}

		string modTip = item.ModItem?.Tooltip?.Value;
		if (!string.IsNullOrWhiteSpace(modTip) && parts.Count == 0)
			parts.Add(modTip);

		return string.Join(" | ", parts);
	}

	public static ItemSemanticProfile Analyze(Item item)
	{
		string tip = GetTooltipText(item);
		string lower = (item.Name + " " + tip).ToLowerInvariant();

		bool hasGenericCombat = ContainsAny(lower, GenericCombatKeywords);

		var profile = new ItemSemanticProfile
		{
			ItemType = item.type,
			CombinedTooltipText = tip,
			IsWings = item.wingSlot > 0 || ContainsAny(lower, WingsKeywords),
			IsMobilityBoots = ContainsAny(lower, BootsKeywords),
			IsDashOrDodge = ContainsAny(lower, DashKeywords),
			IsSurvivability = ContainsAny(lower, SurvivabilityKeywords) || item.defense > 0,
			IsManaUtility = ContainsAny(lower, ManaKeywords),
			IsCombatBuffPotion = item.buffType > 0 && item.buffTime > 0 && item.consumable && IsCombatPotion(lower, item.buffType),
			HasGenericCombat = hasGenericCombat
		};

		float maxPct = ExtractMaxPercent(lower);
		float pctBoost = Math.Clamp(maxPct, 0f, 60f) * 0.6f;

		if (hasGenericCombat && !IsClassSpecific(lower))
			profile.GenericBonusScore += 18f + pctBoost;

		if (profile.IsSurvivability)
			profile.GenericBonusScore += 8f + item.defense * 1.5f;

		if (!hasGenericCombat && !profile.IsWings && !profile.IsMobilityBoots && !profile.IsDashOrDodge && item.defense < 2
			&& ContainsAny(lower, NonCombatUtilityKeywords))
		{
			profile.IsPureNonCombatUtility = !IsClassSpecific(lower);
		}

		// Estimate bonus minion/sentry slots
		if (lower.Contains("minion") || lower.Contains("миньон"))
		{
			if (lower.Contains("max number of minions") || lower.Contains("increases your max number") || lower.Contains("слот") || lower.Contains("максимальное количество миньонов"))
				profile.SummonSlotBonus += 1f;
		}
		if (lower.Contains("sentry") || lower.Contains("страж") || lower.Contains("турел"))
		{
			if (lower.Contains("max number") || lower.Contains("слот") || lower.Contains("максимальное количество"))
				profile.SentrySlotBonus += 1f;
		}

		// Score every loaded DamageClass (vanilla + any modded classes like Rogue/Bard/Druid/etc.)
		for (int i = 0; i < DamageClassLoader.DamageClassCount; i++)
		{
			DamageClass dc = DamageClassLoader.GetDamageClass(i);
			if (dc == null || dc == DamageClass.Default)
				continue;

			float affinity = ComputeClassAffinity(lower, dc, pctBoost, profile);
			if (affinity > 0f)
				profile.ClassAffinityScores[dc.Type] = affinity;
		}

		return profile;
	}

	private static float ComputeClassAffinity(string lower, DamageClass dc, float pctBoost, ItemSemanticProfile profile)
	{
		float score = 0f;

		if (dc.CountsAsClass(DamageClass.Melee) && ContainsAny(lower, MeleeKeywords))
			score += 24f + pctBoost;
		else if (dc.CountsAsClass(DamageClass.Ranged) && ContainsAny(lower, RangedKeywords))
			score += 24f + pctBoost;
		else if (dc.CountsAsClass(DamageClass.Magic) && ContainsAny(lower, MagicKeywords))
			score += 24f + pctBoost;
		else if (dc.CountsAsClass(DamageClass.Summon) && ContainsAny(lower, SummonKeywords))
			score += 24f + pctBoost + profile.SummonSlotBonus * 18f + profile.SentrySlotBonus * 8f;

		// Dynamic match for any custom mod DamageClass by its internal name or localized display name
		string internalWord = CleanClassToken(dc.Name);
		string displayWord = CleanClassToken(dc.DisplayName?.Value);

		if (internalWord.Length >= 3 && lower.Contains(internalWord))
			score += 26f + pctBoost;
		else if (displayWord.Length >= 3 && lower.Contains(displayWord))
			score += 26f + pctBoost;

		return score;
	}

	private static string CleanClassToken(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return string.Empty;

		string s = raw.Trim().ToLowerInvariant();
		s = s.Replace("damageclass", "").Replace("damage", "").Replace("урон", "").Trim();
		return s;
	}

	private static bool IsClassSpecific(string lower)
	{
		return ContainsAny(lower, MeleeKeywords)
			|| ContainsAny(lower, RangedKeywords)
			|| ContainsAny(lower, MagicKeywords)
			|| ContainsAny(lower, SummonKeywords);
	}

	private static bool IsCombatPotion(string lower, int buffType)
	{
		// Skip cosmetic/building/fishing-only buffs when recommending boss fight potions
		if (buffType is BuffID.Builder or BuffID.Mining or BuffID.Fishing or BuffID.Sonar or BuffID.Crate
			or BuffID.Shine or BuffID.NightOwl or BuffID.Spelunker or BuffID.Hunter or BuffID.Dangersense
			or BuffID.Gills or BuffID.Flipper or BuffID.WaterWalking or BuffID.ObsidianSkin or BuffID.Featherfall
			or BuffID.Gravitation or BuffID.Invisibility or BuffID.Calm or BuffID.Battle or BuffID.BiomeSight)
			return false;

		if (buffType is BuffID.Ironskin or BuffID.Regeneration or BuffID.Swiftness or BuffID.Endurance
			or BuffID.Lifeforce or BuffID.Rage or BuffID.Wrath or BuffID.Archery or BuffID.MagicPower
			or BuffID.ManaRegeneration or BuffID.Summoning or BuffID.Thorns or BuffID.Titan or BuffID.Heartreach
			or BuffID.Warmth or BuffID.Inferno or BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3)
			return true;

		return ContainsAny(lower, GenericCombatKeywords)
			|| ContainsAny(lower, SurvivabilityKeywords)
			|| ContainsAny(lower, MeleeKeywords)
			|| ContainsAny(lower, RangedKeywords)
			|| ContainsAny(lower, MagicKeywords)
			|| ContainsAny(lower, SummonKeywords);
	}

	private static float ExtractMaxPercent(string lower)
	{
		float best = 0f;
		foreach (Match m in PercentRegex.Matches(lower))
		{
			string numStr = m.Groups[1].Value.Replace(',', '.');
			if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
				best = Math.Max(best, v);
		}
		return best;
	}

	public static bool ContainsAny(string text, string[] terms)
	{
		for (int i = 0; i < terms.Length; i++)
		{
			if (text.Contains(terms[i]))
				return true;
		}
		return false;
	}

	public static string FormatClassDisplayName(DamageClass dc)
	{
		if (dc == null)
			return "Auto";

		bool ru = Terraria.Localization.Language.ActiveCulture?.Name?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
		if (dc == DamageClass.Melee || dc == DamageClass.MeleeNoSpeed)
			return ru ? "Ближний бой" : "Melee";
		if (dc == DamageClass.Ranged)
			return ru ? "Стрелок" : "Ranged";
		if (dc == DamageClass.Magic)
			return ru ? "Маг" : "Magic";
		if (dc == DamageClass.Summon || dc == DamageClass.SummonMeleeSpeed)
			return ru ? "Призыватель" : "Summoner";
		if (dc == DamageClass.Throwing)
			return ru ? "Метатель" : "Throwing";

		string raw = dc.DisplayName?.Value ?? dc.Name ?? "Class";
		string s = raw.Trim()
			.Replace("ед.", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урона", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урон", "", StringComparison.OrdinalIgnoreCase)
			.Replace("damage", "", StringComparison.OrdinalIgnoreCase)
			.Trim();
		if (string.IsNullOrWhiteSpace(s))
			s = dc.Name ?? "Class";
		if (s.Length > 1)
			s = char.ToUpperInvariant(s[0]) + s[1..];
		return s;
	}
}
