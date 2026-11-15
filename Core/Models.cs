using System;
using System.Collections.Generic;
using Terraria.GameContent.ItemDropRules;
using Terraria.ModLoader;

namespace BossBuildAdvisor.Core;

public sealed class BossStageInfo
{
	public string Key { get; init; }
	public string ModSource { get; init; }
	public string DisplayName { get; init; }
	public float Progression { get; init; }
	public bool IsBoss { get; init; }
	public bool IsMiniboss { get; init; }
	public bool IsEvent { get; init; }
	public Func<bool> DownedCheck { get; init; }
	public List<int> NpcIds { get; init; } = new();
	public List<int> SpawnItems { get; init; } = new();
	public int TreasureBagId { get; init; }
	public List<int> LootItemIds { get; init; } = new();
	public List<DropRateInfo> DropRates { get; init; } = new();
	public int ChecklistOrder { get; init; }
	public Func<bool> AvailableCheck { get; init; }
	public Func<bool> ChecklistVisibleCheck { get; init; }

	public bool IsAvailable => AvailableCheck?.Invoke() ?? true;
	public bool IsVisibleOnChecklist => ChecklistVisibleCheck?.Invoke() ?? IsAvailable;

	public bool IsDowned
	{
		get
		{
			try
			{
				return DownedCheck != null && DownedCheck();
			}
			catch
			{
				return false;
			}
		}
	}
}

public enum ItemSourceType
{
	WorldTier,
	Recipe,
	NpcDrop,
	BagOrCrate,
	BossDrop,
	NpcShop,
	Shimmer,
	EstimatedTier
}

public enum SourceConfidence
{
	Verified,
	Crafted,
	TierEstimated
}

public enum ObtainabilityDifficulty
{
	Easy = 0,
	Medium = 1,
	Hard = 2,
	VeryHard = 3
}

public enum DifficultyFilterMode
{
	Comfortable = 0,
	IncludeHard = 1,
	All = 2
}

public sealed class ItemUnlockPath
{
	public float UnlockProgression { get; set; } = float.MaxValue;
	public ItemSourceType SourceType { get; set; }
	public SourceConfidence Confidence { get; set; }
	public string SourceSummary { get; set; } = string.Empty;
	public ObtainabilityDifficulty Difficulty { get; set; } = ObtainabilityDifficulty.Easy;
	public string DifficultyReason { get; set; } = string.Empty;
	public float BestDropChance { get; set; } = 1f;
	public bool RequiresHardmode { get; set; }
	public bool ExpertOnly { get; set; }
	public bool MasterOnly { get; set; }
	public bool ClassicOnly { get; set; }
	public bool CrimsonOnly { get; set; }
	public bool CorruptOnly { get; set; }
}

public sealed class ItemUnlockInfo
{
	public int ItemType { get; init; }
	public string ModName { get; set; }
	public float UnlockProgression { get; set; }
	public ItemSourceType SourceType { get; set; }
	public SourceConfidence Confidence { get; set; }
	public string SourceSummary { get; set; }
	public ObtainabilityDifficulty Difficulty { get; set; } = ObtainabilityDifficulty.Easy;
	public string DifficultyReason { get; set; } = string.Empty;
	public float BestDropChance { get; set; } = 1f;
	public bool RequiresHardmode { get; set; }
	public bool ExpertOnly { get; set; }
	public bool MasterOnly { get; set; }
	public bool ClassicOnly { get; set; }
	public bool CrimsonOnly { get; set; }
	public bool CorruptOnly { get; set; }
	public bool Unobtainable { get; set; }

	/// <summary>
	/// Best unlock path per DifficultyFilterMode:
	/// [0] = Comfortable (Easy/Medium), [1] = IncludeHard (Easy/Medium/Hard), [2] = All.
	/// </summary>
	public ItemUnlockPath[] PathsByFilter { get; } = new ItemUnlockPath[3];

	public ItemUnlockPath GetPathFor(DifficultyFilterMode mode)
	{
		int idx = Math.Clamp((int)mode, 0, 2);
		for (int i = idx; i >= 0; i--)
		{
			if (PathsByFilter[i] != null && PathsByFilter[i].UnlockProgression < float.MaxValue * 0.5f)
				return PathsByFilter[i];
		}
		return null;
	}

	public void SyncPrimaryFields(DifficultyFilterMode preferredMode = DifficultyFilterMode.Comfortable)
	{
		ItemUnlockPath best = GetPathFor(preferredMode) ?? GetPathFor(DifficultyFilterMode.All);
		if (best == null)
			return;

		UnlockProgression = best.UnlockProgression;
		SourceType = best.SourceType;
		Confidence = best.Confidence;
		SourceSummary = best.SourceSummary;
		Difficulty = best.Difficulty;
		DifficultyReason = best.DifficultyReason;
		BestDropChance = best.BestDropChance;
		RequiresHardmode = best.RequiresHardmode;
		ExpertOnly = best.ExpertOnly;
		MasterOnly = best.MasterOnly;
		ClassicOnly = best.ClassicOnly;
		CrimsonOnly = best.CrimsonOnly;
		CorruptOnly = best.CorruptOnly;
	}
}

public sealed class ItemSemanticProfile
{
	public int ItemType { get; init; }
	public bool IsWings { get; set; }
	public bool IsMobilityBoots { get; set; }
	public bool IsDashOrDodge { get; set; }
	public bool IsSurvivability { get; set; }
	public bool IsManaUtility { get; set; }
	public bool IsCombatBuffPotion { get; set; }
	public bool IsPureNonCombatUtility { get; set; }
	public bool HasGenericCombat { get; set; }
	public float GenericBonusScore { get; set; }
	public float SummonSlotBonus { get; set; }
	public float SentrySlotBonus { get; set; }
	public Dictionary<int, float> ClassAffinityScores { get; } = new();
	public string CombinedTooltipText { get; set; } = string.Empty;
}

public sealed class ArmorSetCandidate
{
	public int HeadType { get; init; }
	public int BodyType { get; init; }
	public int LegsType { get; init; }
	public int TotalDefense { get; init; }
	public float Score { get; init; }
	public float UnlockProgression { get; init; }
	public bool IsTrueSet { get; init; }
	public string SetBonusSummary { get; init; } = string.Empty;
	public string ModName { get; init; } = "Terraria";
	public ObtainabilityDifficulty Difficulty { get; init; } = ObtainabilityDifficulty.Easy;
	public string DifficultyReason { get; init; } = string.Empty;
	public RecommendedSlotItem HeadSlot { get; init; }
	public RecommendedSlotItem BodySlot { get; init; }
	public RecommendedSlotItem LegsSlot { get; init; }
}

public sealed class RecommendedSlotItem
{
	public int ItemType { get; init; }
	public string RoleTag { get; init; }
	public float Score { get; init; }
	public float UnlockProgression { get; init; }
	public string SourceSummary { get; init; }
	public string ReasonSummary { get; init; }
	public SourceConfidence Confidence { get; init; }
	public ItemSourceType SourceType { get; init; }
	public ObtainabilityDifficulty Difficulty { get; init; } = ObtainabilityDifficulty.Easy;
	public string DifficultyReason { get; init; } = string.Empty;
}

public sealed class BuildQueryOptions
{
	public bool IncludeTargetBossDrops { get; set; }
	public bool IncludeTierEstimatedItems { get; set; } = true;
	public bool RespectWorldDifficulty { get; set; } = true;
	public bool RespectWorldEvil { get; set; } = true;
	public DifficultyFilterMode MaxDifficulty { get; set; } = DifficultyFilterMode.Comfortable;
}

public sealed class BuildRecommendation
{
	public BossStageInfo TargetStage { get; init; }
	public DamageClass TargetClass { get; init; }
	public bool IsAutoClass { get; init; }
	public List<RecommendedSlotItem> PrimaryWeapons { get; } = new();
	public List<RecommendedSlotItem> SupportWeapons { get; } = new();
	public RecommendedSlotItem RecommendedAmmo { get; set; }
	public List<RecommendedSlotItem> AmmoOptions { get; } = new();
	public ArmorSetCandidate ArmorSet { get; set; }
	public List<ArmorSetCandidate> ArmorOptions { get; } = new();
	public RecommendedSlotItem HeadSlot { get; set; }
	public RecommendedSlotItem BodySlot { get; set; }
	public RecommendedSlotItem LegsSlot { get; set; }
	public List<RecommendedSlotItem> Accessories { get; } = new();
	public List<RecommendedSlotItem> AltAccessories { get; } = new();
	public RecommendedSlotItem HealingPotion { get; set; }
	public List<RecommendedSlotItem> BuffPotions { get; } = new();
	public int AccessorySlotBudget { get; set; }
}
