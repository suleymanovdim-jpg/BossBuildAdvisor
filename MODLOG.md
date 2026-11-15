# BossBuildAdvisor MODLOG

## Environment & Safety
- Install: `C:\Program Files (x86)\Steam\steamapps\common\tModLoader` (tModLoader `1.4.4.9+2026.09.3.1`, .NET 8 runtime, .NET SDK `10.0.403`)
- User Saves Backup: `%USERPROFILE%\.universal-modder\backups\tml-user-backup\20261115-151709.zip` (83 files, 78.0 MB)
- Decomp Reference (outside repo): `%USERPROFILE%\terraria-decomp`
- Lab Save Directory (isolated): `<workspace>\tml-lab`
- Target Modpack observed in `enabled.json`: `BossChecklist` (2.2.4), `Redemption` (0.8.0.4501), `Split` (1.0.12), `Ultranium` (1.1.3), `MagicStorage`, `RecipeBrowser`, `ProgressionJournal`, `CalamityRuTranslate`, etc.

## Chosen Route
- Pure C# tModLoader (`side = Client`, `weakReferences = BossChecklist`).
- Automated cross-mod progression & build recommendation engine:
  1. `BossCatalog`: Reads `BossChecklist` (`GetBossInfoDictionary`, API `"2.0.0"`) in `PostSetupRecipes()` after `BossUISystem.AddRecipes()` calls `FinalizeEntryData()`, with fallback scanning of `ContentSamples.NpcsByNetId` for `npc.boss`.
  2. `ProgressionGraph`: Computes per-item `UnlockProgression` across ALL loaded mods from `BossCatalog` loot/bags, `Main.ItemDropsDB.GetRulesForNPCID` + `GetRulesForItemID`, `NPCShopDatabase.AllShops`, `Condition` / `IItemDropRuleCondition` gates, crafting stations (`TileID` + modded tile-placing items), `Recipe` + `RecipeGroup` relaxation, `ShimmerTransformToItem`, and rarity/value tier fallback.
  3. `ItemTooltipAnalyzer` + `BuildRecommender`: Class-aware (`DamageClassLoader.DamageClasses` + `CountsAsClass`) DPS + armor set (`Player` / `ItemLoader.IsArmorSet`) + role-budgeted accessory + potion/ammo recommendation engine.
  4. `UI/AdvisorUIState`: Interactive Terraria-styled UI window with boss timeline, dynamic class tabs, build slots, item hover tooltips, and acquisition details.

## Verified APIs (from tModLoader.dll & BossChecklist.dll)
- `BossChecklist.Call("GetBossInfoDictionary", mod, "2.0.0")` -> `Dictionary<string, Dictionary<string, object>>` with keys: `"key"`, `"modSource"`, `"displayName"`, `"progression"`, `"downed"`, `"isBoss"`, `"isMiniboss"`, `"isEvent"`, `"npcIDs"`, `"spawnInfo"`, `"spawnItems"`, `"treasureBag"`, `"relic"`, `"dropRateInfo"`, `"loot"`, `"collectibles"`. Note: `displayName` can be `LocalizedText` in `BossChecklist 2.2.4`, handled via `val is LocalizedText lt ? lt.Value : ...`.
- `ContentSamples.ItemsByType`, `ContentSamples.NpcsByNetId`, `Main.ItemDropsDB.GetRulesForNPCID(id, false)`, `Main.ItemDropsDB.GetRulesForItemID(bagId)`, `NPCShopDatabase.AllShops`, `DamageClassLoader.DamageClasses`, `ItemSlot.DrawItemIcon`.

## Verification Results (Live Modpack in Isolated Lab `tml-lab`)
- Build: `powershell -ExecutionPolicy Bypass -File build.ps1` -> **0 warnings, 0 errors**, packaged `BossBuildAdvisor.tmod` (`%USERPROFILE%\Documents\My Games\Terraria\tModLoader\Mods\BossBuildAdvisor.tmod`).
- Live Modpack Run (`tModLoader 1.4.4.9+2026.09.3.1`, `ru-RU`, full user modpack including `BossChecklist`, `Redemption`, `Split`, `Ultranium`):
  - Indexed `7925` items across `94` boss/event stages (`Redemption:32`, `Terraria:39`, `Split:12`, `Ultranium:11`) in `42 ms` (`BossChecklist=True`).
  - Discovered `193` armor sets and `6` combat classes (`Terraria/MeleeDamageClass`, `Terraria/RangedDamageClass`, `Terraria/MagicDamageClass`, `Terraria/SummonDamageClass`, `Redemption/RitualistClass`, `Split/HeavyMeleeDamageClass`).
  - Verified clean stage milestones (`KingSlime=1`, `Skeletron=5`, `WoF=7`, `Destroyer=10`, `Plantera=12`, `Golem=13`, `MoonLord=18`) with localized boss names and per-class recommendations at `WoF` across all 6 classes.
  - Verified UI layout geometry at `1280x720` (`Root=(155,63,970x594)`, `BossRows=61`, `ClassTabs=7`, `InsideScreen=True`, `PanelsFit=True`).
- Limitation observed: OS window screenshot capture (`um win shot`) could not capture the window from the sandboxed desktop (`Desktop: exebox-...`), so visual in-world rendering and live interactive clicks should be checked by the user on launch.
- Backup integrity (`um backup diff tml-user-backup`): only `Mods/BossBuildAdvisor.tmod` added and `Mods/enabled.json` updated in `%USERPROFILE%\Documents\My Games\Terraria\tModLoader`; all `Players/`, `Worlds/`, `config.json`, and `ModConfigs/` untouched.
