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
	private static readonly Regex MaxManaRegex = new(@"(?:max(?:imum)?\s+mana\s+by|(?:максимум|запас)\s+ман+ы\s+на)\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex PositiveDefenseRegex = new(@"(?:\+\s*\d+\s*(?:defense|к\s+защите|защиты)|(?:increases?|grants?|gain|увеличивает|да[её]т|повышает)\s+(?:your\s+|владельцу\s+)?(?:defense|защиту))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex RangedWordRegex = new(@"(?<![\p{L}])(?:bow|bows|gun|guns|musket|archery|firearm|пуля|пули|пуль|пулями|лук|луки|луков|лука|луком|стрела|стрелы|стрел|стрелами)(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex MinionCountRegex = new(@"(?:max(?:imum)?\s+number\s+of\s+minions|максимальн[а-яё]*\s+(?:число|количество)\s+(?:миньонов|прислужников))[^\d\n|]{0,20}(\d+)|\+(\d+)\s*(?:max\s+)?(?:minion|миньон|прислужник)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex SentryCountRegex = new(@"(?:max(?:imum)?\s+number\s+of\s+sentr(?:y|ies)|максимальн[а-яё]*\s+(?:число|количество)\s+(?:стражей|турелей|часовых))[^\d\n|]{0,20}(\d+)|\+(\d+)\s*(?:max\s+)?(?:sentr(?:y|ies)|страж|турел|часов)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

	private static readonly string[] NonOffensiveDamagePhrases =
	[
		"reduces damage taken", "damage taken", "fall damage", "damage from falling",
		"lava damage", "trap damage", "damage from traps", "contact damage", "touch damage",
		"after taking damage", "when damaged", "when taking damage",
		"получаемый урон", "получаемого урона", "урон от падения", "урона от падения",
		"урон при падении", "урон от лавы", "урона от лавы", "урона от ловушек",
		"урон при контакте", "после получения урона", "при получении урона"
	];

	private static readonly string[] ArmorPenStripPhrases =
	[
		"ignores enemy defense", "ignores defense", "reduces enemy defense",
		"игнорирует защиту", "снижает защиту враг", "уменьшает защиту враг"
	];

	private static readonly string[] BootsKeywords =
	[
		"rocket boots", "sprint", "run super fast", "run extremely fast", "run insanely fast",
		"fast on", "ice skates", "water walking", "boots", "treads",
		"очень быстро бегать", "быстро бегать", "сверхбыстр", "спринт",
		"реактивн", "сапог", "ботин", "протектор", "хождение по воде", "ходить по воде"
	];

	private static readonly string[] DashKeywords =
	[
		"dash", "double tap", "dodge", "shadow dodge",
		"рывок", "двойное нажатие", "дважды нажмите", "дважды коснитесь",
		"броситься", "бросаться", "уклон"
	];

	private static readonly string[] SurvivabilityKeywords =
	[
		"damage reduction", "reduces damage taken", "life regeneration", "max life",
		"endurance", "shield", "potion cooldown", "invincibility", "immunity",
		"уменьшает получаемый урон", "снижает получаемый урон", "регенерац",
		"восстановление здоровья", "максимальное здоровье", "щит", "перезарядку зел",
		"неуязвим", "иммунитет"
	];

	private static readonly string[] ManaKeywords =
	[
		"mana", "magic", "мана", "маны", "ману", "мане", "маной", "магич"
	];

	private static readonly string[] MeleeKeywords =
	[
		"melee", "true melee", "ближн", "истинный ближн", "тяжёлый ближн", "тяжелый ближн"
	];

	private static readonly string[] RangedKeywords =
	[
		"ranged", "archery", "ammo", "arrow", "bullet",
		"дистанцион", "дальнего боя", "дальний бой", "стрелков", "стрел", "лучник",
		"стрельб", "арбалет", "патрон", "боеприпас"
	];

	private static readonly string[] MagicKeywords =
	[
		"magic", "mana", "spell", "магич", "мана", "маны", "ману", "мане", "маной", "заклинан"
	];

	private static readonly string[] SummonKeywords =
	[
		"summon", "minion", "sentry", "sentries", "whip",
		"призыв", "миньон", "прислужник", "питомц", "питомец",
		"страж", "турел", "часов", "кнут"
	];

	private static readonly string[] GenericCombatKeywords =
	[
		"all damage", "damage", "critical strike", "attack speed", "armor penetration",
		"весь урон", "урон", "критическ", "скорость атаки", "пробивание брони", "пробивание броне"
	];

	private static readonly string[] NonCombatUtilityKeywords =
	[
		"tile placement", "block placement", "placement speed", "tile range", "block range",
		"mining speed", "pickaxe speed", "fishing power", "fishing skill", "bait", "critters",
		"pickup range", "grab range", "wall placement", "wire", "paint sprayer", "painting",
		"displays ", "shows ", "depth", "moon phase", "weather", "dps", "rare creatures", "treasured",
		"скорость размещения", "установки блоков", "размещения блоков", "скорость строительства",
		"размещения стен", "установки стен", "скорость копания", "скорость добычи",
		"мощность рыбалки", "навык рыбалки", "наживк",
		"дальность подбора", "дальность сбора", "живност", "покраск",
		"показывает ", "отображает ", "глубины", "глубину", "фазу луны", "погод", "редких существ"
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
		string nameAndTip = (item.Name + " " + tip).Trim();
		bool isBuffPotion = !item.potion && item.buffType > 0 && item.buffTime > 0 && item.consumable
			&& IsCombatPotion(NormalizeLower(nameAndTip), item.buffType);

		return AnalyzeCore(nameAndTip, tip, item.type, item.defense, item.wingSlot > 0, isBuffPotion, item.buffType);
	}

	public static ItemSemanticProfile AnalyzeSetBonus(string setBonusText, int totalDefense = 0)
	{
		return AnalyzeCore(setBonusText ?? string.Empty, setBonusText ?? string.Empty, ItemID.None, totalDefense, false, false, 0);
	}

	private static ItemSemanticProfile AnalyzeCore(string nameAndText, string rawTip, int itemType, int defense, bool isWings, bool isCombatBuffPotion, int buffType)
	{
		string lower = NormalizeLower(nameAndText);
		string offensiveLower = StripPhrases(lower, NonOffensiveDamagePhrases);
		string defenseLower = StripPhrases(lower, ArmorPenStripPhrases);

		bool hasGenericCombat = ContainsAny(offensiveLower, GenericCombatKeywords);

		var profile = new ItemSemanticProfile
		{
			ItemType = itemType,
			CombinedTooltipText = rawTip ?? string.Empty,
			// Only real equipped wings set wingSlot > 0; rocket/running boots with "Allows flight" text must remain Mobility Boots
			IsWings = isWings,
			IsMobilityBoots = ContainsAny(lower, BootsKeywords),
			IsDashOrDodge = ContainsAny(lower, DashKeywords),
			IsSurvivability = ContainsAny(lower, SurvivabilityKeywords) || defense > 0 || PositiveDefenseRegex.IsMatch(defenseLower),
			IsManaUtility = ContainsAny(lower, ManaKeywords),
			IsCombatBuffPotion = isCombatBuffPotion,
			HasGenericCombat = hasGenericCombat,
			SummonSlotBonus = ExtractSlotCount(lower, MinionCountRegex, "minion", "миньон", "прислужник"),
			SentrySlotBonus = ExtractSlotCount(lower, SentryCountRegex, "sentr", "страж", "турел", "часов")
		};

		float maxPct = ExtractMaxPercent(offensiveLower);
		float pctBoost = Math.Clamp(maxPct, 0f, 60f) * 0.6f;

		// Score every loaded DamageClass (vanilla + any modded classes like Rogue/Bard/Druid/etc.)
		for (int i = 0; i < DamageClassLoader.DamageClassCount; i++)
		{
			DamageClass dc = DamageClassLoader.GetDamageClass(i);
			if (dc == null || dc == DamageClass.Default)
				continue;

			float affinity = ComputeClassAffinity(offensiveLower, lower, dc, pctBoost, profile, buffType);
			if (affinity > 0f)
				profile.ClassAffinityScores[dc.Type] = affinity;
		}

		bool isClassSpecific = profile.ClassAffinityScores.Count > 0;
		if (hasGenericCombat && !isClassSpecific)
			profile.GenericBonusScore += 18f + pctBoost;

		if (profile.IsSurvivability)
			profile.GenericBonusScore += 8f + defense * 1.5f;

		if (!hasGenericCombat && !isClassSpecific && !profile.IsWings && !profile.IsMobilityBoots && !profile.IsDashOrDodge && !profile.IsSurvivability && defense < 2
			&& ContainsAny(lower, NonCombatUtilityKeywords))
		{
			profile.IsPureNonCombatUtility = true;
		}

		return profile;
	}

	private static string NormalizeLower(string text)
	{
		if (string.IsNullOrEmpty(text))
			return string.Empty;
		return text.Replace('\u00a0', ' ').Replace('\u202f', ' ').ToLowerInvariant();
	}

	private static string StripPhrases(string text, string[] phrases)
	{
		string s = text;
		for (int i = 0; i < phrases.Length; i++)
		{
			if (s.Contains(phrases[i]))
				s = s.Replace(phrases[i], " ");
		}
		return s;
	}

	private static float ExtractSlotCount(string lower, Regex regex, params string[] fallbackKeywords)
	{
		Match m = regex.Match(lower);
		if (m.Success)
		{
			for (int g = 1; g < m.Groups.Count; g++)
			{
				if (m.Groups[g].Success && int.TryParse(m.Groups[g].Value, out int parsed) && parsed > 0)
					return parsed;
			}
		}

		bool hasEntity = ContainsAny(lower, fallbackKeywords);
		if (!hasEntity)
			return 0f;

		if (lower.Contains("max number") || lower.Contains("слот") || lower.Contains("максимальное число") || lower.Contains("максимальное количество"))
			return 1f;

		return 0f;
	}

	private static float ComputeClassAffinity(string offensiveLower, string fullLower, DamageClass dc, float pctBoost, ItemSemanticProfile profile, int buffType)
	{
		float score = 0f;

		if (dc == DamageClass.Melee || dc == DamageClass.MeleeNoSpeed)
		{
			if (ContainsAny(offensiveLower, MeleeKeywords) || buffType == BuffID.Tipsy)
				score += 24f + pctBoost;
		}
		else if (dc == DamageClass.Ranged)
		{
			if (ContainsAny(offensiveLower, RangedKeywords) || RangedWordRegex.IsMatch(offensiveLower) || buffType == BuffID.Archery)
				score += 24f + pctBoost;
		}
		else if (dc == DamageClass.Magic)
		{
			if (ContainsAny(offensiveLower, MagicKeywords) || buffType is BuffID.MagicPower or BuffID.ManaRegeneration)
			{
				float manaBonus = Math.Clamp(ExtractMaxMana(fullLower) * 0.2f, 0f, 12f);
				if (fullLower.Contains("mana usage") || fullLower.Contains("mana cost") || fullLower.Contains("costs 0 mana")
					|| fullLower.Contains("в мане") || fullLower.Contains("расход маны") || fullLower.Contains("не расходует ману"))
					manaBonus += 6f;
				score += 24f + pctBoost + manaBonus;
			}
		}
		else if (dc == DamageClass.Summon || dc == DamageClass.SummonMeleeSpeed)
		{
			if (ContainsAny(offensiveLower, SummonKeywords) || profile.SummonSlotBonus > 0f || profile.SentrySlotBonus > 0f || buffType == BuffID.Summoning)
				score += 24f + pctBoost + profile.SummonSlotBonus * 18f + profile.SentrySlotBonus * 8f;
		}
		else if (dc != DamageClass.Generic && dc != DamageClass.Default)
		{
			// Dynamic match for any custom mod DamageClass (or vanilla Throwing) by internal name, display name, or stem
			string internalWord = CleanClassToken(dc.Name);
			string displayWord = CleanClassToken(dc.DisplayName?.Value);
			string displayStem = TrimRussianAdjectiveStem(displayWord);

			if ((internalWord.Length >= 3 && offensiveLower.Contains(internalWord))
				|| (displayWord.Length >= 3 && offensiveLower.Contains(displayWord))
				|| (displayStem.Length >= 4 && offensiveLower.Contains(displayStem)))
			{
				score += 26f + pctBoost;
			}
		}

		return score;
	}

	private static string CleanClassToken(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return string.Empty;

		string s = NormalizeLower(raw).Trim();
		s = s.Replace("damageclass", "").Replace("damage", "")
			.Replace("ед.", "").Replace("урона", "").Replace("урон", "")
			.Trim();
		return s;
	}

	private static string TrimRussianAdjectiveStem(string s)
	{
		if (string.IsNullOrWhiteSpace(s) || s.Length < 6)
			return s;

		string first = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
		if (first.Length >= 6 && (first.EndsWith("ого") || first.EndsWith("его") || first.EndsWith("ой") || first.EndsWith("ей")))
			return first[..^3];
		return first;
	}

	private static bool IsCombatPotion(string lower, int buffType)
	{
		// Skip cosmetic, confetti/gold flasks, building, and fishing buffs when recommending boss fight potions
		if (buffType is BuffID.Builder or BuffID.Mining or BuffID.Fishing or BuffID.Sonar or BuffID.Crate
			or BuffID.Shine or BuffID.NightOwl or BuffID.Spelunker or BuffID.Hunter or BuffID.Dangersense
			or BuffID.Gills or BuffID.Flipper or BuffID.WaterWalking or BuffID.ObsidianSkin or BuffID.Featherfall
			or BuffID.Gravitation or BuffID.Invisibility or BuffID.Calm or BuffID.Battle or BuffID.BiomeSight
			or BuffID.WeaponImbueConfetti or BuffID.WeaponImbueGold or BuffID.Stinky or BuffID.Lovestruck)
			return false;

		if (buffType is BuffID.Ironskin or BuffID.Regeneration or BuffID.Swiftness or BuffID.Endurance
			or BuffID.Lifeforce or BuffID.Rage or BuffID.Wrath or BuffID.Archery or BuffID.MagicPower
			or BuffID.ManaRegeneration or BuffID.Summoning or BuffID.Thorns or BuffID.Titan or BuffID.Heartreach
			or BuffID.Warmth or BuffID.Inferno or BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3)
			return true;

		return ContainsAny(lower, GenericCombatKeywords)
			|| ContainsAny(lower, SurvivabilityKeywords)
			|| ContainsAny(lower, MeleeKeywords)
			|| ContainsAny(lower, RangedKeywords) || RangedWordRegex.IsMatch(lower)
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

	private static float ExtractMaxMana(string lower)
	{
		float total = 0f;
		foreach (Match m in MaxManaRegex.Matches(lower))
		{
			if (float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
				total += v;
		}
		return total;
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
			return Terraria.Localization.Language.GetTextValue("Mods.BossBuildAdvisor.UI.ClassAuto");

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

		string raw = (dc.DisplayName?.Value ?? dc.Name ?? "Class").Replace('\u00a0', ' ').Replace('\u202f', ' ');
		string s = raw.Trim()
			.Replace("ед.", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урона", "", StringComparison.OrdinalIgnoreCase)
			.Replace("урон", "", StringComparison.OrdinalIgnoreCase)
			.Replace("damage", "", StringComparison.OrdinalIgnoreCase)
			.Trim();
		if (ru && !string.IsNullOrWhiteSpace(s))
		{
			string[] words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < words.Length; i++)
			{
				string w = words[i];
				if (w.Length >= 6 && w.EndsWith("ого", StringComparison.OrdinalIgnoreCase))
					words[i] = w[..^3] + "ый";
				else if (w.Length >= 6 && w.EndsWith("его", StringComparison.OrdinalIgnoreCase))
					words[i] = w[..^3] + "ий";
			}
			s = string.Join(' ', words);
		}
		if (string.IsNullOrWhiteSpace(s))
			s = dc.Name ?? "Class";
		if (s.Length > 1)
			s = char.ToUpperInvariant(s[0]) + s[1..];
		return s;
	}
}
