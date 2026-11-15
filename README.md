# Boss Build Advisor

Automatic, modpack-aware boss progression build advisor for **tModLoader (Terraria 1.4.4.9+)**.

Instead of relying on hardcoded wikis or static tables, **Boss Build Advisor** scans the mods currently loaded in your modpack at world/mod load time — bosses, items, recipes, NPC drop tables, shop conditions, armor set bonuses, and damage classes — to recommend relevant loadouts for each progression stage.

> **[Русская версия ниже](#boss-build-advisor-ru)**

---

## Features

- **Automatic Modpack Analysis:** Builds a progression graph across vanilla and modded bosses, including cross-mod custom damage classes alongside Melee, Ranged, Magic, and Summon.
- **Stage-by-Stage Expanded Loadouts:** Suggests up to **8 primary + 4 support weapons + 3 ammo options**, **3 selectable armor sets (`#1 / #2 / #3`)** comparing full set bonuses vs. mixed pieces, up to **14 accessories (core + alternatives)**, and **8 buff potions + healing**.
- **Obtainability Difficulty Filter:** Cycles between **`Loot: Comfort`** (Easy/Medium only — filters out 0.2% rare drops, Shimmer skips, and early crates), **`Loot: +Hard`**, and **`Loot: All RNG`**, showing each item's exact difficulty rating and acquisition reason.
- **Exact BossChecklist Sync:** Mirrors `BossChecklist` order, `VisibleOnChecklist` filtering, and defeated status (with standalone fallback if `BossChecklist` is not installed).
- **Bilingual UI:** Built-in English (`en-US`) and Russian (`ru-RU`) localization with localized boss and item names.
- **No World/Save Risk:** Read-only advisory UI and chat commands.

## Controls & Commands

- **Open / Close Advisor UI:** Press **`K`** (configurable under Controls -> Keybindings -> `ToggleAdvisor`, works both in-game and while paused) or click the **`Boss Build Advisor`** icon button positioned next to `BossChecklist`'s book toggle while the inventory is open.
- **Instant Search:** Click the search field and type directly to filter bosses (Terraria chat does not need to be open).
- **Chat Commands:**
  - `/advisor` — toggle or open the advisor window
  - `/advisor next` — jump to the next undefeated boss stage
  - `/advisor stage <boss>` — inspect recommendations for a specific boss stage
  - `/advisor rebuild` — rebuild the progression and item recommendation cache
  - `/advisor dump` — write a diagnostic summary of detected stages and recommendations

## Requirements & Installation

- **Terraria + tModLoader** (`1.4.4.9+`, .NET 8 runtime)
- **Optional:** `BossChecklist` (recommended for exact modpack boss ordering)

### Option A: Steam Workshop (Recommended for Players)
1. Subscribe to **Boss Build Advisor** on Steam Workshop (or copy `BossBuildAdvisor.tmod` into `<Documents>/My Games/Terraria/tModLoader/Mods/`).
2. Enable it in tModLoader under **Workshop -> Manage Mods** and reload mods.

### Option B: Build from Source
1. Clone or copy this repository into your tModLoader `ModSources` folder:
   - Windows: `%USERPROFILE%\Documents\My Games\Terraria\tModLoader\ModSources\BossBuildAdvisor`
   - Linux: `~/.local/share/Terraria/tModLoader/ModSources/BossBuildAdvisor`
   - macOS: `~/Library/Application Support/Terraria/tModLoader/ModSources/BossBuildAdvisor`
2. Open tModLoader -> **Workshop -> Develop Mods** -> click **Build + Reload** on `BossBuildAdvisor`.
3. Alternatively on Windows PowerShell, run:
   ```powershell
   .\build.ps1
   ```
   (Set `$env:TML_DIR` first if tModLoader is not installed under the default Steam directory `${env:ProgramFiles(x86)}\Steam\steamapps\common\tModLoader`.)

## Troubleshooting

If something behaves unexpectedly with a specific modpack, check the tModLoader client log:
- Windows default: `<Steam>\steamapps\common\tModLoader\tModLoader-Logs\client.log`
You can also run `/advisor dump` in chat and attach relevant snippets when opening an issue.

## Credits & Disclosure

- **Author:** `suley`
- **AI Assistance Disclosure:** Code, progression/scoring algorithms, UI layout, and documentation were developed collaboratively with **Antigravity** (AI coding assistant), tested live inside an isolated tModLoader lab environment against modded packs.
- **License:** [MIT](LICENSE)

---

<a id="boss-build-advisor-ru"></a>
## Boss Build Advisor (RU)

Автоматический советник билдов по этапам боссов для **tModLoader (Terraria 1.4.4.9+)**.

Мод не использует захардкоженные таблицы под конкретные моды: при загрузке он сам анализирует установленную сборку (боссов, предметы, рецепты, дроп с врагов, условия покупки, бонусы сетов брони и классы урона — включая кастомные классы из модов) и подбирает актуальное снаряжение под каждый этап игры.

### Основные возможности
- **Расширенные подборки на каждый этап:** до **8 основных + 4 доп. оружий + 3 варианта боеприпасов**, **3 переключаемых варианта брони (`#1 / #2 / #3`)** с анализом бонуса сета, до **14 аксессуаров (основа + замены)** и **8 бафф-зелий + лечение**.
- **Фильтр сложности добычи:** режимы **`Добыча: Комфорт` / `+Сложно` / `Всё RNG`** отсекают ультра-редкий фарм (вроде 0.2% дропа до первого босса), шиммер-скипы и редкие ящики, показывая сложность и причину для каждого предмета.
- **Точная синхронизация с BossChecklist:** повторяет порядок, видимость записей и отметки побед прямо из `BossChecklist` (а без него — работает через автоматический сканер).

### Управление и команды
- **`K`** (настраивается в управлении -> `ToggleAdvisor`, работает и на паузе) или кнопка с иконкой **рядом с книгой BossChecklist** при открытом инвентаре — открыть/закрыть окно билдов.
- **Мгновенный поиск:** строка поиска боссов работает сразу при клике мышью (открывать чат больше не нужно).
- Чат-команды:
  - `/advisor` — открыть/скрыть интерфейс
  - `/advisor next` — показать билд на следующего непобеждённого босса
  - `/advisor stage <boss>` — показать билд для выбранного босса
  - `/advisor rebuild` — пересобрать граф прогрессии и кэш рекомендаций
  - `/advisor dump` — вывести/сохранить диагностическую сводку

### Установка и сборка из исходников
- Для игры достаточно включить мод в tModLoader (**Мастерская -> Управление модами**). Опционально поддерживается **BossChecklist** для точного порядка боссов.
- Для сборки из исходников скопируйте или клонируйте репозиторий в `<Документы>/My Games/Terraria/tModLoader/ModSources/BossBuildAdvisor` и нажмите **Build + Reload** в меню **Мастерская -> Разработка модов** (либо запустите `.\build.ps1` в PowerShell).

### Вклад в разработку
См. [CONTRIBUTING.md](CONTRIBUTING.md). Лицензия — [MIT](LICENSE).
