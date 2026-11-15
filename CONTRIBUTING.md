# Contributing to Boss Build Advisor

Thank you for wanting to improve **Boss Build Advisor**! Because modpacks vary wildly in mechanics, custom damage classes, and progression rules, contributions — especially compatibility fixes, localization, and scoring heuristics — are very welcome.

## Architecture Overview

```text
BossBuildAdvisor/
├── BossBuildAdvisor.cs                 # Mod entry point
├── Core/
│   ├── Models.cs                       # Shared data models (stages, items, builds)
│   ├── BossCatalog.cs                  # Boss discovery & optional BossChecklist integration
│   ├── ConditionProgressionMapper.cs   # Maps drop/shop/recipe conditions to progression tiers
│   ├── ProgressionGraph.cs             # Combines bosses, events, ores, and item tiers
│   ├── ItemTooltipAnalyzer.cs          # Infers weapon/accessory roles & class affinity from tooltips/stats
│   ├── ArmorSetAnalyzer.cs             # Extracts and scores armor set bonuses vs. individual pieces
│   └── BuildRecommender.cs             # Selects top weapons, armor, wings, and accessories per stage/class
├── Systems/
│   └── AdvisorSystem.cs                # Lifecycle hooks, keybinds, inventory button toggle
├── UI/
│   ├── AdvisorUIElements.cs            # Custom UI widgets (slots, cards, tabs)
│   └── AdvisorUIState.cs               # Main advisor window layout & interaction
├── Commands/
│   └── AdvisorCommand.cs               # /advisor chat command handler
└── Localization/
    ├── en-US_Mods.BossBuildAdvisor.hjson
    └── ru-RU_Mods.BossBuildAdvisor.hjson
```

## Local Development Setup

1. Clone this repo directly into your tModLoader `ModSources` directory:
   ```bash
   git clone https://github.com/suleymanovdim-jpg/BossBuildAdvisor.git "<Documents>/My Games/Terraria/tModLoader/ModSources/BossBuildAdvisor"
   ```
   (On Windows: `%USERPROFILE%\Documents\My Games\Terraria\tModLoader\ModSources\BossBuildAdvisor`).
2. `BossBuildAdvisor.csproj` automatically uses `..\tModLoader.targets` if present in `ModSources`, or falls back to `$(MSBuildProgramFiles32)\Steam\steamapps\common\tModLoader\tMLMod.targets`.
3. Build from inside tModLoader (**Workshop -> Develop Mods -> Build + Reload**), or run:
   ```powershell
   .\build.ps1
   ```
   If tModLoader is installed on another drive, set `$env:TML_DIR` before running `.\build.ps1`.

## Debugging & Verification Workflow

1. Launch tModLoader with your test modpack enabled.
2. Run `/advisor dump` and `/advisor stage <boss>` in-game to inspect inferred tiers, class mappings, and chosen items.
3. Check `<Steam>\steamapps\common\tModLoader\tModLoader-Logs\client.log` for warnings, missing localization keys, or scoring diagnostics.
4. Please keep heuristics **general and mod-agnostic** where possible (prefer inspecting `DamageClass`, item tooltips, recipes, loot rules, and progression conditions over hardcoding specific third-party mod item names).

## Submitting Changes

- Fork the repository and create a topic branch (`git checkout -b fix/my-improvement`).
- Update both `Localization/en-US_Mods.BossBuildAdvisor.hjson` and `Localization/ru-RU_Mods.BossBuildAdvisor.hjson` if you add user-facing strings.
- Test building cleanly (`0 warnings, 0 errors`) and verify in-game against at least Vanilla + 1 major content mod.
- Open a Pull Request describing what modpack/scenario you tested against and include before/after `/advisor dump` examples if changing recommendation logic.
