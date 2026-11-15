using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

/// <summary>
/// Collects all bosses, minibosses, and events in progression order using BossChecklist when loaded,
/// with an automatic fallback scanner over ContentSamples.NpcsByNetId.
/// </summary>
public sealed class BossCatalog
{
	public List<BossStageInfo> AllStages { get; } = new();
	public float WallOfFleshProgression { get; private set; } = 7.0f;
	public bool UsedBossChecklist { get; private set; }

	private readonly Dictionary<int, float> _npcProgression = new();
	private readonly HashSet<int> _checklistNpcIds = new();
	private readonly HashSet<string> _checklistModSources = new(StringComparer.OrdinalIgnoreCase);

	public void Build(Mod mod)
	{
		AllStages.Clear();
		_npcProgression.Clear();
		_checklistNpcIds.Clear();
		_checklistModSources.Clear();
		UsedBossChecklist = false;

		if (ModLoader.TryGetMod("BossChecklist", out Mod bossChecklist))
		{
			TryLoadFromBossChecklist(mod, bossChecklist);
		}

		AppendMissingNpcBosses();

		if (UsedBossChecklist)
		{
			AllStages.Sort((a, b) =>
			{
				int cmp = a.Progression.CompareTo(b.Progression);
				return cmp != 0 ? cmp : a.ChecklistOrder.CompareTo(b.ChecklistOrder);
			});
		}
		else
		{
			AllStages.Sort((a, b) =>
			{
				int cmp = a.Progression.CompareTo(b.Progression);
				return cmp != 0 ? cmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
			});
		}

		foreach (BossStageInfo stage in AllStages)
		{
			foreach (int npcId in stage.NpcIds)
			{
				if (!_npcProgression.TryGetValue(npcId, out float existing) || stage.Progression < existing)
					_npcProgression[npcId] = stage.Progression;
			}
		}

		BossStageInfo wof = AllStages.FirstOrDefault(s =>
			string.Equals(s.Key, "Terraria/WallOfFlesh", StringComparison.OrdinalIgnoreCase)
			|| s.NpcIds.Contains(NPCID.WallofFlesh));

		WallOfFleshProgression = wof != null ? wof.Progression : 7.0f;
	}

	public float GetProgressionForNpc(int npcId, float fallback)
	{
		return _npcProgression.TryGetValue(npcId, out float p) ? p : fallback;
	}

	private void TryLoadFromBossChecklist(Mod mod, Mod bossChecklist)
	{
		try
		{
			if (TryLoadFromSortedEntries(mod, bossChecklist))
				return;

			object raw = bossChecklist.Call("GetBossInfoDictionary", mod, "2.0.0");
			if (raw is not Dictionary<string, Dictionary<string, object>> dict || dict.Count == 0)
				return;

			UsedBossChecklist = true;
			int order = 0;

			foreach ((string key, Dictionary<string, object> info) in dict)
			{
				string modSource = GetString(info, "modSource", "Terraria");
				string displayName = GetString(info, "displayName", key);
				float progression = info.TryGetValue("progression", out object pObj) ? Convert.ToSingle(pObj) : 0f;
				bool isBoss = info.TryGetValue("isBoss", out object bObj) && bObj is bool b && b;
				bool isMiniboss = info.TryGetValue("isMiniboss", out object mbObj) && mbObj is bool mb && mb;
				bool isEvent = info.TryGetValue("isEvent", out object evObj) && evObj is bool ev && ev;
				Func<bool> downed = info.TryGetValue("downed", out object dObj) ? dObj as Func<bool> : null;
				Func<bool> available = info.TryGetValue("availability", out object aObj) ? aObj as Func<bool> : null;

				List<int> npcIds = info.TryGetValue("npcIDs", out object nObj) && nObj is List<int> nList
					? new List<int>(nList)
					: new List<int>();

				if ((string.IsNullOrWhiteSpace(displayName) || displayName == key) && npcIds.Count > 0)
				{
					string npcName = Lang.GetNPCNameValue(npcIds[0]);
					if (!string.IsNullOrWhiteSpace(npcName))
						displayName = npcName;
				}

				List<int> spawnItems = info.TryGetValue("spawnItems", out object sObj) && sObj is List<int> sList
					? new List<int>(sList)
					: new List<int>();

				int bagId = info.TryGetValue("treasureBag", out object bagObj) && bagObj is int bagInt ? bagInt : 0;

				List<int> lootIds = info.TryGetValue("loot", out object lObj) && lObj is List<int> lList
					? new List<int>(lList)
					: new List<int>();

				List<DropRateInfo> dropRates = info.TryGetValue("dropRateInfo", out object drObj) && drObj is List<DropRateInfo> drList
					? new List<DropRateInfo>(drList)
					: new List<DropRateInfo>();

				RegisterChecklistStage(new BossStageInfo
				{
					Key = key,
					ModSource = modSource,
					DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
					Progression = progression,
					ChecklistOrder = order++,
					IsBoss = isBoss,
					IsMiniboss = isMiniboss,
					IsEvent = isEvent,
					DownedCheck = downed,
					AvailableCheck = available,
					NpcIds = npcIds,
					SpawnItems = spawnItems,
					TreasureBagId = bagId,
					LootItemIds = lootIds,
					DropRates = dropRates
				});
			}
		}
		catch (Exception ex)
		{
			mod.Logger.Warn($"[BossBuildAdvisor] Failed reading BossChecklist entries, falling back to NPC scan: {ex.Message}");
		}
	}

	private bool TryLoadFromSortedEntries(Mod mod, Mod bossChecklist)
	{
		try
		{
			Assembly bcAsm = bossChecklist.Code;
			Type bcType = bcAsm?.GetType("BossChecklist.BossChecklist");
			FieldInfo trackerField = bcType?.GetField("bossTracker", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
			object tracker = trackerField?.GetValue(null);
			if (tracker == null)
				return false;

			FieldInfo sortedField = tracker.GetType().GetField("SortedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (sortedField?.GetValue(tracker) is not System.Collections.IEnumerable sortedEntries)
				return false;

			const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
			Type entryType = bcAsm.GetType("BossChecklist.EntryInfo");
			if (entryType == null)
				return false;

			PropertyInfo keyProp = entryType.GetProperty("Key", AnyMember);
			FieldInfo modSourceField = entryType.GetField("modSource", AnyMember);
			PropertyInfo displayNameProp = entryType.GetProperty("DisplayName", AnyMember);
			FieldInfo progressionField = entryType.GetField("progression", AnyMember);
			FieldInfo typeField = entryType.GetField("type", AnyMember);
			FieldInfo downedField = entryType.GetField("downed", AnyMember);
			PropertyInfo autoDownedProp = entryType.GetProperty("IsAutoDownedOrMarked", AnyMember);
			FieldInfo availableField = entryType.GetField("available", AnyMember);
			FieldInfo hiddenField = entryType.GetField("hidden", AnyMember);
			MethodInfo visibleMethod = entryType.GetMethod("VisibleOnChecklist", AnyMember);
			FieldInfo npcIdsField = entryType.GetField("npcIDs", AnyMember);
			FieldInfo spawnItemField = entryType.GetField("spawnItem", AnyMember);
			PropertyInfo treasureBagProp = entryType.GetProperty("TreasureBag", AnyMember);
			FieldInfo lootTypesField = entryType.GetField("lootItemTypes", AnyMember);
			FieldInfo lootField = entryType.GetField("loot", AnyMember);

			int order = 0;
			int loaded = 0;

			foreach (object entry in sortedEntries)
			{
				if (entry == null)
					continue;

				object entryObj = entry;
				string key = keyProp?.GetValue(entryObj) as string ?? string.Empty;
				string modSource = modSourceField?.GetValue(entryObj) as string ?? "Terraria";
				string displayName = displayNameProp?.GetValue(entryObj) as string ?? key;
				float progression = progressionField?.GetValue(entryObj) is float p ? p : 0f;
				int entryKind = typeField?.GetValue(entryObj) is object tk ? Convert.ToInt32(tk) : 0;
				Func<bool> rawDowned = downedField?.GetValue(entryObj) as Func<bool>;
				Func<bool> available = availableField?.GetValue(entryObj) as Func<bool>;
				bool isHidden = hiddenField?.GetValue(entryObj) is bool h && h;

				List<int> npcIds = npcIdsField?.GetValue(entryObj) is List<int> nList
					? new List<int>(nList)
					: new List<int>();

				if ((string.IsNullOrWhiteSpace(displayName) || displayName == key) && npcIds.Count > 0)
				{
					string npcName = Lang.GetNPCNameValue(npcIds[0]);
					if (!string.IsNullOrWhiteSpace(npcName))
						displayName = npcName;
				}

				List<int> spawnItems = spawnItemField?.GetValue(entryObj) is List<int> sList
					? new List<int>(sList)
					: new List<int>();

				int bagId = treasureBagProp?.GetValue(entryObj) is int bagInt ? bagInt : 0;

				List<int> lootIds = lootTypesField?.GetValue(entryObj) is List<int> lList
					? new List<int>(lList)
					: new List<int>();

				List<DropRateInfo> dropRates = lootField?.GetValue(entryObj) is List<DropRateInfo> drList
					? new List<DropRateInfo>(drList)
					: new List<DropRateInfo>();

				Func<bool> downedCheck = () =>
				{
					if (autoDownedProp != null)
					{
						try
						{
							return (bool)autoDownedProp.GetValue(entryObj);
						}
						catch
						{
						}
					}
					return rawDowned != null && rawDowned();
				};

				Func<bool> visibleCheck = () =>
				{
					if (visibleMethod != null)
					{
						try
						{
							return (bool)visibleMethod.Invoke(entryObj, null);
						}
						catch
						{
						}
					}
					if (isHidden)
						return false;
					return available == null || available();
				};

				RegisterChecklistStage(new BossStageInfo
				{
					Key = key,
					ModSource = modSource,
					DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
					Progression = progression,
					ChecklistOrder = order++,
					IsBoss = entryKind == 0,
					IsMiniboss = entryKind == 1,
					IsEvent = entryKind == 2,
					DownedCheck = downedCheck,
					AvailableCheck = available,
					ChecklistVisibleCheck = visibleCheck,
					NpcIds = npcIds,
					SpawnItems = spawnItems,
					TreasureBagId = bagId,
					LootItemIds = lootIds,
					DropRates = dropRates
				});
				loaded++;
			}

			if (loaded > 0)
			{
				UsedBossChecklist = true;
				return true;
			}
		}
		catch (Exception ex)
		{
			mod.Logger.Debug($"[BossBuildAdvisor] BossChecklist SortedEntries reflection fallback to Call API: {ex.Message}");
		}

		return false;
	}

	private void RegisterChecklistStage(BossStageInfo stage)
	{
		AllStages.Add(stage);
		if (!string.IsNullOrWhiteSpace(stage.ModSource) && !string.Equals(stage.ModSource, "Unknown", StringComparison.OrdinalIgnoreCase))
			_checklistModSources.Add(stage.ModSource);

		foreach (int npcId in stage.NpcIds)
			_checklistNpcIds.Add(npcId);
	}

	private void AppendMissingNpcBosses()
	{
		var registeredNpcs = new HashSet<int>(_checklistNpcIds);
		foreach (BossStageInfo stage in AllStages)
		{
			foreach (int npcId in stage.NpcIds)
				registeredNpcs.Add(npcId);
		}

		// Skip multi-part boss secondary segments (e.g. Eater of Worlds body/tail, Skeletron hands)
		// and skip mods already registered with BossChecklist so alternate boss phases/forms are not duplicated.
		foreach ((int npcId, NPC npc) in ContentSamples.NpcsByNetId)
		{
			if (npc == null || !npc.boss || registeredNpcs.Contains(npcId))
				continue;

			if (IsSecondaryBossPart(npcId))
				continue;

			string modName = npc.ModNPC?.Mod?.Name ?? "Terraria";
			if (UsedBossChecklist && _checklistModSources.Contains(modName))
				continue;

			float estProg = EstimateBossProgressionFromStats(npc);
			string key = npc.ModNPC != null ? npc.ModNPC.FullName : $"Terraria/{NPCID.Search.GetName(npcId)}";
			string display = Lang.GetNPCNameValue(npcId);
			if (string.IsNullOrWhiteSpace(display))
				display = npc.TypeName;

			Func<bool> downed = GetVanillaDownedCheck(npcId);

			AllStages.Add(new BossStageInfo
			{
				Key = key,
				ModSource = modName,
				DisplayName = display,
				Progression = estProg,
				ChecklistOrder = int.MaxValue,
				IsBoss = true,
				IsMiniboss = false,
				IsEvent = false,
				DownedCheck = downed,
				NpcIds = new List<int> { npcId }
			});
		}
	}

	public BossStageInfo GetNextUndefeatedBoss(bool includeMinibossesAndEvents)
	{
		IEnumerable<BossStageInfo> candidates = AllStages.Where(s =>
			s.IsVisibleOnChecklist && (includeMinibossesAndEvents || s.IsBoss));
		BossStageInfo next = candidates.FirstOrDefault(s => !s.IsDowned);
		return next ?? candidates.LastOrDefault() ?? AllStages.FirstOrDefault(s => s.IsVisibleOnChecklist) ?? AllStages.FirstOrDefault();
	}

	private static string GetString(Dictionary<string, object> dict, string key, string fallback)
	{
		if (!dict.TryGetValue(key, out object val) || val == null)
			return fallback;

		if (val is LocalizedText lt)
			return !string.IsNullOrWhiteSpace(lt.Value) ? lt.Value : fallback;

		if (val is string s)
			return !string.IsNullOrWhiteSpace(s) ? s : fallback;

		return fallback;
	}

	private static bool IsSecondaryBossPart(int npcId)
	{
		return npcId is NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail
			or NPCID.SkeletronHand or NPCID.PrimeCannon or NPCID.PrimeSaw or NPCID.PrimeVice or NPCID.PrimeLaser
			or NPCID.WallofFleshEye or NPCID.GolemFistLeft or NPCID.GolemFistRight or NPCID.GolemHeadFree
			or NPCID.MoonLordHand or NPCID.MoonLordHead or NPCID.MoonLordFreeEye or NPCID.MoonLordCore;
	}

	private static float EstimateBossProgressionFromStats(NPC npc)
	{
		return npc.type switch
		{
			NPCID.KingSlime => 1f,
			NPCID.EyeofCthulhu => 2f,
			NPCID.EaterofWorldsHead or NPCID.BrainofCthulhu => 3f,
			NPCID.QueenBee => 4f,
			NPCID.SkeletronHead => 5f,
			NPCID.Deerclops => 6f,
			NPCID.WallofFlesh => 9f,
			NPCID.QueenSlimeBoss => 10f,
			NPCID.Retinazer or NPCID.Spazmatism => 11.5f,
			NPCID.TheDestroyer => 11f,
			NPCID.SkeletronPrime => 12f,
			NPCID.Plantera => 14f,
			NPCID.Golem => 15f,
			NPCID.DukeFishron => 15.5f,
			NPCID.HallowBoss => 16f,
			NPCID.CultistBoss => 17f,
			NPCID.MoonLordCore => 18f,
			_ => EstimateModBossProgressionByHp(npc.lifeMax)
		};
	}

	private static float EstimateModBossProgressionByHp(int lifeMax)
	{
		if (lifeMax <= 2500) return 1.8f;
		if (lifeMax <= 4500) return 3.5f;
		if (lifeMax <= 7000) return 5.5f;
		if (lifeMax <= 12000) return 8.5f;
		if (lifeMax <= 28000) return 10.5f;
		if (lifeMax <= 60000) return 12.5f;
		if (lifeMax <= 100000) return 14.5f;
		if (lifeMax <= 180000) return 16.5f;
		return 18.5f;
	}

	private static Func<bool> GetVanillaDownedCheck(int npcId)
	{
		return npcId switch
		{
			NPCID.KingSlime => () => NPC.downedSlimeKing,
			NPCID.EyeofCthulhu => () => NPC.downedBoss1,
			NPCID.EaterofWorldsHead or NPCID.BrainofCthulhu => () => NPC.downedBoss2,
			NPCID.QueenBee => () => NPC.downedQueenBee,
			NPCID.SkeletronHead => () => NPC.downedBoss3,
			NPCID.Deerclops => () => NPC.downedDeerclops,
			NPCID.WallofFlesh => () => Main.hardMode,
			NPCID.QueenSlimeBoss => () => NPC.downedQueenSlime,
			NPCID.TheDestroyer => () => NPC.downedMechBoss1,
			NPCID.Retinazer or NPCID.Spazmatism => () => NPC.downedMechBoss2,
			NPCID.SkeletronPrime => () => NPC.downedMechBoss3,
			NPCID.Plantera => () => NPC.downedPlantBoss,
			NPCID.Golem => () => NPC.downedGolemBoss,
			NPCID.DukeFishron => () => NPC.downedFishron,
			NPCID.HallowBoss => () => NPC.downedEmpressOfLight,
			NPCID.CultistBoss => () => NPC.downedAncientCultist,
			NPCID.MoonLordCore => () => NPC.downedMoonlord,
			_ => () => false
		};
	}
}
