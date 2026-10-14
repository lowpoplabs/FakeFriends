# Fake Friends

*"Friendship not included."*

An [Oxide/uMod](https://umod.org/) plugin for **Rust**. It populates a low-pop server with
persistent, player-like residents: recurring characters with names, personas and
personalities who keep play schedules, build and upgrade bases, work jobs, run shops, hold
grudges, die real deaths and talk back in chat. They are built on Rust's native bot player
bodies, not scripted NPCs, so they have real inventories, sleep in their beds when
"offline", leave lootable corpses and show up in the kill feed like anyone else.

It is made for servers where you are usually the only human online. Log in and find
evidence that the island kept going without you: a new shop in a hamlet, a grave on the
map, a pamphlet left at your door, a rumour in chat about where somebody is smelting.

> **Status: 0.21.1.** It has run on a live server since July 2026, across several wipes,
> and most of what is described here has been watched working with real players on.
> The defaults are tuned for one server and a large map; expect to adjust them. Interior
> navigation (residents walking through their own doors on the game's new RustNav
> navmesh) is the newest part and has had little play, so please report anything odd there.

## How it works (in-game)

### The residents

Six personas, each with a trade and a place to live:

- **Trapper**: hunts, skins and cooks; lives in the woods or jungle.
- **Farmer**: forages, grows berries and clones, brews teas; grassland near water.
- **Smelter**: mines ore and runs furnaces; arctic or desert, where the ground is dead.
- **Monument Runner**: walks to monuments, cracks the crates and hauls the loot home.
- **Hostile**: prowls, shoots first, loots the corpse and taunts you about your base.
- **Roleplayer**: busks, hands out pamphlets, plays instruments and comes to visit.

A cast of authored characters ships in `cast.json` (edit it freely), with a generator
for extras up to `MaxBots`. Each has personality traits and a schedule profile
(earlybird, daytime, nightowl, random) that decide when it logs on
for a two-to-four-hour session. Sessions run longer in the first two weeks of a wipe,
when real players do too. Join and leave messages hit chat like anyone else's. An
offline resident is a sleeping body in its own bed.

### Homes, hamlets and bases

Each group picks a home spot that suits its persona's biome, preferring ground near a
road or rail line so players actually pass by. The first corridor-side settlers found up
to two hamlets; later groups anchor on a ring around them. The first hamlet is the
market: it gets a gold map marker and its shops pin the map, while every other bot shop
stays secret.

With [CopyPaste](https://umod.org/plugins/copy-paste) installed and a captured blueprint
for the persona, a group builds its stage 1 base on its first productive session, then
upgrades to stages 2 and 3 as its career grows. Doors and storage are code-locked, so
players have to raid. The furniture inside is placed piece by piece over time. A base
that loses its tool cupboard is noticed, mourned in the story log and rebuilt later from
the group's ledger; a breached shell is quietly repasted when nobody is watching, and a
door blown off by the patrol helicopter is rebuilt by its owner at real material cost.
Residents walk in and out through their own doors to stow loot, hole up, log off and wake.

### The economy

Every group runs a small economy: gather, cook, smelt, craft, stock the shop, pay the
upkeep. When a real player is close the work is embodied (the bot really chops the tree
or swings the pickaxe); when nobody is watching it runs as accounting into the same real
boxes. Vending machines sell what the persona actually makes, and the stock is honest:
raid the boxes and the economy stops until it recovers. Career accrues per hour online
and drives base stages and kit tiers; a banked spare kit in the locker is what a resident
re-gears from after a death. Hostiles get no shop by design: their income is the
Cobalt retainer and the kill bounty, and their camp is worth raiding for what is in it.

### Death, graves and grudges

Anything can kill a resident. The corpse drops its kit, the kill feed says who did it,
and the resident respawns at its bed after `RespawnDelayMinutes`. Too many deaths in one
sitting and it logs off for the day. Each resident has a few lives per wipe; by default
only player and bot kills burn them, not animals or the world. A resident out of lives
is gone for the wipe: a headstone goes up where it fell (with a map marker), its group's
base decays if nobody is left, and a newcomer joins the roster a few hours later. Stand
at a headstone and press **use** to read the stone's story.

Kill a resident and its whole group remembers you. Neighbours exchange warnings, so wary
reputation spreads. A grudged player is called out in chat, and a resident decides to
fight or flee on sight depending on its kit tier and aggression. Grudges carry over wipes
unless you turn that off.

### Hostiles and monument runs

Hostiles prowl in the same native player bodies as everyone else, so there is no
silhouette to give them away until they open fire. They hunt real players and bots
alike, loot the corpse, carry the take back to their depot and earn a bounty. They never
path into a player's building privilege, they prefer people they hold a grudge against,
and they will occasionally route past an online player's base to leave a taunt in chat.
Armed residents shoot back, so bot-on-bot gunfights happen.

A Monument Runner, when observed, picks a monument by loot, distance and how many
scientists it can handle, walks there, strips up to six crates and hauls the loot home.
Scientist fire it cannot answer sends it running, and a run that dies at the monument
leaves the loot on the corpse.

### Roleplayers, visits, pamphlets and rumours

Walk up to a Roleplayer and it stops working, faces you, throws gestures, plays its
instrument and presses a pamphlet into your hands. Roleplayers also walk to nearby
player bases every couple of hours, wave at the door and leave a pamphlet plus one unit
of something they actually sell; the owner does not need to be online. Every so often an
online resident drops a rumour about another one into global chat, with a grid reference
rather than a pin, so there is always somewhere to go.

### Chat

Residents greet and mutter near players from template lines (editable in the Oxide lang
file). With an optional language-model endpoint configured, any resident answers nearby
chat in character, with its persona, backstory, traits, grudges and actual shop stock in
the prompt, stands attending you while the conversation lasts, and remembers one sentence
about you afterwards. Newcomers get a written backstory and fallen residents get a
written epitaph. Without the endpoint, all of that falls back to the template lines.

### Wildlife and the patrol helicopter

Wolves, bears and boars (and scarecrows from zombie plugins) are the island's main
killers of residents, so there are valves: wildlife ignores a sleeping resident and one
that has not built yet, armed residents stand and fight animals, home spots avoid
predator territory and safezones, and an animal that camps an anchor is removed when
nobody is watching. A resident hunted by the patrol helicopter leads it away from home
instead of back to its own roof.

## Installation

1. Copy `FakeFriends.cs` into your server's `oxide/plugins/` folder. The plugin compiles
   and loads automatically, writes `oxide/config/FakeFriends.json` and its data files,
   fills the roster from the cast and begins scheduling. Residents spawn, wander and
   keep schedules with no other plugin installed.
2. **Bases need [CopyPaste](https://umod.org/plugins/copy-paste) and a blueprint per
   persona.** Build a small stage 1 base, aim at it and run
   `ff.base.capture <Persona> 1`; repeat for stages 2 and 3 if you want upgrades.
   The furniture you include decides what the persona can do: no workbench means no
   crafting, no vending machine means no shop, no planter means no crops. Without a
   captured stage 1 a group simply never builds (the log says so, throttled).
3. Copy the pamphlet images that come with the release into
   `oxide/data/FakeFriends/photos/`. They are the prints Roleplayers hand out; without
   them the prints are blank.
4. Optionally point `SLMEndpoint` at an OpenAI-compatible chat endpoint (see
   [Optional integrations](#optional-integrations)).
5. Grant `fakefriends.admin` to whoever should use the admin commands and panel.

## Commands

All `ff.*` commands are console commands; they work from the server console, RCON and
the in-game F1 console. `<bot>` is a bot id or name as shown by `ff.list`.

### Roster and sessions

| Command | What it does |
| --- | --- |
| `ff.list` | The roster: status, deaths and kills, lives, schedule, base stage, next session |
| `ff.info <bot>` | Full record for one resident |
| `ff.session start\|end <bot>` | Force a session to start or end |
| `ff.respawn <bot>` | Skip the respawn delay for a dead resident |
| `ff.tp <bot>` | Teleport yourself to a resident (in-game) |
| `ff.retire <bot>` / `ff.revive <bot>` | Take a resident out of scheduling, or bring it back |
| `ff.anchor.reset <bot> [x z]` | Re-roll (or set) a resident's home spot; removes any existing base |
| `ff.roster.fill` | Top the roster up to `MaxBots` from the cast and generator |
| `ff.cast.reload` | Reload `cast.json` (existing residents keep their identities) |
| `ff.story [n]` | The last n story-log entries (default 25) |
| `ff.wipe.status` | Current and stored wipe id and the wipe carry-over settings |

### Bases

| Command | What it does |
| --- | --- |
| `ff.base.capture <Persona> <1-3>` | Capture the build you are aiming at as that persona's stage blueprint |
| `ff.base.list` | The blueprint catalogue, and whether CopyPaste is loaded |
| `ff.base.paste <bot> [stage] [heightNudge]` | Paste a base at the resident's home spot now |
| `ff.base.stage <bot> [stage]` | Advance the base to the next (or given) stage |
| `ff.base.fill <bot>` | Place all remaining interior furniture now |
| `ff.base.remove <bot>` | Remove the resident's base |
| `ff.base.doors <bot>` | Door states and the indoor paths on the live navmesh |
| `ff.base.walkin <bot>` | Walk the resident in to its bed and back out (test) |

### Economy

| Command | What it does |
| --- | --- |
| `ff.econ <bot>` | The group's ledger: career, scrap, stage, kit tier, next action |
| `ff.econ.tick <bot>` | Force one economy tick for the group |
| `ff.produce <bot> [minutes]` | Force production, cooking, crafting and stocking (test) |
| `ff.craft <bot> <item> [count]` | Craft from the base's boxes, ingredient-honest |
| `ff.shop <bot>` | Refresh and dump the resident's vending shop |
| `ff.kit <bot> [tier]` | Show or override a resident's kit tier (re-gears if live) |
| `ff.gather <bot> [tree\|ore]` | Drive an embodied gather on the nearest node (test) |
| `ff.hunt <bot>` | Make the resident hunt the nearest animal (test) |

### Hostiles, fights and runs

| Command | What it does |
| --- | --- |
| `ff.hostile.state` | Every hostile's body, state and whereabouts |
| `ff.hostile.test spawn\|prey\|list\|kill [x z]` | Test harness: spawn a test hostile or a prey body, list them, clean up |
| `ff.fight <bot> [go]` | Show the fight-or-flight gate; `go` forces a fight with the nearest animal |
| `ff.run <bot> [monument]` | Force a monument run (the resident must be online) |
| `ff.run.state` | Monument runs in progress |

### Grudges, graves and chat

| Command | What it does |
| --- | --- |
| `ff.grudge <bot>` | The resident's grudges and reputation |
| `ff.grudge.reset <bot\|all>` | Clear grudges |
| `ff.grave.eulogize` | Write epitaphs for graves that have none (needs the SLM endpoint) |
| `ff.slm` | Endpoint health: successes, fallbacks, last latency, hourly budget, breaker |
| `ff.slm.test <bot> <message>` | Ask a resident something; the reply lands in the console |

### Admin panel

| Command | What it does |
| --- | --- |
| `/ffadmin` (chat) or `ff.ui` | The roster panel: online first, live activity per row, per-resident buttons for teleport, session, retire and revive, an economy detail page with per-player memories and forget buttons, and a story-log tab |

`ff.ui.*` and `ff.grave.close` are the panel's own button callbacks; nobody types them.

## Permissions

- `fakefriends.admin`: every `ff.*` command and the admin panel for players without
  auth level 2. The server console, RCON and auth level 2 always work. The permission
  name is itself a config key (`AdminPermission`).

```bash
oxide.grant group admin fakefriends.admin
```

## Configuration

`oxide/config/FakeFriends.json`. Most keys carry a short explanation in the key name,
for example `"RespawnDelayMinutes"` is plain but `"ReplaceDeadBots (roster refills with
new identities a few hours after a permadeath)"` is one key. They are shortened to the
leading word below. There are a lot of them; these are the ones worth knowing first.

**Population and personas**

- `MaxBots` (15): roster size. `PersonaWeights` (Hostile 0.4, Roleplayer 0.26, Smelter
  0.13, Trapper, Farmer and MonumentRunner 0.07 each) and `GroupSizeWeights` (Solo 0.6,
  Duo 0.3, Trio 0.1) shape the generated part of the roster. Authored cast entries are
  used first, in file order.
- `PersonaHomeBiomes`: persona to home biomes (Trapper Jungle and Temperate, Farmer
  Temperate, Smelter Arctic and Arid; others anywhere).
- `IdentityMode` (`NativeBot`): leave it. `SteamId` needs the server launched with
  `-disable-server-occlusion` or the residents are invisible.

**Sessions and schedules**

- `SessionHoursMin` / `SessionHoursMax` (2 / 4), scaled up in the first two weeks of a
  wipe. `ScheduleWindows`: hours each schedule profile tends to come online.
- `RespawnDelayMinutes` (5). `RagequitDeathsPerSession` (3), `RagequitSpiralDeaths` (2)
  and `RagequitSpiralMinutes` (30): when a bad day ends early.
- `WanderRadiusMeters` (60). `AwareSlowRadiusMeters` (40): a real player this close
  drops a resident from sprint pace to a catchable, trait-shaped walk.

**Homes and bases**

- `AnchorCorridorBiasMeters` (150): prefer home spots this close to a road or rail.
  `AnchorPredatorRadius` (70) and `AnchorSafeZoneMargin` (30) keep homes out of dens and
  safezones.
- `HamletCount` (2), `HamletSlots` (3), `HamletRingMinMeters` / `HamletRingMaxMeters`
  (80 / 150), `HamletMarketMarker` (true).
- `BaseAutoAcquire` (true), `BaseAutoUpgrade` (true), `BaseUpgradeCareerThresholds`
  (100000, 250000). `LockBotStorage` (true). `InteriorFillMinutes` (12).
- `BaseRebuildDelayMinutes` (45), `BaseRebuildScrapCostPerStage` (100),
  `BaseRepairBlockDeficit` (3): the base-integrity valves. `TcInitialStock`: what a new
  tool cupboard is stocked with.

**Economy**

- `ObserveRangeMeters` (120): a player this close makes a base run embodied rather than
  abstract. `EconomyTickSeconds` (45), `EconomyCatchupCapMinutes` (30).
- `KitTierCareerThresholds` (25000, 75000, 175000), `KitBankScrapCost` (30, 60, 120,
  200 by tier). `CraftStockCap` (4), `CraftMaterialReserve` (350).
- `CookUnitsPerMinute` (15), `SmeltUnitsPerMinute` (20), `SmeltRecipes`,
  `CharcoalWoodReserveUnits` (3000), `ShopRestockOrders` (10), `ShopBroadcast` (false:
  shops stay off the map except in the market hamlet).
- Per-persona gather, craft and sell rules, wages and `CareerPerMinute` live in
  `economy_profiles.json`, not the config.

**Death, grudges and fights**

- `PermadeathBaseLives` (3, plus or minus one by caution), `WorldDeathsBurnLives`
  (false), `ReplaceDeadBots` (true), `GraveMapMarkers` (true).
- `GrudgeDecayDays` (0 = never), `GrudgePropagationRangeMeters` (300).
- `FightMinKitTier` (2), `FightMinAggression` (0.4), `FightRangeMeters` (35),
  `FightDamageScale` (1). `SeasoningDays` (14): time on the server makes a better shot.

**Hostiles and runners**

- `HostileNativeBodies` (true), `MaxConcurrentHostiles` (6), `HostileBountyScrap` (25),
  `HostileBountyCareer` (1500), `HostileTaunts` (true), `HostileTauntCooldownMinutes` (45).
- `MonumentRuns` (true), `MonumentRunRangeMeters` (1500), `MonumentRunCooldownMinutes`
  (20), `MonumentRunBudgetMinutes` (30).

**Wildlife valves**

- `AnimalsIgnoreSleepers` (true), `AnimalsIgnoreBaselessBots` (true),
  `AnimalsIgnoreBots` (false: the master switch, visibly fake, for emergencies),
  `AnimalDamageToBotScale` (1), `WildlifeFightMinKitTier` (0: any armed resident fights).
- `WildlifeCampDeaths` (3) inside `WildlifeCampWindowMinutes` (1440) declares a camp;
  `RespawnPredatorRadius` (60) holds a respawn while a predator is close.

**Roleplayers, visits, pamphlets and rumours**

- `RoleplayerAntics` (true), `RoleplayerAttendRangeMeters` (15),
  `RoleplayerRadioStation` (the stream the boombox plays).
- `BotVisits` (true), `VisitCooldownMinutes` (120), `VisitRangeMeters` (600).
- `RumorLines` (true), `RumorIntervalMinutes` (50). `ChatLines` (true): the template
  greetings and mutters.

**Language-model endpoint**

- `SLMEndpoint` (empty = every language-model feature off), `SLMModel` (`llama3.2:3b`),
  `SLMTimeoutSeconds` (25), `SLMMaxTokens` (60), `SLMHourlyBudget` (120 calls per hour,
  0 = unlimited).
- `SLMChatReplies` (true), `SLMChatAllBots` (true: every persona answers per its
  chattiness, false = Roleplayers only), `SLMEarshotMeters` (40), `SLMAttendRangeMeters`
  (8), `SLMConversationSeconds` (90), `ChatCooldownSeconds` (20),
  `SLMPlayerCooldownSeconds` (45), `SLMPostConvoCooldownMinutes` (5).

**Presence and housekeeping**

- `Presence: join/leave chat messages`, `Presence: kill feed chat messages`,
  `Presence: night light at anchor` (all true).
- `SaveIntervalSeconds` (300). `MetricsIntervalSeconds` (60, 0 = off): how often
  `metrics.json` is written.

**Wipe policy**

- `WipeCastReset` (false) and `WipeGrudgeReset` (false): whether identities and grudges
  start fresh on a map wipe. Bases, graves, hamlets and the story log always reset with
  the map; the characters and their histories carry on unless you say otherwise.

## Data files

`oxide/data/FakeFriends/`:

- `cast.json`: the authored characters. Edit and `ff.cast.reload`. Entry order matters:
  the first `MaxBots` entries are the wipe roster, the rest are the bench that
  permadeath replacements come from.
- `bots/bot_<id>.json`: one record per identity (traits, state, grudges, memories).
- `index.json`: the roster index, wipe id, graves and hamlets. Do not hand-delete it
  while bot files exist, or the roster is rebuilt from scratch over the old files.
- `storylog.json`: the island's history, also rendered readably under
  `oxide/logs/FakeFriends/`.
- `economy.json`: per-group ledgers. `economy_profiles.json`: per-persona gather, craft,
  sell, wage and career rules; tune it live. `kits.json`: the four-tier kit ladder per
  persona.
- `bases.json`: the blueprint catalogue written by `ff.base.capture`; the captures
  themselves are CopyPaste files under `oxide/data/copypaste/`.
- `photos/`: JPGs stamped onto the prints Roleplayers hand out (854x480 aspect, up to
  512 KB each, up to 64 files).
- `metrics.json`: a snapshot of population, economy, combat and chat counters, rewritten
  every `MetricsIntervalSeconds`. An admin who runs a metrics pipeline can point it at
  this file.

Chat pools and presence lines are registered through Oxide's lang system, so they can be
edited or localised in `oxide/lang/en/FakeFriends.json`.

## Optional integrations

- **[CopyPaste](https://umod.org/plugins/copy-paste)** (soft dependency): bases. Without
  it residents still spawn, wander, keep schedules, die and hold grudges, but no group
  ever builds, so there is no economy, no shops and no spare kits; a rebuild that falls
  due while CopyPaste is missing is deferred and retried.
- **An OpenAI-compatible chat endpoint** (`SLMEndpoint`, off by default): in-character
  chat replies, per-player memories, written backstories for newcomers and epitaphs for
  the fallen. The plugin posts to `<endpoint>/v1/chat/completions`; a local
  [Ollama](https://ollama.com/) serving a small model is what it was built against, and
  nothing requires a cloud service. Every call has a timeout, an hourly budget and a
  circuit breaker, and on any failure the template lines fire instead. Model output is
  stripped of markdown and rich-text tags before it reaches chat.
- **RustQuests** (soft): if present, residents keep their bases clear of its trader
  shops. Nothing changes without it.
- **Night Zombies**: its scarecrows are treated as wildlife, so the wildlife valves and
  camp breaker cover them and armed residents fight them.

## Compatibility

- Built against the October 2026 Rust builds. Versions from 0.20.2 onward need the
  October 1, 2026 update (Facepunch's RustNav navmesh) or newer, and work unchanged if
  the server is launched with `-useOldNavmesh`. 0.19.1 is the last version for the
  September 2026 build.
- Oxide/uMod. Residents use the game's native bot player bodies and the server-side
  navmesh; no client mod is needed.
- Other NPC plugins with their own targeting code may still attack residents; only the
  game's wildlife and Night Zombies' scarecrows are covered so far.
- `IdentityMode: SteamId` requires `-disable-server-occlusion`; the default does not.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

[MIT](LICENSE)
