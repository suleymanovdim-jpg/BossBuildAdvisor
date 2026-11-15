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
- Build:
  - `dotnet build BossBuildAdvisor.csproj -c Release` -> **0 warnings, 0 errors**.
  - Pure tModLoader Roslyn compiler (`tModLoader.dll -tmlsavedirectory .../tml-lab -build .../BossBuildAdvisor`) -> **0 errors and 0 warnings**.
- Live Modpack Run (`tModLoader 1.4.4.9+2026.09.3.1`, `ru-RU`, full user modpack including `BossChecklist 2.2.4`, `Redemption`, `Split`, `Ultranium`):
  - Indexed `7925` items across `80` stages (`Visible=68` at setup, `69/80` in world after downed World Evil unlock, `Hidden=12`) (`Redemption:22`, `Terraria:39`, `Split:10`, `Ultranium:9`), `193` armor sets, and `6` combat classes (`Terraria/MeleeDamageClass`, `Terraria/RangedDamageClass`, `Terraria/MagicDamageClass`, `Terraria/SummonDamageClass`, `Redemption/RitualistClass`, `Split/HeavyMeleeDamageClass`).
  - Exact 1:1 `BossChecklist` order & zero duplicate mod bosses (`[v]Король слизней(1) -> Бог факелов(1.5) -> Тёрн(1.5) -> Бледный бес(1.9) -> [v]Глаз Ктулху(2) -> ...`), with inventory button positioned via reflected `BossChecklist.BossLogConfig.BossLogPos=(-270,-50)`, `BtnLoaded=True`, `Keybind=True`.
  - Difficulty progression gates verified at King Slime (`1`) and Moon Lord (`18`): `ObsidianHelm` correctly gated to `Easy@3.3` (`Obsidian + Silk + ShadowScale`), `AncientShadowHelmet` gated to `VeryHard@0` (`0.2% Eater of Souls`, excluded in `Comfortable`), `Gladiator` gated to `Hard` (`+Hard` / `All`), `Zenith` resolved to `Recipe@18.1`, `UILayout 1040x636 InsideScreen=True PanelsFit=True`.
- Noticeable environment constraints: headless screenshot capture inside the Windows session did not allow interactive pointer/keyboard driving inside the DX11 game window, so visual spacing beside `BossChecklist`'s book icon, typing into `BossSearchBox`, and pressing `K` should be visually confirmed on next user launch.
