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
  - Follow-up fix pass (live verified in `tml-lab`, `0` compile errors/warnings):
    - Custom mod recipe/condition token matching + special NPC/quest condition gating (`ConditionProgressionMapper`): `BladeOfTheMountain` / `Zweihander` (`ForgedByCalavia` / `RepairedByFallen`) no longer leak at King Slime (`1`).
    - Mobility boots vs wings separation (`ItemTooltipAnalyzer` `item.wingSlot > 0`, EN/RU boot keywords, `RecipeGroup` upgrade edges, and strict precursor suppression in `BuildRecommender`): `RocketBoots` suppressed cleanly whenever `SpectreBoots` / `LightningBoots` are available, and `LightningBoots` / `FrostsparkBoots` equip cleanly alongside wings.
    - Biome resource floor & armor synergy (`ProgressionGraph.SeedGuaranteedBiomeResources`, `ItemID.Sets.IsSpaceGun`, set-bonus deduplication): `Meteorite` properly resolves to post-evil-boss mining (`Medium@3.01` instead of `2% Meteor Head`), `ArmorOptions` deduplicates `Jungle` / `Ancient Cobalt` variants, and `Meteor Armor` guarantees `Space Gun` when active.
- Noticeable environment constraints: headless screenshot capture inside the Windows session did not allow interactive pointer/keyboard driving inside the DX11 game window, so visual spacing beside `BossChecklist`'s book icon, typing into `BossSearchBox`, and pressing `K` should be visually confirmed on next user launch.

### Multi-Agent Audit & Progression/UI Fixes
- `Systems/AdvisorSystem.cs`: skipped overlay during menu/options/fancy-UI/fullscreen map states; anchored inventory toggle button to BossChecklist's `34x38` `BossLogPos` with screen-edge/chest-UI flip and proper mouse-blocking guards.
- `Players/AdvisorPlayer.cs` & `Commands/AdvisorCommand.cs`: unified hotkey blocking via `AdvisorSystem.IsHotkeyBlocked()`, added `Main.dedServ` guard, lazy `sys.RebuildAnalysis()`, `/advisor dump`, clean 2-piece robe set formatting, and localized class display names.
- `Localization/`: aligned `en-US` and `ru-RU` `.hjson` keys (`UI.*`, `Recommender.*`, `Loot.*`), reaching `MissingLocKeys(0)=[]` in live `ru-RU` modpack test.
- `Core/BuildRecommender.cs` & `Core/ItemTooltipAnalyzer.cs`: fixed vanilla balloon `ItemID` references, fixed robe body-in-legs self-pairing, and tightened buff/potion class affinity (`Archery`, `MagicPower`, `ManaRegeneration`, `Summoning`, `Tipsy`) so off-class potions are excluded.
- `Core/ProgressionGraph.cs`: calibrated Pre-Hardmode rare Lifeform Analyzer NPCs (`Tim`, `UndeadMiner`, `Pinky`, etc.) and seeded Pre-Hardmode ores, gems, and common surface/underground/ice/jungle chest staples (`Aglet`, `HermesBoots`, `CloudinaBottle`, `BandofRegeneration`, `FlurryBoots`, `BlizzardinaBottle`, `IceSkates`, `AnkletoftheWind`) in `SeedGuaranteedBiomeResources` so low-chance crate drops and late shops never mask `Comfortable` upgrade paths (`SpectreBoots`, `LightningBoots`, `FrostsparkBoots`, `DiamondRobe`).
- Live verification in isolated `tml-lab` (`ru-RU` modpack): `92` stages, `7925` items, `210` armor sets indexed in `497 ms`, `MissingLocKeys(0)=[]`, `InsideScreen=True`, `PanelsFit=True`, `0` advisor exceptions, clean `Release` + `.tmod` build (`0` warnings, `0` errors). Headless `um win shot` DX11 capture still fails in background (`0x80070057`), so button placement beside BossChecklist's icon relies on exact `BossLogPos` config coordinates.
- `build.ps1`: trimmed trailing slash from `$projDir` passed to `-build` so directories containing spaces (such as `My Games`) package cleanly.

### v0.2.1 — Inventory Toggle Button Reliability Fix
- Added `UI/AdvisorInventoryButton.cs` (`AdvisorInventoryButton` + `AdvisorButtonUIState`) using tModLoader's standard `UserInterface` + `UIElement.OnLeftClick` lifecycle in `UpdateUI` instead of `Main.mouseLeft && Main.mouseLeftRelease` inside `DrawInterface`, claiming `mouseInterface`/`blockMouse` before `Player.Update`, preventing `NaN` initial-placement staleness, and routing hover tooltips through `Main.hoverItemName`.
- Updated `Systems/AdvisorSystem.cs` to own `_buttonInterface` / `_buttonState` across `Load`, `Unload`, `UpdateUI`, and `"BossBuildAdvisor: Inventory Toggle"`.
- Verified live in `tml-lab` (`1920x1017`, `UIScale = 1.1`, `18`-mod modpack): button placed at `{X:1435 Y:876 Width:32 Height:34}` next to `BossChecklist`'s `{X:1475 Y:874 Width:34 Height:38}`, clicking our book opens `BossBuildAdvisor` (`IsOpen=True`), clicking `BossChecklist`'s book opens `BossLog` (`BossLogVisible=True`), `0` mod errors in `client.log`.
