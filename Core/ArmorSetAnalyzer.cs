using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

public sealed class ArmorSetDefinition
{
	public int HeadType { get; init; }
	public int BodyType { get; init; }
	public int LegsType { get; init; }
	public int TotalDefense { get; init; }
	public string ModName { get; init; } = "Terraria";
	public string SetBonusText { get; init; } = string.Empty;
	public bool IsWeaponSpecificBonus { get; init; }
	public ItemSemanticProfile CombinedProfile { get; init; }
}

/// <summary>
/// Discovers valid vanilla and modded armor sets once at load time without executing unsafe player hooks.
/// </summary>
public sealed class ArmorSetAnalyzer
{
	public List<ArmorSetDefinition> Sets { get; } = new();

	public void Build(Mod mod, ProgressionGraph graph)
	{
		Sets.Clear();

		var headsByMod = new Dictionary<string, List<Item>>();
		var bodiesByMod = new Dictionary<string, List<Item>>();
		var legsByMod = new Dictionary<string, List<Item>>();

		foreach (int itemId in graph.Items.Keys)
		{
			if (!ContentSamples.ItemsByType.TryGetValue(itemId, out Item item) || item.vanity)
				continue;

			string modName = item.ModItem?.Mod?.Name ?? "Terraria";
			if (item.headSlot >= 0) AddToBucket(headsByMod, modName, item);
			if (item.bodySlot >= 0) AddToBucket(bodiesByMod, modName, item);
			if (item.legSlot >= 0) AddToBucket(legsByMod, modName, item);
		}

		DiscoverVanillaSets(
			headsByMod.GetValueOrDefault("Terraria"),
			bodiesByMod.GetValueOrDefault("Terraria"),
			legsByMod.GetValueOrDefault("Terraria"));

		foreach (string modName in headsByMod.Keys)
		{
			if (modName == "Terraria")
				continue;

			DiscoverModdedSets(
				modName,
				headsByMod.GetValueOrDefault(modName),
				bodiesByMod.GetValueOrDefault(modName),
				legsByMod.GetValueOrDefault(modName));
		}

		mod.Logger.Info($"[BossBuildAdvisor] Discovered {Sets.Count} armor sets across loaded mods.");
	}

	private void DiscoverVanillaSets(List<Item> heads, List<Item> bodies, List<Item> legs)
	{
		if (heads == null || bodies == null || legs == null)
			return;

		var bodyBySlot = bodies.GroupBy(x => x.bodySlot).ToDictionary(g => g.Key, g => g.ToList());
		var legsBySlot = legs.GroupBy(x => x.legSlot).ToDictionary(g => g.Key, g => g.ToList());

		foreach (Item h in heads)
		{
			foreach (Item b in bodies)
			{
				if (Math.Abs(h.rare - b.rare) > 3)
					continue;

				foreach (Item l in legs)
				{
					if (Math.Abs(b.rare - l.rare) > 3)
						continue;

					string bonus = GetVanillaArmorSetBonus(h.headSlot, b.bodySlot, l.legSlot);
					if (bonus != null)
					{
						AddSet(h, b, l, "Terraria", bonus);
					}
				}
			}
		}
	}

	private void DiscoverModdedSets(string modName, List<Item> heads, List<Item> bodies, List<Item> legs)
	{
		if (heads == null || bodies == null || legs == null)
			return;

		foreach (Item h in heads)
		{
			foreach (Item b in bodies)
			{
				if (Math.Abs(h.rare - b.rare) > 2)
					continue;

				foreach (Item l in legs)
				{
					if (Math.Abs(b.rare - l.rare) > 2)
						continue;

					if (IsModArmorSetSafe(h, b, l))
					{
						AddSet(h, b, l, modName, string.Empty);
					}
				}
			}
		}
	}

	private static bool IsModArmorSetSafe(Item head, Item body, Item legs)
	{
		try
		{
			if (head.ModItem != null && head.ModItem.IsArmorSet(head, body, legs))
				return true;
			if (body.ModItem != null && body.ModItem.IsArmorSet(head, body, legs))
				return true;
			if (legs.ModItem != null && legs.ModItem.IsArmorSet(head, body, legs))
				return true;
		}
		catch
		{
		}
		return false;
	}

	private void AddSet(Item head, Item body, Item legs, string modName, string setBonusText)
	{
		var combinedItem = new Item();
		string combinedText = string.Join(" | ",
			ItemTooltipAnalyzer.GetTooltipText(head),
			ItemTooltipAnalyzer.GetTooltipText(body),
			ItemTooltipAnalyzer.GetTooltipText(legs),
			setBonusText);

		var combinedProfile = new ItemSemanticProfile
		{
			ItemType = head.type,
			CombinedTooltipText = combinedText
		};

		// Merge piece semantic profiles + setBonus text
		var pHead = ItemTooltipAnalyzer.Analyze(head);
		var pBody = ItemTooltipAnalyzer.Analyze(body);
		var pLegs = ItemTooltipAnalyzer.Analyze(legs);

		combinedProfile.GenericBonusScore = pHead.GenericBonusScore + pBody.GenericBonusScore + pLegs.GenericBonusScore;
		combinedProfile.SummonSlotBonus = pHead.SummonSlotBonus + pBody.SummonSlotBonus + pLegs.SummonSlotBonus;
		combinedProfile.SentrySlotBonus = pHead.SentrySlotBonus + pBody.SentrySlotBonus + pLegs.SentrySlotBonus;
		combinedProfile.IsSurvivability = pHead.IsSurvivability || pBody.IsSurvivability || pLegs.IsSurvivability;

		bool isWeaponSpecific = (head.type == ItemID.MeteorHelmet && body.type == ItemID.MeteorSuit && legs.type == ItemID.MeteorLeggings)
			|| IsWeaponSpecificSetBonus(setBonusText);

		for (int i = 0; i < DamageClassLoader.DamageClassCount; i++)
		{
			float sum = 0f;
			if (pHead.ClassAffinityScores.TryGetValue(i, out float hVal)) sum += hVal;
			if (pBody.ClassAffinityScores.TryGetValue(i, out float bVal)) sum += bVal;
			if (pLegs.ClassAffinityScores.TryGetValue(i, out float lVal)) sum += lVal;

			if (!string.IsNullOrWhiteSpace(setBonusText) && !isWeaponSpecific)
			{
				DamageClass dc = DamageClassLoader.GetDamageClass(i);
				string bonusLower = setBonusText.ToLowerInvariant();
				if (dc != null && dc != DamageClass.Default)
				{
					if (dc.CountsAsClass(DamageClass.Melee) && (bonusLower.Contains("melee") || bonusLower.Contains("ближн"))) sum += 20f;
					else if (dc.CountsAsClass(DamageClass.Ranged) && (bonusLower.Contains("ranged") || bonusLower.Contains("стрел") || bonusLower.Contains("дальн") || bonusLower.Contains("боеприпас") || bonusLower.Contains("ammo"))) sum += 20f;
					else if (dc.CountsAsClass(DamageClass.Magic) && (bonusLower.Contains("magic") || bonusLower.Contains("mana") || bonusLower.Contains("маги") || bonusLower.Contains("ман"))) sum += 20f;
					else if (dc.CountsAsClass(DamageClass.Summon) && (bonusLower.Contains("minion") || bonusLower.Contains("sentry") || bonusLower.Contains("summon") || bonusLower.Contains("миньон") || bonusLower.Contains("страж") || bonusLower.Contains("призыв"))) sum += 20f;
				}
			}

			if (sum > 0f)
				combinedProfile.ClassAffinityScores[i] = sum;
		}

		Sets.Add(new ArmorSetDefinition
		{
			HeadType = head.type,
			BodyType = body.type,
			LegsType = legs.type,
			TotalDefense = head.defense + body.defense + legs.defense,
			ModName = modName,
			SetBonusText = setBonusText,
			IsWeaponSpecificBonus = isWeaponSpecific,
			CombinedProfile = combinedProfile
		});
	}

	private static bool IsWeaponSpecificSetBonus(string setBonusText)
	{
		if (string.IsNullOrWhiteSpace(setBonusText))
			return false;

		string lower = setBonusText.ToLowerInvariant();
		return lower.Contains("space gun")
			|| lower.Contains("космическая пушка")
			|| lower.Contains("0 mana")
			|| lower.Contains("0 маны")
			|| lower.Contains("стоит 0")
			|| lower.Contains("не расходует ману");
	}

	private static void AddToBucket(Dictionary<string, List<Item>> dict, string key, Item item)
	{
		if (!dict.TryGetValue(key, out List<Item> list))
		{
			list = new List<Item>();
			dict[key] = list;
		}
		list.Add(item);
	}

	private static string GetVanillaArmorSetBonus(int head, int body, int legs)
	{
		if (body == 67 && legs == 56 && head is >= 103 and <= 105) return Language.GetTextValue("ArmorSetBonus.Shroomite");
		if ((head == 52 && body == 32 && legs == 31) || (head == 53 && body == 33 && legs == 32) || (head == 54 && body == 34 && legs == 33) || (head == 55 && body == 35 && legs == 34) || (head == 71 && body == 47 && legs == 43) || (head == 166 && body == 173 && legs == 108) || (head == 167 && body == 174 && legs == 109)) return Language.GetTextValue("ArmorSetBonus.Wood");
		if (head == 278 && body == 246 && legs == 234) return Language.GetTextValue("ArmorSetBonus.AshWood");
		if ((head == 1 && body == 1 && legs == 1) || ((head == 72 || head == 2) && body == 2 && legs == 2) || (head == 47 && body == 28 && legs == 27)) return Language.GetTextValue("ArmorSetBonus.MetalTier1");
		if ((head == 3 && body == 3 && legs == 3) || ((head == 73 || head == 4) && body == 4 && legs == 4) || (head == 48 && body == 29 && legs == 28) || (head == 49 && body == 30 && legs == 29)) return Language.GetTextValue("ArmorSetBonus.MetalTier2");
		if (head == 50 && body == 31 && legs == 30) return Language.GetTextValue("ArmorSetBonus.Platinum");
		if (head == 112 && body == 75 && legs == 64) return Language.GetTextValue("ArmorSetBonus.Pumpkin");
		if (head == 180 && body == 182 && legs == 122) return Language.GetTextValue("ArmorSetBonus.Gladiator");
		if (head == 22 && body == 14 && legs == 14) return Language.GetTextValue("ArmorSetBonus.Ninja");
		if (head == 188 && body == 189 && legs == 129) return Language.GetTextValue("ArmorSetBonus.Fossil");
		if ((head == 75 || head == 7) && body == 7 && legs == 7) return Language.GetTextValue("ArmorSetBonus.Bone");
		if (head == 157 && body == 105 && legs == 98) return Language.GetTextValue("ArmorSetBonus.BeetleDamage");
		if (head == 157 && body == 106 && legs == 98) return Language.GetTextValue("ArmorSetBonus.BeetleDefense");
		if ((head == 5 || head == 74) && (body == 5 || body == 48) && (legs == 5 || legs == 44)) return Language.GetTextValue("ArmorSetBonus.ShadowScale");
		if (head == 57 && body == 37 && legs == 35) return Language.GetTextValue("ArmorSetBonus.Crimson");
		if (head == 101 && body == 66 && legs == 55) return Language.GetTextValue("ArmorSetBonus.SpectreHealing");
		if (head == 156 && body == 66 && legs == 55) return Language.GetTextValue("ArmorSetBonus.SpectreDamage");
		if (head == 6 && body == 6 && legs == 6) return Language.GetTextValue("ArmorSetBonus.Meteor");
		if (head == 46 && body == 27 && legs == 26) return Language.GetTextValue("ArmorSetBonus.Frost");
		if ((head == 76 || head == 8) && (body == 49 || body == 8) && (legs == 45 || legs == 8)) return Language.GetTextValue("ArmorSetBonus.Jungle");
		if (head == 9 && body == 9 && legs == 9) return Language.GetTextValue("ArmorSetBonus.Molten");
		if ((head == 58 || head == 77) && (body == 38 || body == 50) && (legs == 36 || legs == 46)) return Language.GetTextValue("ArmorSetBonus.Snow");
		if (head == 70 && body == 46 && legs == 42) return Language.GetTextValue("ArmorSetBonus.Cactus");
		if (head == 99 && body == 65 && legs == 54) return Language.GetTextValue("ArmorSetBonus.Turtle");
		if (head == 78 && body == 51 && legs == 47) return Language.GetTextValue("ArmorSetBonus.ChlorophyteMelee");
		if ((head == 80 || head == 79) && body == 51 && legs == 47) return Language.GetTextValue("ArmorSetBonus.Chlorophyte");
		if (body == 17 && legs == 16 && head == 29) return Language.GetTextValue("ArmorSetBonus.CobaltCaster");
		if (body == 17 && legs == 16 && head == 30) return Language.GetTextValue("ArmorSetBonus.CobaltMelee");
		if (body == 17 && legs == 16 && head == 31) return Language.GetTextValue("ArmorSetBonus.CobaltRanged");
		if (body == 18 && legs == 17 && head == 32) return Language.GetTextValue("ArmorSetBonus.MythrilCaster");
		if (body == 18 && legs == 17 && head == 33) return Language.GetTextValue("ArmorSetBonus.MythrilMelee");
		if (body == 18 && legs == 17 && head == 34) return Language.GetTextValue("ArmorSetBonus.MythrilRanged");
		if (body == 19 && legs == 18 && head == 35) return Language.GetTextValue("ArmorSetBonus.AdamantiteCaster");
		if (body == 19 && legs == 18 && head == 36) return Language.GetTextValue("ArmorSetBonus.AdamantiteMelee");
		if (body == 19 && legs == 18 && head == 37) return Language.GetTextValue("ArmorSetBonus.AdamantiteRanged");
		if (body == 54 && legs == 49 && (head == 83 || head == 84 || head == 85)) return Language.GetTextValue("ArmorSetBonus.Palladium");
		if (body == 55 && legs == 50 && (head == 86 || head == 87 || head == 88)) return Language.GetTextValue("ArmorSetBonus.Orichalcum");
		if (body == 56 && legs == 51 && (head == 89 || head == 90 || head == 91)) return Language.GetTextValue("ArmorSetBonus.Titanium");
		if ((body == 24 || body == 229) && (legs == 23 || legs == 212) && head is 41 or 42 or 43 or 254 or 255 or 256 or 257 or 258)
			return head is 254 or 258 ? Language.GetTextValue("ArmorSetBonus.HallowedSummoner") : Language.GetTextValue("ArmorSetBonus.Hallowed");
		if (head == 261 && body == 230 && legs == 213) return Language.GetTextValue("ArmorSetBonus.CrystalNinja");
		if (head == 82 && body == 53 && legs == 48) return Language.GetTextValue("ArmorSetBonus.Tiki");
		if (head == 134 && body == 95 && legs == 79) return Language.GetTextValue("ArmorSetBonus.Spooky");
		if (head == 160 && body == 168 && legs == 103) return Language.GetTextValue("ArmorSetBonus.Bee");
		if (head == 162 && body == 170 && legs == 105) return Language.GetTextValue("ArmorSetBonus.Spider");
		if (head == 171 && body == 177 && legs == 112) return Language.GetTextValue("ArmorSetBonus.Solar");
		if (head == 169 && body == 175 && legs == 110) return Language.GetTextValue("ArmorSetBonus.Vortex", "");
		if (head == 170 && body == 176 && legs == 111) return Language.GetTextValue("ArmorSetBonus.Nebula");
		if (head == 189 && body == 190 && legs == 130) return Language.GetTextValue("ArmorSetBonus.Stardust", "");
		if (head == 200 && body == 198 && legs == 142) return Language.GetTextValue("ArmorSetBonus.Forbidden", "");
		if (head == 204 && body == 201 && legs == 145) return Language.GetTextValue("ArmorSetBonus.SquireTier2");
		if (head == 203 && body == 200 && legs == 144) return Language.GetTextValue("ArmorSetBonus.ApprenticeTier2");
		if (head == 205 && body == 202 && legs is 146 or 147) return Language.GetTextValue("ArmorSetBonus.HuntressTier2");
		if (head == 206 && body == 203 && legs == 148) return Language.GetTextValue("ArmorSetBonus.MonkTier2");
		if (head == 210 && body == 204 && legs == 152) return Language.GetTextValue("ArmorSetBonus.SquireTier3");
		if (head == 211 && body == 205 && legs == 153) return Language.GetTextValue("ArmorSetBonus.ApprenticeTier3");
		if (head == 212 && body == 206 && legs is 154 or 155) return Language.GetTextValue("ArmorSetBonus.HuntressTier3");
		if (head == 213 && body == 207 && legs == 156) return Language.GetTextValue("ArmorSetBonus.MonkTier3");
		if (head == 185 && body == 187 && legs == 127) return Language.GetTextValue("ArmorSetBonus.ObsidianOutlaw");
		return null;
	}
}
