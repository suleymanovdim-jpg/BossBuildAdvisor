using System;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using BossBuildAdvisor.Core;
using BossBuildAdvisor.Systems;

namespace BossBuildAdvisor.Commands;

public sealed class AdvisorCommand : ModCommand
{
	public override CommandType Type => CommandType.Chat;
	public override string Command => "advisor";
	public override string Usage => "/advisor [next | stage <boss> | rebuild | dump]";
	public override string Description => "Opens Boss Build Advisor or prints automated cross-mod build diagnostics.";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		AdvisorSystem sys = AdvisorSystem.Instance;
		if (sys == null)
			return;

		if (args.Length == 0)
		{
			if (Main.dedServ)
			{
				caller.Reply(Usage);
				return;
			}

			sys.ToggleOpen();
			return;
		}

		if (sys.Recommender == null && args[0].ToLowerInvariant() != "rebuild")
			sys.RebuildAnalysis();

		string sub = args[0].ToLowerInvariant();
		if (sub == "rebuild")
		{
			sys.RebuildAnalysis();
			caller.Reply($"[BossBuildAdvisor] Rebuilt graph in {sys.Graph.BuildDurationMs} ms: {sys.Catalog.AllStages.Count} stages, {sys.Graph.Items.Count} items, {sys.ArmorSets.Sets.Count} armor sets.");
			return;
		}

		if (sub == "dump")
		{
			DumpDiagnostics(caller, sys);
			return;
		}

		BossStageInfo target = sub == "next"
			? sys.Catalog.GetNextUndefeatedBoss(false)
			: FindStageByQuery(sys, string.Join(" ", args.Skip(sub == "stage" ? 1 : 0)));

		if (target == null)
		{
			caller.Reply("[BossBuildAdvisor] Boss stage not found.");
			return;
		}

		BuildRecommendation rec = sys.Recommender.Recommend(target, null, new BuildQueryOptions());
		PrintRecommendation(caller, rec);
	}

	private static BossStageInfo FindStageByQuery(AdvisorSystem sys, string query)
	{
		if (string.IsNullOrWhiteSpace(query))
			return sys.Catalog.GetNextUndefeatedBoss(false);

		return sys.Catalog.AllStages.FirstOrDefault(s =>
			s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
			|| s.Key.Contains(query, StringComparison.OrdinalIgnoreCase));
	}

	private static void PrintRecommendation(CommandCaller caller, BuildRecommendation rec)
	{
		string weapons = string.Join(", ", rec.PrimaryWeapons.Select(w => $"{Lang.GetItemNameValue(w.ItemType)} [{ModOf(w.ItemType)}]"));
		string armor = "None";
		if (rec.ArmorSet != null)
		{
			string pieces = string.Join(" / ", new[] { rec.ArmorSet.HeadType, rec.ArmorSet.BodyType, rec.ArmorSet.LegsType }
				.Where(id => id > ItemID.None)
				.Select(Lang.GetItemNameValue));
			armor = $"{pieces} ({rec.ArmorSet.TotalDefense} def)";
		}
		string accs = string.Join(", ", rec.Accessories.Select(a => Lang.GetItemNameValue(a.ItemType)));
		string className = ItemTooltipAnalyzer.FormatClassDisplayName(rec.TargetClass);

		caller.Reply($"=== Build for {rec.TargetStage.DisplayName} ({rec.TargetStage.ModSource}, stage {rec.TargetStage.Progression:0.#}) | Class: {className} ===");
		caller.Reply($"Weapons: {weapons}");
		caller.Reply($"Armor: {armor}");
		caller.Reply($"Accessories ({rec.Accessories.Count}): {accs}");
	}

	private static void DumpDiagnostics(CommandCaller caller, AdvisorSystem sys)
	{
		var byMod = sys.Catalog.AllStages.GroupBy(s => s.ModSource).Select(g => $"{g.Key}:{g.Count()}");
		string stageSummary = string.Join(", ", byMod);
		string classes = string.Join(", ", sys.Recommender.GetAvailablePlayableClasses().Select(c => $"{c.FullName}({c.DisplayName.Value.Trim()})"));

		string msg = $"[BossBuildAdvisor] Stages ({sys.Catalog.AllStages.Count}): {stageSummary} | Classes: {classes} | ArmorSets: {sys.ArmorSets.Sets.Count} | GraphMs: {sys.Graph.BuildDurationMs}";
		caller.Reply(msg);
		sys.DumpDiagnosticsToLog();
	}

	private static string ModOf(int itemType)
	{
		return ContentSamples.ItemsByType.TryGetValue(itemType, out Item item)
			? (item.ModItem?.Mod?.Name ?? "Terraria")
			: "?";
	}
}
