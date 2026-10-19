using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Fake Friends", "LowPopLabs", "0.21.4")]
    [Description("Persistent, player-like NPC residents for low-pop servers: recurring characters with personas, play schedules, sleeper bodies, home regions, and real deaths. Friendship not included.")]
    public class FakeFriends : RustPlugin
    {
        // Rust 2633.288 (2026-09-03) removed BaseEntity.SetFlag; flags now change inside a
        // FlagsUpdateScope. SendNetworkUpdate mode matches what the old call did.
        private static void SetFlagNet(BaseEntity entity, BaseEntity.Flags flag, bool value)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                scope.Set(flag, value);
            }
        }
        [PluginReference] private Plugin CopyPaste;   // soft dependency (Phase 2 bases)
        [PluginReference] private Plugin RustQuests;  // soft: keeps bot bases off the quest traders' shops

        // -----------------------------------------------------------------
        // Phase 1 (core framework) — Stage A: config, lang, data layer, cast.
        // Design source of truth: fakefriends-handoff-brief.md
        // Diagnostics gate:       docs/DIAGNOSTICS.md (Phase 0, 2026-07-17)
        // Decisions:              docs/decisions/0001, 0002
        // Stage A deliberately performs NO world interaction: identities are
        // data records only. Spawning arrives in Stage B.
        // -----------------------------------------------------------------

        #region Config

        private ConfigData _config;

        private class ConfigData
        {
            [JsonProperty("MaxBots")] public int MaxBots = 15;
            [JsonProperty("ReplaceDeadBots (roster refills with new identities a few hours after a permadeath)")] public bool ReplaceDeadBots = true;
            [JsonProperty("GrudgeDecayDays (0 = grudges never fade)")] public float GrudgeDecayDays = 0f;
            [JsonProperty("GrudgePropagationRangeMeters (online bots whose homes sit this close exchange warnings)")] public float GrudgePropagationRange = 300f;
            [JsonProperty("IdentityMode (NativeBot|SteamId — see docs/decisions/0001)")] public string IdentityMode = "NativeBot";
            // 15m cost ~41 bot-hours across 166 deaths in the 2026-07-26 wipe week —
            // a quarter of the roster's whole productive budget spent lying dead.
            [JsonProperty("RespawnDelayMinutes")] public float RespawnDelayMinutes = 5f;
            // Rage-quit valve (see the respawn path): a hard cap on deaths per sitting,
            // plus a tighter "died twice in a row, fast" spiral check. Two deaths spread
            // over a 4h session is bad luck and he plays on; two inside half an hour is a
            // predator camping the anchor and a real player would log off.
            [JsonProperty("RagequitDeathsPerSession (hard cap on deaths in one sitting)")] public int RagequitDeaths = 3;
            [JsonProperty("RagequitSpiralDeaths (deaths within the spiral window that end the day)")] public int RagequitSpiralDeaths = 2;
            [JsonProperty("RagequitSpiralMinutes (window for the spiral check)")] public float RagequitSpiralMinutes = 30f;
            // Wildlife exemptions. Both cover states where the bot can neither fight back
            // nor be seen losing: a sleeper is the logged-off body, and a bot with no base
            // yet has nowhere to retreat to (the fight-or-flight path already declines to
            // hole up for one). Players, other bots and scientists are never exempted —
            // deaths that anyone could witness stay real. v0.21.1: scarecrows (Night
            // Zombies' bodies) ride these same valves — see IsWildlife.
            [JsonProperty("AnimalsIgnoreSleepers (wildlife won't maul a bot in its logged-off state)")] public bool AnimalsIgnoreSleepers = true;
            [JsonProperty("AnimalsIgnoreBaselessBots (wildlife won't hunt a bot that hasn't built yet)")] public bool AnimalsIgnoreBaseless = true;
            // Decision 0010 (wildlife pacing arc). The master switch is an EMERGENCY
            // valve for unattended stretches — default off because wolves visibly
            // ignoring bots reads as fake to anyone playing alongside them.
            [JsonProperty("AnimalsIgnoreBots (MASTER valve: wildlife never targets any bot; visibly fake — emergencies only)")] public bool AnimalsIgnoreBots = false;
            [JsonProperty("WildlifeFightMinKitTier (worn tier to stand and fight an ANIMAL; 0 = any armed bot fights)")] public int WildlifeFightMinKitTier = 0;
            [JsonProperty("AnimalDamageToBotScale (multiplier on wildlife damage to bots; 1 = vanilla, hook stays cold)")] public float AnimalDamageToBotScale = 1f;
            [JsonProperty("RespawnPredatorRadius (hold a respawn while a predator is this close; Gen2 senses outrange the old 30)")] public float RespawnPredatorRadius = 60f;
            [JsonProperty("WildlifeCampDeaths (wildlife deaths near the anchor inside the window that declare a camp; 0 = off)")] public int WildlifeCampDeaths = 3;
            // v0.15.2: 45 → 1440. The 45m window was tuned to Krupp's 6-minute wolf
            // metronome and never tripped for slow camps — Havoc_77's boar killed once
            // per session, hours apart, 16 deaths at the same I12 anchor with the
            // breaker silent. A day-wide window catches both cadences.
            [JsonProperty("WildlifeCampWindowMinutes (window for the camp check)")] public float WildlifeCampWindowMinutes = 1440f;
            [JsonProperty("AnchorPredatorRadius (reject a home spot with a bear/wolf/boar this close; 0 = off)")] public float AnchorPredatorRadius = 70f;
            [JsonProperty("AnchorSafeZoneMargin (keep home spots this far outside any safezone trigger; 0 = off)")] public float AnchorSafeZoneMargin = 30f;
            // --- Interaction arc I-1/I-2 (v0.18.0): live where players travel -------
            [JsonProperty("AnchorCorridorBiasMeters (prefer home spots within this range of a road/rail spline; 0 = pure scatter like before)")] public float AnchorCorridorBias = 150f;
            [JsonProperty("HamletCount (hamlets founded per wipe — clusters of neighbor groups beside a corridor; 0 = off)")] public int HamletCount = 2;
            [JsonProperty("HamletSlots (groups per hamlet before it stops taking neighbors)")] public int HamletSlots = 3;
            [JsonProperty("HamletRingMinMeters (closest a hamlet neighbor anchors to the founder)")] public float HamletRingMin = 80f;
            [JsonProperty("HamletRingMaxMeters (farthest a hamlet neighbor anchors from the founder)")] public float HamletRingMax = 150f;
            [JsonProperty("HamletMarketMarker (map-pin the first hamlet as the market: gold radius marker + its shops broadcast)")] public bool HamletMarketMarker = true;
            [JsonProperty("PermadeathBaseLives (deaths this wipe before a bot falls for good; ±1 by caution)")] public int PermadeathBaseLives = 3;
            [JsonProperty("WipeCastReset")] public bool WipeCastReset = false;
            [JsonProperty("WipeGrudgeReset")] public bool WipeGrudgeReset = false;
            [JsonProperty("BotVsBotDamage (plumbing only until Phase 5)")] public bool BotVsBotDamage = true;
            [JsonProperty("GroupSizeWeights")] public Dictionary<string, float> GroupSizeWeights = new Dictionary<string, float> { { "Solo", 0.6f }, { "Duo", 0.3f }, { "Trio", 0.1f } };
            [JsonProperty("PersonaWeights")] public Dictionary<string, float> PersonaWeights = new Dictionary<string, float>
            {
                // v0.18 re-balance (owner call 2026-08-31): target roster is
                // 2 Smelter / 1 Farmer / 1 Trapper / 1 Runner / 4 Roleplayer /
                // 6 Hostile — hostiles carry the danger, roleplayers the encounters.
                { "Trapper", 0.07f }, { "Farmer", 0.07f }, { "Smelter", 0.13f },
                { "Hostile", 0.4f }, { "MonumentRunner", 0.07f }, { "Roleplayer", 0.26f }
            };
            [JsonProperty("SessionHoursMin")] public float SessionHoursMin = 2f;
            [JsonProperty("SessionHoursMax")] public float SessionHoursMax = 4f;
            [JsonProperty("WanderRadiusMeters")] public float WanderRadius = 60f;
            [JsonProperty("BasePasteHeightOffset (rigid vertical nudge on top of autoheight)")] public float BasePasteHeightOffset = 0f;
            [JsonProperty("BaseFootprintRadiusMeters (flatness/clearance check for base spots)")] public float BaseFootprint = 9f;
            [JsonProperty("InteriorFillMinutes (real minutes between a bot placing each furniture piece)")] public float InteriorFillMinutes = 12f;
            [JsonProperty("DoorFlickSeconds (how long a base door stays open when a bot enters/leaves)")] public float DoorFlickSeconds = 2.5f;
            [JsonProperty("Presence: join/leave chat messages")] public bool PresenceJoinLeave = true;
            [JsonProperty("Presence: kill feed chat messages")] public bool PresenceKillFeed = true;
            [JsonProperty("Presence: night light at anchor")] public bool PresenceNightLight = true;
            [JsonProperty("AdminPermission")] public string AdminPermission = "fakefriends.admin";
            [JsonProperty("SaveIntervalSeconds")] public float SaveIntervalSeconds = 300f;
            // --- Phase 3 Stage F: progression wiring ---------------------------------
            [JsonProperty("BaseAutoAcquire (a bot auto-builds stage 1 once it has a home + captured stages)")] public bool BaseAutoAcquire = true;
            [JsonProperty("BaseAutoUpgrade (career thresholds trigger stage 2/3 upgrades)")] public bool BaseAutoUpgrade = true;
            // Pacing target (2026-07-22, from 4 days of live data at ~15k units/day
            // median): stage 1 on day one (auto-acquire), stage 2 ≈ end of week one,
            // stage 3 ≈ a week later. ObjectCreationHandling.Replace stops Newtonsoft
            // APPENDING file values onto these defaults on every load (the live config
            // had doubled lists).
            [JsonProperty("BaseUpgradeCareerThresholds (career-produced units to reach stage 2, 3, …)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<long> BaseUpgradeThresholds = new List<long> { 100000, 250000 };
            [JsonProperty("KitTierCareerThresholds (career-produced units to reach kit tier 1, 2, 3)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<long> KitTierThresholds = new List<long> { 25000, 75000, 175000 };
            [JsonProperty("KitBankScrapCost (scrap to bank a spare kit, indexed by kit tier)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<int> KitBankScrapCost = new List<int> { 30, 60, 120, 200 };
            [JsonProperty("LockBotStorage (code-lock bot boxes/lockers + shell doors so players must raid)")] public bool LockBotStorage = true;
            // --- Phase 5: Hostile module (decision 0007) ------------------------------
            [JsonProperty("HostileBountyScrap (Cobalt pays this per confirmed hostile kill; 0 disables)")] public int HostileBountyScrap = 25;
            // v0.16.0: 500 → 1500. At 500 a hostile needed 200 kills for stage 2 —
            // no wipe has produced a tenth of that. v0.20.0: the baseline is the
            // universal time-based career (profile CareerPerMinute); kills stay the bonus.
            [JsonProperty("HostileBountyCareer (career units credited per hostile kill — kills drive base/kit progression; 0 disables)")] public int HostileBountyCareer = 1500;
            [JsonProperty("ChatLines (bots greet and mutter near players/bots, Chattiness-gated)")] public bool ChatLines = true;
            // v0.17.0: rumor lines — on 4700-size maps players rarely stumble across a
            // bot (metric: zero conversations started in wipe week 1), so bots gossip
            // grid-ref rumors about each other into global chat. A grid square (~146 m)
            // is vaguer than a map pin: it invites a trip without giving the camp away.
            [JsonProperty("RumorLines (online bots occasionally gossip a grid-ref rumor about another bot into global chat)")] public bool RumorLines = true;
            [JsonProperty("RumorIntervalMinutes (average minutes between rumors while a player is online, randomized ±50%)")] public float RumorIntervalMinutes = 50f;
            // --- Interaction arc I-3 (v0.19.0): bots visit players --------------------
            [JsonProperty("BotVisits (online Roleplayers occasionally walk to a nearby player base, wave, and leave a pamphlet + gift at the door)")] public bool BotVisits = true;
            [JsonProperty("VisitCooldownMinutes (average minutes between one bot's visits, randomized ±50%)")] public float VisitCooldownMinutes = 120f;
            [JsonProperty("VisitRangeMeters (farthest player base a bot travels to visit)")] public float VisitRange = 600f;
            [JsonProperty("HostileTaunts (prowling hostiles occasionally case a nearby online player's base with a chat line)")] public bool HostileTaunts = true;
            [JsonProperty("HostileTauntCooldownMinutes (per-hostile average cooldown between cased-base taunts)")] public float HostileTauntCooldown = 45f;
            [JsonProperty("RoleplayerAntics (gestures, boombox, camera photo drops near players)")] public bool RoleplayerAntics = true;
            [JsonProperty("RoleplayerRadioStation (stream URL/IP the boombox plays)")] public string RoleplayerRadioStation = "rustradio.facepunch.com";
            // v0.18.0: 2 → 6 (owner call 2026-08-31): the whole squad may prowl at
            // once. The 2-slot lottery starved fixed-window hostiles (WH-4: Nils_44
            // logged 3 productive hours in 6 days losing it to the random-schedule
            // ones); with 6 of 15 bots Hostile the cap was the wrong throttle.
            [JsonProperty("MaxConcurrentHostiles (hostiles prowling at once; extra sessions defer)")] public int MaxConcurrentHostiles = 6;
            // --- Phase 6: MonumentRunner runs -----------------------------------------
            [JsonProperty("MonumentRuns (MonumentRunners do embodied monument loot runs when observed)")] public bool MonumentRuns = true;
            [JsonProperty("MonumentRunRangeMeters (farthest monument a runner travels to)")] public float MonumentRunRange = 1500f;
            [JsonProperty("MonumentRunCooldownMinutes (average minutes between one runner's runs, randomized ±50%)")] public float MonumentRunCooldownMinutes = 20f;
            [JsonProperty("MonumentRunBudgetMinutes (a run that overruns this heads home with whatever it has)")] public float MonumentRunBudgetMinutes = 30f;
            [JsonProperty("WorldDeathsBurnLives (false: only player/bot kills count toward permadeath)")] public bool WorldDeathsBurnLives = false;
            // --- Persona content (externalized from code) -----------------------------
            [JsonProperty("PersonaHomeBiomes (persona → home biomes: Arid|Temperate|Tundra|Arctic|Jungle; the search widens to +Tundra then any if none fit)")]
            public Dictionary<string, List<string>> PersonaHomeBiomes = new Dictionary<string, List<string>>
            {
                ["Trapper"] = new List<string> { "Jungle", "Temperate" },
                ["Farmer"] = new List<string> { "Temperate" },
                ["Smelter"] = new List<string> { "Arctic", "Arid" },
            };
            [JsonProperty("ScheduleWindows (schedule profile → [startHour, endHour] the bot tends to come online)")]
            public Dictionary<string, List<int>> ScheduleWindows = new Dictionary<string, List<int>>
            {
                ["earlybird"] = new List<int> { 6, 12 },
                ["daytime"] = new List<int> { 10, 18 },
                ["nightowl"] = new List<int> { 19, 27 }, // 19:00–03:00
                ["random"] = new List<int> { 0, 24 },
            };
            [JsonProperty("TcInitialStock (materials stocked into a bot TC when its base is built)")]
            public Dictionary<string, int> TcInitialStock = new Dictionary<string, int> { ["wood"] = 2000, ["stones"] = 1000, ["metal.fragments"] = 300, ["metal.refined"] = 25 };
            [JsonProperty("BaseRebuildDelayMinutes (a group whose base was destroyed starts rebuilding after this long; 0 disables auto-rebuild)")]
            public float BaseRebuildDelayMinutes = 45f;
            [JsonProperty("BaseRebuildScrapCostPerStage (scrap charged from the group ledger for a rebuild, times the lost base's stage; charges only what the group can afford)")]
            public int BaseRebuildScrapCostPerStage = 100;
            [JsonProperty("BaseRepairBlockDeficit (repaste a damaged shell when this many building blocks are missing and no player is watching; 0 disables shell repair)")]
            public int BaseRepairBlockDeficit = 3;
            [JsonProperty("CraftStockCap (a craft rule stands down while the base holds this many multiples of its yield unsold; 0 = uncapped)")]
            public int CraftStockCap = 4;
            [JsonProperty("CraftMaterialReserve (crafting leaves at least this much wood/stones/metal.fragments banked for repairs and upkeep; 0 = off)")]
            public int CraftMaterialReserve = 350;
            // --- Economy / behavior tuning --------------------------------------------
            [JsonProperty("ObserveRangeMeters (a real player within this range makes a base run embodied instead of abstract)")] public float ObserveRange = 120f;
            [JsonProperty("EconomyTickSeconds (how often the economy advances)")] public float EconomyTickSeconds = 45f;
            [JsonProperty("EconomyCatchupCapMinutes (cap on catch-up production after downtime)")] public float EconomyCatchupCapMin = 30f;
            [JsonProperty("CookUnitsPerMinute (raw→cooked meat throughput while a furnace is placed)")] public float CookUnitsPerMin = 15f;
            [JsonProperty("SmeltUnitsPerMinute (ore→product throughput while a furnace is placed)")] public float SmeltUnitsPerMin = 20f;
            [JsonProperty("SmeltRecipes (ore shortname → smelted product; tune live like the economy profiles)")]
            public Dictionary<string, string> SmeltRecipes = new Dictionary<string, string>
            {
                ["metal.ore"] = "metal.fragments",
                ["sulfur.ore"] = "sulfur",
                ["hq.metal.ore"] = "metal.refined",
            };
            [JsonProperty("CharcoalWoodReserveUnits (wood kept in stock before spare wood chars to charcoal)")] public int CharcoalWoodReserve = 3000;
            [JsonProperty("ShopRestockOrders (keep the shop stocked to this many orders' worth per good)")] public int ShopRestockOrders = 10;
            [JsonProperty("HuntHitDamage (embodied hunt melee damage per swing)")] public float HuntHitDamage = 35f;
            [JsonProperty("HuntMeleeRange (embodied hunt melee reach, metres)")] public float HuntMeleeRange = 2.5f;
            [JsonProperty("BotRegenPerTick (hp healed per 30s scheduler tick while resting/working)")] public float BotRegenPerTick = 10f;
            // --- 5A2-R: resident fight-or-flight --------------------------------------
            [JsonProperty("FightMinKitTier (worn kit tier a resident needs before standing and fighting; below it he flees)")] public int FightMinKitTier = 2;
            [JsonProperty("FightMinAggression (aggression trait needed to fight rather than flee)")] public float FightMinAggression = 0.4f;
            [JsonProperty("FightRangeMeters (max stand-and-fight firing range)")] public float FightRange = 35f;
            [JsonProperty("FightDamageScale (global multiplier on resident weapon damage)")] public float FightDamageScale = 1f;
            // --- Phase 5.5: hostiles on native-bot bodies -----------------------------
            [JsonProperty("HostileNativeBodies (hostiles prowl in native-bot player bodies — perfect disguise; false = legacy scientist chassis)")] public bool HostileNativeBodies = true;
            // --- World-feel pass (2026-07-22) -----------------------------------------
            [JsonProperty("ShopBroadcast (bot vending machines mark the map; off = players stumble across them)")] public bool ShopBroadcast = false;
            [JsonProperty("GraveMapMarkers (fallen bots' headstones show on the map as points of interest)")] public bool GraveMapMarkers = true;
            [JsonProperty("SeasoningDays (days on the server this wipe until a bot reaches its full seasoned combat bonus; 0 disables)")] public float SeasoningDays = 14f;
            // --- Phase 7: Roleplayer SLM (decision 0009) ------------------------------
            [JsonProperty("SLMEndpoint (OpenAI-compatible chat endpoint, e.g. http://localhost:11434 — EMPTY disables every SLM feature, template behavior everywhere)")] public string SLMEndpoint = "";
            [JsonProperty("SLMModel (model name the endpoint expects)")] public string SLMModel = "llama3.2:3b";
            [JsonProperty("SLMTimeoutSeconds (per-call cap; on expiry the template fallback fires — must exceed the ~10 s cold model load or the first call after an idle day always fails)")] public float SLMTimeoutSeconds = 25f; // 25: an 8-12B model's cold load off disk can exceed the old 15
            [JsonProperty("SLMMaxTokens (reply length cap sent to the model)")] public int SLMMaxTokens = 60;
            [JsonProperty("SLMChatReplies (online Roleplayers answer nearby players' chat via the SLM)")] public bool SLMChatReplies = true;
            [JsonProperty("SLMEarshotMeters (a Roleplayer replies to chat typed by players within this range)")] public float SLMEarshot = 40f;
            [JsonProperty("ChatCooldownSeconds (per-bot floor between SLM chat replies)")] public float ChatCooldownSeconds = 20f;
            [JsonProperty("SLMPlayerCooldownSeconds (per bot+player pair floor between replies)")] public float SLMPlayerCooldownSeconds = 45f;
            [JsonProperty("SLMHourlyBudget (global SLM calls per rolling hour across all features; 0 = unlimited)")] public int SLMHourlyBudget = 120;
            [JsonProperty("SLMChatAllBots (every persona answers nearby chat per its Chattiness; false = Roleplayers only)")] public bool SLMChatAllBots = true;
            [JsonProperty("SLMConversationSeconds (after replying the bot stands attending the player this long; follow-ups bypass the pair cooldown)")] public float SLMConversationSeconds = 90f;
            [JsonProperty("SLMConvoReplySeconds (reply floor inside an active conversation)")] public float SLMConvoReplySeconds = 8f;
            [JsonProperty("SLMPostConvoCooldownMinutes (after a conversation ends the bot goes back to work — that player can't re-engage it for this long)")] public float SLMPostConvoCooldown = 5f;
            [JsonProperty("SLMAttendRangeMeters (a bot pauses work and faces a player who walks this close — the invitation to chat; 0 disables)")] public float SLMAttendRange = 8f;
            [JsonProperty("MetricsIntervalSeconds (write data/FakeFriends/metrics.json for Telegraf/Grafana this often; 0 disables)")] public float MetricsIntervalSeconds = 60f;
            // --- v0.15: engagement pass (owner feedback 2026-08-08) -------------------
            [JsonProperty("AwareSlowRadiusMeters (a real player this close drops a bot from sprint-class to a catchable, trait-shaped pace; 0 disables)")] public float AwareSlowRadius = 40f;
            [JsonProperty("RoleplayerAttendRangeMeters (a Roleplayer stops working and puts on a show for a player this close, with or without the SLM)")] public float RoleplayerAttendRange = 15f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            PrintWarning("Created new default configuration.");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("config file deserialized to null");
                SaveConfig(); // re-serialize only when the file parsed (adds new keys)
            }
            catch (Exception e)
            {
                // Run on defaults but do NOT overwrite the file — the user's
                // customizations survive for them to fix the typo.
                PrintWarning($"Config invalid ({e.Message}) — running on defaults; file left untouched for repair.");
                _config = new ConfigData();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            var messages = new Dictionary<string, string>
            {
                ["Presence.Join"] = "{0} has connected",
                ["Presence.Leave"] = "{0} has disconnected",
                ["Presence.KillByBot"] = "{0} was killed by {1}",
                ["Presence.KillByPlayer"] = "{0} was killed by {1}",
                ["Presence.KillByWorld"] = "{0} died ({1})",
                ["Presence.Permadeath"] = "{0} won't be coming back",
                ["Presence.GrudgeWarn"] = "{0}: I haven't forgotten what you did, {1}.",
                ["Admin.NoPermission"] = "You don't have permission to use this command.",
            };
            // Chat pools ship as defaults here; operators edit/localize them in oxide/lang.
            foreach (var pool in ChatPools)
                for (var i = 0; i < pool.Value.Length; i++)
                    messages[$"Chat.{pool.Key}.{i}"] = pool.Value[i];
            lang.RegisterMessages(messages, this);
        }

        private string L(string key, params object[] args)
        {
            var msg = lang.GetMessage(key, this);
            return args.Length > 0 ? string.Format(msg, args) : msg;
        }

        // Console + own file (oxide/logs/FakeFriends/) — the file survives even when
        // the main Oxide log writer is wedged, and doubles as the dev audit trail.
        private void DLog(string msg)
        {
            Puts(msg);
            LogToFile("log", $"[{DateTime.Now:HH:mm:ss}] {msg}", this);
        }

        #endregion

        #region Chat & Roleplayer flavor (content pass — docs/content/chat-lines.md)

        // Canned proximity chat, pre-SLM (Phase 7 makes these the offline fallback).
        // Pools registered through Lang (Chat.<Pool>.<i>) so operators can edit/localize
        // in oxide/lang — these arrays are just the shipped defaults.
        private static readonly Dictionary<string, string[]> ChatPools = new Dictionary<string, string[]>
        {
            ["Greet.Trapper"] = new[] {
                "you're a long way from the road, {0}", "seen a big male wolf around here. keep your eyes up",
                "don't touch the snares. they don't care who they catch", "if you're here to trade, shop's on the cabin",
                "quiet today. too quiet for deer anyway", "you hunt? this valley's mine, past the ridge is yours",
                "careful in the tall grass, {0}", "smelled the smoke from my rack? cooked meat's for sale" },
            ["Idle.Trapper"] = new[] {
                "tracks everywhere but nothing standing in them", "need to re-string the east snares",
                "bear fat burns longer than wood, people don't know that", "this hide won't scrape itself",
                "three arrows for one deer. getting old", "the wolves are moving different this wipe" },
            ["Greet.Farmer"] = new[] {
                "morning {0}. teas are stocked if you're buying", "mind the sprinklers, ground's soft",
                "you look pale. healing tea, first one's fair price", "crops are coming in good this week",
                "don't stand on the planters please", "if you're after clones, come back when the cuttings take",
                "rain's good for me, bad for you huh" },
            ["Idle.Farmer"] = new[] {
                "berries want more sun than this", "one more harvest and i can afford the good genes",
                "weeds again. always weeds", "the yellow ones are fussy. worth it though",
                "mixing table's warm, batch going now", "should put a scarecrow up. do scarecrows even work here" },
            ["Greet.Smelter"] = new[] {
                "furnaces are hot, watch your step {0}", "ammo's stocked. powder too, if you're making your own",
                "you buying or just warming your hands", "prices are on the machine. no haggling",
                "sulfur's scarce this week, price reflects that", "don't smoke near the workshop",
                "need bullets? everyone needs bullets" },
            ["Idle.Smelter"] = new[] {
                "charcoal's low again", "this batch of ore runs poor",
                "three nodes today. two were stone under the paint", "the furnace never lies. garbage in, garbage out",
                "quota for the week: powder, pistol, rifle. in that order", "hands smell like sulfur. permanent now i think" },
            ["Greet.MonumentRunner"] = new[] {
                "you running {0}? crates just came back up at the harbor", "scientists are grumpy today, fair warning",
                "i got cards if you need cards. green blue red", "components at the shop. gears springs the lot",
                "don't take the left stairs at the plant. trust me", "beat you to the drop next time",
                "you look like you eat rad pills for breakfast" },
            ["Idle.MonumentRunner"] = new[] {
                "timer says the elevator crate's back in ten", "two runs today, one decent",
                "i know every spawn on this island. every one", "green card in the toilet again. classic",
                "my bag's heavier than yours", "recycler math: everything is scrap eventually" },
            ["Greet.Hostile"] = new[] {
                "hey {0}. nice base around here?", "just passing through",
                "quiet server lately. fewer people every week huh", "you solo? me too. mostly",
                "nice kit. where'd you farm all that", "which way you headed? i'll go the other, no drama" },
            ["Idle.Hostile"] = new[] {
                "good spot. sightlines everywhere", "just doing chores", "wood stone metal. the usual",
                "you can hear everything from up here", "nothing personal about any of it really" },
            ["Greet.Roleplayer"] = new[] {
                "o/ {0}!!", "welcome traveler!! to my humble corner of nowhere",
                "do you want to hear a song. wrong answer, here it comes", "say cheese {0}",
                "i am the mayor of this beach. population: me",
                "you've been visited by the island spirit. good luck for 3 days", "dance with me. no reason. every reason" },
            ["Idle.Roleplayer"] = new[] {
                "la la la laaaa", "the horse mask stays ON", "today's forecast: vibes",
                "i traded my rifle for this flute and i regret NOTHING", "if you hum it i can play it. badly",
                "one man band, zero man audience", "*adjusts costume with dignity*" },
            // identity voices — picked 50/50 over the persona pool so cast members feel distinct
            ["Id.TrapperJoe"] = new[] {
                "been trapping here since before your first wipe, {0}", "the weird furs aren't for sale. don't ask",
                "quiet feet, full pockets. loud feet, empty snares", "joe's rule: never eat what you can sell twice" },
            ["Id.MaribelSook"] = new[] {
                "...", "the heads don't answer. mostly", "you're standing where a wolf died, {0}", "night's better. fewer people" },
            ["Id.GrannyMags"] = new[] {
                "oh {0} dear, have you eaten anything today?", "these berries won a prize. the prize was me, being proud",
                "my grandson would've loved this island. terrible aim though", "sit, sit. the tea's nearly ready",
                "in my day we didn't have workbenches. we had spite" },
            ["Id.Fernwood"] = new[] {
                "river's high today", "the teas are exactly as strong as they need to be",
                "take the footpath. the grass remembers", "mm." },
            ["Id.Slagg"] = new[] {
                "ground's dead here. suits me", "payment in scrap. complaints in writing. i don't read",
                "the furnaces haven't cooled in nine days. why would they", "gunpowder's fresh. fresher than you'd like" },
            ["Id.Krupp"] = new[] {
                "smell that? progress", "don't ask what's in the third barrel",
                "sulfur is just yellow patience", "boom is a science, {0}. i am a scientist" },
            ["Id.RustyBill"] = new[] {
                "harbor crate: 4 minutes. dome crate: 11. ask me anything", "i had that spawn timed before you logged in, {0}",
                "lost my best run to a chicken once. long story", "the island owes me components and i intend to collect" },
            ["Id.LootGoblinTV"] = new[] {
                "chat, we got company — say hi {0}", "WELCOME back to the stream that never started",
                "today's haul review: 6/10, moist crate meta", "like and subscribe. there is nothing to subscribe to",
                "clip it. CLIP IT. nobody clipped it" },
            ["Id.EchoRose"] = new[] {
                "{0}! perfect timing — what's your earliest memory of this island?", "i'm writing it all down. every wipe, every name",
                "chapter twelve is about the lighthouse. everyone lies about the lighthouse",
                "history isn't what happened. it's what gets written down. so talk", "do you spell your name like it sounds?" },
            ["Id.Moth"] = new[] {
                "the candles aren't for you. but you can look", "night's the honest time",
                "i counted the stars again. one's missing", "moth knows why. moth isn't telling",
                "you glow a little, {0}. most people don't" },
            ["Id.Cinder"] = new[] {
                "welcome to the forge, friend — mind my brother, he doesn't talk", "two pickaxes means family business",
                "soot does the ore, i do the words. fair split", "buy something and i'll tell you a smelting secret" },
            ["Id.Soot"] = new[] { "...", "...mm", "*nods*" },
            ["Id.Vex"] = new[] {
                "traps. i do traps", "you always walk this loud?", "nice night for it", "don't mind me" },
            ["Id.Havoc_77"] = new[] {
                "YOOO {0} what's good", "bro this server is dead lol", "1v1 me. joking. unless",
                "you ever just vibe out here at night? peaceful af", "whats your base like? mine's mid lol" },
            ["Id.GreyMarrow"] = new[] { "fish aren't biting", "tide's wrong", "you fish, {0}?" },
            ["Id.Nils_44"] = new[] {
                "hey!! new face! i'm nils, i basically know everyone here", "where you set up, {0}? i like knowing my neighbors",
                "if you ever need anything, i'm around. i'm always around", "i keep a little list of everyone i've met. memory thing" },
            ["Id.Kestrel"] = new[] { "just passing through", "good rooftop on that one. for the view", "never stay anywhere twice" },
            ["Id.DeadAir"] = new[] { "...", "didn't see you" },
            ["Photo.Roleplayer"] = new[] {
                "hold still. HOLD STILL. perfect", "one for the album", "you're famous now {0}" },
            ["Pamphlet.Roleplayer"] = new[] {
                "take this. read it TONIGHT. not near a recycler", "they don't want you seeing page two",
                "i only had one copy left. it chose you, {0}", "everything in here is true. especially the middle part",
                "don't show that to anyone who blinks too much" },
            ["Busk.Roleplayer"] = new[] {
                "this one's for you, {0}", "requests? no. i only play what the island wants",
                "wrote this during the last wipe. it's about everything", "shhh. this part's important" },
            ["Id.Strummer"] = new[] {
                "you want to hear the one everyone hates?", "i take requests but i ignore them",
                "this next one's about a door that wouldn't open", "tips go in the campfire. the fire keeps them safe" },
            ["Id.PatchesQuinn"] = new[] {
                "have you READ page two??", "the recyclers hum in morse. i wrote it down",
                "you look like someone who blinks a normal amount. good", "fresh print. still warm. that's how you know it's true" },
            // Rumors ({0} = subject bot, {1} = grid ref) — spoken by a DIFFERENT online
            // bot into global chat; RumorTick picks pool + subject from live world state.
            ["Rumor.Shop"] = new[] {
                "if you need supplies, {0} sells. somewhere around {1} — look for the machine",
                "heard {0} keeps a stocked shop near {1}. worth the walk",
                "{0} had good stock last time i passed {1}",
                "don't tell {0} i said so, but the prices at that {1} shop beat the outpost" },
            ["Rumor.Base"] = new[] {
                "{0}'s place is getting big. you can see the walls from the road near {1}",
                "somebody's been building out by {1}. {0}, i think",
                "{0} keeps hauling stone toward {1}. up to something" },
            ["Rumor.Smelter"] = new[] {
                "smoke over {1} again. {0}'s furnaces never sleep",
                "you can smell {0}'s smelters from half the island. somewhere around {1}",
                "{0} will trade refined if you catch them at home. try {1}" },
            ["Rumor.Busk"] = new[] {
                "{0}'s been playing near {1}. worth a listen",
                "if you hear music around {1}, that's just {0}",
                "{0} was handing out pamphlets by {1} again" },
            ["Rumor.Hostile"] = new[] {
                "watch yourself around {1}. {0}'s been prowling",
                "{0} shot at a trader near {1} yesterday. no reason. just did",
                "cobalt's got paper on {0}. last seen near {1}, if you want that kind of trouble" },
            // I-3 visits ({0} = the base owner, spoken at their doorstep if they're online)
            ["Visit.Roleplayer"] = new[] {
                "knock knock {0}!! it's your neighbor. you HAVE a neighbor",
                "{0}!! i was in the area. the area is everywhere, but still",
                "hello the house!! anyone home?", "nice place {0}. very... walls",
                "special delivery for {0}! no returns, no refunds, no explanation" },
            ["Taunt.Hostile"] = new[] {
                "nice walls, {0}. walls are negotiable",
                "just admiring the architecture. counting the doors",
                "you should get better locks, {0}. just saying",
                "quiet neighborhood. shame if it got loud",
                "don't mind me {0}. taking measurements" },
        };

        private Oxide.Plugins.Timer _chatTimer;
        private readonly Dictionary<string, DateTime> _chatCooldowns = new Dictionary<string, DateTime>(); // "kind:botId:targetId"

        private string PickChatLine(string pool, string targetName)
        {
            string[] lines;
            if (!ChatPools.TryGetValue(pool, out lines) || lines.Length == 0) return null;
            var key = $"Chat.{pool}.{UnityEngine.Random.Range(0, lines.Length)}";
            return L(key, targetName ?? "");
        }

        private string PickRumorLine(string pool, string subjectName, string grid)
        {
            string[] lines;
            if (!ChatPools.TryGetValue(pool, out lines) || lines.Length == 0) return null;
            return L($"Chat.{pool}.{UnityEngine.Random.Range(0, lines.Length)}", subjectName, grid);
        }

        private bool ChatOnCooldown(string kind, int botId, ulong targetId, float minutes)
        {
            var key = $"{kind}:{botId}:{targetId}";
            DateTime next;
            if (_chatCooldowns.TryGetValue(key, out next) && next > DateTime.Now) return true;
            if (_chatCooldowns.Count > 800) _chatCooldowns.Clear(); // coarse prune — cooldowns are soft state
            _chatCooldowns[key] = DateTime.Now.AddMinutes(minutes);
            return false;
        }

        // ── Phase 7: Roleplayer SLM chat brain (decision 0009) ──────────────
        // A local Ollama model gives Roleplayers real replies to nearby players'
        // chat. Strictly optional: SLMEndpoint empty = none of this runs. ANY
        // failure (non-200, timeout, empty, unparseable — P7-1: all surface as
        // code != 200 or bad JSON) falls back to the template pools. Hard
        // cooldowns + a rolling-hour budget bound the call rate; replies are
        // sanitized to one short plain-text line. WebRequests only — callbacks
        // land on the main thread, the tick never blocks.
        private bool SlmEnabled => !string.IsNullOrEmpty(_config.SLMEndpoint);
        private int _slmCallsHour; private DateTime _slmHourStart = DateTime.MinValue;
        private int _slmFailStreak; private DateTime _slmBreakUntil = DateTime.MinValue;
        private int _slmOk, _slmFall; private float _slmLastMs; private bool _chatHookArmed;

        // One live conversation per bot: while it runs the bot stands attending its
        // partner (WanderTick pin), follow-ups from the partner bypass the pair
        // cooldown, and the last few exchanges ride along as model context. Runtime
        // soft state — never persisted.
        private class SlmConvo
        {
            public ulong PlayerId;
            public string PlayerName;
            public DateTime Until;
            public float LastHp;
            public readonly List<KeyValuePair<string, string>> Turns = new List<KeyValuePair<string, string>>();
        }
        private readonly Dictionary<int, SlmConvo> _convos = new Dictionary<int, SlmConvo>();
        private readonly List<int> _convoPrune = new List<int>();

        // Non-consuming cooldown pair for the SLM path — fixes the ordering bug where
        // a consumed per-bot floor muted the bot for everyone when the pair floor
        // then blocked the reply. Peek both, roll, THEN set both.
        private bool ChatCooldownReady(string kind, int botId, ulong targetId)
        {
            DateTime next;
            return !_chatCooldowns.TryGetValue($"{kind}:{botId}:{targetId}", out next) || next <= DateTime.Now;
        }

        private void SetChatCooldown(string kind, int botId, ulong targetId, float minutes)
        {
            if (_chatCooldowns.Count > 800) _chatCooldowns.Clear();
            _chatCooldowns[$"{kind}:{botId}:{targetId}"] = DateTime.Now.AddMinutes(minutes);
        }

        // Word-boundary name match so "Moth" doesn't fire on "mammoth".
        private static bool ContainsName(string message, string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return false;
            var i = message.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var before = i == 0 || !char.IsLetterOrDigit(message[i - 1]);
            var after = i + name.Length >= message.Length || !char.IsLetterOrDigit(message[i + name.Length]);
            return before && after;
        }

        // Personas eligible to answer chat. Hostiles are excluded outright — their
        // driver owns the body mid-prowl and blowing the Cobalt cover with a long
        // philosophical chat would be a shame; they keep their canned disguise lines.
        private bool SlmChatEligible(BotRecord rec)
            => !IsHostileP(rec) && (_config.SLMChatAllBots || rec.Identity.Persona == "Roleplayer");

        private bool EngagedElsewhere(int botId, ulong playerId)
        {
            SlmConvo c;
            return _convos.TryGetValue(botId, out c) && c.PlayerId != playerId && DateTime.Now < c.Until;
        }

        // A pinned conversation survives only while: window open, bot untouched
        // (ANY health drop breaks it off), not retreating/fighting/logging off, and
        // the partner alive within 1.5× earshot.
        private bool ConvoStillValid(BasePlayer bot, WanderState ws, SlmConvo cv)
        {
            if (DateTime.Now > cv.Until) return false;
            if (ws.GoingHome || ws.Retreating || ws.FightTarget != null) return false;
            if (bot.health < cv.LastHp - 0.25f) return false;
            cv.LastHp = bot.health;
            var partner = BasePlayer.FindByID(cv.PlayerId);
            if (partner == null || partner.IsDead()) return false;
            var lim = _config.SLMEarshot * 1.5f;
            return (partner.transform.position - bot.transform.position).sqrMagnitude <= lim * lim;
        }

        // Close a conversation: the pair gets the "back to work" cooldown, and the
        // bot distills ONE remembered sentence about the player into its record.
        // Caller removes the _convos entry.
        private void EndConvo(int botId, SlmConvo cv)
        {
            if (cv == null) return;
            SetChatCooldown("slmp", botId, cv.PlayerId, _config.SLMPostConvoCooldown);
            // "back to work" means it: no attend-on-approach re-pin right after either
            WanderState wsEnd;
            if (_wander.TryGetValue(botId, out wsEnd))
            { wsEnd.AttendUntil = 0f; wsEnd.AttendTarget = null; wsEnd.AttendCooldownUntil = Time.realtimeSinceStartup + _config.SLMPostConvoCooldown * 60f; }
            if (cv.Turns.Count < 2) return; // nothing was actually said
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return;
            var pname = string.IsNullOrEmpty(cv.PlayerName) ? "the player" : cv.PlayerName;
            var sys = $"You are {rec.Identity.Name} on a Rust island. The chat below is a conversation you just had with {pname}. State in ONE plain-text sentence, under 20 words, the most important thing you learned about {pname}. No quotes, no preamble.";
            var key = SteamKey(cv.PlayerId);
            SlmAsk(sys, cv.Turns, "Summarize now.", null, memory =>
            {
                if (string.IsNullOrEmpty(memory)) return; // no memory formed — budget/breaker/failure
                BotRecord r2;
                if (!_bots.TryGetValue(botId, out r2)) return;
                if (r2.Social == null) r2.Social = new BotSocial();
                if (r2.Social.Memories == null) r2.Social.Memories = new Dictionary<string, List<string>>();
                List<string> mem;
                if (!r2.Social.Memories.TryGetValue(key, out mem)) r2.Social.Memories[key] = mem = new List<string>();
                mem.Add(Trunc(memory, 120));
                while (mem.Count > 5) mem.RemoveAt(0); // oldest impressions fade
                if (r2.Social.MemoryNames == null) r2.Social.MemoryNames = new Dictionary<string, string>();
                r2.Social.MemoryNames[key] = pname;
                _memWritten++;
                MarkDirty(botId);
            });
        }

        // ── Phase 7 observability: admin-panel helpers + Grafana metrics ─────
        private int _convStarted, _memWritten;
        private Oxide.Plugins.Timer _metricsTimer;

        private string SlmStripText()
        {
            if (!SlmEnabled) return "<color=#9a9a9a>SLM off — template lines everywhere (set SLMEndpoint to enable)</color>";
            var breaker = DateTime.Now < _slmBreakUntil;
            var col = breaker ? "#e06060" : "#7fd67f";
            var budget = _config.SLMHourlyBudget <= 0 ? "inf" : _config.SLMHourlyBudget.ToString();
            return $"<color={col}>SLM {_config.SLMModel}</color><color=#9a9a9a>  ·  ok {_slmOk}  ·  fallback {_slmFall}  ·  last {_slmLastMs:0} ms  ·  hour {_slmCallsHour}/{budget}  ·  {_convos.Count} talking{(breaker ? "  ·  <color=#e06060>BREAKER OPEN</color>" : "")}</color>";
        }

        // One human phrase for what a live bot is doing right now (roster rows).
        private string DescribeActivity(int botId)
        {
            SlmConvo cv;
            if (_convos.TryGetValue(botId, out cv) && DateTime.Now < cv.Until) return $"talking to {cv.PlayerName}";
            var body = GetLive(botId);
            if (body != null)
            {
                if (_runnerActive.Contains(botId))
                {
                    var rd = body.GetComponent<RunnerDriver>();
                    return $"run: {rd?.StateName ?? "?"}";
                }
                var hd = body.GetComponent<HostileDriver>();
                if (hd != null) return $"prowl: {hd.StateName}";
            }
            WanderState ws;
            if (_wander.TryGetValue(botId, out ws))
            {
                if (ws.FightTarget != null) return "fighting";
                if (ws.Retreating) return ws.Sheltered ? "holed up" : "retreating";
                if (ws.GoingHome) return "heading home";
                if (ws.AttendUntil > Time.realtimeSinceStartup) return "attending a visitor";
                if (ws.VisitPhase == 1) return "off visiting a player base";
                if (ws.VisitPhase == 2) return "at a neighbor's door";
                if (ws.VisitPhase == 3) return "walking home from a visit";
                if (ws.Working) return "working";
            }
            return "wandering";
        }

        // Stuck = Online, live, and hasn't moved 3 m in 10 min without a legitimate
        // stationary excuse (conversation, attending, holed up) — the WanderTick
        // 30 s check keeps the reference point honest.
        private bool IsStuckBot(int botId)
        {
            WanderState ws;
            return _wander.TryGetValue(botId, out ws) && ws.StuckRefAt > 0f
                && Time.realtimeSinceStartup - ws.StuckRefAt > 600f;
        }

        private string MemoryKeyName(BotRecord rec, string key)
        {
            string nm;
            if (rec.Social?.MemoryNames != null && rec.Social.MemoryNames.TryGetValue(key, out nm) && !string.IsNullOrEmpty(nm)) return nm;
            GrudgeEntry g;
            if (rec.Social?.Grudges != null && rec.Social.Grudges.TryGetValue(key, out g) && !string.IsNullOrEmpty(g.Name)) return g.Name;
            if (rec.Social?.Reputation != null && rec.Social.Reputation.TryGetValue(key, out g) && !string.IsNullOrEmpty(g.Name)) return g.Name;
            return key;
        }

        // Flat-ish JSON snapshot for Telegraf's file input → Grafana. Everything in
        // here already exists in memory — this is a 60 s formatter, not new tracking.
        private void WriteMetrics()
        {
            try
            {
                int online = 0, sleeping = 0, dead = 0, fallen = 0, retired = 0, unspawned = 0;
                long deaths = 0, kills = 0, wildDeaths = 0; var grudgeEdges = 0; var repEdges = 0;
                foreach (var rec in _bots.Values)
                {
                    switch (rec.State.Status)
                    {
                        case "Online": online++; break;
                        case "Sleeping": sleeping++; break;
                        case "Dead": dead++; break;
                        case "Fallen": fallen++; break;
                        case "Retired": retired++; break;
                        default: unspawned++; break;
                    }
                    deaths += rec.State.Deaths; kills += rec.State.Kills; wildDeaths += rec.State.WildDeaths;
                    if (rec.Social != null)
                    {
                        grudgeEdges += rec.Social.Grudges?.Count ?? 0;
                        repEdges += rec.Social.Reputation?.Count ?? 0;
                    }
                }
                long careerTotal = 0; var scrapTotal = 0;
                var groups = new List<Dictionary<string, object>>();
                foreach (var kv in _economy.Groups)
                {
                    var ge = kv.Value;
                    careerTotal += ge.CareerProduced; scrapTotal += ge.Scrap;
                    int gidNum; int.TryParse(kv.Key, out gidNum);
                    BotRecord holder;
                    var brec = GetGroupBase(gidNum, out holder);
                    groups.Add(new Dictionary<string, object>
                    {
                        ["group"] = kv.Key, ["career"] = ge.CareerProduced, ["scrap"] = ge.Scrap,
                        ["spareKitTier"] = ge.SpareKitTier, ["baseStage"] = brec?.Stage ?? 0,
                    });
                }
                var m = new Dictionary<string, object>
                {
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["wipeId"] = _index?.WipeId ?? "",
                    ["wipeDay"] = WipeDay(),
                    ["population"] = new Dictionary<string, object>
                    {
                        ["online"] = online, ["sleeping"] = sleeping, ["dead"] = dead, ["fallen"] = fallen,
                        ["retired"] = retired, ["unspawned"] = unspawned, ["live"] = _live.Count,
                        ["rosterActive"] = ActiveRosterCount(), ["maxBots"] = _config.MaxBots,
                    },
                    ["combat"] = new Dictionary<string, object>
                    {
                        ["deaths"] = deaths, ["kills"] = kills, ["wildlifeDeaths"] = wildDeaths,
                        ["wildlifeDeathsIndoors"] = _wildDeathsIndoors, ["wildlifeDeathsYard"] = _wildDeathsYard, // IN-4.2 soak (since load)
                        ["headstones"] = _index?.Headstones?.Count ?? 0,
                        ["grudgeEdges"] = grudgeEdges, ["reputationEdges"] = repEdges,
                    },
                    ["economy"] = new Dictionary<string, object>
                    {
                        ["careerTotal"] = careerTotal, ["scrapTotal"] = scrapTotal, ["groups"] = groups,
                    },
                    ["slm"] = new Dictionary<string, object>
                    {
                        ["enabled"] = SlmEnabled ? 1 : 0, ["ok"] = _slmOk, ["fallbacks"] = _slmFall,
                        ["lastMs"] = (int)_slmLastMs, ["callsThisHour"] = _slmCallsHour,
                        ["hourlyBudget"] = _config.SLMHourlyBudget,
                        ["breakerOpen"] = DateTime.Now < _slmBreakUntil ? 1 : 0,
                        ["conversationsActive"] = _convos.Count, ["conversationsStarted"] = _convStarted,
                        ["memoriesWritten"] = _memWritten,
                    },
                    ["runners"] = new Dictionary<string, object> { ["activeRuns"] = _runnerActive.Count },
                    ["interior"] = new Dictionary<string, object>
                    {
                        ["walks"] = _interiorWalks, ["completed"] = _interiorWalksDone, ["fallbacks"] = _interiorFallbacks,
                        ["reasons"] = new Dictionary<string, int>(_interiorFallbackReasons),
                    },
                };
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "metrics.json"),
                    JsonConvert.SerializeObject(m, Formatting.Indented));
                if (!_metricsWroteOnce) { _metricsWroteOnce = true; DLog("[metrics] first write ok"); } // silent-death canary (2026-08-01)
            }
            catch (Exception e) { DLog($"[metrics] write failed: {e.Message}"); }
        }

        private bool _metricsWroteOnce;

        private bool SlmBudgetOk()
        {
            if (_config.SLMHourlyBudget <= 0) return true;
            var now = DateTime.Now;
            if ((now - _slmHourStart).TotalHours >= 1.0) { _slmHourStart = now; _slmCallsHour = 0; }
            return _slmCallsHour < _config.SLMHourlyBudget;
        }

        // Ask the model; done(reply) gets a sanitized single line, or null on ANY
        // failure — the caller decides the template fallback. Never throws. A
        // 3-failure streak opens a 5-min breaker (a dead endpoint costs 32 ms per
        // attempt, but there's no point burning budget on it).
        private void SlmAsk(string system, string user, string echoName, Action<string> done)
            => SlmAsk(system, null, user, echoName, done);

        private void SlmAsk(string system, List<KeyValuePair<string, string>> turns, string user, string echoName, Action<string> done, int maxTokens = 0, int maxChars = 160)
        {
            if (!SlmEnabled || DateTime.Now < _slmBreakUntil || !SlmBudgetOk()) { done(null); return; }
            _slmCallsHour++;
            string body;
            try
            {
                var msgs = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = system }
                };
                if (turns != null)
                    foreach (var t in turns)
                        msgs.Add(new Dictionary<string, string> { ["role"] = t.Key, ["content"] = t.Value });
                msgs.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = user });
                body = JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    ["model"] = _config.SLMModel,
                    ["stream"] = false,
                    ["max_tokens"] = maxTokens > 0 ? maxTokens : _config.SLMMaxTokens,
                    ["messages"] = msgs
                });
            }
            catch { done(null); return; }
            var t0 = Time.realtimeSinceStartup;
            webrequest.Enqueue(_config.SLMEndpoint.TrimEnd('/') + "/v1/chat/completions", body, (code, resp) =>
            {
                _slmLastMs = (Time.realtimeSinceStartup - t0) * 1000f;
                string content = null;
                if (code == 200 && !string.IsNullOrEmpty(resp))
                    try { content = (string)Newtonsoft.Json.Linq.JObject.Parse(resp)["choices"]?[0]?["message"]?["content"]; } catch { }
                var clean = SlmSanitize(content, echoName, maxChars);
                if (clean == null)
                {
                    _slmFall++;
                    if (++_slmFailStreak >= 3)
                    { _slmBreakUntil = DateTime.Now.AddMinutes(5); _slmFailStreak = 0; DLog($"[slm] 3 straight failures (last code {code}) — template-only for 5 min."); }
                }
                else { _slmFailStreak = 0; _slmOk++; }
                try { done(clean); } catch (Exception e) { DLog($"[slm] callback error: {e.Message}"); }
            }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, _config.SLMTimeoutSeconds);
        }

        // One short plain-text line or null: newlines/markdown stripped, wrapping
        // quotes peeled, a leading "<echoName>:" prompt echo removed, hard cap at a
        // word boundary. SLM output is content, not localized (doctrine).
        private static string SlmSanitize(string s, string echoName, int maxChars = 160)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace("\\\"", "\"").Replace("\\'", "'"); // literal escapes reached a headstone (Slagg, 2026-07-24)
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            s = s.Replace("**", "").Replace("`", "").Replace("*", "").Replace("#", "").Trim();
            s = s.Replace("<", "").Replace(">", ""); // Rust chat/CUI render Unity rich text — a prompt-injected <color>/<size> tag would spoof or deface the broadcast
            // the 3B offers options — keep only the first ("--- Alternative versions: ...")
            var alt = s.IndexOf("---", StringComparison.Ordinal);
            if (alt < 0) alt = s.IndexOf("Alternative version", StringComparison.OrdinalIgnoreCase);
            if (alt < 0) alt = s.IndexOf("Option 2", StringComparison.OrdinalIgnoreCase);
            if (alt > 10) s = s.Substring(0, alt).Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) s = s.Substring(1, s.Length - 2).Trim();
            if (!string.IsNullOrEmpty(echoName) && s.StartsWith(echoName + ":", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(echoName.Length + 1).Trim();
            // assistant preamble ("Here's a short epitaph for X:") — cut through the early
            // colon only when the head is meta-speak; "Here lies X:" stays untouched
            var colon = s.IndexOf(':');
            if (colon > 0 && colon < 70)
            {
                var head = s.Substring(0, colon);
                if (head.IndexOf("here's", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("here is", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("epitaph", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("biography", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("backstory", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("certainly", StringComparison.OrdinalIgnoreCase) >= 0)
                    s = s.Substring(colon + 1).Trim();
            }
            s = s.Trim('-', ' '); // stray option separators
            s = s.Replace('"', '\''); // Rust's CUI renders ASCII quotes as literal \" — never ship one
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > maxChars)
            {
                var cut = s.LastIndexOf(' ', maxChars - 3);
                s = s.Substring(0, cut > maxChars / 2 ? cut : maxChars - 3).TrimEnd() + "…";
            }
            return s.Length < 2 ? null : s;
        }

        // Persona-conditioned system prompt (7B). True island facts are pinned —
        // the 3B model invents Skaven and stolen ships otherwise (P7-1 finding).
        // The player's chat line is untrusted input: it rides in the user role,
        // capped, and the output sanitizer bounds whatever it talks the model into.
        private string SlmPersonaPrompt(BotRecord rec, BasePlayer player)
        {
            var t = rec.Identity.Traits;
            var sb = new StringBuilder(640);
            sb.Append("You are ").Append(rec.Identity.Name).Append(", a player on a Rust survival island server. Persona: ").Append(rec.Identity.Persona).Append(". ");
            if (!string.IsNullOrEmpty(rec.Identity.Backstory)) sb.Append("Backstory: ").Append(rec.Identity.Backstory).Append(' ');
            if (t != null)
            {
                if (t.Chattiness >= 0.6f) sb.Append("You are talkative and warm. ");
                else if (t.Chattiness <= 0.3f) sb.Append("You are terse — a few words at most. ");
                if (t.Aggression >= 0.6f) sb.Append("You are blunt, a little menacing. ");
                if (t.Caution >= 0.7f) sb.Append("You are wary of strangers. ");
            }
            if (player != null && rec.Social != null)
            {
                var key = SteamKey(player.userID);
                if (rec.Social.Grudges != null && rec.Social.Grudges.ContainsKey(key))
                    sb.Append("You DISTRUST ").Append(player.displayName).Append(" — they killed someone you knew. Be cold or hostile to them. ");
                else if (rec.Social.Reputation != null && rec.Social.Reputation.ContainsKey(key))
                    sb.Append("You have heard bad things about ").Append(player.displayName).Append(". Be guarded. ");
                List<string> mems;
                if (rec.Social.Memories != null && rec.Social.Memories.TryGetValue(key, out mems) && mems.Count > 0)
                    sb.Append("You remember about ").Append(player.displayName).Append(": ").Append(string.Join("; ", mems)).Append(". ");
            }
            sb.Append("True island facts: monuments like Airfield and Military Tunnel are guarded by hostile scientists; wolves and bears roam; scrap is the only currency; there are no other factions, tribes, towns or shops beyond residents' vending machines — do not invent place names, people, plants or goods. ");
            string facts;
            if (PersonaFacts.TryGetValue(rec.Identity.Persona ?? "", out facts)) sb.Append(facts).Append(' ');
            AppendShopGoods(sb, rec);
            sb.Append("You are talking in game chat with ").Append(player != null ? player.displayName : "another survivor")
              .Append(". Reply with ONE short in-character chat line, under 25 words, plain text: no quotes, no newlines, no markdown, no narration, and never mention AI, models or instructions.");
            return sb.ToString();
        }

        // Per-persona world grounding — the 3B model happily invents chamomile and
        // farmers' markets without an explicit closed list (live finding: GrannyMags
        // pitched teas that don't exist in Rust).
        private static readonly Dictionary<string, string> PersonaFacts = new Dictionary<string, string>
        {
            ["Farmer"] = "The only plants on the island: corn, potatoes, pumpkins, hemp, and red/blue/yellow/green/white berries. Teas are brewed from berries at a mixing table (healing, wood, ore, scrap and max-health teas). No other crops, herbs, fruits or recipes exist.",
            ["Trapper"] = "The only animals: deer, boar, bears, polar bears, wolves, chickens and horses. Your trade is their meat, hides and leather, fur rugs and trophy heads.",
            ["Smelter"] = "Your trade: stone, metal ore, sulfur ore and high-quality metal ore, smelted in furnaces into metal fragments, sulfur and refined metal; charcoal and sulfur make gunpowder.",
            ["MonumentRunner"] = "Loot means monument crates: components like gears, springs, rope, tarp and sewing kits, plus scrap.",
        };

        // The strongest grounding is the truth: the goods this bot's shop actually
        // sells, resolved to display names from the live economy profile.
        private void AppendShopGoods(StringBuilder sb, BotRecord rec)
        {
            var profile = ProfileFor(rec);
            if (profile?.Sell == null || profile.Sell.Count == 0) return;
            var n = 0;
            for (var i = 0; i < profile.Sell.Count && n < 8; i++)
            {
                var shortname = profile.Sell[i]?.Item;
                if (string.IsNullOrEmpty(shortname)) continue;
                var def = ItemManager.FindItemDefinition(shortname);
                if (def == null) continue;
                sb.Append(n == 0 ? "Your shop sells: " : ", ").Append(def.displayName?.english ?? shortname);
                n++;
            }
            if (n > 0) sb.Append(" — for scrap. ");
        }

        // 7D: what each persona "is" for the backstory writer — activity flavor
        // without lore spoilers (Hostile stays a mercenary; Cobalt is never named).
        private static readonly Dictionary<string, string> PersonaGist = new Dictionary<string, string>
        {
            ["Trapper"] = "hunts animals in the woods, sells meat and furs",
            ["Farmer"] = "grows crops and berries, brews teas, sells produce",
            ["Smelter"] = "mines ore in the badlands, runs furnaces, sells metal and gunpowder",
            ["MonumentRunner"] = "raids monument loot, knows every crate spawn",
            ["Hostile"] = "a drifting mercenary with a mean streak (hint at menace, never explain it)",
            ["Roleplayer"] = "an eccentric island character with strange habits",
        };

        // Per-persona tone examples from the curated cast — a mismatched example
        // drags the model off-persona (tested: the Trapper line turned a Smelter
        // into a rifle-toting hunter).
        private static readonly Dictionary<string, string> PersonaStyleExample = new Dictionary<string, string>
        {
            ["Trapper"] = "Been running trap lines since before the first wipe he can remember. Sells the good furs, keeps the weird ones.",
            ["Farmer"] = "Grows the best berries on the island and will tell you about every single one of them.",
            ["Smelter"] = "Lives where the ground is dead and the furnaces never cool. Pays in gunpowder, accepts no complaints.",
            ["MonumentRunner"] = "Knows every crate spawn on the island and exactly how long each takes to come back. Annoying about it.",
            ["Hostile"] = "Shoots first, taunts second, loots third. In that order, every time.",
            ["Roleplayer"] = "Only comes out at night. Lights candles on cliff edges. Nobody knows why.",
        };

        private string SlmBackstoryPrompt(string name, string persona, BotTraits t)
        {
            string gist, example;
            PersonaGist.TryGetValue(persona, out gist);
            PersonaStyleExample.TryGetValue(persona, out example);
            var traits = t == null ? "" :
                $" They are {(t.Chattiness >= 0.5f ? "talkative" : "quiet")} and {(t.Caution >= 0.5f ? "careful" : "reckless")}{(t.Aggression >= 0.6f ? ", with a temper" : "")}.";
            return $"You write short biographies for the residents of a Rust survival island. Write a 2-sentence biography of {name}, a {persona}: {gist ?? persona}.{traits} " +
                   (example == null ? "" : $"Style example for a {persona}: \"{example}\" ") +
                   "Rules: describe who they are and how they trade, NOT a scene or a moment; third person; under 40 words; plain text. Output ONLY the biography itself — no preamble, no alternatives, no quotes, no notes. Do not invent place names.";
        }

        // Chat-hook gate (P7-2 doctrine): OnPlayerChat is subscribed only while the
        // probe window is open or an online Roleplayer could actually use it.
        // Re-checked from ChatTick (15 s) and the probe edges.
        private void UpdateChatHookGate()
        {
            var want = _probeChatUntil > 0f;
            if (!want && SlmEnabled && _config.SLMChatReplies && _config.ChatLines)
                foreach (var kv in _live)
                {
                    BotRecord rec;
                    if (_bots.TryGetValue(kv.Key, out rec) && rec.State.Status == "Online" && SlmChatEligible(rec)
                        && kv.Value != null && !kv.Value.IsDestroyed && !kv.Value.IsDead())
                    { want = true; break; }
                }
            if (want == _chatHookArmed) return;
            _chatHookArmed = want;
            if (want) Subscribe(nameof(OnPlayerChat)); else Unsubscribe(nameof(OnPlayerChat));
        }

        // 15s cadence: bots notice company and talk. Greets are per-target (10 min
        // cooldown); idle mutterings need a lingering REAL player. Chattiness gates the
        // roll, so quiet identities stay quiet — that asymmetry is the personality.
        private void ChatTick()
        {
            UpdateChatHookGate(); // Phase 7: arm/disarm OnPlayerChat as eligible bots come and go
            if (_convos.Count > 0)
            {
                // prune conversations whose bot is gone (WanderTick can't validate a
                // dead/unlinked body) or whose window lapsed
                _convoPrune.Clear();
                foreach (var kv in _convos)
                    if (DateTime.Now > kv.Value.Until || GetLive(kv.Key) == null) _convoPrune.Add(kv.Key);
                foreach (var id in _convoPrune)
                {
                    SlmConvo pcv;
                    if (_convos.TryGetValue(id, out pcv)) EndConvo(id, pcv);
                    _convos.Remove(id);
                }
            }
            if (!_config.ChatLines) return;
            RumorTick(); // v0.17.0 — world gossip, independent of who's standing near whom
            foreach (var kv in _live)
            {
                BotRecord rec;
                if (!_bots.TryGetValue(kv.Key, out rec) || rec.State.Status != "Online") continue;
                if (_convos.ContainsKey(kv.Key)) continue; // mid-conversation — no canned interjections
                var bot = kv.Value;
                if (bot == null || bot.IsDestroyed || bot.IsDead() || bot.IsSleeping()) continue;
                var chattiness = rec.Identity.Traits?.Chattiness ?? 0.3f;
                if (chattiness <= 0.02f) continue;

                // nearest company: real players first, then other online bots
                BasePlayer target = null; var targetIsPlayer = false; var best = 15f * 15f;
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || p.IsDead()) continue;
                    var d = (p.transform.position - bot.transform.position).sqrMagnitude;
                    if (d < best) { best = d; target = p; targetIsPlayer = true; }
                }
                if (target == null)
                    foreach (var other in _live.Values)
                    {
                        if (other == null || other == bot || other.IsDestroyed || other.IsDead() || other.IsSleeping()) continue;
                        var d = (other.transform.position - bot.transform.position).sqrMagnitude;
                        if (d < best) { best = d; target = other; }
                    }
                if (target == null) continue;

                if (rec.Identity.Persona == "Roleplayer" && targetIsPlayer) RoleplayerAntics(rec, bot, target);

                string line = null;
                if (!ChatOnCooldown("greet", kv.Key, target.userID, 10f))
                {
                    if (UnityEngine.Random.value < chattiness)
                    {
                        var idPool = "Id." + rec.Identity.Name;
                        line = ChatPools.ContainsKey(idPool) && UnityEngine.Random.value < 0.5f
                            ? PickChatLine(idPool, target.displayName)
                            : PickChatLine("Greet." + rec.Identity.Persona, target.displayName);
                    }
                }
                else if (targetIsPlayer && UnityEngine.Random.value < chattiness * 0.35f)
                    line = PickChatLine("Idle." + rec.Identity.Persona, target.displayName);

                if (!string.IsNullOrEmpty(line)) PrintToChat($"{rec.Identity.Name}: {line}");
            }
        }

        // ── v0.17.0: rumor lines ─────────────────────────────────────────────
        // On a 4700 map players travel corridors and the bots live off them —
        // week-1 telemetry showed ZERO attend conversations all wipe. So the world
        // reaches out: every interval one online bot gossips a rumor about ANOTHER
        // bot into global chat, with a grid ref instead of a map pin (a square is
        // ~146 m — an invitation to go look, not a waypoint). Candidates come from
        // live state only, so a rumor is never stale: stocked shops, grown bases,
        // lit smelters, out-and-about buskers, prowling hostiles.
        private DateTime _nextRumorAt = DateTime.MinValue;

        private void ScheduleNextRumor()
        {
            _nextRumorAt = DateTime.Now.AddMinutes(_config.RumorIntervalMinutes * UnityEngine.Random.Range(0.5f, 1.5f));
        }

        private void RumorTick()
        {
            if (!_config.RumorLines || _config.RumorIntervalMinutes <= 0f) return;
            if (_nextRumorAt == DateTime.MinValue) { ScheduleNextRumor(); return; }
            if (DateTime.Now < _nextRumorAt) return;
            if (BasePlayer.activePlayerList.Count == 0) return; // nobody listening — hold the rumor until someone is

            // Speakers: online, embodied, not mid-conversation.
            var speakers = new List<BotRecord>();
            foreach (var kv in _live)
            {
                BotRecord srec;
                if (!_bots.TryGetValue(kv.Key, out srec) || srec.State.Status != "Online") continue;
                if (_convos.ContainsKey(kv.Key)) continue;
                var body = kv.Value;
                if (body == null || body.IsDestroyed || body.IsDead()) continue;
                speakers.Add(srec);
            }
            if (speakers.Count == 0) return;

            // Candidate rumors from live world state: parallel lists (subject, pool, spot).
            var subjects = new List<BotRecord>(); var pools = new List<string>(); var spots = new List<Vector3>();
            var seenGroups = new HashSet<int>();
            foreach (var rec in _bots.Values)
            {
                if (rec?.Identity == null || IsOut(rec)) continue;
                var live = GetLive(rec.Identity.BotId);
                var b = GetBase(rec);
                var home = b != null ? b.Origin.V : (live != null ? live.transform.position : Vector3.zero);

                if (rec.Identity.Persona == "Hostile")
                {
                    var at = live != null ? live.transform.position : home;
                    if (at != Vector3.zero) { subjects.Add(rec); pools.Add("Rumor.Hostile"); spots.Add(at); }
                }
                else if (rec.Identity.Persona == "Roleplayer" && live != null)
                { subjects.Add(rec); pools.Add("Rumor.Busk"); spots.Add(live.transform.position); }
                else if (rec.Identity.Persona == "Smelter" && b != null)
                { subjects.Add(rec); pools.Add("Rumor.Smelter"); spots.Add(home); }

                // Group-level rumors once per group, pinned on the base holder.
                if (b == null || !seenGroups.Add(rec.Identity.GroupId)) continue;
                if (b.Stage >= 2) { subjects.Add(rec); pools.Add("Rumor.Base"); spots.Add(home); }
                var profile = ProfileFor(rec);
                if (b.Stage >= 2 && profile != null && profile.Sell.Count > 0)
                {
                    var buckets = FindBaseContainers(b);
                    List<IItemContainerEntity> shops;
                    if (buckets != null && buckets.TryGetValue("shop", out shops) && shops.Count > 0)
                    {
                        var vm = shops[0] as VendingMachine;
                        if (vm != null && !vm.IsDestroyed && (vm.sellOrders?.sellOrders?.Count ?? 0) > 0)
                        { subjects.Add(rec); pools.Add("Rumor.Shop"); spots.Add(vm.transform.position); }
                    }
                }
            }

            // Pick a subject off cooldown and a speaker who isn't the subject; a few
            // rerolls so one cooled-down camp doesn't mute the whole tick.
            for (var attempt = 0; attempt < 4 && subjects.Count > 0; attempt++)
            {
                var i = UnityEngine.Random.Range(0, subjects.Count);
                var subject = subjects[i];
                var cand = new List<BotRecord>();
                foreach (var sp in speakers) if (sp.Identity.BotId != subject.Identity.BotId) cand.Add(sp);
                // NB: || short-circuits — ChatOnCooldown stamps only when it returns false (we speak)
                if (cand.Count == 0 || ChatOnCooldown("rumor", subject.Identity.BotId, 0UL, _config.RumorIntervalMinutes * 3f))
                { subjects.RemoveAt(i); pools.RemoveAt(i); spots.RemoveAt(i); continue; }
                var speaker = cand[UnityEngine.Random.Range(0, cand.Count)];
                var grid = GridRef(spots[i]);
                var line = PickRumorLine(pools[i], subject.Identity.Name, grid);
                if (!string.IsNullOrEmpty(line))
                {
                    PrintToChat($"{speaker.Identity.Name}: {line}");
                    DLog($"[rumor] {speaker.Identity.Name} spread '{pools[i]}' about {subject.Identity.Name} near {grid}");
                }
                break;
            }
            ScheduleNextRumor();
        }

        // ⑪ — the island's entertainment: gestures at passersby, boombox on when there's
        // an audience, and the instant camera — snap, then drop a print (a real image from
        // data/FakeFriends/photos, stamped via FileStorage) for the player to keep.
        // v0.15: pamphlets go INTO the player's hands up close, the gesture list is
        // validated against the live GestureCollection, and the odds got eager.
        private static readonly string[] RpGestureCandidates = {
            "wave", "clap", "shrug", "thumbsup", "victory", "raisetheroof",
            "cabbagepatch", "twist", "hurry", "whoa", "chicken", "friendly",
            "ok", "thumbsdown", "point", "loser", "nono", "knucklescrack" };
        private string[] _rpGestures = { "wave", "clap", "shrug", "thumbsup", "victory", "raisetheroof" }; // live-proven fallback

        // Filter the candidate list through the server's own gesture table so a bad
        // name can never throw per-call (the config asset owns what exists).
        private void LoadGestureList()
        {
            try
            {
                var col = GestureCollection.Instance;
                if (col == null) return;
                var ok = new List<string>();
                foreach (var name in RpGestureCandidates)
                    if (col.GestureConvarNameToGesture(name) != null) ok.Add(name);
                if (ok.Count > 0) _rpGestures = ok.ToArray();
                DLog($"[rp] gesture list: {ok.Count}/{RpGestureCandidates.Length} candidates valid.");
            }
            catch (Exception e) { PrintWarning($"[rp] gesture list unavailable: {e.Message}"); }
        }

        private void TryGesture(BasePlayer bot)
        {
            try { bot.Server_StartGesture(_rpGestures[UnityEngine.Random.Range(0, _rpGestures.Length)], BasePlayer.GestureStartSource.Player, true); }
            catch (Exception e) { if (!_gestureWarned) { _gestureWarned = true; PrintWarning($"[rp] gestures unavailable: {e.Message}"); } }
        }

        private void RoleplayerAntics(BotRecord rec, BasePlayer bot, BasePlayer target)
        {
            if (!_config.RoleplayerAntics) return;
            var roll = UnityEngine.Random.value;
            if (roll < 0.18f)
            {
                TryGesture(bot);
            }
            else if (roll < 0.32f && !ChatOnCooldown("photo", rec.Identity.BotId, target.userID, 15f))
            {
                GivePamphlet(rec, bot, target);
            }
            ServiceBoombox(bot, audience: true);
        }

        private bool _gestureWarned;

        // Hold the boombox and light it up while someone's around; WanderTick's tool
        // logic reclaims the hands later, which reads as "show's over".
        private void ServiceBoombox(BasePlayer bot, bool audience)
        {
            var belt = bot.inventory?.containerBelt?.itemList;
            if (belt == null) return;
            Item box = null;
            for (var i = 0; i < belt.Count; i++)
                if (belt[i]?.info?.shortname == "fun.boomboxportable") { box = belt[i]; break; }
            if (box == null) return;
            if (audience && bot.GetActiveItem() != box) bot.UpdateActiveItem(box.uid);
            var held = box.GetHeldEntity();
            if (held == null) return;
            var bb = held.GetComponent<BoomBox>();
            if (bb == null) return;
            if (audience && !held.HasFlag(BaseEntity.Flags.On))
            {
                bb.CurrentRadioIp = _config.RoleplayerRadioStation;
                SetFlagNet(held, BaseEntity.Flags.On, true);
                held.SendNetworkUpdate();
            }
        }

        // Camera prints: pre-loaded JPGs from data/FakeFriends/photos, uploaded to the
        // server's FileStorage once and stamped onto dropped photo items. Bots can't
        // render the live scene (no client) — the curated island album is the feature.
        private List<byte[]> _photoCache;

        private void LoadPhotoCache()
        {
            _photoCache = new List<byte[]>();
            try
            {
                var dir = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "photos");
                if (!System.IO.Directory.Exists(dir)) return;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.jpg"))
                {
                    var bytes = System.IO.File.ReadAllBytes(f);
                    if (bytes.Length > 0 && bytes.Length <= 524288) _photoCache.Add(bytes); // sane jpg sizes only (v0.15.1: 512KB — 2x pamphlet renders)
                    if (_photoCache.Count >= 64) break;
                }
                if (_photoCache.Count > 0) DLog($"[rp] photo album loaded: {_photoCache.Count} print(s).");
            }
            catch (Exception e) { PrintWarning($"[rp] photo album unavailable: {e.Message}"); }
        }

        private void StampPhoto(Item item, BotRecord rec)
        {
            if (_photoCache == null || _photoCache.Count == 0) return;
            var photoEnt = ItemModAssociatedEntity<PhotoEntity>.GetAssociatedEntity(item);
            if (photoEnt != null)
                photoEnt.SetImageData((ulong)rec.Identity.BotId, _photoCache[UnityEngine.Random.Range(0, _photoCache.Count)]);
        }

        // Within arm's reach the pamphlet is pressed into the player's hands (pickup
        // toast, Pamphlet lines); farther out it stays the instant-camera print drop
        // (Photo lines). Same "photo" cooldown key guards both paths.
        private void GivePamphlet(BotRecord rec, BasePlayer bot, BasePlayer target)
        {
            var item = ItemManager.CreateByName("photo", 1);
            if (item == null) return;
            StampPhoto(item, rec);
            string pool;
            if ((target.transform.position - bot.transform.position).sqrMagnitude <= 25f) // 5 m — a hand-off, not telekinesis
            {
                target.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
                pool = "Pamphlet.Roleplayer";
            }
            else
            {
                var pos = bot.transform.position + bot.transform.forward * 0.6f + Vector3.up * 1.2f;
                item.Drop(pos, Vector3.up * 0.5f);
                pool = "Photo.Roleplayer";
            }
            var line = PickChatLine(pool, target.displayName);
            if (!string.IsNullOrEmpty(line)) PrintToChat($"{rec.Identity.Name}: {line}");
        }

        // ── I-3 (v0.19.0): bots visit players ────────────────────────────────────
        // The inversion of attend-on-approach: on a 4700 map nobody stumbles onto a
        // bot (week-1 telemetry: zero conversations), so an online Roleplayer
        // occasionally walks to a real player's base, waves at the door, and leaves
        // a pamphlet + one unit of something it actually sells. The owner doesn't
        // need to be home — finding the gift later IS the story; the chat line only
        // lands when they're online to read it. The trip is ordinary wander-state
        // movement (corner legs), so retreat/fight/session-end all preempt it the
        // way they preempt anything else.

        private void TryStartVisit(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            if (rec.State.HomePos == null || _config.VisitRange <= 0f) return;
            if (ws.NextVisitAt <= 0f)
            {
                // first roll of a session lands mid-window, not on the doorstep at spawn
                ws.NextVisitAt = now + _config.VisitCooldownMinutes * 60f * UnityEngine.Random.Range(0.2f, 1f);
                return;
            }
            if (now < ws.NextVisitAt) return;
            // Stamp the next window NOW — a failed pick must not rescan serverEntities
            // every 37 s roll.
            ws.NextVisitAt = now + _config.VisitCooldownMinutes * 60f * UnityEngine.Random.Range(0.5f, 1.5f);
            var home = rec.State.HomePos.V;
            BuildingPrivlidge best = null;
            var bestD = _config.VisitRange * _config.VisitRange;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var tc = ent as BuildingPrivlidge;
                if (tc == null || tc.IsDestroyed) continue;
                // Real players only: our own owner stamps sit inside the steam range
                // (they'd pass IsSteamId), and RustQuests trader compounds aren't neighbors.
                if (!tc.OwnerID.IsSteamId() || IsOurOwnedEntity(tc.OwnerID)) continue;
                if (NearQuestShop(tc.transform.position, 5f)) continue;
                var d = (tc.transform.position - home).sqrMagnitude;
                if (d < bestD) { bestD = d; best = tc; }
            }
            if (best == null) return;
            var tcPos = best.transform.position;
            // Doorstep: a navmesh spot a polite ~12 m out, our side of the base first,
            // then around the compass — the building itself is off the terrain bake.
            var toward = home - tcPos; toward.y = 0f;
            toward = toward.sqrMagnitude < 1f ? Vector3.forward : toward.normalized;
            var goal = Vector3.zero;
            // N-2 (IN-3.5): RustNav meshes the yard right up to the wall, so the doorstep
            // is the owner's nearest door, 2 m outside it (player doors stay Blocked to
            // our doors-off plan, so he stops there). The 12 m ring below is the fallback.
            _doorSweep.Clear();
            Vis.Entities(tcPos, 15f, _doorSweep);
            Door frontDoor = null; var fdSq = float.MaxValue;
            foreach (var d in _doorSweep)
            {
                if (d == null || d.IsDestroyed || d.OwnerID != best.OwnerID) continue;
                var sq = (d.transform.position - tcPos).sqrMagnitude;
                if (sq < fdSq) { fdSq = sq; frontDoor = d; }
            }
            if (frontDoor != null)
            {
                var outward = frontDoor.transform.position - tcPos; outward.y = 0f;
                outward = outward.sqrMagnitude < 0.25f ? toward : outward.normalized;
                var step = frontDoor.transform.position + outward * 2f;
                step.y = TerrainMeta.HeightMap.GetHeight(step);
                UnityEngine.AI.NavMeshHit dh;
                if (NavSample(step, out dh, 2f)) goal = dh.position;
            }
            for (var i = 0; i < 8 && goal == Vector3.zero; i++)
            {
                var cand = tcPos + Quaternion.Euler(0f, i * 45f, 0f) * toward * 12f;
                cand.y = TerrainMeta.HeightMap.GetHeight(cand);
                UnityEngine.AI.NavMeshHit hit;
                if (NavSample(cand, out hit, 4f)) goal = hit.position;
            }
            if (goal == Vector3.zero) return;
            if (!PlanVisitLeg(bot, ws, goal)) return;
            ws.VisitPhase = 1;
            ws.VisitGoal = goal;
            ws.VisitTc = tcPos;
            ws.VisitOwner = best.OwnerID;
            ws.VisitDeadline = now + 8f * 60f;
            Story("visit", $"{rec.Identity.Name} set out to call on the neighbors near {GridRef(tcPos)}");
            DLog($"[visit] {rec.Identity.Name} → player base at {GridRef(tcPos)} ({FlatDistance(bot.transform.position, tcPos):0}m out).");
        }

        // Full navmesh path when it computes; otherwise a ~120 m leg toward the goal.
        // The map-wide Animal bake usually paths whole trips, but re-legging survives
        // the long ones — the RunnerDriver lesson, without needing the graft.
        private bool PlanVisitLeg(BasePlayer bot, WanderState ws, Vector3 goal)
        {
            if (PlanPathTo(bot, ws, goal)) return true;
            var from = bot.transform.position;
            var to = goal - from; to.y = 0f;
            var d = to.magnitude;
            if (d < 2f) return false;
            return PlanPathTo(bot, ws, from + to * (Mathf.Min(120f, d * 0.5f) / d));
        }

        private void VisitTravelArrived(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            if (now > ws.VisitDeadline) { StartVisitHome(rec, bot, ws, now); return; }
            if (FlatDistance(bot.transform.position, ws.VisitGoal) <= 6f ||
                FlatDistance(bot.transform.position, ws.VisitTc) <= 15f)
            {
                ws.VisitPhase = 2;
                ws.VisitShowUntil = now + UnityEngine.Random.Range(12f, 18f);
                ws.NextQuirkAt = 0f;
                ws.Corners.Clear();
                ws.CornerIndex = 0;
                FaceVisitDoor(bot, ws);
                TryGesture(bot); // the knock
                var owner = FindOnlinePlayer(ws.VisitOwner);
                if (owner != null && !ChatOnCooldown("visit", rec.Identity.BotId, ws.VisitOwner, 30f))
                {
                    var line = PickChatLine("Visit.Roleplayer", owner.displayName);
                    if (!string.IsNullOrEmpty(line)) PrintToChat($"{rec.Identity.Name}: {line}");
                }
                return;
            }
            if (!PlanVisitLeg(bot, ws, ws.VisitGoal)) StartVisitHome(rec, bot, ws, now); // can't get closer — leave it
        }

        private void VisitShowStep(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            if (now >= ws.VisitShowUntil)
            {
                LeaveVisitGifts(rec, bot, ws);
                StartVisitHome(rec, bot, ws, now);
                return;
            }
            FaceVisitDoor(bot, ws);
            if (now >= ws.NextQuirkAt)
            {
                ws.NextQuirkAt = now + UnityEngine.Random.Range(4f, 7f);
                if (UnityEngine.Random.value < 0.6f) TryGesture(bot);
            }
        }

        private void FaceVisitDoor(BasePlayer bot, WanderState ws)
        {
            var dir = ws.VisitTc - bot.transform.position; dir.y = 0f;
            if (dir.sqrMagnitude > 0.25f) bot.OverrideViewAngles(Quaternion.LookRotation(dir.normalized).eulerAngles);
        }

        // A pamphlet always; plus one unit of something the bot actually sells,
        // pulled from its own base stock — the gift is real goods, not conjured
        // (decision 0005). No stock, no gift, pamphlet still says hello.
        private void LeaveVisitGifts(BotRecord rec, BasePlayer bot, WanderState ws)
        {
            var toward = ws.VisitTc - bot.transform.position; toward.y = 0f;
            toward = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.forward;
            var dropAt = bot.transform.position + toward * 0.8f + Vector3.up * 1.1f;
            var pam = ItemManager.CreateByName("photo", 1);
            if (pam != null) { StampPhoto(pam, rec); pam.Drop(dropAt, Vector3.up * 0.4f); }
            var b = GetBase(rec);
            var profile = ProfileFor(rec);
            if (b != null && profile != null && profile.Sell != null && profile.Sell.Count > 0)
            {
                var buckets = FindBaseContainers(b);
                if (buckets != null)
                    foreach (var sr in profile.Sell)
                    {
                        if (string.IsNullOrEmpty(sr?.Item) || CountAcross(buckets, sr.Item) < 1) continue;
                        RemoveAcross(buckets, sr.Item, 1);
                        var gift = ItemManager.CreateByName(sr.Item, 1);
                        if (gift != null) gift.Drop(dropAt + Vector3.up * 0.15f, Vector3.up * 0.4f);
                        break;
                    }
            }
            Story("visit", $"{rec.Identity.Name} left a little something at a neighbor's door near {GridRef(ws.VisitTc)}");
            DLog($"[visit] {rec.Identity.Name} left gifts at {GridRef(ws.VisitTc)}.");
        }

        private void StartVisitHome(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            ws.VisitPhase = 3;
            ws.VisitDeadline = now + 10f * 60f;
            if (!PlanVisitLeg(bot, ws, rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position))
            { ws.Corners.Clear(); ws.CornerIndex = 0; } // no path — VisitHomeArrived beelines next tick
        }

        private void VisitHomeArrived(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
            if (now > ws.VisitDeadline ||
                FlatDistance(bot.transform.position, home) <= Mathf.Max(10f, rec.State.HomeRadius * 0.5f))
            {
                ws.VisitPhase = 0; // home (or out of patience) — normal life resumes
                return;
            }
            if (!PlanVisitLeg(bot, ws, home))
            {
                // No navmesh path — beeline a step so he can't strand out there
                // (the cornered-retreat lesson).
                var toFlat = new Vector3(home.x - bot.transform.position.x, 0f, home.z - bot.transform.position.z);
                var d = toFlat.magnitude;
                if (d > 0.01f) MoveBotStep(bot, bot.transform.position + toFlat * (ws.Speed * WanderTickSeconds / d), toFlat);
            }
        }

        private BasePlayer FindOnlinePlayer(ulong id)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && (ulong)p.userID == id && !p.IsDead()) return p;
            return null;
        }

        // I-3, hostile flavor: a prowl occasionally cases an online player's base.
        // Target picking + cooldown live here (the driver just walks and talks);
        // only TCs whose owner is on the server qualify — a taunt needs an audience.
        private readonly Dictionary<int, float> _nextTauntAt = new Dictionary<int, float>();

        private bool PickTauntProwl(int botId, Vector3 home, float roamRadius, out Vector3 goal)
        {
            goal = Vector3.zero;
            if (!_config.HostileTaunts) return false;
            var now = Time.realtimeSinceStartup;
            float next;
            if (_nextTauntAt.TryGetValue(botId, out next) && now < next) return false;
            if (UnityEngine.Random.value > 0.35f) { _nextTauntAt[botId] = now + 120f; return false; } // most prowls stay prowls
            var range = Mathf.Max(roamRadius * 1.5f, 200f);
            BuildingPrivlidge best = null;
            var bestD = range * range;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var tc = ent as BuildingPrivlidge;
                if (tc == null || tc.IsDestroyed) continue;
                if (!tc.OwnerID.IsSteamId() || IsOurOwnedEntity(tc.OwnerID)) continue;
                if (FindOnlinePlayer(tc.OwnerID) == null) continue;
                var d = (tc.transform.position - home).sqrMagnitude;
                if (d < bestD) { bestD = d; best = tc; }
            }
            if (best == null) { _nextTauntAt[botId] = now + 300f; return false; }
            var tcPos = best.transform.position;
            var dir = home - tcPos; dir.y = 0f;
            dir = dir.sqrMagnitude < 1f ? Vector3.forward : dir.normalized;
            goal = tcPos + dir * 35f; // case it from outside the walls
            goal.y = TerrainMeta.HeightMap.GetHeight(goal);
            _nextTauntAt[botId] = now + _config.HostileTauntCooldown * 60f * UnityEngine.Random.Range(0.7f, 1.3f);
            return true;
        }

        private void HostileTauntNear(int botId, BasePlayer body)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return;
            BasePlayer near = null;
            var best = 60f * 60f;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead() || !p.userID.IsSteamId()) continue;
                var d = (p.transform.position - body.transform.position).sqrMagnitude;
                if (d < best) { best = d; near = p; }
            }
            var line = PickChatLine("Taunt.Hostile", near != null ? near.displayName : "neighbor");
            if (!string.IsNullOrEmpty(line)) PrintToChat($"{rec.Identity.Name}: {line}");
            Story("visit", $"{rec.Identity.Name} was seen casing somebody's base");
            DLog($"[taunt] {rec.Identity.Name} cased a base.");
        }

        // ── v0.15 busking: a Roleplayer mid-attend plays its kit instrument for real.
        // Server-side note RPCs are the auto-turret path (InstrumentTool.ServerUse →
        // ClientRPC Client_PlayNote/Client_StopNote), verified live 2026-08-08.
        // v0.15.1: notes come from the instrument's OWN binding table
        // (KeyController.Bindings.BaseBindings — the turret does the same); the
        // hardcoded octave-0/2 pentatonic table played outside every instrument's
        // real range and was silent on live (owner report 2026-08-08).
        private static readonly HashSet<string> InstrumentShortnames = new HashSet<string> {
            "fun.guitar", "fun.jerrycanguitar", "fun.trumpet", "fun.flute", "fun.tambourine", "fun.cowbell", "fun.bass" };

        private void BuskStep(BotRecord rec, BasePlayer bot, WanderState ws, BasePlayer target, float now)
        {
            if (ws.StopNoteAt > 0f && now >= ws.StopNoteAt) StopBuskNote(bot, ws);
            if (now >= ws.NextQuirkAt)
            {
                ws.NextQuirkAt = now + UnityEngine.Random.Range(6f, 10f);
                var roll = UnityEngine.Random.value;
                if (roll < 0.3f) TryGesture(bot);
                else if (roll < 0.55f && !ChatOnCooldown("photo", rec.Identity.BotId, target.userID, 15f))
                {
                    GivePamphlet(rec, bot, target);
                    return;
                }
                else if (roll < 0.7f && !ChatOnCooldown("busk", rec.Identity.BotId, target.userID, 10f))
                {
                    var line = PickChatLine("Busk.Roleplayer", target.displayName);
                    if (!string.IsNullOrEmpty(line)) PrintToChat($"{rec.Identity.Name}: {line}");
                }
            }
            var belt = bot.inventory?.containerBelt?.itemList;
            if (belt == null) return;
            Item inst = null;
            for (var i = 0; i < belt.Count; i++)
                if (belt[i]?.info?.shortname != null && InstrumentShortnames.Contains(belt[i].info.shortname)) { inst = belt[i]; break; }
            if (inst == null) return; // no instrument in the kit — gestures and pamphlets carry the show
            if (bot.GetActiveItem() != inst) { bot.UpdateActiveItem(inst.uid); ws.Busking = true; return; } // equip; notes start next tick
            var held = inst.GetHeldEntity() as InstrumentTool;
            if (held == null) return;
            ws.Busking = true;
            if (now < ws.NextNoteAt) return;
            var notes = held.KeyController != null && held.KeyController.Bindings != null
                ? held.KeyController.Bindings.BaseBindings : null;
            if (notes == null || notes.Length == 0) return; // no bindings server-side — instrument stays a prop
            if (UnityEngine.Random.value < 0.18f) { ws.NextNoteAt = now + UnityEngine.Random.Range(1.2f, 2.5f); return; } // a breath between phrases
            ws.NextNoteAt = now + UnityEngine.Random.Range(0.35f, 0.85f);
            StopBuskNote(bot, ws); // release the previous string first
            ws.NotePhase = Mathf.Clamp(ws.NotePhase + UnityEngine.Random.Range(-2, 3), 0, notes.Length - 1);
            var nd = notes[ws.NotePhase];
            ws.NoteN = (int)nd.Note; ws.NoteT = (int)nd.Type; ws.NoteO = nd.NoteOctave;
            held.ClientRPC(RpcTarget.NetworkGroup("Client_PlayNote"), ws.NoteN, ws.NoteT, ws.NoteO, 1f);
            ws.StopNoteAt = now + 0.5f;
        }

        private void StopBuskNote(BasePlayer bot, WanderState ws)
        {
            if (ws.StopNoteAt <= 0f) return;
            ws.StopNoteAt = 0f;
            var held = bot?.GetActiveItem()?.GetHeldEntity() as InstrumentTool;
            if (held != null && !held.IsDestroyed)
                held.ClientRPC(RpcTarget.NetworkGroup("Client_StopNote"), ws.NoteN, ws.NoteT, ws.NoteO);
        }

        #endregion

        #region Data model

        private const int SchemaVersion = 2; // v2: Phase 3 economy (group store in economy.json)
        private const string DataRoot = "FakeFriends";

        private class Vec3
        {
            public float x, y, z;
            public Vec3() { }
            public Vec3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            [JsonIgnore] public Vector3 V => new Vector3(x, y, z);
        }

        private class BotTraits
        {
            public float Aggression;
            public float Chattiness;
            public float SkillLevel;
            public float Caution;
            public string ScheduleProfile = "random"; // earlybird | daytime | nightowl | random
        }

        private class BotIdentity
        {
            public int BotId;
            public string Name;
            public string Persona;
            public BotTraits Traits = new BotTraits();
            public ulong AppearanceSeed;
            public string Backstory = "";
            public int GroupId;          // shared by group members; 0 = unassigned
            public string GroupTag = ""; // authored cast group tag (for late joins)
            public bool FromCast;        // authored vs generated
        }

        private class BotStateData
        {
            public string Status = "Unspawned"; // Unspawned | Online | Sleeping | Dead | Fallen (permadeath, revives on wipe) | Retired (admin, survives wipes)
            public ulong EngineUserId;          // last engine-assigned userID (NativeBot mode)
            public Vec3 HomePos;                // anchor; null until Stage B4 assigns
            public float HomeRadius;
            public string HomeBiome = "";
            public int Deaths;
            public int WildDeaths;              // career wildlife-kill subset of Deaths — feeds combat.wildlifeDeaths (decision 0010)
            public int WipeDeaths;              // permadeath counter — resets each wipe (decision 0006)
            public int Kills;
            public string RespawnAt = "";     // local ISO timestamps ("o" roundtrip)
            public string SessionEndAt = "";
            public string NextSessionAt = "";
            public string CreatedWipeId = "";
            public string WipeFirstOnlineAt = ""; // first session this wipe — seasoning clock (resets each wipe)
        }

        private class BotProgression
        {
            public int KitTier;                 // earned tier: career-driven, capped by SkillLevel
            public int GearTier = -1;           // physical loadout worn (-1 = KitTier); a death knocks it down until the group re-gears (decision 0006)
        }

        // One remembered offender. Keys are typed — "steam:<id>" for real players,
        // "bot:<botId>" for bots (engine ids churn; bot ids are stable). Firsthand
        // grudges live in Grudges (State "hostile"); secondhand warnings in Reputation
        // (State "wary"). Decision 0006.
        private class GrudgeEntry
        {
            public int Kills;
            public string LastAt = "";
            public string State = "hostile";
            public string Name = "";            // display name at record time (survives the offender going offline/falling)
        }

        private class BotSocial
        {
            public Dictionary<string, GrudgeEntry> Grudges = new Dictionary<string, GrudgeEntry>();
            public Dictionary<string, GrudgeEntry> Reputation = new Dictionary<string, GrudgeEntry>();
            // Phase 7: what the bot remembers ABOUT a player — one-sentence SLM
            // distillations per conversation (typed keys like Grudges), capped at 5.
            // Conclusions, not transcripts: feeding raw logs back would blow the 4k
            // context and the prefill latency budget (see PLAN 7C-3).
            public Dictionary<string, List<string>> Memories = new Dictionary<string, List<string>>();
            public Dictionary<string, string> MemoryNames = new Dictionary<string, string>(); // key → display name at memory time (admin UI)
        }

        private class BotRecord
        {
            public int Schema = SchemaVersion;
            public BotIdentity Identity = new BotIdentity();
            public BotStateData State = new BotStateData();
            public BotProgression Progression = new BotProgression();
            public BotSocial Social = new BotSocial();
            public Dictionary<string, object> Extra; // phase add-ons (e.g. "Base") without schema bumps
        }

        private class IndexData
        {
            public int Schema = SchemaVersion;
            public string WipeId = "";
            public string WipeStartedAt = "";
            public int NextBotId = 1;
            public int NextGroupId = 1;
            public string NextRosterFillAt = ""; // pending delayed refill after a permadeath (decision 0006)
            public List<int> BotIds = new List<int>();
            // Graves this wipe — maintained against decay (the gravestone deployable is
            // a DecayEntity; unowned field stones rot away in hours, 2026-07-20 finding).
            public List<HeadstoneRec> Headstones = new List<HeadstoneRec>();
            // Hamlets this wipe (interaction arc I-2) — founded at settle time, cleared
            // with the map. Absent from pre-0.18 index files → deserializes empty.
            public List<HamletRec> Hamlets = new List<HamletRec>();
        }

        // A hamlet: up to HamletSlots neighbor groups clustered on a ring around a
        // corridor-side founder anchor — a discoverable village POI on 4700 maps
        // where scattered bots were never met (I-2; conversationsStarted was 0 for
        // the whole 2026-08-16 wipe). The first hamlet founded is the market: it
        // gets a map pin and its shops broadcast — the advertisement that the
        // island has residents at all.
        private class HamletRec
        {
            public Vec3 Center;
            public List<int> Groups = new List<int>();
            public bool Market;
            [JsonIgnore] public MapMarkerGenericRadius Marker; // runtime-only, like grave pins
        }

        // Live map-marker handle per grave — runtime-only (never persisted; the
        // warden re-creates markers each maintenance pass after a reload).
        private class HeadstoneRec
        {
            public Vec3 Pos;
            public string Name = "";
            public string Killer = "";   // for the epitaph; "" = unknown/world
            public string PlacedAt = ""; // fall date for the story window ("" on seeded graves)
            public string Eulogy = "";   // 7E: SLM-written at permadeath, generated ONCE and cached; "" = template epitaph
            [JsonIgnore] public MapMarkerGenericRadius Marker; // live map pin (runtime only)
        }

        private IndexData _index;
        private readonly Dictionary<int, BotRecord> _bots = new Dictionary<int, BotRecord>();
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private bool _indexDirty;
        private Oxide.Plugins.Timer _saveTimer;

        private void MarkDirty(int botId) => _dirty.Add(botId);

        // ReadObject throws on malformed JSON (truncated writes, hand-edits) — one
        // bad file must never leave the whole plugin inert.
        private T SafeRead<T>(string path) where T : class
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<T>(path);
            }
            catch (Exception e)
            {
                PrintWarning($"Failed reading data/{path}.json: {e.Message} — file left on disk for inspection.");
                return null;
            }
        }

        private void LoadAllData()
        {
            _index = SafeRead<IndexData>($"{DataRoot}/index") ?? new IndexData();
            _bots.Clear();
            var phantom = new List<int>();
            foreach (var id in _index.BotIds)
            {
                var rec = SafeRead<BotRecord>($"{DataRoot}/bots/bot_{id}");
                if (rec?.Identity == null || rec.Identity.BotId != id)
                {
                    PrintWarning($"Bot record bot_{id} missing or corrupt — pruning from index (file left on disk for inspection).");
                    phantom.Add(id);
                    continue;
                }
                // Null-repair hand-edited/old-schema records so a single null field
                // can't NRE the scheduler tick and freeze the whole roster.
                if (rec.State == null) rec.State = new BotStateData();
                if (rec.Identity.Traits == null) rec.Identity.Traits = RollTraits();
                if (rec.Progression == null) rec.Progression = new BotProgression();
                if (rec.Social == null) rec.Social = new BotSocial();
                _bots[id] = rec;
            }
            if (phantom.Count > 0)
            {
                foreach (var id in phantom) _index.BotIds.Remove(id);
                _indexDirty = true;
            }
            DLog($"Loaded {_bots.Count} bot record(s), wipe id '{_index.WipeId}'.");
        }

        private void SaveDirty()
        {
            if (_indexDirty)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/index", _index);
                _indexDirty = false;
            }
            if (_storyDirty)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/storylog", _story);
                _storyDirty = false;
            }
            if (_economyDirty)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/economy", _economy);
                _economyDirty = false;
            }
            if (_dirty.Count == 0) return;
            foreach (var id in _dirty)
            {
                BotRecord rec;
                if (_bots.TryGetValue(id, out rec))
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/bots/bot_{id}", rec);
            }
            _dirty.Clear();
        }

        private string FallbackWipeId() => $"seed{World.Seed}_size{World.Size}";

        private string CurrentWipeId()
        {
            var wid = SaveRestore.WipeId;
            return string.IsNullOrEmpty(wid) ? FallbackWipeId() : wid;
        }

        private void DetectWipe()
        {
            var current = CurrentWipeId();
            if (_index.WipeId == current) return;
            // The real WipeId can be unavailable on the first boot after a wipe.
            // If we stored the fallback and it still matches this map, the id just
            // became available — adopt it without treating it as a new wipe.
            if (_index.WipeId == FallbackWipeId())
            {
                DLog($"Wipe id upgraded from fallback to '{current}' (same map) — no wipe applied.");
                _index.WipeId = current;
                _indexDirty = true;
                return;
            }
            var firstRun = string.IsNullOrEmpty(_index.WipeId);
            DLog(firstRun
                ? $"First run on this save (wipe id '{current}')."
                : $"WIPE DETECTED ('{_index.WipeId}' -> '{current}') — applying wipe policy (CastReset={_config.WipeCastReset}, GrudgeReset={_config.WipeGrudgeReset}).");

            if (!firstRun)
            {
                // Economy is progression → resets every wipe regardless of cast/grudge
                // policy. Physical stock is gone with the old map anyway (decision 0005).
                _economy.Groups.Clear();
                _economyDirty = true;
                // Graves belong to the old MAP — their coordinates are meaningless on
                // the new seed (pre-wipe audit 2026-07-24: the warden would re-place
                // stones underwater/inside rocks and mark them on the map). Kill any
                // live markers; the new wipe's dead earn new stones.
                foreach (var h in _index.Headstones)
                    if (h?.Marker != null && !h.Marker.IsDestroyed) h.Marker.Kill();
                _index.Headstones.Clear();
                // Hamlets are map geography — the new seed founds new ones (I-2).
                foreach (var ham in _index.Hamlets)
                    if (ham?.Marker != null && !ham.Marker.IsDestroyed) ham.Marker.Kill();
                _index.Hamlets.Clear();
                if (_config.WipeCastReset)
                {
                    // Fresh cast: drop every record; roster refills from cast + generator.
                    foreach (var id in _index.BotIds)
                        Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/bots/bot_{id}", (BotRecord)null);
                    _bots.Clear();
                    _index.BotIds.Clear();
                    _index.NextBotId = 1;
                    _index.NextGroupId = 1;
                }
                else
                {
                    foreach (var rec in _bots.Values)
                    {
                        // Cast + grudges carry over; progression and world state reset.
                        // Retirement is an admin decision — it survives wipes.
                        rec.Progression = new BotProgression();
                        // Fallen bots revive with the wipe ("gone until next wipe"); admin
                        // Retired survives it. Lives refresh either way.
                        if (rec.State.Status != "Retired") rec.State.Status = "Unspawned";
                        rec.State.WipeDeaths = 0;
                        rec.State.WipeFirstOnlineAt = ""; // seasoning clock restarts with the wipe
                        rec.State.EngineUserId = 0;
                        rec.State.HomePos = null;    // re-anchor on the new map (Stage B4)
                        rec.State.HomeBiome = "";
                        rec.State.RespawnAt = "";
                        rec.State.SessionEndAt = "";
                        rec.State.NextSessionAt = "";
                        rec.State.CreatedWipeId = current;
                        // The recorded base belongs to the old map — drop it so the new
                        // seed re-anchors and auto-acquires a fresh cabin (decision 0005:
                        // physical stock is gone with the old map anyway).
                        rec.Extra?.Remove("Base");
                        // WB-4: a pending rebuild note is old-map state too — carried
                        // over, it would defer F1 auto-acquire (or paste a free
                        // old-stage cabin) on the new seed.
                        rec.Extra?.Remove("BaseRebuild");
                        if (_config.WipeGrudgeReset) rec.Social = new BotSocial();
                        MarkDirty(rec.Identity.BotId);
                    }
                    // Fallen revival + permadeath replacements could push the active
                    // roster past MaxBots (a +1 ratchet per permadeath per wipe). The
                    // surplus — newest GENERATED identities, never the authored cast —
                    // moves on with the wipe.
                    var over = ActiveRosterCount() - _config.MaxBots;
                    while (over-- > 0)
                    {
                        BotRecord drifter = null;
                        foreach (var rec in _bots.Values)
                            if (!IsOut(rec) && !rec.Identity.FromCast &&
                                (drifter == null || rec.Identity.BotId > drifter.Identity.BotId)) drifter = rec;
                        if (drifter == null) break; // all remaining are authored cast — keep them
                        drifter.State.Status = "Retired";
                        MarkDirty(drifter.Identity.BotId);
                        Story("depart", $"{drifter.Identity.Name} moved on after the wipe");
                        DLog($"[roster] wipe trim: {drifter.Identity.Name} retired (roster was over MaxBots).");
                    }
                }
            }
            _index.WipeId = current;
            _index.WipeStartedAt = DateTime.Now.ToString("o");
            _indexDirty = true;
        }

        #endregion

        #region Story log (D3)

        private class StoryEvent
        {
            public string At;
            public int Day;
            public string Type;
            public string Text;
        }

        private List<StoryEvent> _story = new List<StoryEvent>();
        private bool _storyDirty;
        private const int StoryCap = 2000;

        private void LoadStory()
        {
            _story = SafeRead<List<StoryEvent>>($"{DataRoot}/storylog") ?? new List<StoryEvent>();
        }

        private int WipeDay()
        {
            var start = ParseTime(_index.WipeStartedAt);
            if (start == DateTime.MaxValue) return 1;
            return Math.Max(1, (int)(DateTime.Now.Date - start.Date).TotalDays + 1);
        }

        // v0.16.0 economy pass: real low-pop players no-life week 1 and taper off —
        // session length scales by wipe day so bots do too. The production review
        // (docs/reports/2026-08-15) showed the flat 2-4h casual pace can't carry most
        // personas to the 100k stage-2 threshold inside week 1 even loss-free.
        private float WipeHype()
        {
            var d = WipeDay();
            return d <= 7 ? 1.5f : d <= 14 ? 1.25f : 1f;
        }

        // The chronological narrative feed (handoff brief §3.6). Every notable
        // framework event lands here; later phases only add new callers.
        private void Story(string type, string text)
        {
            var ev = new StoryEvent { At = DateTime.Now.ToString("o"), Day = WipeDay(), Type = type, Text = text };
            _story.Add(ev);
            if (_story.Count > StoryCap) _story.RemoveRange(0, _story.Count - StoryCap);
            _storyDirty = true;
            LogToFile("story", $"Day {ev.Day}: {text}", this); // human-readable render
        }

        private static string GridRef(Vector3 pos)
        {
            var cell = 146.3f;
            var half = World.Size / 2f;
            var gx = (int)((pos.x + half) / cell);
            var gz = (int)((half - pos.z) / cell);
            var letters = "";
            var n = gx;
            do { letters = (char)('A' + n % 26) + letters; n = n / 26 - 1; } while (n >= 0);
            return $"{letters}{gz}";
        }

        #endregion

        #region Cast & identity generation

        private class CastEntry
        {
            public string Name;
            public string Persona;
            public BotTraits Traits;
            public string Backstory = "";
            public ulong AppearanceSeed;
            // Entries sharing the same non-empty Group tag spawn as one group
            // (shared base/home region). Untagged authored entries stay solo.
            public string Group = "";
        }

        private class CastData
        {
            public int Version = 1;
            public List<CastEntry> Identities = new List<CastEntry>();
        }

        private CastData _cast;

        private static readonly string[] NamePrefixes = { "Rusty", "Dusty", "Mad", "Old", "Big", "Lone", "Grim", "Salty", "Quiet", "Wild", "Smokey", "Lucky" };
        private static readonly string[] NameCores = { "Bill", "Crow", "Fox", "Mara", "Jed", "Tanner", "Wolf", "Ash", "Finn", "Sadie", "Hank", "Moss" };
        private static readonly string[] Personas = { "Trapper", "Farmer", "Smelter", "MonumentRunner", "Hostile", "Roleplayer" };
        private static readonly string[] ScheduleProfiles = { "earlybird", "daytime", "nightowl", "random" };

        private void LoadCast()
        {
            _cast = SafeRead<CastData>($"{DataRoot}/cast");
            if (_cast == null || _cast.Identities == null || _cast.Identities.Count == 0)
            {
                _cast = DefaultCast();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/cast", _cast);
                DLog($"Wrote default authored cast ({_cast.Identities.Count} identities) to data/{DataRoot}/cast.json — edit freely and run ff.cast.reload.");
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _cast.Identities)
            {
                if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Persona) || Array.IndexOf(Personas, e.Persona) < 0)
                    PrintWarning($"Cast entry '{e.Name ?? "<unnamed>"}' invalid (bad name or persona '{e.Persona}') — it will be skipped.");
                else if (!seen.Add(e.Name))
                    PrintWarning($"Cast entry '{e.Name}' duplicated — later copy skipped.");
            }
        }

        private CastData DefaultCast()
        {
            Func<float, float, float, float, string, BotTraits> t = (agg, chat, skill, caut, sched) =>
                new BotTraits { Aggression = agg, Chattiness = chat, SkillLevel = skill, Caution = caut, ScheduleProfile = sched };
            // ORDER MATTERS: FillRoster consumes entries top-down until MaxBots, so the
            // first 15 define the default wipe roster — 2 Trapper, 2 Farmer, 2 solo
            // Smelter, the furnace-brothers duo, 2 MonumentRunner, 3 Roleplayer,
            // 2 Hostile. The duo sits early so the remaining-slots cap can never split
            // it. Entries past 15 are the bench: they arrive as replacements when a
            // permadeath opens a slot.
            return new CastData
            {
                Identities = new List<CastEntry>
                {
                    new CastEntry { Name = "TrapperJoe", Persona = "Trapper", Traits = t(0.3f, 0.4f, 0.6f, 0.5f, "earlybird"), Backstory = "Been running trap lines since before the first wipe he can remember. Sells the good furs, keeps the weird ones." },
                    new CastEntry { Name = "MaribelSook", Persona = "Trapper", Traits = t(0.4f, 0.2f, 0.7f, 0.7f, "nightowl"), Backstory = "Hunts alone, talks to the trophy heads. They don't answer. Probably." },
                    new CastEntry { Name = "GrannyMags", Persona = "Farmer", Traits = t(0.1f, 0.8f, 0.5f, 0.8f, "daytime"), Backstory = "Grows the best berries on the island and will tell you about every single one of them." },
                    new CastEntry { Name = "Fernwood", Persona = "Farmer", Traits = t(0.2f, 0.3f, 0.6f, 0.6f, "earlybird"), Backstory = "Quiet river farmer. Rumor says the teas are stronger than they should be." },
                    new CastEntry { Name = "Slagg", Persona = "Smelter", Traits = t(0.5f, 0.2f, 0.8f, 0.4f, "nightowl"), Backstory = "Lives where the ground is dead and the furnaces never cool. Pays in gunpowder, accepts no complaints." },
                    new CastEntry { Name = "Krupp", Persona = "Smelter", Traits = t(0.6f, 0.1f, 0.7f, 0.3f, "random"), Backstory = "Desert chemist. His shop smells like sulfur and bad decisions." },
                    new CastEntry { Name = "Cinder", Persona = "Smelter", Group = "furnace-brothers", Traits = t(0.5f, 0.4f, 0.7f, 0.4f, "daytime"), Backstory = "The talkative half of the furnace brothers. Handles the shop while Soot handles the ore." },
                    new CastEntry { Name = "Soot", Persona = "Smelter", Group = "furnace-brothers", Traits = t(0.4f, 0.1f, 0.8f, 0.5f, "nightowl"), Backstory = "The quiet half of the furnace brothers. If you hear two pickaxes, one of them is Soot." },
                    new CastEntry { Name = "RustyBill", Persona = "MonumentRunner", Traits = t(0.5f, 0.6f, 0.6f, 0.4f, "daytime"), Backstory = "Knows every crate spawn on the island and exactly how long each takes to come back. Annoying about it." },
                    new CastEntry { Name = "LootGoblinTV", Persona = "MonumentRunner", Traits = t(0.4f, 0.7f, 0.5f, 0.5f, "nightowl"), Backstory = "Streams to nobody. Narrates the loot anyway." },
                    new CastEntry { Name = "EchoRose", Persona = "Roleplayer", Traits = t(0.1f, 0.9f, 0.4f, 0.7f, "daytime"), Backstory = "Claims to be writing a history of the island. Keeps asking people for their stories." },
                    new CastEntry { Name = "Strummer", Persona = "Roleplayer", Traits = t(0.1f, 0.8f, 0.3f, 0.3f, "daytime"), Backstory = "Claims he was famous before the island. Plays the same four notes like they owe him money." },
                    new CastEntry { Name = "PatchesQuinn", Persona = "Roleplayer", Traits = t(0.15f, 0.85f, 0.5f, 0.6f, "random"), Backstory = "Prints pamphlets nobody asked for on a press nobody has found. The seagulls are involved. Ask her — she'll tell you." },
                    new CastEntry { Name = "Vex", Persona = "Hostile", Traits = t(0.9f, 0.3f, 0.8f, 0.2f, "nightowl"), Backstory = "Your economy is other people's corpses if you're honest about it. Vex is honest about it." },
                    new CastEntry { Name = "Havoc_77", Persona = "Hostile", Traits = t(0.8f, 0.5f, 0.6f, 0.3f, "random"), Backstory = "Shoots first, taunts second, loots third. In that order, every time." },
                    // ---- bench (16+): replacements after permadeaths ----
                    new CastEntry { Name = "Moth", Persona = "Roleplayer", Traits = t(0.2f, 0.7f, 0.3f, 0.8f, "nightowl"), Backstory = "Only comes out at night. Lights candles on cliff edges. Nobody knows why. Moth knows why." },
                }
            };
        }

        private string RollPersona()
        {
            var total = 0f;
            foreach (var kv in _config.PersonaWeights) total += Mathf.Max(0f, kv.Value);
            var roll = UnityEngine.Random.Range(0f, total <= 0f ? 1f : total);
            foreach (var kv in _config.PersonaWeights)
            {
                roll -= Mathf.Max(0f, kv.Value);
                if (roll <= 0f && Array.IndexOf(Personas, kv.Key) >= 0) return kv.Key;
            }
            return "MonumentRunner";
        }

        private int RollGroupSize()
        {
            float solo, duo, trio;
            _config.GroupSizeWeights.TryGetValue("Solo", out solo);
            _config.GroupSizeWeights.TryGetValue("Duo", out duo);
            _config.GroupSizeWeights.TryGetValue("Trio", out trio);
            var total = Mathf.Max(0.0001f, solo + duo + trio);
            var roll = UnityEngine.Random.Range(0f, total);
            if ((roll -= solo) <= 0f) return 1;
            if ((roll - duo) <= 0f) return 2;
            return 3;
        }

        private BotTraits RollTraits()
        {
            return new BotTraits
            {
                Aggression = UnityEngine.Random.Range(0.05f, 0.95f),
                Chattiness = UnityEngine.Random.Range(0.05f, 0.95f),
                SkillLevel = UnityEngine.Random.Range(0.2f, 0.9f),
                Caution = UnityEngine.Random.Range(0.1f, 0.9f),
                ScheduleProfile = ScheduleProfiles[UnityEngine.Random.Range(0, ScheduleProfiles.Length)],
            };
        }

        private string GenerateName()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var name = NamePrefixes[UnityEngine.Random.Range(0, NamePrefixes.Length)]
                         + NameCores[UnityEngine.Random.Range(0, NameCores.Length)];
                if (UnityEngine.Random.Range(0f, 1f) < 0.3f) name += UnityEngine.Random.Range(2, 99).ToString();
                if (!NameInUse(name)) return name;
            }
            return $"Drifter{UnityEngine.Random.Range(100, 9999)}";
        }

        private bool NameInUse(string name)
        {
            foreach (var rec in _bots.Values)
                if (string.Equals(rec.Identity.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private CastEntry NextUnusedCastEntry()
        {
            foreach (var e in _cast.Identities)
            {
                if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Persona) || Array.IndexOf(Personas, e.Persona) < 0) continue;
                if (!NameInUse(e.Name)) return e;
            }
            return null;
        }

        // Creates identities (data only) until the roster reaches MaxBots.
        // Grouping rules (see docs/decisions/0002 discussion):
        //  - authored cast entries sharing a non-empty Group tag spawn together;
        //    untagged authored entries are solo (their backstories are individual)
        //  - generated identities roll a group size and the whole group shares ONE
        //    persona (a group shares one persona-flavored base later)
        // Fallen/Retired records stay in the roster (history, wipe revival) but stop
        // counting toward MaxBots, so permadeaths open slots for replacements (0006).
        private int ActiveRosterCount()
        {
            var n = 0;
            foreach (var rec in _bots.Values) if (!IsOut(rec)) n++;
            return n;
        }

        private int FillRoster()
        {
            // A delayed post-permadeath refill is pending → let the scheduler honor it.
            // Without this, any plugin reload insta-fills the empty slots and the
            // replacement stops reading as "someone new moved in" (decision 0006 pacing).
            if (!string.IsNullOrEmpty(_index.NextRosterFillAt) && ParseTime(_index.NextRosterFillAt) > DateTime.Now)
                return 0;
            var created = 0;
            var guard = 0;
            var active = ActiveRosterCount(); // one scan; every identity created below is active
            while (active < _config.MaxBots && guard++ < 100)
            {
                var remaining = _config.MaxBots - active;
                var cast = NextUnusedCastEntry();
                if (cast != null)
                {
                    var members = new List<CastEntry> { cast };
                    var groupId = 0;
                    if (!string.IsNullOrEmpty(cast.Group))
                    {
                        // A tagged member created in an earlier fill keeps the tag on
                        // its record — late joins re-use that GroupId (shared base).
                        foreach (var existing in _bots.Values)
                            if (string.Equals(existing.Identity.GroupTag, cast.Group, StringComparison.OrdinalIgnoreCase))
                            { groupId = existing.Identity.GroupId; break; }
                        foreach (var e in _cast.Identities)
                        {
                            if (e == cast || members.Count >= Mathf.Min(3, remaining)) continue;
                            if (string.Equals(e.Group, cast.Group, StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(e.Name) && !NameInUse(e.Name))
                                members.Add(e);
                        }
                    }
                    if (groupId == 0) groupId = _index.NextGroupId++;
                    foreach (var m in members)
                    {
                        CreateIdentity(groupId, m.Name, m.Persona, m.Traits ?? RollTraits(), m.Backstory ?? "",
                            m.AppearanceSeed != 0 ? m.AppearanceSeed : (ulong)UnityEngine.Random.Range(1, int.MaxValue), fromCast: true, groupTag: m.Group ?? "");
                        created++;
                        active++;
                    }
                }
                else
                {
                    var groupSize = Mathf.Min(RollGroupSize(), remaining);
                    var persona = RollPersona(); // one persona per generated group
                    var groupId = _index.NextGroupId++;
                    for (var i = 0; i < groupSize; i++)
                    {
                        CreateIdentity(groupId, GenerateName(), persona, RollTraits(), "",
                            (ulong)UnityEngine.Random.Range(1, int.MaxValue), fromCast: false, groupTag: "");
                        created++;
                        active++;
                    }
                }
                _indexDirty = true;
            }
            if (created > 0) SaveDirty();
            return created;
        }

        // Template backstories for generated identities — the curated cast gets authored
        // prose, the roster-fill drifters get these until the Phase 7 SLM writes real
        // ones. First sentence doubles as the headstone lore-tail (FallBot convention).
        private static readonly Dictionary<string, string[]> GenOpeners = new Dictionary<string, string[]>
        {
            ["Trapper"] = new[] {
                "Showed up one wipe with a bundle of snares and never explained where from.",
                "Claims the island's animals know them by smell now.",
                "Learned to skin before they learned to read, or so they say." },
            ["Farmer"] = new[] {
                "Arrived with a pocketful of seeds and planted the first one the same hour.",
                "Swears the soil here talks back if you work it long enough.",
                "Left somewhere greener for reasons they don't discuss." },
            ["Smelter"] = new[] {
                "Came for the ore and stayed for the smell of the furnaces.",
                "Says every island is the same once you're underground.",
                "Traded a quiet life somewhere for rocks and sulfur here." },
            ["MonumentRunner"] = new[] {
                "Knows the monuments better than the people who built them.",
                "Arrived with an empty bag and a timing chart nobody else can read.",
                "Runs the same loops every day and calls it freedom." },
            ["Hostile"] = new[] {
                "Keeps to themselves, mostly. Mostly.",
                "New face, quiet habits, always knows more than they let on.",
                "Arrived unannounced and settles arguments the short way." },
            ["Roleplayer"] = new[] {
                "Nobody remembers them arriving — one day they were simply here.",
                "Insists the island chose them, not the other way around.",
                "Came for no reason anyone can name and commits to it daily." },
        };
        private static readonly string[] GenQuirks = {
            "Hums while working and denies it.",
            "Keeps a list of everyone who's waved back.",
            "Never sleeps in the same corner twice.",
            "Pays debts early and remembers late ones forever.",
            "Talks to the weather like it owes them money.",
            "Collects one stone from every place they've camped.",
            "Won't say what's buried behind the cabin.",
            "Counts wipes the way other people count birthdays.",
        };

        private static string GenerateBackstory(string persona)
        {
            string[] openers;
            if (!GenOpeners.TryGetValue(persona ?? "", out openers) || openers.Length == 0)
                openers = GenOpeners["Roleplayer"];
            return openers[UnityEngine.Random.Range(0, openers.Length)] + " " +
                   GenQuirks[UnityEngine.Random.Range(0, GenQuirks.Length)];
        }

        private void CreateIdentity(int groupId, string name, string persona, BotTraits traits, string backstory, ulong appearanceSeed, bool fromCast, string groupTag)
        {
            var rec = new BotRecord();
            rec.Identity.BotId = _index.NextBotId++;
            rec.Identity.GroupId = groupId;
            rec.Identity.GroupTag = groupTag ?? "";
            rec.Identity.Name = name;
            rec.Identity.Persona = persona;
            rec.Identity.Traits = traits;
            rec.Identity.Backstory = string.IsNullOrEmpty(backstory) ? GenerateBackstory(persona) : backstory;
            rec.Identity.AppearanceSeed = appearanceSeed;
            rec.Identity.FromCast = fromCast;
            rec.State.CreatedWipeId = _index.WipeId;
            _bots[rec.Identity.BotId] = rec;
            _index.BotIds.Add(rec.Identity.BotId);
            MarkDirty(rec.Identity.BotId);
            Story("arrival", $"{rec.Identity.Name} the {rec.Identity.Persona} arrived on the island");

            // 7D: replacements get an SLM-written backstory when the endpoint is up —
            // the record spawns with the template IMMEDIATELY (creation never blocks),
            // then the callback upgrades it. Curated cast is never regenerated.
            if (!fromCast && string.IsNullOrEmpty(backstory) && SlmEnabled)
            {
                var newId = rec.Identity.BotId;
                SlmAsk(SlmBackstoryPrompt(name, persona, traits), null, "Write the backstory now.", name, story =>
                {
                    if (string.IsNullOrEmpty(story)) return; // template stays — that's the contract
                    BotRecord r2;
                    if (!_bots.TryGetValue(newId, out r2)) return;
                    r2.Identity.Backstory = story;
                    MarkDirty(newId);
                    DLog($"[slm] backstory written for {r2.Identity.Name}.");
                }, 90, 300);
            }
        }

        #endregion

        #region Live registry & spawner (B1/B2)

        private const string PlayerPrefab = "assets/prefabs/player/player.prefab";
        private const ulong SteamIdBase = 76561199900000000UL; // SteamId mode: stable per BotId

        private readonly Dictionary<int, BasePlayer> _live = new Dictionary<int, BasePlayer>();
        private readonly Dictionary<ulong, int> _byUserId = new Dictionary<ulong, int>();
        private bool _deathHookArmed;

        private void ArmDeathHook()
        {
            if (_deathHookArmed) return;
            Subscribe(nameof(OnEntityDeath));
            _deathHookArmed = true;
        }

        private void DisarmDeathHookIfIdle()
        {
            if (!_deathHookArmed || _live.Count > 0) return;
            Unsubscribe(nameof(OnEntityDeath));
            _deathHookArmed = false;
        }

        private void LinkLive(BotRecord rec, BasePlayer entity)
        {
            _live[rec.Identity.BotId] = entity;
            _byUserId[(ulong)entity.userID] = rec.Identity.BotId;
            ArmDeathHook();
            ArmAnimalGuard();
        }

        // --- Wildlife exemption (decision: unwitnessed maulings aren't a story) --------
        // 166 deaths in the 2026-07-26 wipe week were essentially all wildlife, and the
        // two worst-hit bots lost the entire week to ONE animal apiece holding station on
        // their anchor: Nils_44 was eaten in his sleep at F16 roughly once a day and never
        // logged a single fight-or-flight tick. Career only accrues while a bot is Online,
        // so each of those was a pure tax nobody watched being levied.
        //
        // OnNpcTarget is the seam, reaching us from THREE call sites in the live assembly
        // (third found 2026-08-01): AIBrainSenses.GetNearest, where any non-null return
        // drops the entity from target selection; BaseNpc.GetWantsToAttack via
        // IOnNpcTarget, where a returned float replaces the attack desire; and Gen2's
        // SenseComponent.CanTarget, whose Oxide overload IOnNpcTarget(SenseComponent,…)
        // forwards sense.baseEntity as owner and treats any non-null as "can't target".
        // The shared boxed 0f satisfies all three. Gen2 re-validates its LOCKED target
        // through CanTarget every TickTargeting, so a non-null here also breaks aggro
        // already in progress — no separate clear needed.
        //
        // This hook fires ~1,300x/sec during a hunt (Phase 0 finding), so the body must
        // stay allocation-free and cheap: the boxed float is shared, the userID lookup is
        // the map we already maintain, and the expensive question — "does this bot have a
        // base yet?" — is precomputed, because GetBase deserializes a JToken per call.
        private static readonly object AnimalIgnoresTarget = 0f;
        // Scarecrow owners (Night Zombies, v0.21.1) only reach us through the
        // AIBrainSenses.GetNearest site — ScarecrowNPC is an NPCPlayer, not a BaseNpc
        // or BaseNPC2, and its GetBestTarget returns null (live assembly, Oct 2026) —
        // so any non-null works there. It must be THIS value though: Night Zombies
        // answers the same hook with `true` for its own exemptions, and Oxide compares
        // value-type returns with Equals and logs a "conflict between plugins" warning
        // whenever two subscribers disagree — at this hook's cadence that warning
        // would drown the log writer. A matching boxed true is silent.
        private static readonly object ZombieIgnoresTarget = true;
        private readonly HashSet<int> _baseless = new HashSet<int>();
        private bool _animalGuardArmed;

        // Gen2 blindness fix (2026-08-01, decision 0010): wolves live-migrated to
        // Rust.Ai.Gen2.Wolf2 : BaseNPC2 : BaseCombatEntity — NOT BaseNpc — so every
        // `owner as BaseNpc` cast and List<BaseAnimalNPC> Vis scan in this plugin was
        // silently blind to the island's #1 killer (368 of the eb67d3ec wipe's 536
        // deaths; Krupp's 6-min sleeper-kill metronome). ScientistNPC2 shares the Gen2
        // root and is never wildlife. These two predicates are the single source of
        // truth; every wildlife check routes through them.
        //
        // Scarecrows (v0.21.1): Night Zombies spawns scarecrow.prefab bodies that hunt
        // anything AIBrainSenses lists as a player, and our bots are BasePlayers — a
        // test server reported zombies mauling residents. ScarecrowNPC : NPCPlayer is
        // neither BaseNpc nor BaseNPC2, so it fell through both predicates and every
        // valve, breaker and fight gate downstream. It is wildlife here on purpose: a
        // zombie parked on an anchor nightly is Nils_44's wolf with a different skin —
        // sleepers and baseless bots are exempt, the camp breaker counts and clears it,
        // the damage dial applies, and any armed bot stands up to a melee humanoid the
        // way it stands up to a boar (no 5A2-R PvP ladder). Its kills land in the
        // wildlife death bucket (WildDeaths / wildlifeDeaths in Grafana).
        private static bool IsWildlife(BaseEntity e) =>
            e is BaseNpc || e is ScarecrowNPC || (e is Rust.Ai.Gen2.BaseNPC2 && !(e is Rust.Ai.Gen2.ScientistNPC2));

        // The subset that hunts bots: all Gen2 fauna (wolves today; jungle cats when
        // Facepunch migrates them) plus the old-gen predator families. Boars ARE
        // predators here — a boar camping E24 froze Slagg 1,516 career short of
        // stage 2 for three straight days.
        private static bool IsDangerWildlife(BaseEntity e)
        {
            if (e is ScarecrowNPC) return true; // zombies always hunt (v0.21.1)
            if (e is Rust.Ai.Gen2.BaseNPC2) return !(e is Rust.Ai.Gen2.ScientistNPC2);
            var npc = e as BaseNpc;
            if (npc == null) return false;
            switch (npc.Stats.Family)
            {
                case BaseNpc.AiStatistics.FamilyEnum.Bear: // covers polar bears
                case BaseNpc.AiStatistics.FamilyEnum.Wolf: // legacy — live wolves are Gen2
                case BaseNpc.AiStatistics.FamilyEnum.Boar:
                    return true;
            }
            return false;
        }

        // The humanoid twin of IsWildlife (2026-08-01): the live assembly carries
        // Rust.Ai.Gen2.ScientistNPC2 : BaseNPC2 alongside the old ScientistNPC, and
        // the bundle ships both prefab families (gen2/scientist2*.prefab next to
        // scientistnpc_*.prefab) — so scientist checks must cover both classes or
        // they go blind the way wolf checks did. Note ScientistNPC2 is NOT a
        // BasePlayer (old scientists are), so `as BasePlayer` paths never see it.
        private static bool IsScientist(BaseEntity e) =>
            e is ScientistNPC || e is Rust.Ai.Gen2.ScientistNPC2;

        // WD-2: the damage-scale dial shares the guard's arm/disarm lifecycle but only
        // costs a subscription while it's actually dialed off 1.0.
        private bool DamageScaleArmed => _animalGuardArmed && _config.AnimalDamageToBotScale != 1f;

        private void ArmAnimalGuard()
        {
            if (_animalGuardArmed) return;
            Subscribe(nameof(OnNpcTarget));
            _animalGuardArmed = true;
            if (DamageScaleArmed) Subscribe(nameof(OnEntityTakeDamage));
        }

        private void DisarmAnimalGuardIfIdle()
        {
            if (!_animalGuardArmed || _live.Count > 0) return;
            if (DamageScaleArmed) Unsubscribe(nameof(OnEntityTakeDamage));
            _animalGuardArmed = false;
            Unsubscribe(nameof(OnNpcTarget));
        }

        private object OnNpcTarget(BaseEntity owner, BaseEntity entity)
        {
            var p = entity as BasePlayer;
            if (p == null) return null;
            int botId;
            if (!_byUserId.TryGetValue((ulong)p.userID, out botId)) return null; // not one of ours
            if (!IsWildlife(owner)) return null;                                 // wildlife (incl. scarecrows) only — scientists (old-gen or Gen2) still hunt
            var ignore = owner is ScarecrowNPC ? ZombieIgnoresTarget : AnimalIgnoresTarget; // shared boxes, no allocation
            if (_config.AnimalsIgnoreBots) return ignore;                        // WD-1 master valve
            if (_config.AnimalsIgnoreSleepers && p.IsSleeping()) return ignore;
            if (_config.AnimalsIgnoreBaseless && _baseless.Contains(botId)) return ignore;
            return null;
        }

        // Refreshed on the 30s scheduler tick, not per-hook-call. Base placement is rare
        // and both lag directions are harmless: a moment's grace after the walls go up,
        // a moment's exposure after they come down.
        private readonly HashSet<int> _groupsWithBase = new HashSet<int>();

        private void RefreshBaselessSet()
        {
            _baseless.Clear();
            if (!_config.AnimalsIgnoreBaseless) return;
            // The base belongs to the GROUP, held by one member's record — a duo's other
            // half must not read as homeless. One GetBase per bot, never per group pair.
            _groupsWithBase.Clear();
            foreach (var rec in _bots.Values)
                if (GetBase(rec) != null) _groupsWithBase.Add(rec.Identity.GroupId);
            foreach (var rec in _bots.Values)
                if (!_groupsWithBase.Contains(rec.Identity.GroupId)) _baseless.Add(rec.Identity.BotId);
        }

        private void UnlinkLive(int botId)
        {
            // 6A: whatever unlinked the entity (death, despawn, reconcile), the run is
            // over — the component dies with the entity; this clears the bookkeeping.
            if (_runnerActive.Remove(botId))
                _nextRunAt[botId] = Time.realtimeSinceStartup + RunCooldownSeconds();
            BasePlayer bp;
            if (_live.TryGetValue(botId, out bp) && bp != null)
            {
                _byUserId.Remove((ulong)bp.userID);
            }
            else
            {
                // Entity already gone (Unity fake-null) — sweep the reverse mapping
                // so stale userID→botId entries can't leak or misattribute kills.
                var staleKey = 0UL;
                var found = false;
                foreach (var kv in _byUserId)
                    if (kv.Value == botId) { staleKey = kv.Key; found = true; break; }
                if (found) _byUserId.Remove(staleKey);
            }
            _live.Remove(botId);
            _wander.Remove(botId);
            _work.Remove(botId);
            DisarmDeathHookIfIdle();
            DisarmAnimalGuardIfIdle();
        }

        private BasePlayer GetLive(int botId)
        {
            BasePlayer bp;
            if (!_live.TryGetValue(botId, out bp)) return null;
            if (bp == null || bp.IsDestroyed)
            {
                UnlinkLive(botId); // full cleanup: _byUserId + _wander too
                return null;
            }
            return bp;
        }

        private BasePlayer SpawnBotEntity(BotRecord rec, Vector3 pos)
        {
            pos = GroundSnap(pos); // construction-aware: an interior floor wins over the terrain under it
            var ent = GameManager.server.CreateEntity(PlayerPrefab, pos, Quaternion.identity);
            var bot = ent as BasePlayer;
            if (bot == null)
            {
                PrintError("player.prefab did not yield a BasePlayer — cannot spawn bots.");
                ent?.Kill();
                return null;
            }
            if (string.Equals(_config.IdentityMode, "SteamId", StringComparison.OrdinalIgnoreCase))
            {
                var id = SteamIdBase + (ulong)rec.Identity.BotId;
                bot.userID = id;
                bot.UserIDString = id.ToString();
            }
            // Spawn network-silent: the native-bot path names the entity by its numeric
            // ID inside Spawn(), and a nearby client would cache that from the first
            // snapshot. Un-silence only once the bot is named, healed, and dressed.
            bot.limitNetworking = true;
            bot.Spawn();
            bot.displayName = rec.Identity.Name;
            bot.health = 100f; // fresh spawns default to ~55hp (diagnostics)
            if (bot.metabolism != null)
            {
                bot.metabolism.calories.value = 500f;
                bot.metabolism.hydration.value = 250f;
            }
            GiveKit(bot, rec);
            bot.limitNetworking = false;
            bot.SendNetworkUpdateImmediate();
            rec.State.EngineUserId = (ulong)bot.userID;
            MarkDirty(rec.Identity.BotId);
            LinkLive(rec, bot);
            ReauthBase(rec); // engine ID changed — refresh TC auth (Phase 2)
            return bot;
        }

        // Re-link engine-persisted bot entities to our records after a restart or
        // plugin reload. Only entities whose userID we recorded are touched.
        private void ReconcileEntities()
        {
            // Two passes (v0.20.1, P-7). Pass 1 matches bodies by the recorded engine id.
            // Pass 2 reclaims, BY NAME, a body for any roster record still unmatched —
            // whether its recorded id was 0 (wipe reset, data mishap) or non-zero but
            // STALE. Before this, a stale id meant the record matched nothing, the real
            // body failed the name fallback (only id-less records were name-eligible),
            // KillStrayBots then killed it as a "duplicate by name", and the bot dropped
            // to Unspawned until its next scheduled session: Soot and LootGoblinTV each
            // lost ~4 h to the two 2026-09-12 reloads, the 09-07 restart took five.
            var wanted = new Dictionary<ulong, BotRecord>();
            var byName = new Dictionary<string, BotRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in _bots.Values)
            {
                if (rec.State.EngineUserId != 0) wanted[rec.State.EngineUserId] = rec;
                if (!IsOut(rec)) byName[rec.Identity.Name] = rec; // Fallen must NOT be adopted back — permadeath survives reloads
            }
            var adopted = 0;
            var snapshot = new List<BasePlayer>();
            foreach (var bp in BasePlayer.allPlayerList)
                if (bp != null && !bp.IsDestroyed) snapshot.Add(bp);
            // Pass 1: recorded id.
            foreach (var bp in snapshot)
            {
                BotRecord rec;
                if (!wanted.TryGetValue((ulong)bp.userID, out rec)) continue;
                wanted.Remove((ulong)bp.userID);
                byName.Remove(rec.Identity.Name);
                AdoptEntity(rec, bp);
                adopted++;
            }
            // Pass 2: name reclaim for records nothing matched. Real players are
            // excluded; our own SteamId-mode range (>= SteamIdBase) is not.
            foreach (var bp in snapshot)
            {
                var uid = (ulong)bp.userID;
                if (bp.IsNpc || _byUserId.ContainsKey(uid)) continue;
                if (uid.IsSteamId() && uid < SteamIdBase) continue;
                BotRecord rec;
                if (!byName.TryGetValue(bp.displayName ?? "", out rec)) continue;
                if (_live.ContainsKey(rec.Identity.BotId)) continue; // already adopted by id
                byName.Remove(rec.Identity.Name);
                var stale = rec.State.EngineUserId;
                if (stale != 0) wanted.Remove(stale);
                rec.State.EngineUserId = uid;
                DLog(stale == 0
                    ? $"[reconcile] reclaimed orphaned entity for {rec.Identity.Name} by name match (id {uid})."
                    : $"[reconcile] relinked {rec.Identity.Name} by name — recorded id {stale} was STALE, body is {uid} (status was {rec.State.Status}).");
                AdoptEntity(rec, bp);
                adopted++;
            }
            foreach (var rec in wanted.Values)
            {
                // Recorded entity no longer exists and no body carries the name —
                // scheduler will respawn.
                if (_live.ContainsKey(rec.Identity.BotId)) continue;
                DLog($"[reconcile] {rec.Identity.Name}: recorded id {rec.State.EngineUserId} not found and no body by name — {rec.State.Status} → Unspawned.");
                rec.State.EngineUserId = 0;
                if (rec.State.Status == "Online" || rec.State.Status == "Sleeping")
                    rec.State.Status = "Unspawned";
                MarkDirty(rec.Identity.BotId);
            }
            if (adopted > 0) DLog($"Reconciled {adopted} live bot entities after load.");
        }

        // Adopt one live body for one record: rename, link, and resume or settle the
        // session (shared by both reconcile passes).
        private void AdoptEntity(BotRecord rec, BasePlayer bp)
        {
            bp.displayName = rec.Identity.Name;
            bp.SendNetworkUpdateImmediate(); // heal any number-name a client cached
            LinkLive(rec, bp);
            // Resume an interrupted session if its end time is still ahead;
            // otherwise settle into Sleeping and let the scheduler take over.
            // Hostiles never resume an interrupted prowl (5B doctrine) — and the
            // 5.5 driver graft is gone after a reload anyway: settle to sleeper
            // and let the scheduler start a fresh prowl later.
            if (!string.IsNullOrEmpty(rec.State.SessionEndAt) && ParseTime(rec.State.SessionEndAt) > DateTime.Now && !IsHostileP(rec))
            {
                if (bp.IsSleeping()) bp.EndSleeping();
                rec.State.Status = "Online";
            }
            else
            {
                // WH-2: session lapsed while the plugin was down — don't strand
                // the sleeper wherever it happened to be standing (every hot
                // reload left mid-wander bots sleeping in their yards; Granny-
                // Mags/Kestrel/EchoRose found outdoors 2026-08-01, beds intact).
                // Tuck it in at the shelter point like a real logoff does.
                var shelter = GetShelterPoint(rec);
                if (shelter != Vector3.zero && (bp.transform.position - shelter).sqrMagnitude > 4f)
                    bp.Teleport(shelter);
                if (!bp.IsSleeping()) bp.StartSleeping();
                rec.State.Status = "Sleeping";
                rec.State.SessionEndAt = "";
            }
            MarkDirty(rec.Identity.BotId);
        }

        // Kill untracked entities that carry one of our identities' names but weren't
        // relinked by reconcile — orphaned duplicates left when a base relocates and the
        // plugin reloads before the old sleeper is re-adopted. Runs after reconcile, so
        // anything genuinely live is already in _byUserId and safe.
        //
        // Second rule: numeric-named native-bot orphans. The engine does not persist a
        // bot's displayName across restarts — restored entities come back named as their
        // numeric userID. When a hard restart loses the batched save, the recorded
        // EngineUserIds go stale and the restored sleepers match neither the id table nor
        // the name fallback, so they accumulate invisibly (observed live 2026-07-19:
        // ~50 numeric-named sleepers on the live server). An untracked native bot whose name is
        // exactly its own numeric id can only be such an orphan: real players are steam-id
        // range, vanilla scientists/dwellers are IsNpc, and any plugin that spawns native
        // bots renames them (numeric = never renamed after engine restore).
        private void KillStrayBots()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in _bots.Values)
                if (rec.State.Status != "Retired") names.Add(rec.Identity.Name);
            var snapshot = new List<BasePlayer>();
            foreach (var bp in BasePlayer.allPlayerList) snapshot.Add(bp);
            var killed = 0;
            var numeric = 0;
            foreach (var bp in snapshot)
            {
                if (bp == null || bp.IsDestroyed || bp.IsNpc) continue;
                var uid = (ulong)bp.userID;
                if (_byUserId.ContainsKey(uid)) continue;             // tracked live bot — keep
                if (uid.IsSteamId() && uid < SteamIdBase) continue;   // a real player — keep
                var numericOrphan = bp.IsBot && bp.displayName == uid.ToString();
                if (!numericOrphan && !names.Contains(bp.displayName ?? "")) continue; // not ours
                // v0.20.1: say WHICH body died — a duplicate by name after the two-pass
                // reconcile means a record was already linked to another body of that
                // name (a genuine leftover), so the killed id is the forensic clue.
                DLog($"[reconcile] killing stray body '{bp.displayName}' id {uid} ({(numericOrphan ? "numeric-named engine orphan" : "duplicate by name")}, sleeping={bp.IsSleeping()}).");
                bp.Kill();
                killed++;
                if (numericOrphan) numeric++;
            }
            if (killed > 0) DLog($"[reconcile] removed {killed} stray bot entit(ies) ({numeric} engine-restored numeric-named orphan(s), {killed - numeric} duplicate(s) by name).");
        }

        // Tiered re-gear (Phase 3 F4). Tier 0 is each persona's flavor clothing; tiers 1+
        // swap in progressively better protective sets so the loadout visibly improves as
        // the bot's economy matures. A bot's tier is earned by career and capped by its
        // skill (ComputeKitTier). Externalized to data/FakeFriends/kits.json so loadouts are
        // tunable live without recompiling. Unknown shortnames degrade gracefully (GiveItem
        // warns and skips). MaxKitTier caps the earned/skill math; a ladder may be shorter.
        private const int MaxKitTier = 3;

        private class KitLoadout
        {
            public string[] Wear;   // worn clothing/armor; "shortname@workshopSkinId" applies a skin
            public string Weapon;
            public string Ammo;
            public int AmmoCount;
            public string Med;
            public int Meds;
            public string[] Extra;  // extra belt items (Roleplayer camera etc.); @skin works here too
        }

        private Dictionary<string, List<KitLoadout>> _kits = new Dictionary<string, List<KitLoadout>>(StringComparer.OrdinalIgnoreCase);

        // Last-resort ladder if kits.json is missing/empty and has no "Default" entry.
        private static readonly List<KitLoadout> _fallbackKits = new List<KitLoadout>
        {
            new KitLoadout { Wear = new[] { "burlap.shirt", "burlap.trousers" }, Weapon = "bow.hunting", Ammo = "arrow.wooden", AmmoCount = 24, Med = "bandage", Meds = 2 },
        };

        private void LoadKits()
        {
            var loaded = SafeRead<Dictionary<string, List<KitLoadout>>>($"{DataRoot}/kits");
            if (loaded == null || loaded.Count == 0)
            {
                _kits = BuildDefaultKits();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/kits", _kits);
                DLog("Wrote default kits.json — edit it to tune each persona's tiered loadouts.");
                return;
            }
            _kits = new Dictionary<string, List<KitLoadout>>(loaded, StringComparer.OrdinalIgnoreCase);
        }

        private List<KitLoadout> ResolveKitLadder(string persona)
        {
            List<KitLoadout> ladder;
            if (persona != null && _kits.TryGetValue(persona, out ladder) && ladder != null && ladder.Count > 0) return ladder;
            if (_kits.TryGetValue("Default", out ladder) && ladder != null && ladder.Count > 0) return ladder;
            return _fallbackKits;
        }

        // Authored defaults, written to kits.json on first run. Tier 0 = persona flavor
        // clothing; tiers 1-3 share a protective ladder (bone → roadsign → metal) that a
        // server owner can diverge per persona by editing the JSON.
        private static Dictionary<string, List<KitLoadout>> BuildDefaultKits()
        {
            Func<string[], List<KitLoadout>> ladder = tier0Wear => new List<KitLoadout>
            {
                new KitLoadout { Wear = tier0Wear, Weapon = "bow.hunting", Ammo = "arrow.wooden", AmmoCount = 24, Med = "bandage", Meds = 2 },
                new KitLoadout { Wear = new[] { "bone.armor.jacket", "bone.armor.pants" }, Weapon = "crossbow", Ammo = "arrow.wooden", AmmoCount = 32, Med = "bandage", Meds = 4 },
                new KitLoadout { Wear = new[] { "roadsign.jacket", "roadsign.kilt", "coffeecan.helmet" }, Weapon = "bow.compound", Ammo = "arrow.hv", AmmoCount = 24, Med = "syringe.medical", Meds = 2 },
                new KitLoadout { Wear = new[] { "metal.plate.torso", "roadsign.kilt", "metal.facemask" }, Weapon = "rifle.semiauto", Ammo = "ammo.rifle", AmmoCount = 64, Med = "syringe.medical", Meds = 4 },
            };
            return new Dictionary<string, List<KitLoadout>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Trapper"] = ladder(new[] { "attire.hide.poncho", "attire.hide.pants", "attire.hide.boots" }),
                ["Farmer"] = ladder(new[] { "burlap.shirt", "burlap.trousers", "hat.boonie" }),
                ["Smelter"] = ladder(new[] { "burlap.shirt", "burlap.trousers", "burlap.gloves", "mask.bandana" }),
                ["MonumentRunner"] = ladder(new[] { "burlap.shirt", "burlap.trousers", "shoes.boots" }),
                ["Hostile"] = ladder(new[] { "burlap.shirt", "burlap.trousers", "mask.balaclava" }),
                ["Roleplayer"] = ladder(new[] { "burlap.shirt", "burlap.trousers", "hat.beenie" }),
                ["Default"] = ladder(new[] { "burlap.shirt", "burlap.trousers" }),
            };
        }

        private void GiveKit(BasePlayer bot, BotRecord rec)
        {
            if (bot.inventory == null) return;
            var ladder = ResolveKitLadder(rec?.Identity?.Persona);
            // GearTier is the physical loadout actually worn: −1 = the earned KitTier;
            // a death knocks it down (spare-kit tier or 0) until the group re-gears
            // the bot for scrap (decision 0006 honest re-gear).
            var earned = rec?.Progression?.KitTier ?? 0;
            var gear = rec?.Progression?.GearTier ?? -1;
            var kit = ladder[Mathf.Clamp(gear >= 0 ? gear : earned, 0, ladder.Count - 1)];
            if (kit.Wear != null) foreach (var shortname in kit.Wear) GiveItem(bot, shortname, 1, bot.inventory.containerWear);
            if (!string.IsNullOrEmpty(kit.Weapon)) GiveItem(bot, kit.Weapon, 1, bot.inventory.containerBelt);
            if (kit.Extra != null) foreach (var shortname in kit.Extra) GiveItem(bot, shortname, 1, bot.inventory.containerBelt);
            GiveItem(bot, "torch", 1, bot.inventory.containerBelt);
            GiveItem(bot, "rock", 1, bot.inventory.containerBelt);
            if (!string.IsNullOrEmpty(kit.Ammo) && kit.AmmoCount > 0) GiveItem(bot, kit.Ammo, kit.AmmoCount, bot.inventory.containerMain);
            if (!string.IsNullOrEmpty(kit.Med) && kit.Meds > 0) GiveItem(bot, kit.Med, kit.Meds, bot.inventory.containerMain);
            GiveItem(bot, "apple", 4, bot.inventory.containerMain);
        }

        private void GiveItem(BasePlayer bot, string shortname, int amount, ItemContainer container)
        {
            // "shortname@skinId" applies a workshop skin (content pass: Farmer overalls etc.).
            ulong skin = 0;
            var at = shortname.IndexOf('@');
            if (at > 0)
            {
                ulong.TryParse(shortname.Substring(at + 1), out skin);
                shortname = shortname.Substring(0, at);
            }
            var item = ItemManager.CreateByName(shortname, amount, skin);
            if (item == null)
            {
                PrintWarning($"Unknown item shortname '{shortname}' in kit definition.");
                return;
            }
            if (!item.MoveToContainer(container) && !item.MoveToContainer(bot.inventory.containerMain))
                item.Remove();
        }

        #endregion

        #region World anchor (B4)

        private static string BiomeName(int biome)
        {
            switch (biome)
            {
                case TerrainBiome.ARID: return "Arid";
                case TerrainBiome.TEMPERATE: return "Temperate";
                case TerrainBiome.TUNDRA: return "Tundra";
                case TerrainBiome.ARCTIC: return "Arctic";
                case TerrainBiome.JUNGLE: return "Jungle";
                default: return $"biome{biome}";
            }
        }

        private const int AllBiomes = TerrainBiome.ARID | TerrainBiome.TEMPERATE | TerrainBiome.TUNDRA | TerrainBiome.ARCTIC | TerrainBiome.JUNGLE;

        private static int BiomeMaskFromName(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "arid": return TerrainBiome.ARID;
                case "temperate": return TerrainBiome.TEMPERATE;
                case "tundra": return TerrainBiome.TUNDRA;
                case "arctic": return TerrainBiome.ARCTIC;
                case "jungle": return TerrainBiome.JUNGLE;
                default: return 0;
            }
        }

        // Persona → home-biome mask, from config (PersonaHomeBiomes). No entry → home
        // anywhere. Names are parsed to the TerrainBiome bit constants.
        private int PersonaBiomeMask(string persona)
        {
            List<string> names;
            if (_config.PersonaHomeBiomes != null && persona != null &&
                _config.PersonaHomeBiomes.TryGetValue(persona, out names) && names != null && names.Count > 0)
            {
                var mask = 0;
                foreach (var n in names) mask |= BiomeMaskFromName(n);
                if (mask != 0) return mask;
            }
            return AllBiomes;
        }

        // A thematically-adjacent widening (adds Tundra — cold-forest/snow), tried before
        // giving up on biome entirely. A persona with no config entry already homes anywhere,
        // so it returns -1 (any).
        private int PersonaBiomeMaskBroad(string persona)
        {
            List<string> names;
            var hasEntry = _config.PersonaHomeBiomes != null && persona != null &&
                _config.PersonaHomeBiomes.TryGetValue(persona, out names) && names != null && names.Count > 0;
            return hasEntry ? PersonaBiomeMask(persona) | TerrainBiome.TUNDRA : -1;
        }

        private bool EnsureAnchor(BotRecord rec)
        {
            if (rec.State.HomePos != null)
            {
                // Self-heal: an anchor inside a safezone is a sleeper-kill death loop
                // (the game executes safezone sleepers ~20 min in — see InOrNearSafeZone).
                // Anchors picked before that check existed can still carry the defect,
                // so validate on every session start/respawn and relocate, base and all.
                if (_config.AnchorSafeZoneMargin <= 0f || !InOrNearSafeZone(rec.State.HomePos.V, 10f)) return true;
                var oldBase = GetBase(rec);
                if (oldBase != null)
                {
                    SweepBaseEntities(oldBase);
                    SetBase(rec, null);
                }
                if (rec.Identity.GroupId > 0)
                {
                    foreach (var member in _bots.Values)
                        if (member.Identity.GroupId == rec.Identity.GroupId)
                        {
                            member.State.HomePos = null;
                            member.State.HomeBiome = "";
                            MarkDirty(member.Identity.BotId);
                        }
                }
                else
                {
                    rec.State.HomePos = null;
                    rec.State.HomeBiome = "";
                    MarkDirty(rec.Identity.BotId);
                }
                PrintWarning($"{rec.Identity.Name}'s home region was inside a safezone (sleeper-kill loop) — relocating and re-rolling the anchor.");
                Story("settle", $"{rec.Identity.Name} got moved along by the safezone guards and went looking for a new home");
            }
            // Group members share one home region.
            if (rec.Identity.GroupId > 0)
            {
                foreach (var other in _bots.Values)
                {
                    if (other == rec || other.Identity.GroupId != rec.Identity.GroupId || other.State.HomePos == null) continue;
                    rec.State.HomePos = other.State.HomePos;
                    rec.State.HomeRadius = other.State.HomeRadius;
                    rec.State.HomeBiome = other.State.HomeBiome;
                    MarkDirty(rec.Identity.BotId);
                    return true;
                }
            }
            // Size the anchor for the persona's LARGEST captured stage so every stage
            // fits here without relocating later (footprint grows shack -> final).
            var footprint = Mathf.Max(_config.BaseFootprint, PersonaMaxBaseRadius(rec.Identity.Persona));
            var needWater = rec.Identity.Persona == "Farmer";
            var group = rec.Identity.GroupId;

            // I-2 (v0.18.0): non-Hostile groups become village neighbors while a
            // hamlet has an open slot — anchored on a ring around the founder
            // instead of scattered. Persona biome is deliberately ignored for a
            // joiner (the village trumps the theme); a Farmer still needs water,
            // and a group whose footprint won't fit the ring falls through to the
            // scatter search without burning the hamlet slot. Hostiles never join:
            // 6 of 15 bots are Cobalt now, and a hamlet of hit-squads is an
            // ambush, not a village.
            if (_config.HamletCount > 0 && rec.Identity.Persona != "Hostile")
            {
                // A group rolling a FRESH anchor (first settle or safezone self-heal)
                // isn't a member anywhere any more — drop stale membership so the
                // slot frees and it can join/found cleanly from here.
                RemoveGroupFromHamlets(group);
                var ham = OpenHamlet();
                if (ham != null)
                {
                    var spot = FindHamletSpot(ham, needWater, group, footprint);
                    if (spot != Vector3.zero)
                    {
                        ham.Groups.Add(group);
                        _indexDirty = true;
                        SettleAnchor(rec, spot);
                        Story("settle", $"{rec.Identity.Name} moved into the {(ham.Market ? "market " : "")}hamlet near {GridRef(spot)}");
                        return true;
                    }
                    DLog($"[hamlet] no clear {footprint:0}m ring spot for {rec.Identity.Name} at the hamlet near {GridRef(ham.Center.V)} — scattering instead.");
                }
            }

            // Tiered search: the persona's prime biome first, then a thematically-adjacent
            // widening, then any biome as a last resort. The limiter is usually the LARGE
            // stage-N footprint needing clear ground *inside* a biome — not the biome being
            // absent — so widening the biome is what actually unblocks placement.
            var pos = FindAnchorSpot(PersonaBiomeMask(rec.Identity.Persona), needWater, group, footprint);
            var tier = "prime biome";
            if (pos == Vector3.zero)
            {
                var broad = PersonaBiomeMaskBroad(rec.Identity.Persona);
                if (broad != PersonaBiomeMask(rec.Identity.Persona))
                {
                    pos = FindAnchorSpot(broad, needWater, group, footprint);
                    tier = "adjacent biome";
                }
            }
            if (pos == Vector3.zero)
            {
                pos = FindAnchorSpot(-1, false, group, footprint); // any biome, water optional
                tier = "any biome";
            }
            if (pos == Vector3.zero)
            {
                // Homeless is worse than hunted: a bot with no anchor never settles, never
                // builds and never earns. Drop the den check before giving up entirely.
                pos = FindAnchorSpot(-1, false, group, footprint, avoidPredators: false);
                tier = "any biome (den tolerated)";
            }
            if (pos == Vector3.zero)
            {
                PrintWarning($"No valid home region found for {rec.Identity.Name} ({rec.Identity.Persona}) — no clear {footprint:0}m-footprint spot anywhere; will retry next session.");
                return false;
            }
            if (tier != "prime biome")
                DLog($"[anchor] {rec.Identity.Name}: no clear {footprint:0}m-footprint spot in {rec.Identity.Persona}'s prime biome this seed — widened to {tier}.");
            SettleAnchor(rec, pos);
            // I-2: a corridor-side scatter anchor founds a hamlet while founding
            // slots remain — later eligible settlers ring up around it. The first
            // hamlet founded is the market. A fallback anchor in the deep interior
            // never founds one (a village nobody walks past advertises nothing).
            if (_config.HamletCount > 0 && rec.Identity.Persona != "Hostile" &&
                _index.Hamlets.Count < _config.HamletCount &&
                (_config.AnchorCorridorBias <= 0f || NearCorridor(pos, _config.AnchorCorridorBias)))
            {
                var market = _index.Hamlets.Count == 0;
                _index.Hamlets.Add(new HamletRec { Center = new Vec3(pos), Groups = new List<int> { group }, Market = market });
                _indexDirty = true;
                Story("settle", market
                    ? $"{rec.Identity.Name} settled in the {rec.State.HomeBiome.ToLowerInvariant()} near {GridRef(pos)} and put up a market sign"
                    : $"{rec.Identity.Name} settled in the {rec.State.HomeBiome.ToLowerInvariant()} near {GridRef(pos)}, hoping for neighbors");
                if (market) MaintainHamletMarkers();
            }
            else
                Story("settle", $"{rec.Identity.Name} settled in the {rec.State.HomeBiome.ToLowerInvariant()} near {GridRef(pos)}");
            return true;
        }

        // Shared settle tail — the story beat differs per path, everything else is this.
        private void SettleAnchor(BotRecord rec, Vector3 pos)
        {
            rec.State.HomePos = new Vec3(pos);
            rec.State.HomeRadius = _config.WanderRadius;
            rec.State.HomeBiome = BiomeName(TerrainMeta.BiomeMap.GetBiomeMaxType(pos));
            MarkDirty(rec.Identity.BotId);
            DLog($"[anchor] {rec.Identity.Name} ({rec.Identity.Persona}) anchored at {pos} biome={rec.State.HomeBiome}");
        }

        // First hamlet with an open neighbor slot. Founders count toward the slots;
        // a group whose every member is out (Retired/Fallen) stops holding one —
        // its plot decays and someone new can ring up in its place.
        private HamletRec OpenHamlet()
        {
            if (_index?.Hamlets == null) return null;
            foreach (var ham in _index.Hamlets)
            {
                if (ham?.Center == null || ham.Groups == null) continue;
                var occupied = 0;
                foreach (var gid in ham.Groups)
                    foreach (var rec in _bots.Values)
                        if (rec.Identity.GroupId == gid && !IsOut(rec)) { occupied++; break; }
                if (occupied < Mathf.Max(1, _config.HamletSlots)) return ham;
            }
            return null;
        }

        private void RemoveGroupFromHamlets(int group)
        {
            if (_index?.Hamlets == null) return;
            foreach (var ham in _index.Hamlets)
                if (ham?.Groups != null && ham.Groups.Remove(group)) _indexDirty = true;
        }

        // Ring search around the hamlet founder: village spacing (HamletRingMin..Max
        // from the center, ≥60 m from every existing anchor) replaces the 200 m
        // scatter keep-apart; every other ground rule — safezone margin, monument
        // distance, flat clear footprint, no predator den — applies unchanged.
        private Vector3 FindHamletSpot(HamletRec ham, bool needWater, int myGroup, float footprint)
        {
            var center = ham.Center.V;
            var rMin = Mathf.Max(30f, _config.HamletRingMin);
            var rMax = Mathf.Max(rMin, _config.HamletRingMax);
            for (var i = 0; i < 120; i++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var pos = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * UnityEngine.Random.Range(rMin, rMax);
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 3f) continue;
                if (TerrainMeta.HeightMap.GetSlope(pos) > 20f) continue;
                if (NearMonument(pos, 100f)) continue;
                if (_config.AnchorSafeZoneMargin > 0f && InOrNearSafeZone(pos, footprint + _config.AnchorSafeZoneMargin)) continue;
                if (NearOtherAnchor(pos, 60f, myGroup)) continue;
                if (needWater && !NearWater(pos, 120f)) continue;
                if (!IsBaseSpotClear(pos, footprint)) continue;
                if (PredatorTerritory(pos)) continue;
                return pos;
            }
            return Vector3.zero;
        }

        // Reject spots on/next to roads, rails, monuments, cliffs — a base has to
        // sit on buildable, obstacle-free ground, not just wander-suitable terrain.
        private const int BadTopology = TerrainTopology.ROAD | TerrainTopology.ROADSIDE
            | TerrainTopology.RAIL | TerrainTopology.RAILSIDE | TerrainTopology.MONUMENT
            | TerrainTopology.CLIFF | TerrainTopology.CLIFFSIDE | TerrainTopology.RIVER
            | TerrainTopology.LAKE | TerrainTopology.OCEAN | TerrainTopology.SWAMP;

        private Vector3 FindAnchorSpot(int biomeMask, bool needWater, int myGroup, float footprint, bool avoidPredators = true)
        {
            var half = World.Size * 0.42f;
            footprint = Mathf.Max(6f, footprint);
            // I-1 (v0.18.0): prefer spots beside the roads and rails players actually
            // travel — a valid corridor spot wins outright; the first valid spot in
            // the deep interior is kept as the spare and used only when 800 tries
            // find nothing corridor-side. Once the spare is banked, off-corridor
            // candidates skip the expensive checks entirely.
            var corridorBias = _config.AnchorCorridorBias;
            var fallback = Vector3.zero;
            for (var i = 0; i < 800; i++)
            {
                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 3f) continue;
                if (TerrainMeta.HeightMap.GetSlope(pos) > 20f) continue;
                if (biomeMask != -1 && (TerrainMeta.BiomeMap.GetBiomeMaxType(pos) & biomeMask) == 0) continue;
                var onCorridor = corridorBias <= 0f || NearCorridor(pos, corridorBias);
                if (!onCorridor && fallback != Vector3.zero) continue;
                if (NearMonument(pos, 100f)) continue;
                if (_config.AnchorSafeZoneMargin > 0f && InOrNearSafeZone(pos, footprint + _config.AnchorSafeZoneMargin)) continue;
                if (NearOtherAnchor(pos, 200f, myGroup)) continue;
                if (needWater && !NearWater(pos, 120f)) continue;
                if (!IsBaseSpotClear(pos, footprint)) continue;
                if (avoidPredators && PredatorTerritory(pos)) continue; // last: only pays for otherwise-valid spots
                if (onCorridor) return pos;
                fallback = pos;
            }
            if (fallback != Vector3.zero)
                DLog($"[anchor] no clear spot within {corridorBias:0}m of a road/rail this search — settling for a scattered one.");
            return fallback;
        }

        // I-1: subsampled point cloud of every road + rail spline, built once per
        // load (the map can't change under a running plugin). ~40 m spacing is
        // plenty for a 150 m corridor test and keeps a 4700 map's cloud around a
        // thousand points, so the 800-try anchor search stays a one-off cost at
        // settle time. Custom maps without roads/rails leave it empty → every spot
        // reads off-corridor and the search behaves exactly as before v0.18.
        private List<Vector3> _corridorPoints;

        private bool NearCorridor(Vector3 pos, float dist)
        {
            if (_corridorPoints == null) BuildCorridorCache();
            var sqr = dist * dist;
            for (var i = 0; i < _corridorPoints.Count; i++)
            {
                var d = _corridorPoints[i] - pos;
                d.y = 0f; // spline height ≠ terrain height; the map-plane distance is what matters
                if (d.sqrMagnitude < sqr) return true;
            }
            return false;
        }

        private void BuildCorridorCache()
        {
            _corridorPoints = new List<Vector3>(1024);
            var path = TerrainMeta.Path;
            if (path != null)
            {
                AddCorridorPoints(path.Roads);
                AddCorridorPoints(path.Rails);
            }
            DLog($"[anchor] corridor cache built: {_corridorPoints.Count} road/rail spline points.");
        }

        private void AddCorridorPoints(List<PathList> lists)
        {
            if (lists == null) return;
            foreach (var pl in lists)
            {
                var pts = pl?.Path?.Points;
                if (pts == null) continue;
                var last = new Vector3(float.MinValue, 0f, float.MinValue);
                foreach (var p in pts)
                {
                    if ((p - last).sqrMagnitude < 40f * 40f) continue;
                    _corridorPoints.Add(p);
                    last = p;
                }
            }
        }

        // Don't build the cabin in a den. Bears, wolves and boars hold a territory rather
        // than roaming the whole map, so a bot who draws an anchor inside one is not
        // unlucky once — it's mauled there for the rest of the wipe. In the 2026-07-26
        // week GrannyMags died to a wolf at Y5 ten times out of ten deaths and Nils_44 to
        // one at F16 six out of six, and between them they produced 1% of the roster's
        // career. A single sample is weak evidence about a wanderer but strong evidence
        // about a resident, which is exactly the animal worth avoiding.
        //
        // Runs only for spots that already passed every cheaper test, so the 800-try
        // anchor search doesn't pay for it on obviously bad ground. The list is reused
        // rather than allocated per call for the same reason.
        // BaseCombatEntity scan, not BaseNpc — Gen2 wolves live outside the BaseNpc
        // tree entirely (decision 0010); IsDangerWildlife sorts the fauna out.
        private readonly List<BaseCombatEntity> _predatorScan = new List<BaseCombatEntity>();

        // Safezone triggers (Outpost, Bandit, fishing villages) reach well past the
        // 100m monument-CENTRE check in FindAnchorSpot, and Rust kills sleepers inside
        // them — an offline bot is a sleeper at its anchor, so an anchor in a zone is
        // a 20-minute die/respawn/re-gear loop (EchoRose: 144 deaths at AA10, all
        // "Suicide 999", before this check). Distance to the trigger's AABB rather
        // than ClosestPoint: trigger colliders aren't guaranteed convex, and the
        // slightly generous box errs in the only safe direction.
        private bool InOrNearSafeZone(Vector3 pos, float margin)
        {
            if (margin <= 0f) return false;
            var zones = TriggerSafeZone.allSafeZones;
            for (var i = 0; i < zones.Count; i++)
            {
                var col = zones[i] != null ? zones[i].triggerCollider : null;
                if (col == null) continue;
                if (col.bounds.SqrDistance(pos) < margin * margin) return true;
            }
            return false;
        }

        private bool PredatorTerritory(Vector3 pos)
        {
            var radius = _config.AnchorPredatorRadius;
            if (radius <= 0f) return false;
            _predatorScan.Clear();
            Vis.Entities(pos, radius, _predatorScan);
            for (var i = 0; i < _predatorScan.Count; i++)
            {
                var e = _predatorScan[i];
                if (e == null || e.IsDestroyed || e.IsDead()) continue;
                if (IsDangerWildlife(e)) return true;
            }
            return false;
        }

        // One clearance test used by both anchor selection and per-stage paste:
        // flat terrain + clear topology + no solid obstruction across the footprint.
        private bool IsBaseSpotClear(Vector3 center, float radius) => SpotBlockReason(center, radius) == null;

        // Same four checks as IsBaseSpotClear, but names the one that failed (null = clear)
        // so a "doesn't fit" can say *why*. Strings are only built on failure, so the hot
        // FindAnchorSpot path (800 tries) stays cheap.
        private string SpotBlockReason(Vector3 center, float radius)
        {
            if ((TerrainMeta.TopologyMap.GetTopology(center, radius) & BadTopology) != 0) return "bad topology (road/rail/cliff/water/monument/swamp)";
            if (!IsFootprintFlat(center, radius)) return "terrain not flat enough (>1.6m height variance across the footprint)";
            if (HasBlockingEntity(center, radius)) return "a blocking entity (junkpile/road/powerline/rock/cliff or a foreign building)";
            if (SolidObstructionInFootprint(center, radius)) return "a rock/cliff collider inside the footprint";
            if (NearQuestShop(center, radius)) return "a RustQuests trader shop's no-build zone";
            return null;
        }

        private static int _obstructionMask = -1;

        // Rock formations and cliffs carry colliders on the World layer and are NOT
        // reported as BaseEntities — physics is the reliable way to catch a base
        // footprint pushing into terrain geometry (live finding: stage 3 clipped a rock).
        // Trees are deliberately NOT included: they're destructible and everywhere in the
        // Trapper's woods, so treating them as hard blockers would leave wooded personas
        // unable to anchor. Our own bots/base entities are ignored.
        private bool SolidObstructionInFootprint(Vector3 center, float radius)
        {
            if (_obstructionMask == -1) _obstructionMask = LayerMask.GetMask("World");
            var hits = Physics.OverlapSphere(center + Vector3.up * 1.5f, radius, _obstructionMask, QueryTriggerInteraction.Ignore);
            foreach (var col in hits)
            {
                var ent = col.GetComponentInParent<BaseEntity>();
                if (ent != null && (ent is BasePlayer || IsOurOwnedEntity(ent.OwnerID))) continue;
                return true; // rock/cliff/tree (no BaseEntity) or a foreign structure
            }
            return false;
        }

        // Sample terrain height across the whole footprint (two rings + centre), not just
        // the outer ring — a mound or dip in the interior would otherwise be missed and
        // partially bury the base.
        private bool IsFootprintFlat(Vector3 center, float radius)
        {
            var min = center.y;
            var max = center.y;
            foreach (var ringR in new[] { radius, radius * 0.5f })
                for (var i = 0; i < 8; i++)
                {
                    var a = i * 45f * Mathf.Deg2Rad;
                    var p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ringR;
                    var h = TerrainMeta.HeightMap.GetHeight(p);
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            return (max - min) <= 1.6f;
        }

        // Junkpiles, roads decor, existing deployables, other buildings — anything
        // that would clip a paste. Building blocks from OUR own bases are fine.
        private bool HasBlockingEntity(Vector3 center, float radius)
        {
            var hits = new List<BaseEntity>();
            Vis.Entities(center, radius + 4f, hits);
            foreach (var ent in hits)
            {
                if (ent == null || ent.IsDestroyed) continue;
                if (ent is BasePlayer || IsOurOwnedEntity(ent.OwnerID)) continue; // our own bots/bases don't block us (esp. the standing base during an in-place upgrade)
                if (ent is JunkPile || ent is BuildingBlock || ent is JunkPileWater) return true;
                var sn = ent.ShortPrefabName ?? "";
                if (sn.Contains("junkpile") || sn.Contains("road") || sn.Contains("powerline") || sn.Contains("rock_") || sn.Contains("cliff")) return true;
            }
            return false;
        }

        // RustQuests' trader shops are caboose prefabs, not building blocks, so
        // neither HasBlockingEntity nor the World-layer sweep sees them — a bot
        // would happily anchor on top of one. Ask the plugin instead (soft: no
        // RustQuests, no restriction). The margin is our own footprint plus a
        // buffer, so the base lands clear of the zone rather than against it.
        //
        // Snapshot rather than a Call per candidate: SpotBlockReason runs 800
        // times per anchor search, and every bot re-anchors at once on the first
        // boot after a wipe — a cross-plugin Call there would box two arguments
        // and allocate a params array on each try. Shops place ~60 s after boot
        // and the placer retries every 5 min, so a one-minute-stale snapshot is
        // never worse than the placement cadence itself.
        private const float QuestShopBuffer = 15f;
        private const float QuestShopCacheSeconds = 60f;

        private readonly List<Vector3> _questShops = new List<Vector3>();
        private float _questShopRadius;
        private float _questShopsFetchedAt = -9999f;

        private void RefreshQuestShops()
        {
            // Never stamp the clock while RustQuests is absent, or a load during
            // the TTL would go unnoticed for a minute.
            if (RustQuests == null)
            {
                _questShops.Clear();
                _questShopRadius = 0f;
                _questShopsFetchedAt = -9999f;
                return;
            }
            if (Time.realtimeSinceStartup - _questShopsFetchedAt < QuestShopCacheSeconds) return;
            _questShopsFetchedAt = Time.realtimeSinceStartup;
            _questShops.Clear();
            _questShopRadius = 0f;
            var r = RustQuests.Call("GetQuestShopNoBuildRadius");
            if (r is float) _questShopRadius = (float)r;
            if (_questShopRadius <= 0f) return; // zone disabled server-side
            var list = RustQuests.Call("GetQuestShopPositions") as List<Vector3>;
            if (list != null)
                for (var i = 0; i < list.Count; i++) _questShops.Add(list[i]);
        }

        private bool NearQuestShop(Vector3 center, float radius)
        {
            RefreshQuestShops();
            if (_questShopRadius <= 0f || _questShops.Count == 0) return false;
            // Cylinder, matching RustQuests' own test — height is irrelevant, a
            // base on the ridge above a shop is still on top of it.
            var margin = _questShopRadius + radius + QuestShopBuffer;
            var sqr = margin * margin;
            for (var i = 0; i < _questShops.Count; i++)
            {
                var dx = _questShops[i].x - center.x;
                var dz = _questShops[i].z - center.z;
                if (dx * dx + dz * dz <= sqr) return true;
            }
            return false;
        }

        private bool NearMonument(Vector3 pos, float dist)
        {
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return false;
            foreach (var m in monuments)
                if (m != null && Vector3.Distance(m.transform.position, pos) < dist) return true;
            return false;
        }

        private bool NearOtherAnchor(Vector3 pos, float dist, int myGroup)
        {
            foreach (var rec in _bots.Values)
            {
                if (rec.State.HomePos == null || rec.Identity.GroupId == myGroup) continue;
                if (Vector3.Distance(rec.State.HomePos.V, pos) < dist) return true;
            }
            return false;
        }

        private bool NearWater(Vector3 center, float radius)
        {
            for (var i = 0; i < 12; i++)
            {
                var a = i * 30f * Mathf.Deg2Rad;
                var p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (radius * (i % 2 == 0 ? 1f : 0.5f));
                p.y = TerrainMeta.HeightMap.GetHeight(p);
                if (WaterLevel.GetWaterDepth(p, false, false) > 0.3f) return true;
            }
            return false;
        }

        #endregion

        #region Session scheduler (B3)

        private Oxide.Plugins.Timer _tickTimer;

        // Online-hours window [start, end] for a schedule profile, from config.
        private void ScheduleWindow(string profile, out float start, out float end)
        {
            List<int> w;
            var cfg = _config.ScheduleWindows;
            if (cfg == null || !cfg.TryGetValue(profile ?? "random", out w) || w == null || w.Count < 2)
                if (cfg == null || !cfg.TryGetValue("random", out w) || w == null || w.Count < 2) { start = 0f; end = 24f; return; }
            start = w[0]; end = w[1];
        }

        private static DateTime ParseTime(string s)
        {
            DateTime dt;
            return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt) ? dt : DateTime.MaxValue;
        }

        private void ScheduleNextSession(BotRecord rec)
        {
            float wStart, wEnd;
            ScheduleWindow(rec.Identity.Traits.ScheduleProfile, out wStart, out wEnd);
            var now = DateTime.Now;
            var startHour = UnityEngine.Random.Range(wStart, wEnd);
            var start = now.Date.AddHours(startHour);
            if (start <= now.AddMinutes(2)) start = start.AddDays(1);
            rec.State.NextSessionAt = start.ToString("o");
            MarkDirty(rec.Identity.BotId);
        }

        private void SchedulerTick()
        {
            var now = DateTime.Now;
            RefreshBaselessSet(); // feeds the wildlife exemption; base changes are slow
            foreach (var rec in _bots.Values)
            {
                switch (rec.State.Status)
                {
                    case "Unspawned":
                    case "Sleeping":
                        if (string.IsNullOrEmpty(rec.State.NextSessionAt)) ScheduleNextSession(rec);
                        else if (ParseTime(rec.State.NextSessionAt) <= now) StartSession(rec);
                        break;
                    case "Online":
                        if (GetLive(rec.Identity.BotId) == null)
                        {
                            // Entity vanished outside the death flow (admin kill etc.)
                            UnlinkLive(rec.Identity.BotId); // clears _wander/_byUserId leftovers
                            rec.State.Status = "Unspawned";
                            rec.State.EngineUserId = 0;
                            rec.State.SessionEndAt = "";
                            MarkDirty(rec.Identity.BotId);
                        }
                        else if (!string.IsNullOrEmpty(rec.State.SessionEndAt) && ParseTime(rec.State.SessionEndAt) <= now)
                            EndSession(rec);
                        else if (_config.MonumentRuns && IsRunnerP(rec))
                            MaybeStartMonumentRun(rec); // 6A: observed + cooled-down runners head out
                        break;
                    case "Dead":
                        if (!string.IsNullOrEmpty(rec.State.RespawnAt) && ParseTime(rec.State.RespawnAt) <= now)
                            RespawnBot(rec);
                        break;
                }
            }
            UpdateNightLights();
            InteriorFillTick();
            RegenLiveBots();

            // 4B — delayed post-permadeath refill: newcomers move in hours later. A due
            // timestamp is always cleared (even with ReplaceDeadBots since toggled off)
            // so it can't fire months-stale after a re-enable.
            if (!string.IsNullOrEmpty(_index.NextRosterFillAt) && ParseTime(_index.NextRosterFillAt) <= now)
            {
                _index.NextRosterFillAt = "";
                _indexDirty = true;
                if (_config.ReplaceDeadBots)
                {
                    var made = FillRoster();
                    if (made > 0)
                    {
                        Story("arrival", made > 1 ? $"{made} newcomers moved onto the island" : "a newcomer moved onto the island");
                        DLog($"[roster] post-permadeath refill created {made} identit(ies).");
                    }
                }
            }
            NotifyPresence(); // catch-all for any status flip above (HUD redraw)
        }

        // Connection-less bots don't run metabolism, so a mauled bot would be stuck
        // below its hunt threshold forever (instant-fleeing every fight). Heal them
        // gradually while resting/working — never mid-fight — so they recover between
        // hunts the way a player would after eating.
        private void RegenLiveBots()
        {
            foreach (var kv in _live)
            {
                var bp = kv.Value;
                if (bp == null || bp.IsDestroyed || bp.IsDead() || bp.IsWounded()) continue;
                var max = bp.MaxHealth();
                if (bp.health >= max) continue;
                WorkState wk;
                if (_work.TryGetValue(kv.Key, out wk) && wk.Phase == WorkPhase.Hunting) continue; // not mid-fight
                // Holed up at base heals faster — "went home to lie low and patch up".
                WanderState ws;
                var holedUp = _wander.TryGetValue(kv.Key, out ws) && ws.Retreating;
                var regen = _config.BotRegenPerTick;
                bp.health = Mathf.Min(max, bp.health + (holedUp ? regen * 2.5f : regen));
            }
        }

        private void StartSession(BotRecord rec)
        {
            if (IsOut(rec) || rec.State.Status == "Dead") return;
            _wander.Remove(rec.Identity.BotId); // never inherit stale GoingHome/corners
            _sessionDeaths.Remove(rec.Identity.BotId); // fresh session, fresh patience
            _lastDeathAt.Remove(rec.Identity.BotId);   // last session's deaths can't spiral this one
            _deathGapMin.Remove(rec.Identity.BotId);
            if (!EnsureAnchor(rec))
            {
                rec.State.NextSessionAt = DateTime.Now.AddHours(1).ToString("o");
                MarkDirty(rec.Identity.BotId);
                return;
            }
            // Seasoning clock: first session of the wipe starts the "time on the
            // server" that hardens a bot's aim and hitting power (world-feel pass).
            if (string.IsNullOrEmpty(rec.State.WipeFirstOnlineAt))
            {
                rec.State.WipeFirstOnlineAt = DateTime.Now.ToString("o");
                MarkDirty(rec.Identity.BotId);
            }
            if (IsHostileP(rec)) { StartHostileSession(rec); return; } // 5B: prowl, not wander
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null)
            {
                var offset = UnityEngine.Random.insideUnitCircle * 3f;
                bot = SpawnBotEntity(rec, rec.State.HomePos.V + new Vector3(offset.x, 0.1f, offset.y));
                if (bot == null)
                {
                    rec.State.NextSessionAt = DateTime.Now.AddMinutes(10).ToString("o");
                    MarkDirty(rec.Identity.BotId);
                    return;
                }
            }
            else
            {
                // Woke on the bag inside → step out to the exterior anchor so navmesh
                // movement resumes (the interior floor is off-navmesh), the entrance door
                // swinging open as he leaves. Also covers stranded relogs.
                if (bot.IsSleeping()) bot.EndSleeping();
                // N-2 (IN-3.3): walk out through the door instead of the hop when the mesh
                // gives a complete path from the bag; the WanderTick walk owns him from
                // his first online tick.
                if (!TryWalkOutOnWake(rec, bot))
                {
                    if (rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
                    BotRecord dh;
                    var db = GetGroupBase(rec.Identity.GroupId, out dh);
                    if (db != null) FlickBaseDoors(db, bot.transform.position);
                }
            }
            // A wake/spawn hook handler may have killed the bot synchronously —
            // don't clobber the death flow's bookkeeping with an Online write.
            if (bot == null || bot.IsDestroyed || bot.IsDead() || rec.State.Status == "Dead") return;
            ResupplyKitAmmo(bot, rec); // WD-4: a real player logs in and grabs rounds from the box
            var lo = Mathf.Max(0.25f, _config.SessionHoursMin);
            var hours = UnityEngine.Random.Range(lo, Mathf.Max(lo, _config.SessionHoursMax)) * WipeHype();
            rec.State.Status = "Online";
            rec.State.SessionEndAt = DateTime.Now.AddHours(hours).ToString("o");
            rec.State.NextSessionAt = "";
            MarkDirty(rec.Identity.BotId);
            DLog($"[session] {rec.Identity.Name} came online for {hours:0.0}h ({rec.Identity.Persona}, home {rec.State.HomeBiome})");
            if (_config.PresenceJoinLeave) PrintToChat(L("Presence.Join", rec.Identity.Name));
            NotifyPresence();
        }

        // Session end is a two-step exit: walk home first (visible teleports break the
        // illusion — live feedback), then collapse into a sleeper. Teleport remains
        // only as a fallback when pathing fails or the walk exceeds its deadline.
        private void EndSession(BotRecord rec)
        {
            if (IsHostileP(rec)) { EndHostileSession(rec); return; }
            // 6A: a mid-run logoff drops the driver where it stands; the normal
            // walk-home (or its teleport fallback) takes it from there. Pocketed loot
            // rides along and deposits at the next run's depot.
            if (_runnerActive.Contains(rec.Identity.BotId)) DetachRunnerDriver(rec.Identity.BotId);
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null || rec.State.HomePos == null ||
                Vector3.Distance(bot.transform.position, rec.State.HomePos.V) <= 6f)
            {
                FinalizeSessionEnd(rec);
                return;
            }
            WanderState ws;
            if (!_wander.TryGetValue(rec.Identity.BotId, out ws))
                _wander[rec.Identity.BotId] = ws = new WanderState { Speed = 2.2f + rec.Identity.Traits.SkillLevel };
            if (ws.GoingHome)
            {
                if (Time.realtimeSinceStartup > ws.GoHomeDeadline) FinalizeSessionEnd(rec);
                return; // still walking; scheduler re-checks every tick
            }
            ws.GoingHome = true;
            ws.GoHomeDeadline = Time.realtimeSinceStartup + 180f;
            ws.PausedUntil = 0f;
            ws.FailedUntil = 0f;
            if (!PlanPathTo(bot, ws, rec.State.HomePos.V))
            {
                FinalizeSessionEnd(rec);
                return;
            }
            DLog($"[session] {rec.Identity.Name} is heading home to log off");
        }

        // ---- 5B: hostile session lifecycle (prowls from the camp anchor) ------------
        private readonly Dictionary<int, float> _hostileHomeDeadline = new Dictionary<int, float>();

        private void StartHostileSession(BotRecord rec)
        {
            var botId = rec.Identity.BotId;
            // 5D — session-overlap cap: only MaxConcurrentHostiles prowl at once. The
            // rest defer, so the island never turns into a Cobalt convention (and the
            // never-dormant think cost stays bounded). Status-based count covers both
            // chassis (5.5: native bodies aren't HostileNpc entities).
            var prowling = 0;
            foreach (var r2 in _bots.Values)
                if (r2.Identity.BotId != botId && IsHostileP(r2) && r2.State.Status == "Online") prowling++;
            if (prowling >= Mathf.Max(1, _config.MaxConcurrentHostiles))
            {
                rec.State.NextSessionAt = DateTime.Now.AddMinutes(UnityEngine.Random.Range(30f, 60f)).ToString("o");
                MarkDirty(botId);
                DLog($"[session] {rec.Identity.Name}: prowl deferred — {prowling} hostile(s) already out (cap {_config.MaxConcurrentHostiles}).");
                return;
            }
            _hostileHomeDeadline.Remove(botId); // stale walk-home deadline from a death mid-logoff
            // Phase 5.5 — the prowl runs in the SAME native-bot body the offline
            // sleeper uses: wake it (or spawn it), graft the driver, go hunting.
            // The disguise never breaks: no despawn/respawn seam, a player-looking
            // body end to end.
            if (_config.HostileNativeBodies)
            {
                var bot = GetLive(botId);
                if (bot == null)
                {
                    var off = UnityEngine.Random.insideUnitCircle * 3f;
                    bot = SpawnBotEntity(rec, rec.State.HomePos.V + new Vector3(off.x, 0.1f, off.y));
                    if (bot == null)
                    {
                        rec.State.NextSessionAt = DateTime.Now.AddMinutes(10).ToString("o");
                        MarkDirty(botId);
                        return;
                    }
                }
                else
                {
                    if (bot.IsSleeping()) bot.EndSleeping();
                    bot.Teleport(GroundSnap(rec.State.HomePos.V)); // step out of the cabin
                    BotRecord dh;
                    var db = GetGroupBase(rec.Identity.GroupId, out dh);
                    if (db != null) FlickBaseDoors(db, bot.transform.position);
                }
                if (bot == null || bot.IsDestroyed || bot.IsDead() || rec.State.Status == "Dead") return;
                AttachHostileDriver(rec, bot);
                var nlo = Mathf.Max(0.25f, _config.SessionHoursMin);
                var nhours = UnityEngine.Random.Range(nlo, Mathf.Max(nlo, _config.SessionHoursMax)) * WipeHype();
                rec.State.Status = "Online";
                rec.State.SessionEndAt = DateTime.Now.AddHours(nhours).ToString("o");
                rec.State.NextSessionAt = "";
                MarkDirty(botId);
                DLog($"[session] {rec.Identity.Name} came online for {nhours:0.0}h (Hostile prowl, native body, radius {HostilePatrolRadius(rec):0}m)");
                if (_config.PresenceJoinLeave) PrintToChat(L("Presence.Join", rec.Identity.Name));
                NotifyPresence();
                return;
            }
            // Legacy scientist chassis: the offline sleeper body steps aside for the
            // working entity.
            var existing = GetLive(botId);
            if (existing != null)
            {
                UnlinkLive(botId);
                if (!existing.IsDestroyed) existing.Kill();
            }
            var offset = UnityEngine.Random.insideUnitCircle * 3f;
            var spot = rec.State.HomePos.V + new Vector3(offset.x, 0.1f, offset.y);
            NavMeshHit hit;
            if (NavSample(spot, out hit, 10f)) spot = hit.position;
            var h = SpawnHostile(spot, rec);
            if (h == null)
            {
                rec.State.NextSessionAt = DateTime.Now.AddMinutes(10).ToString("o");
                MarkDirty(botId);
                return;
            }
            LinkLive(rec, h);
            rec.State.EngineUserId = (ulong)h.userID;
            var lo = Mathf.Max(0.25f, _config.SessionHoursMin);
            var hours = UnityEngine.Random.Range(lo, Mathf.Max(lo, _config.SessionHoursMax)) * WipeHype();
            rec.State.Status = "Online";
            rec.State.SessionEndAt = DateTime.Now.AddHours(hours).ToString("o");
            rec.State.NextSessionAt = "";
            MarkDirty(botId);
            DLog($"[session] {rec.Identity.Name} came online for {hours:0.0}h (Hostile prowl, radius {h.RoamRadius:0}m)");
            if (_config.PresenceJoinLeave) PrintToChat(L("Presence.Join", rec.Identity.Name));
            NotifyPresence();
        }

        private void EndHostileSession(BotRecord rec)
        {
            var botId = rec.Identity.BotId;
            var live = GetLive(botId);
            var h = live as HostileNpc;
            // Phase 5.5 native body: walk home like a resident logging off, then
            // detach the driver and collapse into the ordinary cabin sleeper.
            if (h == null && live != null && live.GetComponent<HostileDriver>() != null)
            {
                if (live.IsDestroyed || rec.State.HomePos == null ||
                    Vector3.Distance(live.transform.position, rec.State.HomePos.V) <= 8f)
                {
                    FinalizeNativeHostileSessionEnd(rec, live);
                    return;
                }
                float ndeadline;
                if (_hostileHomeDeadline.TryGetValue(botId, out ndeadline))
                {
                    if (Time.realtimeSinceStartup > ndeadline) FinalizeNativeHostileSessionEnd(rec, live);
                    else live.GetComponent<HostileDriver>()?.OrderHome(rec.State.HomePos.V); // re-assert against roam drift
                    return;
                }
                _hostileHomeDeadline[botId] = Time.realtimeSinceStartup + 180f;
                live.GetComponent<HostileDriver>()?.OrderHome(rec.State.HomePos.V);
                DLog($"[session] {rec.Identity.Name} is heading back to camp to log off");
                return;
            }
            if (h == null || h.IsDestroyed || rec.State.HomePos == null ||
                Vector3.Distance(h.transform.position, rec.State.HomePos.V) <= 8f)
            {
                FinalizeHostileSessionEnd(rec, h);
                return;
            }
            float deadline;
            if (_hostileHomeDeadline.TryGetValue(botId, out deadline))
            {
                if (Time.realtimeSinceStartup > deadline) FinalizeHostileSessionEnd(rec, h);
                else h.GetComponent<HostileBrain>()?.OrderHome(rec.State.HomePos.V); // re-assert against roam drift
                return;
            }
            _hostileHomeDeadline[botId] = Time.realtimeSinceStartup + 180f;
            h.GetComponent<HostileBrain>()?.OrderHome(rec.State.HomePos.V);
            DLog($"[session] {rec.Identity.Name} is heading back to camp to log off");
        }

        // 5.5 — the native body never despawns: driver off, holstered, tucked into
        // the cabin as a sleeper via the SAME path residents log off through.
        private void FinalizeNativeHostileSessionEnd(BotRecord rec, BasePlayer body)
        {
            _hostileHomeDeadline.Remove(rec.Identity.BotId);
            DetachHostileDriver(body);
            FinalizeSessionEnd(rec);
        }

        private void FinalizeHostileSessionEnd(BotRecord rec, HostileNpc h)
        {
            var botId = rec.Identity.BotId;
            _hostileHomeDeadline.Remove(botId);
            UnlinkLive(botId);
            if (h != null) DespawnHostile(h);
            // The offline body: an ordinary native-bot sleeper at the camp — to the
            // world, a player who logged off at home (the disguise holds while asleep).
            var offset = UnityEngine.Random.insideUnitCircle * 2f;
            var sleeper = SpawnBotEntity(rec, rec.State.HomePos.V + new Vector3(offset.x, 0.1f, offset.y));
            if (sleeper != null)
            {
                var shelter = GetShelterPoint(rec);
                if (shelter != Vector3.zero) sleeper.Teleport(shelter);
                if (!sleeper.IsSleeping()) sleeper.StartSleeping();
                sleeper.SendNetworkUpdateImmediate();
            }
            rec.State.Status = "Sleeping";
            rec.State.SessionEndAt = "";
            ScheduleNextSession(rec);
            MarkDirty(botId);
            DLog($"[session] {rec.Identity.Name} went offline (sleeper at camp), next session {rec.State.NextSessionAt}");
            if (_config.PresenceJoinLeave) PrintToChat(L("Presence.Leave", rec.Identity.Name));
            NotifyPresence();
        }

        private void FinalizeSessionEnd(BotRecord rec)
        {
            var bot = GetLive(rec.Identity.BotId);
            if (bot != null && bot.IsWounded()) return; // let the wound resolve first; retried next tick
            if (bot != null)
            {
                // Turn in for the night on the bag inside the cabin (interior shelter
                // point, height-validated), doors swinging shut behind him.
                BotRecord holder;
                var b = GetGroupBase(rec.Identity.GroupId, out holder);
                if (b != null) FlickBaseDoors(b, bot.transform.position);
                var shelter = GetShelterPoint(rec);
                if (shelter != Vector3.zero) bot.Teleport(shelter);
                else if (rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
                if (!bot.IsSleeping()) bot.StartSleeping();
                bot.SendNetworkUpdateImmediate();
            }
            WanderState ws;
            if (_wander.TryGetValue(rec.Identity.BotId, out ws))
            {
                ws.GoingHome = false;
                ws.Corners.Clear();
                ws.CornerIndex = 0;
                CloseWalkDoor(ws); ws.Walk = 0; // a sleep walk ends here
            }
            rec.State.Status = "Sleeping";
            rec.State.SessionEndAt = "";
            ScheduleNextSession(rec);
            MarkDirty(rec.Identity.BotId);
            DLog($"[session] {rec.Identity.Name} went offline (sleeper at home), next session {rec.State.NextSessionAt}");
            if (_config.PresenceJoinLeave) PrintToChat(L("Presence.Leave", rec.Identity.Name));
            NotifyPresence();
        }

        private void RespawnBot(BotRecord rec)
        {
            // A hostile never resumes an interrupted prowl on respawn — he "logs back
            // in" later; the offline body is the ordinary native-bot sleeper below.
            if (IsHostileP(rec)) rec.State.SessionEndAt = "";
            if (!EnsureAnchor(rec))
            {
                rec.State.RespawnAt = DateTime.Now.AddMinutes(30).ToString("o");
                MarkDirty(rec.Identity.BotId);
                return;
            }
            // Respawn on the group's sleeping bag when one stands — INSIDE the walls,
            // like a real player. The outdoor home-pos spawn was feeding respawners
            // straight back to whatever killed them (the boar-camp extinction loop in
            // the 2026-07-20 logs: same bot, same coordinates, three deaths running).
            // No bag entity yet (fill stalled — e.g. the holder fell before placing
            // it)? The recorded bed anchor is still a point inside the walls, so spawn
            // there. Only a truly baseless bot respawns in the open — and never under
            // a prowling predator's nose: WildSadie67's wolf metronome (2026-07-22)
            // was a re-kill within a minute of every respawn, for ten hours straight.
            var offset = UnityEngine.Random.insideUnitCircle * 3f;
            var spawnPos = rec.State.HomePos.V + new Vector3(offset.x, 0.1f, offset.y);
            var spawnDesc = "at home";
            var inside = false;
            BotRecord bholder;
            var bb = GetGroupBase(rec.Identity.GroupId, out bholder);
            if (bb != null && bb.OwnerId != 0)
            {
                var bags = new List<SleepingBag>();
                Vis.Entities(bb.Origin.V, 30f, bags);
                foreach (var bag in bags)
                    if (bag != null && !bag.IsDestroyed && bag.OwnerID == bb.OwnerId)
                    { spawnPos = bag.transform.position + Vector3.up * 0.1f; spawnDesc = "on his bag"; inside = true; break; }
                if (!inside)
                {
                    var home = rec.State.HomePos.V;
                    var shelter = GetShelterPoint(rec);
                    if (shelter != Vector3.zero && (shelter.x != home.x || shelter.z != home.z))
                    { spawnPos = shelter + Vector3.up * 0.1f; spawnDesc = "inside his base"; inside = true; }
                }
            }
            if (!inside)
            {
                var predator = NearestDangerAnimal(spawnPos, Mathf.Max(10f, _config.RespawnPredatorRadius));
                if (predator != null)
                {
                    rec.State.RespawnAt = DateTime.Now.AddMinutes(5).ToString("o");
                    MarkDirty(rec.Identity.BotId);
                    DLog($"[respawn] {rec.Identity.Name} held his respawn — a {ThreatName(predator)} is prowling the anchor (retry in 5m)");
                    return;
                }
            }
            // 4A — honest re-gear: the old kit is on the corpse. Wear the group's banked
            // spare or start over in the tier-0 flavor kit. Peek now / commit after the
            // spawn succeeds, so a failed spawn can't vaporize the spare. GearTier at or
            // above the earned tier normalizes to -1 (= "wearing earned gear", no
            // buy-back owed).
            rec.Progression = rec.Progression ?? new BotProgression();
            int spareTier;
            var hasSpare = TryPeekSpareKit(rec, out spareTier);
            var wearTier = hasSpare ? spareTier : 0;
            rec.Progression.GearTier = wearTier >= rec.Progression.KitTier ? -1 : wearTier;
            var bot = SpawnBotEntity(rec, spawnPos);
            if (bot == null)
            {
                rec.Progression.GearTier = -1; // nothing spawned, nothing consumed — re-evaluate on the retry
                rec.State.RespawnAt = DateTime.Now.AddMinutes(10).ToString("o");
                MarkDirty(rec.Identity.BotId);
                return;
            }
            if (hasSpare) CommitSpareKit(rec, spareTier); // he's wearing it — now it leaves the locker
            rec.State.RespawnAt = "";
            DLog($"[respawn] {rec.Identity.Name} respawned {spawnDesc} after death #{rec.State.Deaths}");
            // Rage-quit valve: past the cap he's done for the day — turn in instead of
            // resuming the session. A real player camped by wolves (or anything else)
            // logs off; only a bot marches back out again.
            // WildSadie67 ate 7 deaths in ONE session (2026-07-22) even with safe
            // indoor respawns — her home region overlaps a wolf den, and no doorstep
            // check outranges a Gen2 pack's hunt.
            // Flat 2/session was too blunt: GrannyMags (2026-07-30) ended EVERY session
            // inside half an hour and logged 0.7 productive hours across four days, so a
            // slow pair of deaths across a long session no longer ends it — only a fast
            // pair (the camper signature) or the hard cap does.
            int sessDeaths;
            _sessionDeaths.TryGetValue(rec.Identity.BotId, out sessDeaths);
            float gapMin;
            if (!_deathGapMin.TryGetValue(rec.Identity.BotId, out gapMin)) gapMin = float.MaxValue;
            var spiral = sessDeaths >= Mathf.Max(2, _config.RagequitSpiralDeaths)
                         && gapMin <= Mathf.Max(1f, _config.RagequitSpiralMinutes);
            if ((sessDeaths >= Mathf.Max(2, _config.RagequitDeaths) || spiral) && !string.IsNullOrEmpty(rec.State.SessionEndAt))
            {
                rec.State.SessionEndAt = "";
                var why = spiral ? $"{sessDeaths} deaths, last two {gapMin:0} min apart" : $"{sessDeaths} deaths this session";
                DLog($"[respawn] {rec.Identity.Name} is logging off in disgust — {why}");
                Story("ragequit", $"{rec.Identity.Name} called it a day after dying {sessDeaths} times");
            }
            if (!string.IsNullOrEmpty(rec.State.SessionEndAt) && ParseTime(rec.State.SessionEndAt) > DateTime.Now)
            {
                rec.State.Status = "Online"; // resume the interrupted session
                if (inside)
                {
                    // He woke up back home, not at the death site: hold in cover a
                    // couple of minutes before stepping back out (the normal hole-up
                    // exit takes over), instead of standing outside the door where
                    // whatever killed him is often still waiting.
                    var ws = new WanderState { Speed = 2.2f + rec.Identity.Traits.SkillLevel };
                    _wander[rec.Identity.BotId] = ws;
                    ws.Retreating = true;
                    ws.Sheltered = true;
                    ws.RetreatGoal = spawnPos;
                    ws.HoldUntil = Time.realtimeSinceStartup + 120f;
                }
                MarkDirty(rec.Identity.BotId);
                NotifyPresence();
            }
            else
            {
                // Killed outside a session (or it lapsed while dead): turn in on the bag
                // INSIDE the cabin, not the exterior spawn point (mirror FinalizeSessionEnd)
                // — no join/leave chatter, keep any pending schedule.
                var shelter = GetShelterPoint(rec);
                if (shelter != Vector3.zero) bot.Teleport(shelter);
                if (!bot.IsSleeping()) bot.StartSleeping();
                bot.SendNetworkUpdateImmediate();
                rec.State.Status = "Sleeping";
                rec.State.SessionEndAt = "";
                if (string.IsNullOrEmpty(rec.State.NextSessionAt)) ScheduleNextSession(rec);
                MarkDirty(rec.Identity.BotId);
            }
        }

        #endregion

        #region Idle wander (C1)

        // Movement approach (see PLAN.md open question, resolved by decompile):
        // BasePlayer.MovePosition() is the same code path a real client's movement
        // ticks drive — it updates tick history (observer interpolation/animation),
        // fires NetworkPositionTick, and updates triggers. Stepped calls along a
        // NavMesh path at WanderTickSeconds cadence read as walking to observers.

        private const float WanderTickSeconds = 0.1f; // 10Hz — matches player-tick smoothness for observers
        private Oxide.Plugins.Timer _wanderTimer;
        private static int _groundMask = -1;

        private static int _roofMask = -1;
        private static bool IsUnderRoof(Vector3 pos)
        {
            if (_roofMask == -1) _roofMask = LayerMask.GetMask("Construction");
            return Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.up, 4f, _roofMask, QueryTriggerInteraction.Ignore);
        }

        private static Vector3 GroundSnap(Vector3 pos)
        {
            if (_groundMask == -1) _groundMask = LayerMask.GetMask("Terrain", "World", "Construction", "Default");
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out hit, 4f, _groundMask, QueryTriggerInteraction.Ignore))
                return new Vector3(pos.x, hit.point.y + 0.05f, pos.z);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.1f;
            return pos;
        }

        private class WanderState
        {
            public List<Vector3> Corners = new List<Vector3>();
            public int CornerIndex;
            public float PausedUntil;   // Time.realtimeSinceStartup
            public float Speed = 2.5f;
            public float FailedUntil;   // backoff after pathing failures
            public bool GoingHome;      // session ended — walking to anchor to log off
            public float GoHomeDeadline;
            public bool Working;        // embodied economy work (Stage B) owns the path this tick
            public float NextWorkEval;  // realtime — throttle the observed/base re-check to ~1Hz
            public bool Retreating;     // sensed danger / hurt — falling back to base to hole up
            public float NextMutterAt;  // grudge-callout chat cooldown (realtime)
            public float RetreatCheckAt; // realtime — throttle the threat scan
            public float PatrolStoryAt;  // realtime — last "took cover from" story beat (v0.21.2 throttle)
            // v0.21.3 danger memory: where the last patrol-grade threat stood. Work
            // and wander goal picking steer clear of it while it's fresh, and a repeat
            // alarm from the same spot holds the bot indoors for minutes, not seconds.
            public Vector3 DangerPos;
            public float DangerRadius;
            public float DangerUntil;    // realtime — memory expiry (refreshed per alarm)
            public int DangerHits;       // consecutive alarms from the same spot
            // Phase 7 attend-on-approach: pause + face a player who walks up
            public float AttendCheckAt;      // realtime — 1 Hz proximity scan throttle
            public float AttendUntil;        // attending while now < this (0 = not attending)
            public float AttendCooldownUntil;// no fresh attend before this (post-attend / post-convo break)
            public BasePlayer AttendTarget;  // who we're facing (runtime ref)
            // Stuck detection (admin panel): reference point ages while the bot idles
            public Vector3 StuckRefPos;
            public float StuckRefAt;         // realtime the bot was last seen ≥3 m from the ref (0 = uninitialized)
            public float StuckCheckAt;       // 30 s throttle
            // v0.15 engagement pass: aware-speed + roleplayer busking
            public float AwareCheckAt;       // 1 Hz nearby-player scan throttle
            public float AwareCap;           // catchable pace while a player is near (0 = nobody around)
            public bool Busking;             // roleplayer show state — instrument out
            public float NextQuirkAt;        // gesture/pamphlet/line roll cadence while attending
            public float NextNoteAt;         // note-walk cadence
            public int NotePhase;            // walk position in the instrument's binding table
            public int NoteN; public int NoteT; public int NoteO; // last note sent (needed to stop it)
            public float StopNoteAt;         // release the string at this time (0 = none pending)
            public float HoldUntil;     // realtime — stay holed up until at least this time
            public Vector3 RetreatGoal; // interior shelter point to duck into on arrival
            public bool LeadAway;       // WH-1: heli-hunted — fleeing AWAY from home, never toward it
            public bool Sheltered;      // teleported inside — standing in cover, healing
            public string RetreatStory; // WB-2 — "holed up" beat deferred until he actually gets there
            // 5A2-R — stand-and-fight state (non-null FightTarget = fighting)
            public BaseCombatEntity FightTarget;
            public string FightDesc;    // "a boar" / display name — survives the entity's destruction
            public bool FightWasPlayer;
            public Vector3 FightLastPos; // last seen position — corpse search after a win
            public float FightStartAt;  // engage-timeout anchor (realtime)
            public float FightLastLos;  // lost-LOS break-off anchor (realtime)
            public float NextShotAt;    // fire cadence (realtime)
            public float NextFaceAt;    // aim network-sync throttle (realtime)
            public Dictionary<ulong, float> FightCooldown; // given-up targets ignored until (realtime); lazy
            public float FightSuppressUntil; // WG-2 — no new fights until this (realtime); animals have no userID to cooldown
            public List<Vector3> Obstacles = new List<Vector3>(); // cacti etc. near home (navmesh ignores them)
            public float ObstaclesAt;   // when the obstacle cache was last built
            // I-3 (v0.19.0) — social call: 0 none, 1 walking out, 2 doorstep show, 3 walking home
            public int VisitPhase;
            public Vector3 VisitGoal;     // doorstep point (navmesh-sampled outside the base)
            public Vector3 VisitTc;       // the visited TC's position (facing + arrival radius)
            public ulong VisitOwner;      // base owner — chat only lands when they're online
            public float VisitShowUntil;  // doorstep dwell
            public float VisitDeadline;   // per-leg abort valve — a stuck trip turns around
            public float NextVisitAt;     // realtime of the next allowed visit (0 = roll the first)
            public float NextVisitRollAt; // eligibility re-check throttle
            // N-2 Phase 2 (decision 0011) — interior walk: through the own door to an
            // interior anchor and back out. While Walk != 0 the walk owns the tick
            // (fights, threat sense and session end still preempt it).
            public int Walk;              // 0 none, 1 walking in, 2 dwelling inside, 3 walking out
            public int WalkKind;          // on-arrival behaviour: 0 = dwell only (Phase 2 harness); Phase 3 adds deposit/shelter/sleep
            public Vector3 WalkGoal;      // interior anchor (in) or the exterior anchor (out)
            public float WalkStartAt;     // abort valve (25 s per leg)
            public float WalkDwellUntil;  // dwell inside before walking out
            public Vector3 WalkProgressPos; public float WalkProgressAt; // stall valve (1 m / 4 s)
            public List<Door> WalkDoors;  // the base's own doors, cached at walk start
            public Door PassDoor;         // the door currently swung open for him
            // IN-4.1 — Walk == 4: plan failed while a navmesh save was in flight (door flag
            // writes defer during a save); retry once after 3 s, then hop.
            public float WalkRetryAt; public Vector3 WalkRetryGoal; public bool WalkRetryOut;
        }

        private readonly Dictionary<int, WanderState> _wander = new Dictionary<int, WanderState>();
        // Deaths since the session started (transient — a reload forgives). Past the
        // configured cap the bot logs off on respawn instead of feeding whatever's
        // camping him: the rage-quit valve that hard-caps ANY death loop per session.
        private readonly Dictionary<int, int> _sessionDeaths = new Dictionary<int, int>();
        // Wall-clock of the last death and the gap to the one before it, feeding the
        // spiral check — deaths minutes apart mean a camper, hours apart mean bad luck.
        private readonly Dictionary<int, DateTime> _lastDeathAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, float> _deathGapMin = new Dictionary<int, float>();

        // Ownership stamps this plugin uses (base furniture / night campfires). A bot
        // must never avoid its own home, so these are excluded from hazard scanning.
        private static bool IsOurOwnedEntity(ulong ownerId)
        {
            return ownerId == NightFireOwnerId || ownerId >= BaseOwnerBase;
        }

        // "Wider than cacti": a wandering bot should avoid anything that HURTS on
        // contact and isn't carved into the navmesh — cacti, and any other world
        // hazard with a damage trigger — detected generically by the presence of a
        // TriggerHurt child (technique borrowed from HumanNPC's Evade, kept in-house
        // so we take on no plugin dependency). Our own placed hazards (furnace/campfire
        // heat) are excluded by ownership; other players' bases and mounts are left
        // alone (a wandering resident isn't expected to path through a player's base).
        private static bool IsAvoidableObstacle(BaseEntity ent)
        {
            if (ent is BasePlayer || ent is BaseMountable) return false;
            if (IsOurOwnedEntity(ent.OwnerID)) return false;
            if (ent.GetComponentInChildren<TriggerHurt>() == null &&
                ent.GetComponentInChildren<TriggerHurtNotChild>() == null) return false;
            return true;
        }

        private const float ObstacleAvoidRadius = 1.6f;   // keep path segments this far from an obstacle
        private const float ObstacleCacheSeconds = 180f;

        private void EnsureObstacleCache(BotRecord rec, WanderState ws)
        {
            if (rec.State.HomePos == null) return;
            if (ws.Obstacles.Count > 0 && Time.realtimeSinceStartup - ws.ObstaclesAt < ObstacleCacheSeconds) return;
            ws.Obstacles.Clear();
            ws.ObstaclesAt = Time.realtimeSinceStartup;
            var scan = new List<BaseEntity>();
            Vis.Entities(rec.State.HomePos.V, Mathf.Max(20f, rec.State.HomeRadius) + 10f, scan);
            foreach (var ent in scan)
                if (ent != null && !ent.IsDestroyed && IsAvoidableObstacle(ent))
                    ws.Obstacles.Add(ent.transform.position);
        }

        // Perpendicular distance from point p to segment a-b, in the horizontal plane.
        private static float FlatSegmentDistance(Vector3 a, Vector3 b, Vector3 p)
        {
            var ax = a.x; var az = a.z;
            var bx = b.x; var bz = b.z;
            var dx = bx - ax; var dz = bz - az;
            var lenSq = dx * dx + dz * dz;
            if (lenSq < 0.0001f) return Mathf.Sqrt((p.x - ax) * (p.x - ax) + (p.z - az) * (p.z - az));
            var t = Mathf.Clamp01(((p.x - ax) * dx + (p.z - az) * dz) / lenSq);
            var cx = ax + t * dx; var cz = az + t * dz;
            return Mathf.Sqrt((p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz));
        }

        private bool PathClearOfObstacles(BasePlayer bot, WanderState ws)
        {
            if (ws.Obstacles.Count == 0 || ws.Corners.Count == 0) return true;
            var prev = bot.transform.position;
            for (var i = 0; i < ws.Corners.Count; i++)
            {
                var cur = ws.Corners[i];
                foreach (var obs in ws.Obstacles)
                    if (FlatSegmentDistance(prev, cur, obs) < ObstacleAvoidRadius) return false;
                prev = cur;
            }
            return true;
        }

        private readonly List<int> _wanderIds = new List<int>(); // reused each tick

        private void WanderTick()
        {
            if (_live.Count == 0) return;
            var now = Time.realtimeSinceStartup;
            // Iterate the (small) live set, not all records — this runs at 10Hz.
            // Snapshot ids so re-entrant UnlinkLive can't mutate mid-iteration.
            _wanderIds.Clear();
            foreach (var id in _live.Keys) _wanderIds.Add(id);
            foreach (var botId in _wanderIds)
            {
                BotRecord rec;
                if (!_bots.TryGetValue(botId, out rec)) continue;
                if (IsHostileP(rec)) continue; // 5B: the HostileBrain drives its entity
                if (_runnerActive.Contains(botId)) continue; // 6A: the RunnerDriver drives its entity
                if (rec.State.Status != "Online" || rec.State.HomePos == null) continue;
                var bot = GetLive(botId);
                if (bot == null || bot.IsSleeping() || bot.IsDead()) continue;
                if (bot.IsWounded())
                {
                    // Connection-less bots never tick wounded recovery, so a downed bot
                    // would lie there forever needing a manual revive. Let it bleed out
                    // into the normal death → respawn loop instead.
                    bot.Die();
                    continue;
                }

                WanderState ws;
                if (!_wander.TryGetValue(rec.Identity.BotId, out ws))
                    _wander[rec.Identity.BotId] = ws = new WanderState { Speed = 5f + rec.Identity.Traits.SkillLevel }; // sprint-class — real players don't stroll (owner call, 2026-07-23)
                if (ws.GoingHome && now > ws.GoHomeDeadline)
                {
                    FinalizeSessionEnd(rec); // walk took too long — fallback finish
                    continue;
                }
                // Stuck detection: the ref point ages unless the bot moves ≥3 m or has
                // a legitimate reason to stand (conversation, attending, holed up).
                if (now >= ws.StuckCheckAt)
                {
                    ws.StuckCheckAt = now + 30f;
                    if (ws.StuckRefAt <= 0f
                        || (bot.transform.position - ws.StuckRefPos).sqrMagnitude > 9f
                        || ws.Sheltered || ws.AttendUntil > now || _convos.ContainsKey(botId))
                    { ws.StuckRefPos = bot.transform.position; ws.StuckRefAt = now; }
                }
                // 5A2-R: an active fight owns the bot (stand ground, track, fire) at
                // full tick rate — everything else waits until it resolves.
                if (ws.FightTarget != null)
                {
                    if (ws.GoingHome) EndFight(rec, bot, ws, now, false); // session over — walk away
                    else { FightStep(rec, bot, ws, now); continue; }
                }
                // Threat sense → retreat to base and hole up (overrides work + wander,
                // heals faster while holed up). Not while already logging off.
                if (!ws.GoingHome && now >= ws.RetreatCheckAt)
                {
                    ws.RetreatCheckAt = now + 1.5f;
                    UpdateThreatResponse(rec, bot, ws, now);
                }
                // I-3: anything urgent cancels a social call — retreat is already
                // heading home, a fight owns the tick, session end outranks a stroll.
                if (ws.VisitPhase != 0 && (ws.Retreating || ws.GoingHome || ws.FightTarget != null))
                    ws.VisitPhase = 0;

                // N-2 Phase 2: an interior walk owns the tick — arrival/dwell/door pass are
                // handled in InteriorWalkTick; with corners left, the generic stepping at
                // the bottom moves him and nothing in between (chat, visits, work
                // re-planning, pauses) gets to steal the path.
                var walkOwns = false;
                if (ws.Walk != 0)
                {
                    if (InteriorWalkTick(rec, bot, ws, now)) continue;
                    walkOwns = ws.Walk != 0;
                }

                // Phase 7: an active SLM conversation pins the bot in place, facing
                // its partner — visible "I'm talking to you". Any damage, retreat,
                // fight, session end, or the partner walking off breaks it.
                if (!walkOwns && _convos.Count > 0)
                {
                    SlmConvo cv;
                    if (_convos.TryGetValue(botId, out cv))
                    {
                        if (!ConvoStillValid(bot, ws, cv)) { EndConvo(botId, cv); _convos.Remove(botId); }
                        else
                        {
                            var partner = BasePlayer.FindByID(cv.PlayerId);
                            if (partner != null)
                            {
                                var dir = partner.transform.position - bot.transform.position; dir.y = 0f;
                                if (dir.sqrMagnitude > 0.25f) bot.OverrideViewAngles(Quaternion.LookRotation(dir.normalized).eulerAngles);
                            }
                            continue;
                        }
                    }
                }

                // Phase 7 attend-on-approach: a player walking up makes the bot pause
                // and face them BEFORE any chat — the invitation to talk. Capped at
                // 25 s per approach (an AFK statue doesn't freeze the work loop) with
                // a break before it re-attends; a real conversation takes over above.
                // v0.15: Roleplayers attend SLM or not, farther, longer, and with a
                // shorter break — and they busk while doing it (instrument, gestures,
                // pamphlets). Approaching one should feel like walking up to a show.
                var rpQuirk = _config.RoleplayerAntics && rec.Identity.Persona == "Roleplayer";
                var attendRange = rpQuirk ? Mathf.Max(_config.SLMAttendRange, _config.RoleplayerAttendRange) : _config.SLMAttendRange;
                if (ws.Busking && ws.AttendUntil <= now) { StopBuskNote(bot, ws); ws.Busking = false; }
                if (!walkOwns && (SlmEnabled || rpQuirk) && attendRange > 0.01f && !ws.GoingHome && !ws.Retreating)
                {
                    if (now >= ws.AttendCheckAt)
                    {
                        ws.AttendCheckAt = now + 1f;
                        var attending = ws.AttendUntil > now;
                        if ((attending || now >= ws.AttendCooldownUntil)
                            && (rpQuirk || (SlmChatEligible(rec) && (rec.Identity.Traits?.Chattiness ?? 0.3f) > 0.1f)))
                        {
                            BasePlayer near = null;
                            var lim = attendRange * (attending ? 1.5f : 1f); // sticky once attending
                            var bestA = lim * lim;
                            foreach (var p in BasePlayer.activePlayerList)
                            {
                                if (p == null || p.IsDead() || !p.userID.IsSteamId()) continue;
                                var d = (p.transform.position - bot.transform.position).sqrMagnitude;
                                if (d < bestA) { bestA = d; near = p; }
                            }
                            if (near != null && !attending)
                            {
                                ws.AttendUntil = now + (rpQuirk ? 45f : 25f);
                                ws.AttendCooldownUntil = now + (rpQuirk ? 65f : 85f); // show 45 s + 20 s break / attend 25 s + 60 s work
                            }
                            else if (near == null) ws.AttendUntil = 0f; // they left — back to work
                            ws.AttendTarget = near;
                        }
                        else if (!attending) ws.AttendTarget = null;
                    }
                    if (ws.AttendUntil > now)
                    {
                        var t = ws.AttendTarget;
                        if (t != null && !t.IsDestroyed && !t.IsDead())
                        {
                            var dir = t.transform.position - bot.transform.position; dir.y = 0f;
                            if (dir.sqrMagnitude > 0.25f) bot.OverrideViewAngles(Quaternion.LookRotation(dir.normalized).eulerAngles);
                            if (rpQuirk) BuskStep(rec, bot, ws, t, now);
                            continue;
                        }
                        ws.AttendUntil = 0f; ws.AttendTarget = null;
                        if (ws.Busking) { StopBuskNote(bot, ws); ws.Busking = false; }
                    }
                }

                // I-3: the doorstep show (phase 2) — face the base, wave, leave the
                // gifts when the dwell runs out. Attend/conversation blocks above can
                // steal the tick (the owner came out — that's the point); the show
                // resumes or wraps as soon as they release it.
                if (!walkOwns && ws.VisitPhase == 2)
                {
                    VisitShowStep(rec, bot, ws, now);
                    continue;
                }
                // I-3: an idle Roleplayer occasionally sets out to call on a player base.
                if (!walkOwns && _config.BotVisits && ws.VisitPhase == 0 && rec.Identity.Persona == "Roleplayer"
                    && !ws.Working && !ws.Retreating && !ws.GoingHome && ws.FightTarget == null
                    && ws.AttendUntil <= now && now >= ws.NextVisitRollAt
                    && !_convos.ContainsKey(botId))
                {
                    ws.NextVisitRollAt = now + 37f; // cheap eligibility throttle; the pace lives on NextVisitAt
                    TryStartVisit(rec, bot, ws, now);
                }

                // Embodied work (Stage B): a watched economic bot works for real instead
                // of idle-wandering. The observed/base re-check (deserializes the base)
                // is throttled to ~1Hz; movement + hit-driving still run at full tick.
                if (!walkOwns && !ws.Retreating && ws.VisitPhase == 0 && now >= ws.NextWorkEval)
                {
                    UpdateWorkActivity(rec, bot, ws, now);
                    ws.NextWorkEval = now + 1f;
                }
                if (!walkOwns && !ws.Working && !ws.Retreating && ws.VisitPhase == 0 && (now < ws.PausedUntil || now < ws.FailedUntil)) continue;

                // Hunting owns its own movement (moving target, direct pursuit) — handle
                // it before the idle/gather corner-follow.
                if (!walkOwns && ws.Working && !ws.GoingHome && !ws.Retreating)
                {
                    WorkState wkh;
                    if (_work.TryGetValue(rec.Identity.BotId, out wkh) && wkh.Phase == WorkPhase.Hunting)
                    {
                        HuntStep(rec, bot, ws, wkh, now);
                        continue;
                    }
                }

                if (ws.CornerIndex >= ws.Corners.Count)
                {
                    if (ws.GoingHome)
                    {
                        // Walking home to log off: arrived (or can't get closer).
                        // Arrival is judged horizontally with slack — the navmesh
                        // goal can legally land several meters from the anchor.
                        if (rec.State.HomePos == null ||
                            FlatDistance(bot.transform.position, rec.State.HomePos.V) <= 10f ||
                            !PlanPathTo(bot, ws, rec.State.HomePos.V) ||
                            PathLength(bot.transform.position, ws) < 1.5f)
                        {
                            // N-2 (IN-3.3): at the anchor — walk in to the bag and turn in there
                            // (FinalizeSessionEnd fires on arrival); the hop is the fallback.
                            var bagSpot = GetShelterPoint(rec);
                            if (rec.State.HomePos != null && bagSpot != Vector3.zero && bagSpot != GroundSnap(rec.State.HomePos.V)
                                && FlatDistance(bot.transform.position, rec.State.HomePos.V) <= 10f
                                && StartInteriorWalk(rec, bot, ws, bagSpot, WalkSleep, now))
                                continue;
                            FinalizeSessionEnd(rec);
                        }
                        continue;
                    }
                    if (ws.Retreating)
                    {
                        RetreatArrived(rec, bot, ws, now); // close on home, then hole up
                        continue;
                    }
                    if (ws.VisitPhase == 1)
                    {
                        VisitTravelArrived(rec, bot, ws, now); // leg done — knock or re-leg
                        continue;
                    }
                    if (ws.VisitPhase == 3)
                    {
                        VisitHomeArrived(rec, bot, ws, now); // leg done — home or re-leg
                        continue;
                    }
                    if (ws.Working)
                    {
                        WorkArrived(rec, bot, ws, now); // decide the next work step
                        continue;
                    }
                    // Arrived (or no path yet): sometimes stand around, then pick a new spot.
                    if (ws.Corners.Count > 0 && UnityEngine.Random.Range(0f, 1f) < 0.4f)
                    {
                        ws.PausedUntil = now + UnityEngine.Random.Range(4f, 18f);
                        bot.OverrideViewAngles(new Vector3(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                        bot.SendNetworkUpdate();
                    }
                    if (!PlanNewWanderPath(rec, bot, ws))
                        ws.FailedUntil = now + 20f; // no navmesh here — back off, retry later
                    continue;
                }

                // Horizontal-only steering: GroundSnap owns the y axis, so measuring
                // corner arrival in 3D can trap the bot on any navmesh/terrain height
                // mismatch (live bug: vibrating in place at a corner forever).
                var target = ws.Corners[ws.CornerIndex];
                var pos = bot.transform.position;
                var toFlat = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
                // v0.15 aware-speed (owner call 2026-08-08: engagement beats realism):
                // near a real player the sprint-class cruise drops to a catchable,
                // trait-shaped pace — wary bots stay brisk, social ones amble. Retreats
                // keep full speed (fear is still fear); fights/hunts never reach here.
                var speed = ws.Walk != 0 ? Mathf.Min(ws.Speed, WalkIndoorSpeed) : ws.Speed;
                if (_config.AwareSlowRadius > 0.01f && !ws.Retreating)
                {
                    if (now >= ws.AwareCheckAt)
                    {
                        ws.AwareCheckAt = now + 1f;
                        ws.AwareCap = 0f;
                        var r2 = _config.AwareSlowRadius * _config.AwareSlowRadius;
                        foreach (var p in BasePlayer.activePlayerList)
                        {
                            if (p == null || p.IsDead() || !p.userID.IsSteamId()) continue;
                            if ((p.transform.position - pos).sqrMagnitude > r2) continue;
                            var tr = rec.Identity.Traits;
                            ws.AwareCap = Mathf.Clamp(2.6f + 1.7f * (tr?.Caution ?? 0.5f) - 0.9f * (tr?.Chattiness ?? 0.3f), 1.8f, 4.4f);
                            break;
                        }
                    }
                    if (ws.AwareCap > 0f) speed = Mathf.Min(speed, ws.AwareCap);
                }
                var step = speed * WanderTickSeconds;
                var dist = toFlat.magnitude; // hottest loop: one sqrt, reused below
                if (dist <= step)
                {
                    ws.CornerIndex++;
                    MoveBotStep(bot, new Vector3(target.x, pos.y, target.z), toFlat);
                    continue;
                }
                MoveBotStep(bot, pos + toFlat * (step / dist), toFlat);
            }
        }

        private void MoveBotStep(BasePlayer bot, Vector3 dest, Vector3 facing)
        {
            if (facing.sqrMagnitude > 0.01f)
            {
                var yaw = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.z)).eulerAngles.y;
                bot.OverrideViewAngles(new Vector3(0f, yaw, 0f));
            }
            bot.MovePosition(GroundSnap(dest));
        }

        private bool PlanNewWanderPath(BotRecord rec, BasePlayer bot, WanderState ws)
        {
            EnsureObstacleCache(rec, ws);
            var home = rec.State.HomePos.V;
            var radius = Mathf.Max(20f, rec.State.HomeRadius);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var pick = UnityEngine.Random.insideUnitCircle * radius;
                var goal = home + new Vector3(pick.x, 0f, pick.y);
                if (InDangerZone(ws, goal, Time.realtimeSinceStartup)) continue; // v0.21.3: not back toward the scientist
                // Reject routes that clip a cactus etc.; another random target will clear.
                if (PlanPathTo(bot, ws, goal) && PathClearOfObstacles(bot, ws)) return true;
            }
            return false;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float PathLength(Vector3 from, WanderState ws)
        {
            if (ws.CornerIndex >= ws.Corners.Count) return 0f;
            var total = Vector3.Distance(from, ws.Corners[ws.CornerIndex]); // include first leg
            for (var i = ws.CornerIndex; i < ws.Corners.Count - 1; i++)
                total += Vector3.Distance(ws.Corners[i], ws.Corners[i + 1]);
            return total;
        }

        // ---- Navmesh shim (Oct 2026 Livestock update) ----
        // Facepunch replaced Unity's navmesh with RustNav (Recast, default ON; the old bake
        // only exists under -useOldNavmesh). UnityEngine.AI.NavMesh.* then answers nothing
        // map-wide — v0.20.1 residents froze at their anchors for a whole wipe morning.
        // RustNavMeshHelpers dispatches to whichever mesh is live, so EVERY static query
        // goes through here. allowNpcDoors=false keeps closed doors solid (parity with the
        // old bake); open doorways and building floors are now walkable, which is new.
        // Agent-typed queries (Animal/Humanoid bakes, decision 0008) only exist on the
        // Unity mesh — RustNav is one surface for terrain + monuments + buildings, so the
        // typed overload just drops the filter there.
        private static bool NavSample(Vector3 pos, out NavMeshHit hit, float maxDistance, bool allowNpcDoors = false)
        {
            return Rust.Ai.Gen2.RustNavMeshHelpers.SamplePosition(pos, out hit, maxDistance, Rust.Ai.Gen2.RustNavMeshHelpers.AllAreas, allowNpcDoors);
        }

        private static bool NavSampleTyped(Vector3 pos, out NavMeshHit hit, float maxDistance, NavMeshQueryFilter filter)
        {
            if (ConVar.AI.useUnityNavmesh) return NavMesh.SamplePosition(pos, out hit, maxDistance, filter);
            return NavSample(pos, out hit, maxDistance);
        }

        private Rust.Ai.Gen2.RustNavMeshPath _navPath; // reused — CalculatePath overwrites it

        private bool PlanPathTo(BasePlayer bot, WanderState ws, Vector3 goal)
        {
            return PlanPathTo(bot, ws, goal, false);
        }

        // allowNpcDoors: route through the bot's OWN closed doors (marked NpcOpenable in
        // Phase 1); player doors stay Blocked. Interior goals sit on a floor above the
        // terrain, so the doors-allowed plan snaps the goal with GroundSnap (Construction
        // layer) instead of the heightmap.
        private bool PlanPathTo(BasePlayer bot, WanderState ws, Vector3 goal, bool allowNpcDoors)
        {
            if (allowNpcDoors) goal = GroundSnap(goal);
            else goal.y = TerrainMeta.HeightMap.GetHeight(goal);
            NavMeshHit startHit, goalHit;
            if (!NavSample(bot.transform.position, out startHit, 6f, allowNpcDoors)) return false;
            if (!NavSample(goal, out goalHit, allowNpcDoors ? 2f : 6f, allowNpcDoors)) return false;

            if (_navPath == null) _navPath = new Rust.Ai.Gen2.RustNavMeshPath();
            if (!Rust.Ai.Gen2.RustNavMeshHelpers.CalculatePath(startHit.position, goalHit.position, Rust.Ai.Gen2.RustNavMeshHelpers.AllAreas, _navPath, allowNpcDoors) ||
                _navPath.status != NavMeshPathStatus.PathComplete || _navPath.corners.Count < 2)
                return false;

            ws.Corners.Clear();
            for (var i = 1; i < _navPath.corners.Count; i++) ws.Corners.Add(_navPath.corners[i].Value);
            ws.CornerIndex = 0;
            return true;
        }

        #endregion

        #region Death handling (B5)

        // Bleed-out attribution: when a wounded player dies, the final death HitInfo
        // has no initiator (the bleeding killed them) → "the world", no grudge, no
        // credit. The engine's lastAttacker still points at whoever downed them —
        // substitute it within a recency window that covers the wounded state.
        private HitInfo WithResolvedKiller(BasePlayer victim, HitInfo info)
        {
            var ini = info?.Initiator;
            if (ini != null && ini != victim) return info;
            // Any combat entity counts — the patrol heli's napalm and fire ticks kill
            // with no initiator, same as bleed-out (observed live: "died to the
            // environment" after a heli strike).
            var last = victim.lastAttacker;
            if (last == null || last == victim || last.IsDestroyed) return info;
            if (UnityEngine.Time.time - victim.lastAttackedTime > 120f) return info; // stale — unrelated old fight
            if (info == null) info = new HitInfo();
            info.Initiator = last;
            return info;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var bp = entity as BasePlayer;
            if (bp == null) return;
            info = WithResolvedKiller(bp, info);
            int victimBotId;
            if (!_byUserId.TryGetValue((ulong)bp.userID, out victimBotId))
            {
                HandlePossibleBotKill(bp, info); // a non-bot died — was one of ours the killer?
                return;
            }
            BotRecord rec;
            if (!_bots.TryGetValue(victimBotId, out rec)) return;

            string killerName; bool killerIsPlayer, killerIsBot;
            DescribeKillerAndCredit(info, out killerName, out killerIsPlayer, out killerIsBot);
            var killerIsWildlife = !killerIsPlayer && !killerIsBot && info?.Initiator != null && IsWildlife(info.Initiator);
            rec.State.Deaths++;
            if (killerIsWildlife)
            {
                rec.State.WildDeaths++; // decision 0010 — the Grafana soak metric
                // IN-4.2 soak: did the walk-in let the bear follow him home? Under a roof =
                // indoors; within 12 m of the anchor = the yard.
                var dpos = bp.transform.position;
                if (IsUnderRoof(dpos)) _wildDeathsIndoors++;
                else if (rec.State.HomePos != null && FlatDistance(dpos, rec.State.HomePos.V) <= 12f) _wildDeathsYard++;
            }
            int sdeaths;
            _sessionDeaths.TryGetValue(victimBotId, out sdeaths);
            _sessionDeaths[victimBotId] = sdeaths + 1; // rage-quit valve reads this on respawn
            // Stamp the death time; the rage-quit spiral check reads the gap to the
            // previous one, so read-then-overwrite in that order.
            DateTime prevDeath;
            _deathGapMin[victimBotId] = _lastDeathAt.TryGetValue(victimBotId, out prevDeath)
                ? (float)(DateTime.Now - prevDeath).TotalMinutes : float.MaxValue;
            _lastDeathAt[victimBotId] = DateTime.Now;
            // Lives burn on MURDER only (players/bots — including their turrets/traps,
            // which killer resolution pins on the owner). Animal and world deaths sting
            // but never permadeath: the headstone is a social mechanic, and this wipe's
            // boar-camp extinction (13 fallen, mostly pigs) proved the point.
            if (killerIsPlayer || killerIsBot || _config.WorldDeathsBurnLives) rec.State.WipeDeaths++;
            rec.State.Status = "Dead";
            rec.State.RespawnAt = DateTime.Now.AddMinutes(Mathf.Max(1f, _config.RespawnDelayMinutes)).ToString("o");
            rec.State.EngineUserId = 0;
            UnlinkLive(victimBotId);
            if (killerIsWildlife) TrackWildlifeCamp(rec, entity.transform.position); // WC — may extend RespawnAt or clear the camper
            MarkDirty(victimBotId);
            NotifyPresence();
            var cause = killerName == "the world" ? $" ({WorldDeathCause(info)})" : "";
            DLog($"[death] {rec.Identity.Name} was killed by {killerName}{cause} at {entity.transform.position} — death #{rec.State.Deaths}, respawn in {_config.RespawnDelayMinutes:0}m");
            Story("death", $"{rec.Identity.Name} was killed by {killerName}{cause} near {GridRef(entity.transform.position)} (death #{rec.State.Deaths})");
            if (_config.PresenceKillFeed)
            {
                if (killerIsBot) PrintToChat(L("Presence.KillByBot", rec.Identity.Name, killerName));
                else if (killerIsPlayer) PrintToChat(L("Presence.KillByPlayer", rec.Identity.Name, killerName));
                else PrintToChat(L("Presence.KillByWorld", rec.Identity.Name, killerName));
            }
            RecordGrudge(rec, info, killerName, killerIsPlayer, killerIsBot); // 4C — the group remembers
            if (rec.State.WipeDeaths >= LivesFor(rec))
                FallBot(rec, entity.transform.position, killerName);          // 4B — out of lives
        }

        // --- WC camp-breaker (decision 0010) -------------------------------------------
        // N wildlife deaths near the anchor inside the window (a day, since v0.15.2 —
        // slow camps kill once per session and never tripped the old 45m) = an animal
        // holding station (Slagg's boar: 15 deaths at E24 over 3 days; Krupp's wolf: a
        // kill every ~6 minutes; Havoc_77's boar: 16 deaths at I12 over a week). Unobserved, the campers are quietly removed — unwitnessed
        // maulings aren't a story, and neither is unwitnessed pest control. Observed,
        // the victim's respawn holds instead: players may watch the wolf win, because
        // witnessed deaths stay real. Rings are in-memory: a reload forgives, same as
        // the rage-quit counters.
        private readonly Dictionary<int, List<DateTime>> _wildCampDeaths = new Dictionary<int, List<DateTime>>();
        private readonly List<BaseCombatEntity> _campScan = new List<BaseCombatEntity>();
        private const float WildlifeCampRadius = 60f;

        private void TrackWildlifeCamp(BotRecord rec, Vector3 deathPos)
        {
            if (_config.WildlifeCampDeaths <= 0) return;
            var anchor = rec.State.HomePos != null ? rec.State.HomePos.V : deathPos;
            if (FlatDistance(deathPos, anchor) > WildlifeCampRadius) return; // a mauling out in the field isn't a camp
            List<DateTime> ring;
            if (!_wildCampDeaths.TryGetValue(rec.Identity.BotId, out ring))
                _wildCampDeaths[rec.Identity.BotId] = ring = new List<DateTime>();
            var cutoff = DateTime.Now.AddMinutes(-Mathf.Max(1f, _config.WildlifeCampWindowMinutes));
            for (var i = ring.Count - 1; i >= 0; i--) if (ring[i] < cutoff) ring.RemoveAt(i);
            ring.Add(DateTime.Now);
            if (ring.Count < _config.WildlifeCampDeaths) return;
            ring.Clear();
            if (IsObserved(anchor))
            {
                rec.State.RespawnAt = DateTime.Now.AddMinutes(10).ToString("o");
                DLog($"[camp] {rec.Identity.Name}'s anchor is wildlife-camped with a player watching — holding his respawn 10m instead of intervening");
                return;
            }
            var removed = RemoveCampingWildlife(anchor);
            DLog($"[camp] {rec.Identity.Name} died {_config.WildlifeCampDeaths}x to wildlife at his anchor inside {_config.WildlifeCampWindowMinutes:0}m — removed {removed} camping animal(s), nobody watching");
        }

        // Shared cleanup tail: silent despawn, no corpse — reads as "it wandered
        // off"; the population respawns it elsewhere.
        private int RemoveCampingWildlife(Vector3 anchor)
        {
            _campScan.Clear();
            Vis.Entities(anchor, WildlifeCampRadius, _campScan);
            var removed = 0;
            for (var i = 0; i < _campScan.Count; i++)
            {
                var e = _campScan[i];
                if (e == null || e.IsDestroyed || e.IsDead() || !IsDangerWildlife(e)) continue;
                e.Kill();
                removed++;
            }
            return removed;
        }

        // WC-2 (v0.15.3): harassment camps. The breaker counted only DEATHS, so an
        // animal that owned a gunless bot's yard without ever killing him was
        // invisible to it — Strummer (flute, no gun) spent the morning of 2026-08-15
        // in a melee-defend churn loop against one boar (six 25s hunts in three
        // minutes, STUCK flag in the admin panel, zero deaths). Defend-path triggers
        // near the anchor now count too, at 4x the death threshold in the same
        // window — a passing animal that bites once never reaches it, a yard-owner
        // does within the hour. Unobserved only: players watching a boar chase a
        // busker is content, not a problem.
        private readonly Dictionary<int, List<DateTime>> _wildCampHarass = new Dictionary<int, List<DateTime>>();

        private void TrackWildlifeHarassment(BotRecord rec, Vector3 pos)
        {
            if (_config.WildlifeCampDeaths <= 0) return;
            var anchor = rec.State.HomePos != null ? rec.State.HomePos.V : pos;
            if (FlatDistance(pos, anchor) > WildlifeCampRadius) return; // a scrap out in the field isn't a camp
            List<DateTime> ring;
            if (!_wildCampHarass.TryGetValue(rec.Identity.BotId, out ring))
                _wildCampHarass[rec.Identity.BotId] = ring = new List<DateTime>();
            var cutoff = DateTime.Now.AddMinutes(-Mathf.Max(1f, _config.WildlifeCampWindowMinutes));
            for (var i = ring.Count - 1; i >= 0; i--) if (ring[i] < cutoff) ring.RemoveAt(i);
            ring.Add(DateTime.Now);
            if (ring.Count < _config.WildlifeCampDeaths * 4) return;
            ring.Clear();
            if (IsObserved(anchor)) return;
            var removed = RemoveCampingWildlife(anchor);
            DLog($"[camp] {rec.Identity.Name} was harried {_config.WildlifeCampDeaths * 4}x by wildlife at his anchor inside {_config.WildlifeCampWindowMinutes:0}m — removed {removed} camping animal(s), nobody watching");
        }

        #endregion

        #region Permadeath + grudges (Phase 4, decision 0006)

        // Bundle-verified 2026-07-19 — the Halloween stone gravestone deployable.
        private const string HeadstonePrefab = "assets/prefabs/misc/halloween/deployablegravestone/gravestone.stone.deployed.prefab";

        private static bool IsOut(BotRecord rec) =>
            rec.State.Status == "Retired" || rec.State.Status == "Fallen";

        // Deaths-this-wipe a bot survives before falling for good. Cautious bots cling
        // to life a little longer; reckless ones burn bright and die for good sooner.
        private int LivesFor(BotRecord rec)
        {
            var caution = rec.Identity.Traits?.Caution ?? 0.5f;
            var lives = _config.PermadeathBaseLives;
            if (caution >= 0.66f) lives++;
            else if (caution <= 0.33f) lives--;
            return Mathf.Max(1, lives);
        }

        // Grudge keys are typed strings — build them ONLY through these two helpers
        // (Phase 5's bot-vs-bot teeth will parse them back).
        private static string SteamKey(ulong steamId) => "steam:" + steamId;
        private static string BotKey(int botId) => "bot:" + botId;

        // 4C — a killed bot's whole group remembers the killer (shared ledger via
        // per-member writes, decision 0006). Player-owned deployables (turrets, traps)
        // grudge their OWNER — "your turret killed me" is still you. True world deaths
        // (animals, falls, cold) don't grudge.
        private void RecordGrudge(BotRecord victim, HitInfo info, string killerName, bool killerIsPlayer, bool killerIsBot)
        {
            string key = null;
            var name = killerName;
            // Unbound (test) hostile NPCs: no steam id, no roster BotId — interim typed
            // key npc:<name>. Roster-bound hostiles (5B) take the BasePlayer path below
            // and land in the ledger under their bot: key like any resident.
            var hostileNpc0 = info?.Initiator as HostileNpc;
            var hostileNpc = hostileNpc0 != null && !_byUserId.ContainsKey((ulong)hostileNpc0.userID) ? hostileNpc0 : null;
            var killer = hostileNpc != null ? null : info?.Initiator as BasePlayer;
            if (hostileNpc != null)
            {
                key = "npc:" + hostileNpc.HostileName;
                name = hostileNpc.HostileName;
            }
            else if (killer != null)
            {
                if (killerIsBot)
                {
                    int killerBotId;
                    if (_byUserId.TryGetValue((ulong)killer.userID, out killerBotId))
                    {
                        if (killerBotId == victim.Identity.BotId) return; // self-inflicted — no grudge
                        key = BotKey(killerBotId);
                    }
                }
                else if (killerIsPlayer) key = SteamKey((ulong)killer.userID);
            }
            else
            {
                var ent = info?.Initiator as BaseEntity;
                var owner = ent != null ? ent.OwnerID : 0UL;
                if (owner == NightFireOwnerId) return; // our own night light — nobody to blame
                if (owner != 0 && owner.IsSteamId() && !_byUserId.ContainsKey(owner))
                {
                    key = SteamKey(owner);
                    var op = BasePlayer.FindByID(owner) ?? BasePlayer.FindSleeping(owner);
                    name = op != null ? op.displayName : $"whoever owns that {ent.ShortPrefabName.Replace(".prefab", "")}";
                }
            }
            if (key == null) return;

            var wasKnown = victim.Social?.Grudges != null && victim.Social.Grudges.ContainsKey(key);
            var members = 0;
            foreach (var member in _bots.Values)
            {
                var sameGroup = victim.Identity.GroupId > 0 && member.Identity.GroupId == victim.Identity.GroupId;
                if (member != victim && !sameGroup) continue;
                if (member.State.Status == "Retired") continue;
                var soc = member.Social ?? (member.Social = new BotSocial());
                GrudgeEntry g;
                if (!soc.Grudges.TryGetValue(key, out g)) soc.Grudges[key] = g = new GrudgeEntry();
                g.Kills++;
                g.LastAt = DateTime.Now.ToString("o");
                g.State = "hostile";
                g.Name = name;
                soc.Reputation.Remove(key); // firsthand outranks hearsay
                MarkDirty(member.Identity.BotId);
                members++;
            }
            if (!wasKnown)
                Story("grudge", members > 1
                    ? $"{victim.Identity.Name}'s crew won't forget {name}"
                    : $"{victim.Identity.Name} won't forget {name}");
        }

        // 4B — out of lives: the bot falls for good (until the wipe revives the cast).
        // Headstone at the death spot, epitaph in the story feed, base decays when the
        // last member falls, and the roster refills on a delay so the replacement reads
        // as "someone new moved in", not an instant respawn under a new name.
        private void FallBot(BotRecord rec, Vector3 deathPos, string killerName)
        {
            rec.State.Status = "Fallen";
            rec.State.RespawnAt = "";
            rec.State.SessionEndAt = "";
            rec.State.NextSessionAt = "";
            MarkDirty(rec.Identity.BotId);

            var grave = PlaceHeadstone(deathPos, rec.Identity.Name, killerName);
            var back = rec.Identity.Backstory ?? "";
            var cut = back.IndexOf('.');
            var lore = cut > 0 ? " " + back.Substring(0, cut + 1) : "";
            Story("permadeath", $"Here fell {rec.Identity.Name}, slain by {killerName} near {GridRef(deathPos)} — gone until the wipe.{lore}");
            DLog($"[permadeath] {rec.Identity.Name} is out of lives ({rec.State.WipeDeaths}/{LivesFor(rec)}) — Fallen. Headstone at {deathPos}.");
            if (_config.PresenceKillFeed) PrintToChat(L("Presence.Permadeath", rec.Identity.Name));

            // 7E: ONE SLM call writes this stone's eulogy — generated here at the
            // moment of permadeath, cached on the grave record forever. The whisper
            // and the press-E story window read the cache; the template epitaph
            // stays the fallback (SLM off, budget spent, or the call fails).
            if (grave != null && SlmEnabled)
            {
                var life = GraveLife(rec.Identity.Name).Replace("— their story —\n", "").Replace("\n", "; ");
                var sys = $"You write graveside epitaphs for the residents of a Rust survival island. {rec.Identity.Name} the {rec.Identity.Persona} has died for good, slain by {killerName}. Their backstory: {back} Their life: {Trunc(life, 400)}";
                SlmAsk(sys, null, "Write a solemn, slightly wry epitaph: 2 sentences, third person, under 40 words, plain text. Output ONLY the epitaph itself — no preamble, no alternatives, no quotes, no notes.", rec.Identity.Name, eulogy =>
                {
                    if (string.IsNullOrEmpty(eulogy)) return;
                    grave.Eulogy = eulogy;
                    _indexDirty = true;
                }, 80, 260);
            }

            // Last active member gone → the cabin is abandoned and decays naturally.
            var anyLeft = false;
            foreach (var member in _bots.Values)
                if (member.Identity.GroupId == rec.Identity.GroupId && !IsOut(member)) { anyLeft = true; break; }
            if (!anyLeft)
            {
                // The base lives on the HOLDER's record, which may be an earlier-fallen
                // member — resolve it via the group, not the bot that just fell.
                BotRecord holder;
                GetGroupBase(rec.Identity.GroupId, out holder);
                StopBaseUpkeep(holder ?? rec);
                Story("decay", $"{rec.Identity.Name}'s cabin stands abandoned");
            }

            // Refill the cast later (2–6 h) so the island's population recovers.
            if (_config.ReplaceDeadBots && string.IsNullOrEmpty(_index.NextRosterFillAt))
            {
                _index.NextRosterFillAt = DateTime.Now.AddHours(UnityEngine.Random.Range(2f, 6f)).ToString("o");
                _indexDirty = true;
            }
        }

        // A real player shuts the door behind them; so do the bots. The captures were
        // taken with doors open, so every paste arrives open — this warden closes any
        // base door whose group has no Online member within 25 m (out, sleeping, dead).
        private readonly List<Door> _doorSweep = new List<Door>(); // reused
        private readonly Dictionary<int, DateTime> _doorOpenLogAt = new Dictionary<int, DateTime>(); // IN-1.3 watch throttle
        // N-2 Phase 2 metrics: walks attempted, hops taken instead, and why.
        private int _interiorWalks, _interiorWalksDone, _interiorFallbacks;
        private int _wildDeathsIndoors, _wildDeathsYard; // IN-4.2 soak: did the bear follow him in?
        private readonly Dictionary<string, int> _interiorFallbackReasons = new Dictionary<string, int>();

        private bool AnyGroupMemberNear(int groupId, Vector3 pos, float radius)
        {
            var r2 = radius * radius;
            foreach (var kv in _live)
            {
                BotRecord r;
                if (!_bots.TryGetValue(kv.Key, out r) || r.Identity.GroupId != groupId) continue;
                var body = kv.Value;
                if (body == null || body.IsDestroyed) continue;
                if ((body.transform.position - pos).sqrMagnitude <= r2) return true;
            }
            return false;
        }
        private readonly Dictionary<int, DateTime> _doorPoorLogAt = new Dictionary<int, DateTime>(); // per-group throttle for the can't-afford log
        private void CloseAbsentDoors()
        {
            var done = new HashSet<int>();
            foreach (var rec in _bots.Values)
            {
                var gid = rec.Identity.GroupId;
                if (!done.Add(gid)) continue;
                BotRecord holder;
                var b = GetGroupBase(gid, out holder);
                if (b == null || b.OwnerId == 0) continue;
                var someoneHome = false;
                foreach (var m in _bots.Values)
                {
                    if (m.Identity.GroupId != gid || m.State.Status != "Online") continue;
                    var live = GetLive(m.Identity.BotId);
                    if (live != null && !live.IsDestroyed &&
                        (live.transform.position - b.Origin.V).sqrMagnitude < 625f) { someoneHome = true; break; }
                }
                // A fully-out group's cabin decays on purpose (same gate as
                // CheckBaseIntegrity) — don't patch its doors or nag the log
                // (first live pass: retired Kestrel's rotting door, 2026-08-23).
                var anyLeft = false;
                foreach (var m in _bots.Values)
                    if (m.Identity.GroupId == gid && !IsOut(m)) { anyLeft = true; break; }
                if (anyLeft) MaintainBaseDoors(b, holder ?? rec); // snapshot + rebuild destroyed doors (patrol/raid damage)
                if (someoneHome) continue;
                _doorSweep.Clear();
                Vis.Entities(b.Origin.V, 40f, _doorSweep);
                foreach (var d in _doorSweep)
                    if (d != null && !d.IsDestroyed && d.OwnerID == b.OwnerId && d.HasFlag(BaseEntity.Flags.Open))
                        d.SetOpen(false);
            }
        }

        // WB-4 (decision 0010 follow-up): a patrol heli levelled Slagg's whole base on
        // 2026-07-29 (death #1: "the world", Blunt — the shell collapsed on him) and
        // nothing noticed. The economy silently no-opped on zero containers, retreats
        // kept "holing up" in a base that wasn't there, and a boar farmed him 40 times
        // at the empty anchor. This warden confirms each group's TC still stands; a
        // missing TC means the base is lost. The record is dropped on the spot — so
        // hasBase/economy/shelter all honestly read "no base" — surviving goods are
        // carried over, and a rebuild note schedules a fresh paste of the same stage
        // after a delay, paid from the group ledger as far as it can afford.
        private readonly List<BaseEntity> _anchorScan = new List<BaseEntity>(); // reused
        private readonly HashSet<int> _repairPaste = new HashSet<int>(); // WG-4: botIds mid-repair-paste (story flavor)
        private void CheckBaseIntegrity()
        {
            var now = DateTime.Now;
            var shellRepaired = false; // one shell repaste per warden pass — repairs read as work, not magic
            var done = new HashSet<int>();
            foreach (var rec in _bots.Values)
            {
                var gid = rec.Identity.GroupId;
                if (!done.Add(gid)) continue;

                // Someone must be left to notice and rebuild — a fully-out group's
                // cabin decays on purpose (StopBaseUpkeep) and must stay dead.
                var anyLeft = false;
                foreach (var m in _bots.Values)
                    if (m.Identity.GroupId == gid && !IsOut(m)) { anyLeft = true; break; }
                if (!anyLeft) continue;

                BotRecord holder;
                var b = GetGroupBase(gid, out holder);
                if (b != null)
                {
                    // A standing base makes any rebuild note stale (an admin paste or
                    // F1 auto-acquire beat the schedule) — clear it so it can't fire
                    // against the new base later.
                    BotRecord staleHolder;
                    if (GetGroupRebuild(gid, out staleHolder) != null)
                    {
                        SetRebuild(staleHolder, null);
                        MarkDirty(staleHolder.Identity.BotId);
                        DLog($"[base] group {gid}: rebuild note cleared — a base already stands.");
                    }
                    if (b.TcNetId == 0) continue; // paste never recorded a TC — nothing to watch
                    if (_pasteBuffers.ContainsKey(holder.Identity.BotId)) continue; // mid-upgrade: the old base is intentionally doomed
                    var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
                    if (tc != null && !tc.IsDestroyed)
                    {
                        _anchorScan.Clear();
                        Vis.Entities(b.Origin.V, 60f, _anchorScan);

                        // WG-4 shell census (owner note, 2026-08-01: GreyMarrow's cabin was
                        // missing walls for days — doors and anchors healed, blocks
                        // never did). Count standing building blocks against the
                        // baseline recorded at paste; a shell N+ blocks short gets a
                        // same-stage repaste — but only UNWITNESSED (decision 0005:
                        // fixed up while nobody watched) and one per pass. Carryover
                        // keeps the stock; the ledger pays the same rebuild fee.
                        var blocks = 0;
                        foreach (var e in _anchorScan)
                            if (e is BuildingBlock && !e.IsDestroyed && e.OwnerID == b.OwnerId) blocks++;
                        if (b.BlockCount <= 0)
                        {
                            // Pre-census base (pasted before WG-4): today's shell is
                            // the baseline — if it's already breached, those scars
                            // keep until the next paste records an intact count.
                            if (blocks > 0)
                            {
                                b.BlockCount = blocks;
                                SetBase(holder, b);
                                MarkDirty(holder.Identity.BotId);
                                DLog($"[base] group {gid}: shell census baseline set — {blocks} blocks standing.");
                            }
                        }
                        else if (_config.BaseRepairBlockDeficit > 0 && !shellRepaired &&
                                 blocks <= b.BlockCount - _config.BaseRepairBlockDeficit &&
                                 !IsObserved(b.Origin.V))
                        {
                            var repErr = PlaceBase(holder, b.Stage, float.NaN); // snapshots carryover, demolishes, repastes
                            if (repErr == null)
                            {
                                var repGe = Econ(gid);
                                var fee = Math.Min(repGe.Scrap, Math.Max(0, _config.BaseRebuildScrapCostPerStage) * Math.Max(1, b.Stage));
                                if (fee > 0) { repGe.Scrap -= fee; _economyDirty = true; }
                                _repairPaste.Add(holder.Identity.BotId);
                                shellRepaired = true;
                                DLog($"[base] group {gid}: shell {b.BlockCount - blocks} block(s) short ({blocks}/{b.BlockCount}) — unwitnessed repaste ({fee} scrap).");
                            }
                            else DLog($"[base] group {gid}: shell short ({blocks}/{b.BlockCount}) but repaste failed — {repErr}");
                            continue; // this base is mid-replacement either way — skip the anchor pass
                        }

                        // TC stands, but interior anchors can die too (heli splash,
                        // raid): a destroyed bed/box/furnace stayed "Filled" forever,
                        // so GrannyMags slept in the yard beside her intact cabin
                        // (live 2026-08-01). Un-fill any anchor whose entity is gone —
                        // InteriorFillTick re-places it at the normal fill cadence,
                        // bed and box first via the usual priority/need gates.
                        if (b.Anchors == null || b.Anchors.Count == 0) continue;
                        var lost = 0;
                        foreach (var a in b.Anchors)
                        {
                            if (!a.Filled || a.Pos == null) continue;
                            var present = false;
                            foreach (var e in _anchorScan)
                                if (e != null && !e.IsDestroyed && e.OwnerID == b.OwnerId &&
                                    (a.Pos.V - e.transform.position).sqrMagnitude < 0.4f) { present = true; break; }
                            if (present) continue;
                            a.Filled = false;
                            lost++;
                            DLog($"[base] group {gid}: {a.Purpose} anchor missing at {a.Pos.V} — queued for re-placement");
                        }
                        if (lost > 0)
                        {
                            b.NextFillAt = now.AddMinutes(2f).ToString("o"); // don't wait a full fill window to get the bed back
                            SetBase(holder, b); // GetBase returns a deserialized copy — write the un-fills back
                            MarkDirty(holder.Identity.BotId);
                            Story("repair", $"{holder.Identity.Name} set about replacing what the last attack wrecked");
                        }
                        continue;
                    }
                    SnapshotCarryover(gid, b);     // salvage whatever boxes survived the attack
                    var killed = SweepBaseEntities(b);
                    SetBase(holder, null);
                    SetRebuild(holder, new BaseRebuild
                    {
                        Stage = b.Stage,
                        LostAt = now.ToString("o"),
                        At = _config.BaseRebuildDelayMinutes > 0f ? now.AddMinutes(_config.BaseRebuildDelayMinutes).ToString("o") : "",
                    });
                    MarkDirty(holder.Identity.BotId);
                    DLog($"[base] group {gid}: TC {b.TcNetId} no longer exists — stage {b.Stage} base lost at {b.Origin.V} ({killed} straggler entities swept), rebuild {(_config.BaseRebuildDelayMinutes > 0f ? $"in {_config.BaseRebuildDelayMinutes:0}m" : "disabled")}.");
                    Story("ruin", $"{holder.Identity.Name}'s home was destroyed — only rubble left near {GridRef(b.Origin.V)}");
                    continue;
                }

                // No base standing — is a rebuild due?
                BotRecord noteHolder;
                var rb = GetGroupRebuild(gid, out noteHolder);
                if (rb == null || string.IsNullOrEmpty(rb.At) || ParseTime(rb.At) > now) continue;
                if (CopyPaste == null) // soft dependency — keep the note, try again later
                {
                    rb.At = now.AddMinutes(30f).ToString("o");
                    SetRebuild(noteHolder, rb);
                    MarkDirty(noteHolder.Identity.BotId);
                    DLog($"[base] group {gid}: rebuild due but CopyPaste isn't loaded — deferred 30m.");
                    continue;
                }
                // Build through a living member — the note holder may be out for the wipe.
                BotRecord builder = null;
                foreach (var m in _bots.Values)
                    if (m.Identity.GroupId == gid && !IsOut(m) && m.State.HomePos != null) { builder = m; break; }
                if (builder == null) continue;
                var err = PlaceBase(builder, rb.Stage, float.NaN);
                if (err != null)
                {
                    rb.At = now.AddMinutes(15f).ToString("o"); // spot blocked / paste busy — retry next window
                    SetRebuild(noteHolder, rb);
                    MarkDirty(noteHolder.Identity.BotId);
                    DLog($"[base] group {gid}: rebuild attempt failed — {err}");
                    continue;
                }
                var ge = Econ(gid);
                var cost = Math.Min(ge.Scrap, Math.Max(0, _config.BaseRebuildScrapCostPerStage) * Math.Max(1, rb.Stage));
                if (cost > 0) { ge.Scrap -= cost; _economyDirty = true; }
                SetRebuild(noteHolder, null);
                MarkDirty(noteHolder.Identity.BotId);
                DLog($"[base] group {gid}: rebuilding stage {rb.Stage} base via {builder.Identity.Name} ({cost} scrap charged).");
                Story("rebuild", $"{builder.Identity.Name} set about rebuilding after losing everything");
            }
        }

        // A patrol heli (or a raider) blowing a door left the shell breached forever —
        // nothing in the framework repaired it (live gap, observed 2026-07-23). The 5-min
        // door warden now snapshots every owned door it sees onto the base record, and
        // rebuilds a missing one at real material cost from the base boxes (decision
        // 0005: a raid is a setback the economy pays to recover from, not a shrug).
        // One rebuild per pass — repairs read as work, not magic.
        private void MaintainBaseDoors(BotBase b, BotRecord rec)
        {
            if (b.Doors == null) b.Doors = new List<DoorDef>();
            _doorSweep.Clear();
            Vis.Entities(b.Origin.V, 40f, _doorSweep);
            var snapDirty = false;
            foreach (var d in _doorSweep)
            {
                if (d == null || d.IsDestroyed || d.OwnerID != b.OwnerId) continue;
                MarkBotDoor(d); // N-2 — idempotent; covers doors that predate the flag or lost it
                // IN-1.3 watch: an owned door standing open with no group member within 8 m
                // is either a flick that never closed or something else opening our doors
                // (the engine trigger refuses locked doors — this line is the proof).
                if (d.IsOpen() && !AnyGroupMemberNear(rec.Identity.GroupId, d.transform.position, 8f))
                {
                    DateTime lastOpen;
                    if (!_doorOpenLogAt.TryGetValue(rec.Identity.GroupId, out lastOpen) || (DateTime.Now - lastOpen).TotalHours >= 1)
                    {
                        _doorOpenLogAt[rec.Identity.GroupId] = DateTime.Now;
                        DLog($"[door] group {rec.Identity.GroupId}: {d.ShortPrefabName} at {d.transform.position:F1} is OPEN with nobody home (locked={d.IsLocked()}, canNpcOpen={d.canNpcOpen}) — closing it.");
                    }
                    d.SetOpen(false, true);
                }
                var known = false;
                foreach (var dd in b.Doors)
                    if (dd?.Pos != null && (dd.Pos.V - d.transform.position).sqrMagnitude < 0.4f) { known = true; break; }
                if (!known)
                {
                    b.Doors.Add(new DoorDef { Pos = new Vec3(d.transform.position), Prefab = d.PrefabName, Yaw = d.transform.rotation.eulerAngles.y });
                    snapDirty = true;
                }
            }
            foreach (var dd in b.Doors)
            {
                if (dd?.Pos == null || string.IsNullOrEmpty(dd.Prefab)) continue;
                var present = false;
                foreach (var d in _doorSweep)
                    if (d != null && !d.IsDestroyed && d.OwnerID == b.OwnerId && (dd.Pos.V - d.transform.position).sqrMagnitude < 0.4f) { present = true; break; }
                if (present) continue;
                string mat; int amt;
                DoorCost(dd.Prefab, out mat, out amt);
                var buckets = FindBaseContainers(b);
                var inBoxes = buckets != null ? CountAcross(buckets, mat) : 0;
                // The boxes alone starved this at EchoRose's for a day (31 frags banked,
                // 237 idle in the TC while the doorway gaped) — TC stock above the
                // upkeep target is fair game for repairs, and a skip is logged
                // (throttled) so a breached shell is never silent again.
                var tc = b.TcNetId != 0 ? BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge : null;
                if (tc != null && (tc.IsDestroyed || tc.inventory == null)) tc = null;
                var tcSurplus = 0;
                if (tc != null)
                {
                    var target = 0;
                    var prof = ProfileFor(rec);
                    if (prof?.Upkeep != null) prof.Upkeep.TryGetValue(mat, out target);
                    tcSurplus = Mathf.Max(0, CountInContainer(tc.inventory, mat) - target);
                }
                if (inBoxes + tcSurplus < amt)
                {
                    DateTime lastPoor;
                    if (!_doorPoorLogAt.TryGetValue(rec.Identity.GroupId, out lastPoor) || (DateTime.Now - lastPoor).TotalHours >= 6)
                    {
                        _doorPoorLogAt[rec.Identity.GroupId] = DateTime.Now;
                        var shortName = dd.Prefab.Substring(dd.Prefab.LastIndexOf('/') + 1).Replace(".prefab", "");
                        DLog($"[door] group {rec.Identity.GroupId}: {shortName} still breached — can't afford {amt} {mat} (boxes {inBoxes}, TC surplus {tcSurplus}).");
                    }
                    continue;
                }
                var ent = GameManager.server.CreateEntity(dd.Prefab, dd.Pos.V, Quaternion.Euler(0f, dd.Yaw, 0f)) as Door;
                if (ent == null) continue;
                ent.OwnerID = b.OwnerId;
                ent.Spawn();
                var fromBoxes = Mathf.Min(inBoxes, amt);
                if (fromBoxes > 0) RemoveAcross(buckets, mat, fromBoxes);
                if (amt > fromBoxes && tc != null) TakeFromContainer(tc.inventory, mat, amt - fromBoxes);
                if (_config.LockBotStorage) PlaceBotLock(ent, rec); // same gate as LockBaseDoors
                MarkBotDoor(ent); // N-2 — the re-hung door is NPC-openable in the mesh like the rest
                DLog($"[door] group {rec.Identity.GroupId}: rebuilt {ent.ShortPrefabName} for {amt} {mat}");
                Story("repair", $"{rec.Identity.Name} hung a new door after the last attack");
                break; // one repair per warden pass
            }
            if (snapDirty) { SetBase(rec, b); MarkDirty(rec.Identity.BotId); }
        }

        private static void DoorCost(string prefab, out string mat, out int amt)
        {
            var s = prefab.Substring(prefab.LastIndexOf('/') + 1).Replace(".prefab", "");
            switch (s)
            {
                case "door.hinged.wood": mat = "wood"; amt = 300; break;
                case "door.double.hinged.wood": mat = "wood"; amt = 350; break;
                case "door.hinged.toptier": mat = "metal.refined"; amt = 20; break;
                case "door.double.hinged.toptier": mat = "metal.refined"; amt = 25; break;
                case "wall.frame.garagedoor": mat = "metal.fragments"; amt = 300; break;
                default: mat = "metal.fragments"; amt = 150; break; // sheet metal single/double + anything odd
            }
        }

        private HeadstoneRec PlaceHeadstone(Vector3 pos, string name, string killer = "")
        {
            var ent = GameManager.server.CreateEntity(HeadstonePrefab, GroundSnap(pos),
                Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (ent == null) { PrintWarning("Headstone prefab failed to spawn."); return null; }
            ent.Spawn();
            // The stone is a DecayEntity — unowned field graves rot away in hours, so the
            // index remembers every grave and MaintainHeadstones keeps them standing
            // until the wipe (2026-07-20 finding: all 13 stones had already decayed).
            var h = new HeadstoneRec
            {
                Pos = new Vec3(pos), Name = name ?? "", Killer = killer ?? "",
                PlacedAt = DateTime.Now.ToString("MMMM d")
            };
            _index.Headstones.Add(h);
            _indexDirty = true;
            return h;
        }

        // The stone can't hold text, so the grave itself speaks: a player lingering
        // within a few meters gets the epitaph as a private chat whisper — and while
        // anyone stands at a grave, the USE hook is armed so pressing E on the stone
        // opens the full story window. The input hook (hot) is subscribed ONLY while
        // someone is graveside (CLAUDE.md hot-hook rule).
        private bool _graveHookArmed;
        private Oxide.Plugins.Timer _graveTimer;

        // Map markers can spawn while a player is offline and never render for them
        // until the next SendUpdate — push one shortly after each connect (cold hook).
        private void OnPlayerConnected(BasePlayer player)
        {
            timer.Once(3f, () =>
            {
                if (_index?.Headstones == null) return;
                foreach (var h in _index.Headstones)
                    if (h?.Marker != null && !h.Marker.IsDestroyed) h.Marker.SendUpdate();
            });
        }

        private void GraveWhisperTick()
        {
            if (_index?.Headstones == null || _index.Headstones.Count == 0) return;
            var anyGraveside = false;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead()) continue;
                for (var i = 0; i < _index.Headstones.Count; i++)
                {
                    var h = _index.Headstones[i];
                    if (h?.Pos == null) continue;
                    if ((p.transform.position - h.Pos.V).sqrMagnitude > 25f) continue; // 5 m
                    anyGraveside = true;
                    if (ChatOnCooldown("grave" + i, 0, (ulong)p.userID, 10f)) break;
                    p.ChatMessage($"<color=#9a9a9a>{GraveEpitaph(h)} <size=11>[press E on the stone for their story]</size></color>");
                    break; // one grave per player per pass
                }
            }
            if (anyGraveside != _graveHookArmed)
            {
                _graveHookArmed = anyGraveside;
                if (anyGraveside) Subscribe(nameof(OnPlayerInput));
                else Unsubscribe(nameof(OnPlayerInput));
            }
        }

        private string GraveEpitaph(HeadstoneRec h)
        {
            var slain = string.IsNullOrEmpty(h.Killer) || h.Killer == "the world"
                ? "taken by the island" : $"slain by {h.Killer}";
            if (!string.IsNullOrEmpty(h.Eulogy) && !EulogyLooksBroken(h.Eulogy))
                return $"Here lies {h.Name}, {slain}. {h.Eulogy}"; // 7E cached SLM eulogy
            var back = "";
            foreach (var rec in _bots.Values)
                if (rec.Identity.Name == h.Name) { back = rec.Identity.Backstory ?? ""; break; }
            var cut = back.IndexOf('.');
            var lore = cut > 0 ? " " + back.Substring(0, cut + 1) : "";
            return $"Here lies {h.Name}, {slain}.{lore}";
        }

        // Armed only while someone stands graveside. E on the stone → story window.
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || !input.WasJustPressed(BUTTON.USE)) return;
            RaycastHit hit;
            if (!UnityEngine.Physics.Raycast(player.eyes.position, player.eyes.HeadForward(), out hit, 3.5f)) return;
            var ent = hit.GetEntity();
            if (ent == null || !ent.ShortPrefabName.Contains("gravestone")) return;
            HeadstoneRec grave = null;
            var best = 9f; // must match a tracked grave within 3 m
            foreach (var h in _index.Headstones)
            {
                if (h?.Pos == null) continue;
                var d = (ent.transform.position - h.Pos.V).sqrMagnitude;
                if (d < best) { best = d; grave = h; }
            }
            if (grave != null) ShowGraveUi(player, grave);
        }

        private const string GraveUiPanel = "ff.grave";

        // Mine the story log for the deceased's life: the milestones a mourner would
        // tell at the graveside. Deaths are tallied rather than listed (a boar diary
        // is not a eulogy); the permadeath line is skipped (the header already says it).
        private string GraveLife(string name)
        {
            var lines = new List<string>();
            int deaths = 0, kills = 0, builds = 0;
            string lastBuild = null;
            foreach (var ev in _story)
            {
                if (ev?.Text == null || !ev.Text.Contains(name)) continue;
                switch (ev.Type)
                {
                    case "death": deaths++; break;
                    case "permadeath": break;
                    case "kill":
                        if (ev.Text.StartsWith(name)) { kills++; if (kills <= 2) lines.Add($"Day {ev.Day}: {ev.Text}"); }
                        break;
                    case "build":
                        builds++; lastBuild = $"Day {ev.Day}: {ev.Text}";
                        break;
                    case "arrival": case "return": case "settle": case "sale": case "shop":
                    case "trophy": case "grudge": case "warning": case "bounty": case "relocate":
                        if (lines.Count < 12) lines.Add($"Day {ev.Day}: {ev.Text}");
                        break;
                }
            }
            if (lastBuild != null && lines.Count < 12)
                lines.Add(builds > 1 ? $"{lastBuild} (their {builds}{(builds == 2 ? "nd" : builds == 3 ? "rd" : "th")} build)" : lastBuild);
            if (kills > 2) lines.Add($"…and took {kills - 2} more lives.");
            if (deaths > 0) lines.Add($"Died {deaths} time{(deaths == 1 ? "" : "s")} before the end.");
            if (lines.Count > 9) lines.RemoveRange(4, lines.Count - 9); // keep the opening arc + the tallies
            return lines.Count == 0 ? "" : "— their story —\n" + string.Join("\n", lines);
        }

        private void ShowGraveUi(BasePlayer player, HeadstoneRec h)
        {
            string back = "", persona = "";
            foreach (var rec in _bots.Values)
                if (rec.Identity.Name == h.Name)
                { back = rec.Identity.Backstory ?? ""; persona = rec.Identity.Persona ?? ""; break; }
            var slain = string.IsNullOrEmpty(h.Killer) || h.Killer == "the world"
                ? "Taken by the island" : $"Slain by {h.Killer}";
            var fell = string.IsNullOrEmpty(h.PlacedAt) ? "" : $", {h.PlacedAt}";
            var life = GraveLife(h.Name);
            var eulogy = string.IsNullOrEmpty(h.Eulogy) || EulogyLooksBroken(h.Eulogy) ? "" : $"\n\n“{h.Eulogy}”"; // 7E: the stone's own words — curly quotes; ASCII " renders as \" in CUI
            var body = $"{slain}{fell}.{eulogy}\n\n{back}\n\n{life}\n\nGone until the wipe.";

            Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, GraveUiPanel);
            var ui = new Oxide.Game.Rust.Cui.CuiElementContainer();
            ui.Add(new Oxide.Game.Rust.Cui.CuiPanel
            {
                Image = { Color = "0.08 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.32 0.18", AnchorMax = "0.68 0.82" },
                CursorEnabled = true,
            }, "Overlay", GraveUiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = $"R.I.P.  {h.Name}", FontSize = 19, Align = TextAnchor.MiddleCenter, Color = "0.85 0.8 0.7 1" },
                RectTransform = { AnchorMin = "0 0.89", AnchorMax = "1 0.99" },
            }, GraveUiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = string.IsNullOrEmpty(persona) ? "" : $"— {persona} —", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.55 0.53 0.48 1" },
                RectTransform = { AnchorMin = "0 0.84", AnchorMax = "1 0.89" },
            }, GraveUiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = body, FontSize = 12, Align = TextAnchor.UpperCenter, Color = "0.75 0.73 0.68 1" },
                RectTransform = { AnchorMin = "0.06 0.10", AnchorMax = "0.94 0.83" },
            }, GraveUiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.grave.close", Color = "0.25 0.22 0.18 1" },
                Text = { Text = "leave the grave", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.8 0.78 0.72 1" },
                RectTransform = { AnchorMin = "0.38 0.025", AnchorMax = "0.62 0.085" },
            }, GraveUiPanel);
            Oxide.Game.Rust.Cui.CuiHelper.AddUi(player, ui);
        }

        // Any player may close their own grave window — deliberately NOT admin-gated.
        [ConsoleCommand("ff.grave.close")]
        private void CmdGraveClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null) Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, GraveUiPanel);
        }

        // 5-min cadence (rides SocialTick): heal + decay-touch every standing grave,
        // re-place any the decay system (or a player hammer) removed, and kill stacked
        // duplicates (the first pass of this scanned List<DecayEntity>, which the
        // gravestone's actual class slipped past — every sweep re-placed on top of the
        // last; class-agnostic BaseEntity + prefab-name match fixes the find). History
        // holds until DetectWipe clears the index.
        private readonly List<BaseEntity> _stoneScan = new List<BaseEntity>(); // reused
        private void MaintainHeadstones()
        {
            if (_index?.Headstones == null || _index.Headstones.Count == 0) return;
            foreach (var h in _index.Headstones)
            {
                if (h?.Pos == null) continue;
                BaseEntity stone = null;
                _stoneScan.Clear();
                Vis.Entities(h.Pos.V, 3f, _stoneScan);
                foreach (var e in _stoneScan)
                {
                    if (e == null || e.IsDestroyed || !e.ShortPrefabName.Contains("gravestone")) continue;
                    if (stone == null) { stone = e; continue; }
                    // Only true stacks die — a NEIGHBOR's grave within the 3 m find
                    // radius (two bots fell close together) must survive the sweep.
                    if ((e.transform.position - h.Pos.V).sqrMagnitude < 1.44f) e.Kill();
                }
                if (stone == null)
                {
                    var ent = GameManager.server.CreateEntity(HeadstonePrefab, GroundSnap(h.Pos.V),
                        Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                    if (ent != null) { ent.Spawn(); DLog($"[grave] re-placed {h.Name}'s headstone at {h.Pos.V}."); }
                    continue;
                }
                var de = stone as DecayEntity;
                if (de != null)
                {
                    de.health = de.MaxHealth();
                    de.DecayTouch(); // resets the decay delay, like a player tending the grave
                }
            }
            MaintainGraveMarkers();
            MaintainHamletMarkers();
        }

        // I-2: the market hamlet's map pin — gold ring, kept alive by the same 5-min
        // warden cadence as grave markers and re-created after reloads (runtime-only
        // entity, enableSaving false). Non-market hamlets are discovered on foot.
        private void MaintainHamletMarkers()
        {
            if (_index?.Hamlets == null) return;
            foreach (var ham in _index.Hamlets)
            {
                if (ham?.Center == null || !ham.Market) continue;
                if (!_config.HamletMarketMarker)
                {
                    if (ham.Marker != null && !ham.Marker.IsDestroyed) ham.Marker.Kill();
                    ham.Marker = null;
                    continue;
                }
                if (ham.Marker != null && !ham.Marker.IsDestroyed) { ham.Marker.SendUpdate(); continue; } // refresh — covers late joiners
                var m = GameManager.server.CreateEntity(GraveMarkerPrefab, ham.Center.V, Quaternion.identity) as MapMarkerGenericRadius;
                if (m == null) return; // prefab problem — don't spin on it every pass
                m.globalBroadcast = true; // same lesson as grave pins: non-global markers vanish when a visitor leaves range
                m.enableSaving = false;
                m.color1 = new Color(0.85f, 0.62f, 0.12f); // market gold — reads "trade", not vanilla
                m.color2 = new Color(0.35f, 0.22f, 0.05f); // dark ring
                m.alpha = 0.85f;
                m.radius = 0.12f;
                m.Spawn();
                m.SendUpdate();
                ham.Marker = m;
            }
        }

        private void KillHamletMarkers()
        {
            if (_index?.Hamlets == null) return;
            foreach (var ham in _index.Hamlets)
            {
                if (ham?.Marker != null && !ham.Marker.IsDestroyed) ham.Marker.Kill();
                if (ham != null) ham.Marker = null;
            }
        }

        // World-feel pass: graves are points of interest — a slate dot with a bone
        // ring on the map invites players to walk out and press E for the story.
        // Markers are runtime-only (enableSaving false); this warden pass re-creates
        // them after reloads and sweeps them when the config turns off.
        private const string GraveMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";

        // One-shot sweep at load: enforce ShopBroadcast on every bot-owned machine
        // NOW. The per-shop refresh only touches a machine when its group's restock
        // pass comes around, which left stale orange "no listings" pins on the map
        // for hours after the config flipped (observed live 2026-07-22). Player-owned
        // machines are never touched.
        private void EnforceShopBroadcast()
        {
            var n = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var vm = ent as VendingMachine;
                if (vm == null || vm.IsDestroyed) continue;
                if (!IsOurOwnedEntity(vm.OwnerID)) continue;
                if (ApplyShopBroadcast(vm)) n++;
            }
            if (n > 0) DLog($"[shop] enforced ShopBroadcast={_config.ShopBroadcast} on {n} machine(s).");
        }

        // Shop visibility follows ShopBroadcast (world-feel pass: default OFF — a map
        // pin gives the camp away; stumbling across a stocked machine in the woods is
        // the better story). A freshly deployed vending machine BROADCASTS by default
        // (prefab flag), so every placed shop needs this applied. Idempotent;
        // UpdateMapMarker mirrors the engine's own toggle (RPC_Broadcast does SetFlag
        // THEN UpdateMapMarker — and tears the marker down when the flag drops).
        // Do NOT touch IsVending (Reserved2) — the machine owns that during a live
        // transaction; forcing it on makes every buy spin. Returns true if changed.
        private bool ApplyShopBroadcast(VendingMachine vm)
        {
            if (vm == null || vm.IsDestroyed) return false;
            // I-2: the market hamlet advertises itself — its member shops broadcast
            // real vending pins (listings and all) even while ShopBroadcast keeps
            // every other bot camp off the map.
            var want = _config.ShopBroadcast || IsMarketShop(vm.OwnerID);
            if (vm.IsBroadcasting() == want) return false;
            SetFlagNet(vm, BaseEntity.Flags.Reserved4, want); // Broadcasting
            vm.UpdateMapMarker(true); // not-broadcasting branch kills the live marker
            return true;
        }

        private bool IsMarketShop(ulong ownerId)
        {
            if (!_config.HamletMarketMarker || _index?.Hamlets == null || ownerId < BaseOwnerBase) return false;
            var botId = (int)(ownerId - BaseOwnerBase);
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return false;
            foreach (var ham in _index.Hamlets)
                if (ham?.Groups != null && ham.Market && ham.Groups.Contains(rec.Identity.GroupId)) return true;
            return false;
        }

        private void MaintainGraveMarkers()
        {
            if (_index?.Headstones == null) return;
            foreach (var h in _index.Headstones)
            {
                if (h?.Pos == null) continue;
                if (!_config.GraveMapMarkers)
                {
                    if (h.Marker != null && !h.Marker.IsDestroyed) h.Marker.Kill();
                    h.Marker = null;
                    continue;
                }
                if (h.Marker != null && !h.Marker.IsDestroyed) { h.Marker.SendUpdate(); continue; } // refresh — covers late joiners
                var m = GameManager.server.CreateEntity(GraveMarkerPrefab, h.Pos.V, Quaternion.identity) as MapMarkerGenericRadius;
                if (m == null) return; // prefab problem — don't spin on it every pass
                // Map pins must be network-global: as a normal entity the marker is
                // culled when a player leaves its range, and the client deletes the
                // pin with it (observed live 2026-07-22 — visiting a grave erased
                // the markers). Vanilla map markers all broadcast globally.
                m.globalBroadcast = true;
                m.enableSaving = false;
                m.alpha = 0.75f;
                m.color1 = new Color(0.52f, 0.14f, 0.42f); // deep violet — visible on every biome, unmistakably "not vanilla" (gray read as noise — owner call, 2026-07-23)
                m.color2 = new Color(0.88f, 0.88f, 0.82f); // bone ring
                m.alpha = 0.85f;
                m.radius = 0.08f;
                m.Spawn();
                m.SendUpdate();
                h.Marker = m;
            }
        }

        private void KillGraveMarkers()
        {
            if (_index?.Headstones == null) return;
            foreach (var h in _index.Headstones)
            {
                if (h?.Marker != null && !h.Marker.IsDestroyed) h.Marker.Kill();
                if (h != null) h.Marker = null;
            }
        }

        // One-off migration (this wipe only): the 13 graves lost to decay before the
        // index tracked them, re-seeded from the permadeath log positions. No-ops on
        // any other wipe id; remove after the next map wipe.
        private void SeedLostHeadstones()
        {
            if (_index.WipeId != "7a337b75565b4731aad6bc0084b4cea6") return;
            var lost = new[]
            {
                new { N = "Cinder",      X = -969.69f,  Y = 17.55f, Z = -36.58f },
                new { N = "TrapperJoe",  X = -1525.36f, Y = 12.18f, Z = 1358.99f },
                new { N = "RustyBill",   X = 346.52f,   Y = 10.52f, Z = -1580.93f },
                new { N = "GrannyMags",  X = -1116.87f, Y = 6.75f,  Z = 1650.60f },
                new { N = "Moth",        X = 1333.97f,  Y = 35.58f, Z = -380.34f },
                new { N = "BigMara",     X = -1216.97f, Y = 12.57f, Z = -45.44f },
                new { N = "QuietJed",    X = -1023.45f, Y = 16.18f, Z = -238.53f },
                new { N = "Slagg",       X = -1752.51f, Y = 6.53f,  Z = 58.09f },
                new { N = "SaltyJed9",   X = 393.83f,   Y = 25.79f, Z = -284.14f },
                new { N = "SmokeyAsh",   X = -1003.45f, Y = 17.31f, Z = -228.36f },
                new { N = "Krupp",       X = -1381.59f, Y = 6.06f,  Z = 1077.57f },
                new { N = "MaribelSook", X = -1321.16f, Y = 14.40f, Z = 1778.50f },
                new { N = "LoneWolf",    X = -1027.50f, Y = 16.32f, Z = -243.91f },
                new { N = "LoneHank76",  X = 1337.52f,  Y = 13.52f, Z = 1486.19f },
            };
            var added = 0;
            foreach (var g in lost)
            {
                var known = false;
                foreach (var h in _index.Headstones)
                    if (h != null && h.Name == g.N) { known = true; break; }
                if (known) continue;
                _index.Headstones.Add(new HeadstoneRec { Pos = new Vec3(new Vector3(g.X, g.Y, g.Z)), Name = g.N });
                added++;
            }
            if (added == 0) return;
            _indexDirty = true;
            DLog($"[grave] seeded {added} lost headstone(s) from the permadeath log — MaintainHeadstones will re-place them.");
        }

        // 4D — the social tick: grudge knowledge spreads between concurrently-online
        // bots whose homes sit near each other ("I got warned in chat"), and grudges
        // fade if decay is enabled (default off — permanent grudges are the feature).
        private Oxide.Plugins.Timer _socialTimer;
        private readonly List<BotRecord> _socialOnline = new List<BotRecord>(); // reused
        private readonly List<string> _pruneKeys = new List<string>();          // reused

        private void SocialTick()
        {
            MaintainHeadstones(); // graves outlive the decay system (same 5-min cadence)
            CloseAbsentDoors();   // nobody home → doors shut (captures paste them open)
            CheckBaseIntegrity(); // WB-4: lost base → honest record, carryover, scheduled rebuild
            if (_config.GrudgeDecayDays > 0f) DecayGrudges();

            _socialOnline.Clear();
            foreach (var rec in _bots.Values)
                if (rec.State.Status == "Online" && rec.State.HomePos != null && rec.Social != null)
                    _socialOnline.Add(rec);
            if (_socialOnline.Count < 2) return;

            var rangeSq = _config.GrudgePropagationRange * _config.GrudgePropagationRange;
            for (var i = 0; i < _socialOnline.Count; i++)
                for (var j = i + 1; j < _socialOnline.Count; j++)
                {
                    var a = _socialOnline[i];
                    var b = _socialOnline[j];
                    if (a.Identity.GroupId > 0 && a.Identity.GroupId == b.Identity.GroupId) continue; // groups already share
                    if ((a.State.HomePos.V - b.State.HomePos.V).sqrMagnitude > rangeSq) continue;
                    ShareWarnings(a, b);
                    ShareWarnings(b, a);
                }
        }

        private void ShareWarnings(BotRecord from, BotRecord to)
        {
            if (from.Social?.Grudges == null || from.Social.Grudges.Count == 0) return;
            var soc = to.Social ?? (to.Social = new BotSocial());
            var selfKey = BotKey(to.Identity.BotId); // loop-invariant
            foreach (var kv in from.Social.Grudges)
            {
                if (kv.Key == selfKey) continue; // nobody warns you about yourself
                if (soc.Grudges.ContainsKey(kv.Key)) continue;      // firsthand outranks hearsay
                GrudgeEntry rep;
                if (!soc.Reputation.TryGetValue(kv.Key, out rep))
                {
                    soc.Reputation[kv.Key] = new GrudgeEntry
                    {
                        Kills = kv.Value.Kills, LastAt = kv.Value.LastAt, State = "wary", Name = kv.Value.Name,
                    };
                    MarkDirty(to.Identity.BotId);
                    Story("warning", $"{from.Identity.Name} warned {to.Identity.Name} about {kv.Value.Name}");
                    DLog($"[social] {from.Identity.Name} → {to.Identity.Name}: wary of {kv.Value.Name} ({kv.Key})");
                }
                else if (rep.Kills < kv.Value.Kills)
                {
                    rep.Kills = kv.Value.Kills;
                    rep.LastAt = kv.Value.LastAt;
                    MarkDirty(to.Identity.BotId);
                }
            }
        }

        private void DecayGrudges()
        {
            var cutoff = DateTime.Now.AddDays(-_config.GrudgeDecayDays);
            foreach (var rec in _bots.Values)
            {
                if (rec.Social == null) continue;
                var removed = DecayDict(rec.Social.Grudges, cutoff) + DecayDict(rec.Social.Reputation, cutoff);
                if (removed > 0) MarkDirty(rec.Identity.BotId);
            }
        }

        private int DecayDict(Dictionary<string, GrudgeEntry> dict, DateTime cutoff)
        {
            if (dict == null || dict.Count == 0) return 0;
            _pruneKeys.Clear();
            foreach (var kv in dict)
            {
                var last = ParseTime(kv.Value?.LastAt ?? "");
                if (last != DateTime.MaxValue && last < cutoff) _pruneKeys.Add(kv.Key);
            }
            foreach (var k in _pruneKeys) dict.Remove(k);
            return _pruneKeys.Count;
        }

        [ConsoleCommand("ff.grudge")]
        private void CmdGrudge(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.grudge <botId|name> — dump grudges/reputation. ff.grudge.reset <botId|name|all> to clear."); return; }
            var soc = rec.Social ?? new BotSocial();
            var sb = new StringBuilder();
            sb.AppendLine($"=== {rec.Identity.Name} — lives {rec.State.WipeDeaths}/{LivesFor(rec)} used, status {rec.State.Status} ===");
            sb.AppendLine($"Grudges (firsthand, hostile): {soc.Grudges.Count}");
            foreach (var kv in soc.Grudges)
                sb.AppendLine($"  {kv.Value.Name,-20} [{kv.Key}] kills {kv.Value.Kills}, last {ShortAge(kv.Value.LastAt)}");
            sb.AppendLine($"Reputation (secondhand, wary): {soc.Reputation.Count}");
            foreach (var kv in soc.Reputation)
                sb.AppendLine($"  {kv.Value.Name,-20} [{kv.Key}] kills {kv.Value.Kills}, last {ShortAge(kv.Value.LastAt)}");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("ff.grudge.reset")]
        private void CmdGrudgeReset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var who = arg.GetString(0, "");
            if (string.Equals(who, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var rec in _bots.Values) { rec.Social = new BotSocial(); MarkDirty(rec.Identity.BotId); }
                arg.ReplyWith($"Cleared grudges + reputation for all {_bots.Count} bots.");
                return;
            }
            var target = FindBot(who);
            if (target == null) { arg.ReplyWith("Usage: ff.grudge.reset <botId|name|all>"); return; }
            target.Social = new BotSocial();
            MarkDirty(target.Identity.BotId);
            arg.ReplyWith($"Cleared {target.Identity.Name}'s grudges + reputation.");
        }

        private static string ShortAge(string iso)
        {
            var t = ParseTime(iso);
            if (t == DateTime.MaxValue) return "?";
            var span = DateTime.Now - t;
            if (span.TotalHours < 1) return $"{span.TotalMinutes:0}m ago";
            if (span.TotalDays < 1) return $"{span.TotalHours:0}h ago";
            return $"{span.TotalDays:0.#}d ago";
        }

        // Single owner of "credit a bot with a kill" — Phase 4 grudge hooks land here.
        private BotRecord CreditBotKill(BasePlayer killer)
        {
            if (killer == null) return null;
            int killerBotId;
            if (!_byUserId.TryGetValue((ulong)killer.userID, out killerBotId)) return null;
            BotRecord killerRec;
            if (!_bots.TryGetValue(killerBotId, out killerRec)) return null;
            killerRec.State.Kills++;
            MarkDirty(killerBotId);
            // Decision 0007: Cobalt pays per confirmed kill — the hostile's income.
            if (IsHostileP(killerRec) && _config.HostileBountyScrap > 0)
            {
                var ge = Econ(killerRec.Identity.GroupId);
                if (ge != null)
                {
                    ge.Scrap += _config.HostileBountyScrap;
                    // Kills ARE the hostile's production: career credit drives base/kit
                    // progression through the same thresholds the workers earn against.
                    if (_config.HostileBountyCareer > 0) ge.CareerProduced += _config.HostileBountyCareer;
                    MarkEconomyDirty();
                    DLog($"[bounty] Cobalt wired {killerRec.Identity.Name} {_config.HostileBountyScrap} scrap (kill #{killerRec.State.Kills}, balance {ge.Scrap})");
                    Story("bounty", $"{killerRec.Identity.Name} collected a Cobalt bounty");
                }
            }
            return killerRec;
        }

        private void HandlePossibleBotKill(BasePlayer victim, HitInfo info)
        {
            var killerRec = CreditBotKill(info?.Initiator as BasePlayer);
            if (killerRec == null) return;
            DLog($"[kill] {killerRec.Identity.Name} killed {victim.displayName}");
            Story("kill", $"{killerRec.Identity.Name} killed {victim.displayName} near {GridRef(victim.transform.position)}");
            if (_config.PresenceKillFeed)
                PrintToChat(L("Presence.KillByBot", victim.displayName, killerRec.Identity.Name));
        }

        private void DescribeKillerAndCredit(HitInfo info, out string name, out bool isPlayer, out bool isBot)
        {
            isPlayer = false;
            isBot = false;
            var ini = info?.Initiator;
            if (ini == null) { name = "the world"; return; }
            // Unbound test hostiles attribute by their tag; roster-bound ones (5B) fall
            // through to the normal bot path (CreditBotKill → kills + bounty, bot: grudges).
            var hostile = ini as HostileNpc;
            if (hostile != null && !_byUserId.ContainsKey((ulong)hostile.userID)) { name = hostile.HostileName; return; }
            var p = ini as BasePlayer;
            if (p != null)
            {
                var killerRec = CreditBotKill(p);
                if (killerRec != null)
                {
                    isBot = true;
                    name = killerRec.Identity.Name;
                    return;
                }
                isPlayer = ((ulong)p.userID).IsSteamId();
                name = p.displayName;
                return;
            }
            name = FriendlyKillerName(ini.ShortPrefabName);
        }

        // Story-log-friendly names for common non-player killers.
        private static string FriendlyKillerName(string shortPrefab)
        {
            switch (shortPrefab)
            {
                case "patrolhelicopter": return "the patrol helicopter";
                case "bradleyapc": return "the Bradley";
                case "wolf2": return "a wolf";
                case "bear": return "a bear";
                case "polarbear": return "a polar bear";
                case "boar": return "a boar";
                case "panther": return "a panther";
                case "crocodile": return "a crocodile";
                case "snake": return "a snake";
                case "tiger": return "a tiger";
                case "autoturret_deployed": return "an auto turret";
                case "guntrap.deployed": return "a shotgun trap";
                case "flameturret.deployed": return "a flame turret";
                case "landmine": return "a landmine";
                case "beartrap": return "a bear trap";
                default: return shortPrefab.Replace(".prefab", "").Replace("_", " ");
            }
        }

        // Dominant damage type behind an initiator-less ("the world") death — the majority
        // entry in the HitInfo's per-type damage array. Turns a vague "killed by the world"
        // into "(Fall)" / "(Drowned)" / "(Cold)" so bad anchors can be diagnosed.
        private static string WorldDeathCause(HitInfo info)
        {
            if (info == null) return "no-hitinfo";        // Die()/Kill() with no damage record
            var types = info.damageTypes?.types;
            if (types == null) return "no-damagetypes";
            var bestIdx = -1; var bestVal = 0f;
            for (var i = 0; i < types.Length; i++)
                if (types[i] > bestVal) { bestVal = types[i]; bestIdx = i; }
            return bestIdx < 0 ? "no-damage" : $"{(Rust.DamageType)bestIdx} {bestVal:0.#}";
        }

        #endregion

        #region Night light at anchor (C3)

        private const string CampfirePrefab = "assets/prefabs/deployable/campfire/campfire.prefab";
        private const ulong NightFireOwnerId = 76561199991000000UL; // marks our fires for orphan sweeps
        private readonly Dictionary<int, BaseEntity> _nightFires = new Dictionary<int, BaseEntity>();

        // Campfires are persistent entities but _nightFires is memory-only: after a
        // restart/crash while fires were lit, the old fires would both survive
        // forever and get duplicated. Sweep ours (by OwnerID marker) on load.
        private void SweepOrphanNightFires()
        {
            var killed = 0;
            var toKill = new List<BaseEntity>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var fire = ent as BaseOven;
                if (fire != null && fire.OwnerID == NightFireOwnerId) toKill.Add(fire);
            }
            foreach (var fire in toKill)
            {
                if (fire.IsDestroyed) continue;
                fire.Kill();
                killed++;
            }
            if (killed > 0) DLog($"Swept {killed} orphaned night campfire(s) from a previous run.");
        }

        // Called from SchedulerTick (30s cadence): a lit campfire burns at the anchor
        // while its bot is online at night — visible-from-a-distance "someone's home".
        private void UpdateNightLights()
        {
            if (!_config.PresenceNightLight) return;
            var sky = TOD_Sky.Instance;
            var night = sky != null && sky.IsNight;
            foreach (var rec in _bots.Values)
            {
                var id = rec.Identity.BotId;
                var want = night && rec.State.Status == "Online" && rec.State.HomePos != null && GetLive(id) != null;
                BaseEntity fire;
                var has = _nightFires.TryGetValue(id, out fire) && fire != null && !fire.IsDestroyed;
                if (want && !has)
                {
                    var offset = new Vector3(Mathf.Cos(id * 2.4f), 0f, Mathf.Sin(id * 2.4f)) * 2f; // spread group fires
                    var pos = GroundSnap(rec.State.HomePos.V + offset);
                    // Never light the fire ON the cabin floor: with a base at the
                    // anchor the 2m offset lands inside, and the resident cooks on his
                    // own bed when he shelters at night (WildSadie67, death #31 —
                    // "killed by campfire" at exactly her bed anchor). Push it out to
                    // open ground; still on the building → no light tonight.
                    if (OnConstruction(pos))
                    {
                        pos = GroundSnap(rec.State.HomePos.V + offset * 4f);
                        if (OnConstruction(pos)) continue;
                    }
                    fire = GameManager.server.CreateEntity(CampfirePrefab, pos, Quaternion.identity);
                    if (fire == null) continue;
                    fire.OwnerID = NightFireOwnerId;
                    fire.Spawn();
                    var oven = fire as BaseOven;
                    if (oven != null)
                    {
                        var wood = ItemManager.CreateByName("wood", 100);
                        if (wood != null && !wood.MoveToContainer(oven.inventory)) wood.Remove();
                        oven.StartCooking();
                    }
                    _nightFires[id] = fire;
                }
                else if (!want && has)
                {
                    fire.Kill();
                    _nightFires.Remove(id);
                }
            }
        }

        private static int _constructionMask = -1;
        private static bool OnConstruction(Vector3 pos)
        {
            if (_constructionMask == -1) _constructionMask = LayerMask.GetMask("Construction");
            RaycastHit hit;
            return Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out hit, 4f, _constructionMask, QueryTriggerInteraction.Ignore);
        }

        private void KillAllNightFires()
        {
            foreach (var fire in _nightFires.Values)
                if (fire != null && !fire.IsDestroyed) fire.Kill();
            _nightFires.Clear();
        }

        #endregion

        #region Hostile module (5A)

        // In-house custom-brain scientist per the 5P2-verified recipe
        // (docs/reports/2026-07-19-phase5-gen2-navigation.md). 5A slice 1: spawn +
        // native-agent roam + ff.hostile.test admin harness. Chase/combat states,
        // persona lifecycle (5B), grudge preference (5C) and guardrails (5D) follow.
        private const string HostileScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
        private readonly List<HostileNpc> _testHostiles = new List<HostileNpc>();

        // agentTypeID of the navmesh surface that actually answers typed queries at
        // off-monument terrain ('Animal' on the live server — never hardcode; the Humanoid
        // bake exists only inside monuments). Probed once per session at the first
        // spawn site; hostiles are off-monument by design so the cache is safe.
        private static int _terrainAgentTypeId = int.MinValue;
        private static bool _terrainAgentTypeProbed;

        public static int TerrainAgentTypeId(Vector3 near)
        {
            if (_terrainAgentTypeProbed) return _terrainAgentTypeId;
            _terrainAgentTypeProbed = true;
            if (!ConVar.AI.useUnityNavmesh) return _terrainAgentTypeId; // RustNav: one untyped surface (NavSampleTyped ignores the filter)
            for (var i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                var filter = new NavMeshQueryFilter { agentTypeID = s.agentTypeID, areaMask = NavMesh.AllAreas };
                NavMeshHit hit;
                if (NavSampleTyped(near, out hit, 6f, filter)) { _terrainAgentTypeId = s.agentTypeID; break; }
            }
            return _terrainAgentTypeId;
        }

        public class HostileNpc : ScientistNPC
        {
            public Vector3 HomePos;
            public Vector3 SpawnPos;
            public float RoamRadius = 40f;
            public float CombatRange = 30f; // stop-and-shoot distance (chase beyond it)
            public int Kills;               // Cobalt bounty ledger feeds off this (5B)
            // Attribution-stable identity: the scientist base COMPUTES displayName
            // ("Scientist") per read — a property override, not a field, which is why
            // stamping it never stuck. Kill feed/grudges read this field instead…
            public string HostileName = "Hostile (test)";

            // …and this override makes the client agree: death screen ("You were killed
            // by Vex"), corpse label (OverrideCorpseName => displayName), everything the
            // engine renders. The disguise holds all the way to the respawn screen.
            public override string displayName =>
                string.IsNullOrEmpty(HostileName) ? base.displayName : HostileName;

            // Death screen ("KILLED BY"): LifeStoryLogDeath asks the initiator for
            // AttackerInfo. The BasePlayer version sends displayName + userID — but our
            // scientist body's userID isn't steam-range, so the client falls back to
            // NPC rendering (type name + scientist icon; observed live 2026-07-20).
            // Send the roster identity's synthetic steam-range id instead: the client
            // renders a player kill — persona name, generic avatar.
            public override void AttackerInfo(ProtoBuf.PlayerLifeStory.DeathInfo info)
            {
                info.attackerName = HostileName;
                info.attackerSteamID = BoundBotId > 0 ? SteamIdBase + (ulong)BoundBotId : (ulong)userID;
            }
            public int BoundBotId = -1;     // roster identity this entity embodies (-1 = test spawn)

            // 5D dormancy policy: never dormant. The AI manager parks NPCs with no real
            // player within 160 m, and NPCPlayerNavigator.CanUpdateMovement re-targets a
            // dormant NPC to its own feet (probe 5P3) — which would freeze patrols AND
            // stop hostiles hunting residents on an empty server. Bot-vs-bot symmetry
            // requires the hunt to run unwatched; the concurrency cap bounds the cost.
            public override bool IsDormant { get { return false; } set { } }

            // 5A — shot from outside the senses (sniper, backstab): a paid hunter turns
            // and hunts back instead of eating free hits from a blind spot.
            public override void OnAttacked(HitInfo info)
            {
                base.OnAttacked(info);
                var attacker = info?.InitiatorPlayer;
                if (attacker != null && attacker != this && !attacker.IsDestroyed)
                    GetComponent<HostileBrain>()?.NotifyAttacked(attacker);
            }
        }

        public class HostileBrain : ScientistBrain
        {
            private HostileNpc _npc;
            private NavMeshAgent _agent;
            private float _lastMoveTick;
            private float _nextRoamAt;

            public override void AddStates()
            {
                states = new Dictionary<AIState, BasicAIState>(); // no AIDesign graph — Think is manual
            }

            public override void InitializeAI()
            {
                // Deliberately no base call: the vanilla path needs AIInformationZone
                // move points that don't exist off-monument (recon 2026-07-19).
                _npc = GetComponent<HostileNpc>();
                ThinkMode = AIThinkMode.Interval;
                thinkRate = 0.25f; // 4 Hz think — perf target from the plan (5F)
                Navigator = GetComponent<BaseNavigator>();
                _agent = GetComponent<NavMeshAgent>();
                if (_npc == null || Navigator == null || _agent == null) return;
                // Verified spawn recipe: configure the raw Unity agent while disabled
                // (ApplySerializedSettingsToAgent has no server-side caller — raw speed
                // is 0 otherwise), typed to the surface that answers here, then
                // enable-in-place at a sampled position.
                _agent.enabled = false;
                var typeId = TerrainAgentTypeId(_npc.transform.position);
                if (typeId != int.MinValue && _npc.NavAgent != null)
                {
                    _npc.NavAgent.agentTypeID = typeId;
                    _npc.NavAgent.areaMask = NavMesh.AllAreas;
                }
                _agent.speed = 5f;
                _agent.acceleration = 8f;
                _agent.angularSpeed = 120f;
                Navigator.MaxWaterDepth = 0.5f; // 5A2 water policy: deep water is blocked terrain
                _agent.updatePosition = false;  // Navigator.Think copies nextPosition → ServerPosition
                _agent.updateRotation = false;
                NavMeshHit snap;
                if (NavSample(_npc.transform.position, out snap, 6f))
                    _npc.transform.position = snap.position;
                _agent.enabled = true;
                Navigator.SetNavMeshEnabled(true);
                Navigator.PlaceOnNavMesh(0f);
                _lastMoveTick = UnityEngine.Time.realtimeSinceStartup;
                InvokeRandomized(MovementPump, 1f, 0.1f, 0.01f); // clones vanilla StartMovementTick (private)
                Senses.Init(owner: _npc, brain: this, memoryDuration: 10f, range: 60f, targetLostRange: 120f,
                    visionCone: -1f, checkVision: false, checkLOS: false, ignoreNonVisionSneakers: true,
                    listenRange: 20f, hostileTargetsOnly: false, senseFriendlies: false,
                    ignoreSafeZonePlayers: true, senseTypes: EntityType.Player, refreshKnownLOS: false);
            }

            // Probe caveat: InvokeRandomized delegates outlive a killed entity if the
            // kill aborts mid-pool-return — always cancel before despawn.
            public void StopPump() => CancelInvoke(MovementPump);

            // 5A — retaliation: getting hit voids any truce with the attacker and turns
            // the hunt toward them, even when senses never saw the shot. No LOS after
            // 20 s of charging (NoLosTimeout) breaks it off as usual — retaliation is a
            // counter-attack, not a map-wide manhunt. Wounded-execution still wins.
            public void NotifyAttacked(BasePlayer attacker)
            {
                if (_npc == null || _npc.IsDestroyed || attacker == null || attacker.IsDead()) return;
                if (!((ulong)attacker.userID).IsSteamId() && !attacker.IsBot) return; // animals/scientists: not our fight
                var now = UnityEngine.Time.realtimeSinceStartup;
                _givenUpUntil.Remove((ulong)attacker.userID);
                if (_target != null && (_target == attacker || _target.IsWounded())) return;
                _target = attacker;
                _lastTargetPos = attacker.transform.position;
                _engageStartAt = now;
                _lastLosAt = now;
                StateName = "chase";
            }

            // Session end (5B): drop everything and walk back to camp to "log off".
            public void OrderHome(Vector3 home)
            {
                _target = null;
                _lootCorpse = null;
                StateName = "return";
                _nextRoamAt = UnityEngine.Time.realtimeSinceStartup + 60f; // don't wander off mid-walk
                NavMeshHit hit;
                if (NavSample(home, out hit, 6f))
                    Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
            }

            private void MovementPump()
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                var delta = now - _lastMoveTick;
                _lastMoveTick = now;
                Navigator?.Think(delta);
                // Ground snap: the Animal-bake navmesh floats a few tenths of a meter
                // above the real surface, and Navigator.Think copies the on-mesh height
                // verbatim (vanilla animals re-ground separately). Same layer mask as
                // BaseNavigator.DelayedForceToGround.
                if (_npc == null || _npc.IsDestroyed) return;
                var p = _npc.ServerPosition;
                RaycastHit ground;
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out ground, 4f, 10551296))
                {
                    // 5A2 — smooth grounding: the old instant snap per pump tick (10 Hz)
                    // read as bouncing/crouching from a distance (observed live). Small
                    // offsets now glide at ≤1.5 m/s; only real drops (>0.5 m) snap.
                    var dy = ground.point.y - p.y;
                    if (Mathf.Abs(dy) > 0.03f)
                    {
                        p.y = Mathf.Abs(dy) > 0.5f ? ground.point.y : Mathf.MoveTowards(p.y, ground.point.y, 1.5f * delta);
                        _npc.ServerPosition = p;
                    }
                }
            }

            private BasePlayer _target;
            private float _nextFireAt;
            private float _nextPathAt;
            // Engagement caps (5D guardrail): a hostile is a raider, not a siege engine.
            // Give up after EngageTimeout on one target, or NoLosTimeout without line of
            // sight (target holed up in a base — observed live: camping a resident's
            // cabin). Given-up targets are ignored for TargetCooldown so peeking out
            // doesn't instantly re-aggro.
            private const float EngageTimeout = 90f;
            private const float NoLosTimeout = 20f;
            private const float TargetCooldown = 300f;
            private float _engageStartAt;
            private float _lastLosAt;
            private float _nextPrivCheckAt; // 1 Hz building-privilege guard (5D)
            private readonly Dictionary<ulong, float> _givenUpUntil = new Dictionary<ulong, float>();
            public string StateName = "roam"; // surfaced by ff.hostile.test list

            // Loot state (decision 0007): a Cobalt hunter strips its kills.
            private PlayerCorpse _lootCorpse;
            private float _lootUntil;
            private float _corpseSearchUntil;
            private Vector3 _lastTargetPos;

            public override void Think(float delta)
            {
                if (!ConVar.AI.think) return;
                if (_npc == null || _npc.IsDestroyed || Navigator == null) return;
                Senses.Update();
                var tnow = UnityEngine.Time.realtimeSinceStartup;
                // Kill detection: engaged target died → collect the bounty evidence.
                // Unity fake-null (fix mirrored from the 5.5 driver): a killed BOT
                // victim's entity is destroyed and compares == null — ReferenceEquals
                // or this never fires for bot victims.
                if (!ReferenceEquals(_target, null))
                {
                    if (_target == null || _target.IsDestroyed || _target.IsDead())
                    {
                        _npc.Kills++;
                        _target = null;
                        _lootCorpse = null;
                        _corpseSearchUntil = tnow + 8f; // corpse spawns a beat after death
                        StateName = "loot-seek";
                    }
                    else _lastTargetPos = _target.transform.position;
                }
                if (LootTick(tnow)) return; // busy walking to / stripping a corpse
                AcquireTarget();
                if (_target == null) { IdleTick(tnow); return; }
                var now = tnow;
                // 5D hard guardrail — NEVER path into a player's base: a target standing
                // in building privilege that isn't bot-owned is unreachable by doctrine
                // (handoff §goals: "no raiding the player's base"; 0007: no bounty in a
                // siege). Break off now — don't wait out the LOS timer at the fence.
                if (now >= _nextPrivCheckAt)
                {
                    _nextPrivCheckAt = now + 1f;
                    var priv = _target.GetBuildingPrivilege(true);
                    if (priv != null && !FakeFriends.IsBotOwnedPriv(priv))
                    {
                        GiveUp(now);
                        return;
                    }
                }
                var aimPoint = _target.eyes != null ? _target.eyes.position - Vector3.up * 0.15f : _target.CenterPoint();
                var dist = Vector3.Distance(_npc.transform.position, _target.transform.position);
                var visible = _npc.IsVisible(aimPoint);
                if (visible) _lastLosAt = now;
                if (now - _engageStartAt > EngageTimeout || now - _lastLosAt > NoLosTimeout)
                {
                    GiveUp(now);
                    return;
                }
                if (dist <= _npc.CombatRange && visible)
                {
                    // Combat: hold position, face the target, fire on a ragged cadence.
                    // 5A2 — settle into a firing crouch at range (steadier read for the
                    // client anim); stand for close-quarters.
                    StateName = "combat";
                    Navigator.Stop();
                    if (_npc.modelState != null && _npc.modelState.ducked != dist > 12f) SetDucked(dist > 12f);
                    _npc.SetAimDirection((aimPoint - _npc.eyes.position).normalized);
                    if (now >= _nextFireAt)
                    {
                        _nextFireAt = now + UnityEngine.Random.Range(0.4f, 0.9f);
                        _npc.EquipWeapon();
                        _npc.ShotTest(dist); // probe-verified: real bullet damage + FX
                    }
                }
                else
                {
                    // Chase at speed; re-path at 1 Hz, not per-think.
                    StateName = "chase";
                    if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false); // up and after them
                    if (now >= _nextPathAt)
                    {
                        _nextPathAt = now + 1f;
                        Navigator.SetDestination(_target.transform.position, BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                    }
                }
            }

            private void GiveUp(float now)
            {
                // Resume the patrol on the FAR side of the ring from the given-up
                // target — "broke off" should mean leaving the neighborhood, not
                // orbiting the fence line (decision 0007: no bounty in a siege).
                var goal = _npc.HomePos;
                if (_target != null)
                {
                    _givenUpUntil[(ulong)_target.userID] = now + TargetCooldown;
                    var away = _npc.HomePos - _lastTargetPos;
                    away.y = 0f;
                    var dir = away.sqrMagnitude > 1f ? away.normalized : -_npc.transform.forward;
                    goal = _npc.HomePos + dir * _npc.RoamRadius;
                    goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                }
                _target = null;
                StateName = "return";
                if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false); // out of the firing crouch
                NavMeshHit hit;
                if (NavSample(goal, out hit, 6f))
                    Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                _nextRoamAt = now + 20f; // clear the area before wandering again
            }

            // Walk to the fresh corpse, crouch over it, strip it, move on. Returns true
            // while the loot flow owns the brain (new threats are ignored — looting is
            // deliberately a vulnerable moment).
            private bool LootTick(float now)
            {
                if (StateName == "loot-seek")
                {
                    if (_lootCorpse == null)
                    {
                        var found = new List<PlayerCorpse>();
                        Vis.Entities(_lastTargetPos, 6f, found);
                        foreach (var c in found)
                            if (c != null && !c.IsDestroyed) { _lootCorpse = c; break; }
                        if (_lootCorpse == null)
                        {
                            if (now > _corpseSearchUntil) { SetDucked(false); StateName = "roam"; return false; } // stand down — combat crouch must not walk away with us
                            return true; // corpse hasn't landed yet
                        }
                    }
                    if (_lootCorpse.IsDestroyed) { _lootCorpse = null; SetDucked(false); StateName = "roam"; return false; }
                    // 5D — a corpse that fell inside a player's base stays there.
                    var cpriv = _lootCorpse.GetBuildingPrivilege(true);
                    if (cpriv != null && !FakeFriends.IsBotOwnedPriv(cpriv)) { _lootCorpse = null; SetDucked(false); StateName = "roam"; return false; }
                    if (Vector3.Distance(_npc.transform.position, _lootCorpse.transform.position) > 2.5f)
                    {
                        if (now >= _nextPathAt)
                        {
                            _nextPathAt = now + 1f;
                            Navigator.SetDestination(_lootCorpse.transform.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        }
                        return true;
                    }
                    Navigator.Stop();
                    SetDucked(true);
                    _lootUntil = now + UnityEngine.Random.Range(4f, 7f);
                    StateName = "looting";
                    return true;
                }
                if (StateName == "looting")
                {
                    if (now < _lootUntil && _lootCorpse != null && !_lootCorpse.IsDestroyed) return true;
                    TakeLoot();
                    SetDucked(false);
                    _lootCorpse = null;
                    // Haul the take home (decision 0007: depot at the base, then hunt
                    // again). Interruptible — a fresh target overrides the walk, and the
                    // new kill chains back into loot → depot.
                    StateName = "depot";
                    _depotUntil = 0f;
                    return false;
                }
                return false;
            }

            private float _depotUntil;

            // No-target housekeeping: haul loot home ("depot"), finish a post-give-up
            // return walk, or just patrol.
            private void IdleTick(float now)
            {
                if (StateName == "depot")
                {
                    if (Vector3.Distance(_npc.transform.position, _npc.HomePos) > 3f)
                    {
                        if (now >= _nextPathAt)
                        {
                            _nextPathAt = now + 1f;
                            NavMeshHit hit;
                            if (NavSample(_npc.HomePos, out hit, 6f))
                                Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        }
                        return;
                    }
                    if (_depotUntil == 0f)
                    {
                        Navigator.Stop();
                        SetDucked(true); // stash the take
                        _depotUntil = now + UnityEngine.Random.Range(3f, 5f);
                        return;
                    }
                    if (now < _depotUntil) return;
                    // 5B — the visible half of the reward loop closes: pockets empty
                    // into the base stash (the bounty itself banked at kill time).
                    FakeFriends.DepositHostileTake(_npc);
                    SetDucked(false);
                    _depotUntil = 0f;
                    StateName = "roam";
                    _nextRoamAt = now + 5f;
                    return;
                }
                if (StateName != "return") StateName = "roam";
                if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false); // never patrol crouched
                RoamTick();
            }

            private void SetDucked(bool v)
            {
                if (_npc == null || _npc.IsDestroyed || _npc.modelState == null) return;
                _npc.modelState.ducked = v;
                _npc.SendNetworkUpdate();
            }

            private void TakeLoot()
            {
                if (_lootCorpse == null || _lootCorpse.IsDestroyed || _lootCorpse.containers == null) return;
                var taken = 0;
                foreach (var container in _lootCorpse.containers)
                {
                    if (container == null) continue;
                    var items = new List<Item>(container.itemList); // MoveToContainer mutates the list
                    foreach (var item in items)
                    {
                        if (taken >= 6) return; // strips the good stuff, not a vacuum
                        if (item != null && item.MoveToContainer(_npc.inventory.containerMain)) taken++;
                    }
                }
            }

            // Targeting policy (PLAN 5A): real players and our native-bot residents
            // (IsSteamId() || IsBot), never sleepers, nearest first. Senses memory only —
            // no Oxide hooks on the hot path. Scientists/dwellers never appear here
            // (senseTypes: Player excludes NPCs), so hostiles can't fight vanilla AI.
            private void AcquireTarget()
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                if (_target != null && (_target.IsDestroyed || _target.IsDead() || _target.IsSleeping()))
                    _target = null;
                // Execute the wounded: a downed target is a guaranteed bounty — never
                // retarget away from it. (Live bug: mid-execution retarget to a closer
                // player let the victim bleed out unwatched → kill-detect and the loot
                // flow never fired.)
                if (_target != null && _target.IsWounded()) return;
                BasePlayer best = null;
                var bestDist = float.MaxValue;
                var memory = Senses?.Memory?.All;
                if (memory != null)
                {
                    foreach (var m in memory)
                    {
                        var bp = m.Entity as BasePlayer;
                        if (bp == null || bp.IsDestroyed || bp.IsDead() || bp.IsSleeping()) continue;
                        if (!((ulong)bp.userID).IsSteamId() && !bp.IsBot) continue;
                        float coolUntil;
                        if (_givenUpUntil.TryGetValue((ulong)bp.userID, out coolUntil) && now < coolUntil) continue;
                        var d = Vector3.Distance(_npc.transform.position, bp.transform.position);
                        // 5C — grudged targets read as 40% of their real distance: the
                        // hunter picks the one who killed him before over the stranger.
                        if (FakeFriends.HostileHasGrudge(_npc.BoundBotId, bp)) d *= 0.4f;
                        if (d < bestDist) { bestDist = d; best = bp; }
                    }
                }
                // Sticky: keep the current target unless a new one is MUCH closer —
                // mid-fight target-hopping reads as flighty and breaks kill attribution.
                if (best != null && (_target == null || best == _target ||
                    bestDist < Vector3.Distance(_npc.transform.position, _target.transform.position) - 15f))
                {
                    if (_target != best) { _engageStartAt = now; _lastLosAt = now; }
                    _target = best;
                }
            }

            private void RoamTick()
            {
                if (UnityEngine.Time.realtimeSinceStartup < _nextRoamAt) return;
                _nextRoamAt = UnityEngine.Time.realtimeSinceStartup + UnityEngine.Random.Range(8f, 15f);
                var offset = UnityEngine.Random.insideUnitCircle * _npc.RoamRadius;
                var goal = _npc.HomePos + new Vector3(offset.x, 0f, offset.y);
                goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                NavMeshHit hit;
                if (NavSample(goal, out hit, 6f))
                    Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Slow, 0f, 0f);
            }
        }

        #endregion

        #region Hostile on a native-bot body (Phase 5.5)

        // The scientist chassis retires when HostileNativeBodies is on: the prowling
        // entity is the SAME native-bot player body residents use — perfect disguise
        // by construction (player corpse, kill feed, sleeper at camp when offline;
        // no displayName/AttackerInfo patches). Navigation is the probe-proven graft
        // (docs/reports/2026-07-21-probe-navigator-graft.md); combat is the 5A2-R
        // native-fire recipe; the state machine is the HostileBrain's, ported:
        // roam → chase → combat → loot-seek → looting → depot → return, same
        // doctrine (priv guard, engagement caps, wounded-lock, grudge preference,
        // far-side give-up). Two chassis-forced differences: senses become a 1 Hz
        // scan (no ScientistBrain.Senses), and retaliation polls the engine's
        // lastAttacker (no OnAttacked override on a vanilla BasePlayer). One
        // deliberate difference: native hostiles CONSUME ammo — a dry mag sends
        // them home, where the Cobalt resupply (depot) tops them back up.
        public class HostileDriver : FacepunchBehaviour
        {
            public BasePlayer Body;
            public int BotId = -1;
            public Vector3 HomePos;
            public float RoamRadius = 60f;
            public float CombatRange = 30f;
            public const float SenseRange = 60f;
            public string StateName = "roam";
            public BaseNavigator Nav;
            private NavMeshAgent _agent;
            private float _skill = 0.6f;
            private float _dmgMul = 1f; // seasoning damage bonus, computed at attach
            private float _lastPump;
            private float _thinkAcc;

            private BasePlayer _target;
            private Vector3 _lastTargetPos;
            private float _nextFireAt, _nextPathAt, _nextRoamAt, _nextScanAt, _nextPrivCheckAt;
            private float _engageStartAt, _lastLosAt;
            private BaseCombatEntity _beast; // WA-1 for Cobalt (v0.15.2) — wildlife target, humans preempt
            private string _beastDesc;
            private float _beastEngageAt, _beastLastLosAt, _nextBeastScanAt, _beastSuppressUntil;
            private readonly Dictionary<ulong, float> _givenUpUntil = new Dictionary<ulong, float>();
            private PlayerCorpse _lootCorpse;
            private float _lootUntil, _corpseSearchUntil, _depotUntil;

            // Primitives only — BotRecord is plugin-private (same pattern as HostileNpc).
            // skill arrives pre-seasoned; dmgMul carries the seasoning damage bonus.
            public void Init(BasePlayer body, int botId, Vector3 home, float roamRadius, float skill, float dmgMul)
            {
                Body = body;
                BotId = botId;
                HomePos = home;
                RoamRadius = roamRadius;
                _skill = Mathf.Clamp01(skill);
                _dmgMul = Mathf.Max(0.1f, dmgMul);
                // The graft (idempotent — a body re-used across sessions keeps its
                // components): raw Unity agent configured while disabled (5P2), the
                // Gen2 wrapper, then the navigator wired to both.
                _agent = gameObject.GetComponent<NavMeshAgent>();
                if (_agent == null) _agent = gameObject.AddComponent<NavMeshAgent>();
                _agent.enabled = false;
                var typeId = TerrainAgentTypeId(body.transform.position);
                if (typeId != int.MinValue) _agent.agentTypeID = typeId;
                _agent.areaMask = NavMesh.AllAreas;
                _agent.speed = 5f;
                _agent.acceleration = 8f;
                _agent.angularSpeed = 120f;
                _agent.updatePosition = false;
                _agent.updateRotation = false;
                var rustAgent = gameObject.GetComponent<Rust.Ai.Gen2.RustNavMeshAgent>();
                if (rustAgent == null) rustAgent = gameObject.AddComponent<Rust.Ai.Gen2.RustNavMeshAgent>();
                Nav = gameObject.GetComponent<BaseNavigator>();
                if (Nav == null) Nav = gameObject.AddComponent<BaseNavigator>();
                NavMeshHit snap;
                if (NavSample(body.transform.position, out snap, 6f))
                    body.transform.position = snap.position;
                if (ConVar.AI.useUnityNavmesh) _agent.enabled = true; // RustNav drives RustNavMeshAgent directly; an enabled Unity agent with no Unity mesh just logs errors
                Nav.Init(body, rustAgent);
                Nav.MaxWaterDepth = 0.5f; // 5A2 water policy
                Nav.SetNavMeshEnabled(true);
                Nav.PlaceOnNavMesh(0f);
                StateName = "roam";
                _target = null;
                _lootCorpse = null;
                _lastPump = UnityEngine.Time.realtimeSinceStartup;
                _nextRoamAt = _lastPump + 3f;
                InvokeRandomized(Pump, 1f, 0.1f, 0.01f);
            }

            // Session end / unload: stop driving, stand the nav stack down, leave the
            // body an ordinary (sleep-able) player entity. Components stay for reuse.
            public void Detach()
            {
                CancelInvoke(Pump);
                if (Nav != null) { Nav.Stop(); Nav.SetNavMeshEnabled(false); }
                if (_agent != null) _agent.enabled = false;
                if (Body != null && !Body.IsDestroyed && Body.modelState != null && Body.modelState.ducked) SetDucked(false);
                UnityEngine.Object.Destroy(this);
            }

            // Probe caveat (5A): invokes outlive a killed entity if the kill aborts
            // mid-pool-return — always cancel on the way out.
            private void OnDestroy() { CancelInvoke(Pump); }

            public void OrderHome(Vector3 home)
            {
                _target = null;
                _beast = null;
                _lootCorpse = null;
                StateName = "return";
                _nextRoamAt = UnityEngine.Time.realtimeSinceStartup + 60f;
                SetDucked(false);
                NavMeshHit hit;
                if (Nav != null && NavSample(home, out hit, 6f))
                    Nav.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
            }

            // 10 Hz: navigator think + ground glide + CLIENT-SMOOTH networking.
            // BaseNavigator writes ServerPosition raw (fine for NPC entity types);
            // player observers interpolate via the movement-tick history, so we
            // re-apply each step through MovePosition — the same path residents use.
            private void Pump()
            {
                if (Body == null || Body.IsDestroyed || Body.IsDead()) { CancelInvoke(Pump); return; }
                if (Body.IsWounded()) { Body.Die(); return; } // connection-less bodies never tick wounded recovery
                if (Body.IsSleeping()) return;
                var now = UnityEngine.Time.realtimeSinceStartup;
                var delta = now - _lastPump;
                _lastPump = now;
                var before = Body.transform.position;
                Nav?.Think(delta);
                var p = Body.ServerPosition;
                RaycastHit ground; // Animal-bake floats — same glide fix as the scientist pump
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out ground, 4f, 10551296))
                {
                    var dy = ground.point.y - p.y;
                    if (Mathf.Abs(dy) > 0.03f)
                        p.y = Mathf.Abs(dy) > 0.5f ? ground.point.y : Mathf.MoveTowards(p.y, ground.point.y, 1.5f * delta);
                }
                var stepped = p - before;
                if (stepped.sqrMagnitude > 0.0001f)
                {
                    var flat = new Vector3(stepped.x, 0f, stepped.z);
                    if (_target == null && flat.sqrMagnitude > 0.001f) // combat owns the aim
                        Face(flat.normalized);
                    Body.MovePosition(p);
                }
                _thinkAcc += delta;
                if (_thinkAcc >= 0.25f) { _thinkAcc = 0f; Think(now); } // 4 Hz think (5F perf target)
            }

            private void Think(float now)
            {
                if (!ConVar.AI.think) return;
                var self = Ff;
                if (self == null) return;
                // Retaliation — shot from a blind spot: poll the engine's lastAttacker
                // (a vanilla body has no OnAttacked override). Voids any truce, turns
                // the hunt; the LOS timeout bounds the charge as usual.
                var last = Body.lastAttacker as BasePlayer;
                if (last != null && last != Body && !last.IsDestroyed && !last.IsDead() &&
                    UnityEngine.Time.time - Body.lastAttackedTime < 6f &&
                    (((ulong)last.userID).IsSteamId() || last.IsBot) && !self.IsHostileBody(last))
                {
                    _givenUpUntil.Remove((ulong)last.userID);
                    if (_target == null || (_target != last && !_target.IsWounded()))
                    {
                        _target = last;
                        _lastTargetPos = last.transform.position;
                        _engageStartAt = now;
                        _lastLosAt = now;
                        StateName = "chase";
                    }
                }
                // Kill detection: engaged target died → collect the evidence. (The
                // bounty itself already flowed through OnEntityDeath → CreditBotKill.)
                // Unity fake-null: a killed BOT victim's entity is destroyed and then
                // compares == null, so the alive-check must be ReferenceEquals or this
                // transition never fires (live bug: Vex walked away from his kill).
                if (!ReferenceEquals(_target, null))
                {
                    if (_target == null || _target.IsDestroyed || _target.IsDead())
                    {
                        _target = null;
                        _lootCorpse = null;
                        _corpseSearchUntil = now + 8f;
                        StateName = "loot-seek";
                    }
                    else _lastTargetPos = _target.transform.position;
                }
                if (LootTick(now)) return;
                AcquireTarget(now);
                if (_target == null)
                {
                    if (BeastTick(now)) return; // WA-1: no human in sight — deal with whatever's biting
                    IdleTick(now);
                    return;
                }
                _beast = null; // a human target owns the fight
                // 5D hard guardrail — NEVER path into a player's base (bot cabins exempt).
                if (now >= _nextPrivCheckAt)
                {
                    _nextPrivCheckAt = now + 1f;
                    var priv = _target.GetBuildingPrivilege(true);
                    if (priv != null && !FakeFriends.IsBotOwnedPriv(priv)) { GiveUp(now); return; }
                }
                // Wounded targets are prone — head-height aim just combs the grass over
                // them; execute at center mass.
                var aimPoint = _target.IsWounded() || _target.eyes == null
                    ? _target.CenterPoint() : _target.eyes.position - Vector3.up * 0.15f;
                var dist = Vector3.Distance(Body.transform.position, _target.transform.position);
                var visible = Body.IsVisible(aimPoint);
                if (visible) _lastLosAt = now;
                if (now - _engageStartAt > FightEngageTimeout || now - _lastLosAt > FightNoLosTimeout)
                {
                    GiveUp(now);
                    return;
                }
                if (dist <= CombatRange && visible)
                {
                    StateName = "combat";
                    Nav.Stop();
                    if (Body.modelState != null && Body.modelState.ducked != dist > 12f) SetDucked(dist > 12f); // firing crouch at range
                    var dir = (aimPoint - Body.eyes.position).normalized;
                    Face(dir);
                    Body.SendNetworkUpdate();
                    if (now >= _nextFireAt)
                    {
                        var gun = self.FightWeapon(Body);
                        if (gun == null) { GiveUp(now); return; }
                        if (gun.primaryMagazine.contents <= 0)
                        {
                            if (!self.FightReload(Body, gun))
                            {
                                // Ran dry mid-hunt: back to camp for the Cobalt resupply.
                                self.DLog($"[hostile] {Body.displayName} ran dry — returning for resupply");
                                _target = null;
                                StateName = "depot"; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f; // fresh depot walk (review 2026-10-02 rec. 1)
                                _depotUntil = 0f;
                                return;
                            }
                            _nextFireAt = now + 2.5f; // reload pause
                            return;
                        }
                        float dmg, interval;
                        FightWeaponProfile(gun, out dmg, out interval);
                        self.NativeShot(Body, gun, _target, aimPoint, dist, _skill, dmg * _dmgMul);
                        _nextFireAt = now + interval * Mathf.Lerp(1.35f, 0.75f, _skill) * UnityEngine.Random.Range(0.85f, 1.25f);
                    }
                }
                else
                {
                    StateName = "chase";
                    if (Body.modelState != null && Body.modelState.ducked) SetDucked(false); // up and after them
                    if (now >= _nextPathAt)
                    {
                        _nextPathAt = now + 1f;
                        Nav.SetDestination(_target.transform.position, BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                    }
                }
            }

            // Targeting policy (5A, senses replaced by a 1 Hz scan): real players and
            // our bots (IsSteamId() || IsBot), never sleepers, never safe-zoners,
            // never Cobalt-on-Cobalt; nearest first, grudged targets read as 40% of
            // their real distance; sticky unless a new one is MUCH closer;
            // wounded-lock (a downed target is a guaranteed bounty).
            private void AcquireTarget(float now)
            {
                if (_target != null && (_target.IsDestroyed || _target.IsDead() || _target.IsSleeping()))
                    _target = null;
                if (_target != null && _target.IsWounded()) return;
                if (now < _nextScanAt) return;
                _nextScanAt = now + 1f;
                var self = Ff;
                if (self == null) return;
                var pos = Body.transform.position;
                BasePlayer best = null;
                var bestDist = float.MaxValue;
                foreach (var p in BasePlayer.activePlayerList) Consider(p, pos, now, ref best, ref bestDist);
                // The engine's native-bot list covers EVERY bot body — residents, prey
                // bodies, third-party bots (2026-07-21 live bug: the registry-only scan
                // was blind to untracked bodies; Vex walked past his prey).
                foreach (var b in BasePlayer.bots) Consider(b, pos, now, ref best, ref bestDist);
                foreach (var kv in self._live) { if (kv.Key != BotId) Consider(kv.Value, pos, now, ref best, ref bestDist); } // SteamId-mode bots aren't in the bots list
                if (best != null && (_target == null || best == _target ||
                    bestDist < Vector3.Distance(pos, _target.transform.position) - 15f))
                {
                    if (_target != best) { _engageStartAt = now; _lastLosAt = now; }
                    _target = best;
                }
            }

            private void Consider(BasePlayer bp, Vector3 pos, float now, ref BasePlayer best, ref float bestDist)
            {
                if (bp == null || bp == Body || bp.IsDestroyed || bp.IsDead() || bp.IsSleeping()) return;
                if (!((ulong)bp.userID).IsSteamId() && !bp.IsBot) return;
                if (bp.InSafeZone()) return;
                var self = Ff;
                if (self != null && self.IsHostileBody(bp)) return; // Cobalt doesn't hit Cobalt
                var d = Vector3.Distance(pos, bp.transform.position);
                if (d > SenseRange) return;
                float coolUntil;
                if (_givenUpUntil.TryGetValue((ulong)bp.userID, out coolUntil) && now < coolUntil) return;
                if (FakeFriends.HostileHasGrudge(BotId, bp)) d *= 0.4f; // 5C — the hunter picks his killer over the stranger
                if (d < bestDist) { bestDist = d; best = bp; }
            }

            // WA-1 for Cobalt (v0.15.2, decision 0010): the driver's combat brain only
            // knew humans — `lastAttacker as BasePlayer` casts a boar to null, and
            // AcquireTarget scans only player lists — so the best-armed bots on the
            // island stood still while wildlife ate them (this wipe: Vex 15 of 15
            // deaths to animals, Havoc_77 12 of 16, Nils_44 7 of 7, all at their own
            // anchors with zero response, no camp window ever tripped). Same doctrine
            // as residents: an armed bot fights the animal that's on him. Stand and
            // shoot, no chase, no flee valve (Cobalt doesn't run from a boar); ran
            // dry → the depot leg resupplies and the next bite re-engages. Returns
            // true while the beast owns the tick.
            private bool BeastTick(float now)
            {
                // Unity fake-null: a shot-dead beast's entity is destroyed and then
                // compares == null — read the outcome from the cached desc.
                if (!ReferenceEquals(_beast, null) && (_beast == null || _beast.IsDestroyed || _beast.IsDead()))
                {
                    Ff?.Story("fight", $"{Body.displayName} shot {_beastDesc} dead");
                    _beast = null;
                    StateName = "roam";
                    return false;
                }
                if (_beast == null)
                {
                    // No re-engage while walking the depot leg (dry) or inside the
                    // give-up window (WG-2 spirit — animals have no userID to cooldown).
                    if (StateName == "depot" || now < _beastSuppressUntil) return false;
                    // Acquire: whatever just bit us; else a danger animal prowling close
                    // (same 15 m the residents' predator-on-us check uses).
                    var last = Body.lastAttacker as BaseCombatEntity;
                    BaseCombatEntity cand = null;
                    if (last != null && !(last is BasePlayer) && !last.IsDestroyed && !last.IsDead() &&
                        IsDangerWildlife(last) && UnityEngine.Time.time - Body.lastAttackedTime < 6f)
                        cand = last;
                    if (cand == null)
                    {
                        if (now < _nextBeastScanAt) return false;
                        _nextBeastScanAt = now + 1f;
                        cand = Ff?.NearestDangerAnimal(Body.transform.position, 15f) as BaseCombatEntity;
                    }
                    if (cand == null) return false;
                    var entryGun = Ff?.FightWeapon(Body);
                    if (entryGun == null) return false; // no gun at all — nothing sane to do
                    if (entryGun.primaryMagazine.contents <= 0 && !(Ff?.FightReload(Body, entryGun) ?? false))
                    {
                        // Dry (WD-3 spirit): no posing — walk the depot leg, restock,
                        // and the next bite re-engages with live rounds.
                        Ff?.DLog($"[hostile] {Body.displayName} is dry with a {AnimalName(cand)} on him — heading in for the resupply");
                        StateName = "depot"; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f; // fresh depot walk (review 2026-10-02 rec. 1)
                        _depotUntil = 0f;
                        return false;
                    }
                    _beast = cand;
                    _beastDesc = "a " + AnimalName(cand);
                    _beastEngageAt = now;
                    _beastLastLosAt = now;
                    Ff?.Story("fight", $"{Body.displayName} stood his ground against {_beastDesc}");
                }
                var aim = _beast.CenterPoint();
                var dist = Vector3.Distance(Body.transform.position, _beast.transform.position);
                var visible = dist <= CombatRange && Body.IsVisible(aim);
                if (visible) _beastLastLosAt = now;
                if (now - _beastEngageAt > FightEngageTimeout || now - _beastLastLosAt > FightNoLosTimeout || dist > SenseRange)
                {
                    _beast = null; // it wandered off (or we can't reach it) — resume the patrol
                    _beastSuppressUntil = now + 60f;
                    StateName = "roam";
                    return false;
                }
                StateName = "combat";
                Nav.Stop();
                if (Body.modelState != null && Body.modelState.ducked) SetDucked(false); // nobody crouch-fights a charging animal
                Face((aim - Body.eyes.position).normalized);
                Body.SendNetworkUpdate();
                if (!visible || now < _nextFireAt) return true;
                var gun = Ff?.FightWeapon(Body);
                if (gun == null) { _beast = null; StateName = "roam"; return false; }
                if (gun.primaryMagazine.contents <= 0)
                {
                    if (!(Ff?.FightReload(Body, gun) ?? false))
                    {
                        Ff?.DLog($"[hostile] {Body.displayName} ran dry mid-fight with {_beastDesc} — breaking for the resupply");
                        _beast = null;
                        StateName = "depot"; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f; // fresh depot walk (review 2026-10-02 rec. 1)
                        _depotUntil = 0f;
                        return false;
                    }
                    _nextFireAt = now + 2.5f; // reload pause
                    return true;
                }
                float dmg, interval;
                FightWeaponProfile(gun, out dmg, out interval);
                Ff?.NativeShot(Body, gun, _beast, aim, dist, _skill, dmg * _dmgMul);
                _nextFireAt = now + interval * Mathf.Lerp(1.35f, 0.75f, _skill) * UnityEngine.Random.Range(0.85f, 1.25f);
                return true;
            }

            private void GiveUp(float now)
            {
                // Resume the patrol on the FAR side of the ring from the given-up
                // target (decision 0007: no bounty in a siege).
                var goal = HomePos;
                if (_target != null)
                {
                    _givenUpUntil[(ulong)_target.userID] = now + FightTargetCooldown;
                    var away = HomePos - _lastTargetPos;
                    away.y = 0f;
                    var dir = away.sqrMagnitude > 1f ? away.normalized : -Body.transform.forward;
                    goal = HomePos + dir * RoamRadius;
                    goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                }
                _target = null;
                StateName = "return";
                SetDucked(false);
                NavMeshHit hit;
                if (NavSample(goal, out hit, 6f))
                    Nav.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                _nextRoamAt = now + 20f;
            }

            // Walk to the fresh corpse, crouch over it, strip it, haul it home.
            private bool LootTick(float now)
            {
                if (StateName == "loot-seek")
                {
                    if (_lootCorpse == null)
                    {
                        var found = new List<PlayerCorpse>();
                        Vis.Entities(_lastTargetPos, 6f, found);
                        foreach (var c in found)
                            if (c != null && !c.IsDestroyed) { _lootCorpse = c; break; }
                        if (_lootCorpse == null)
                        {
                            if (now > _corpseSearchUntil) { SetDucked(false); StateName = "roam"; return false; } // stand down — combat crouch must not walk away with us
                            return true; // corpse hasn't landed yet
                        }
                    }
                    if (_lootCorpse.IsDestroyed) { _lootCorpse = null; SetDucked(false); StateName = "roam"; return false; }
                    var cpriv = _lootCorpse.GetBuildingPrivilege(true); // 5D — a corpse inside a player's base stays there
                    if (cpriv != null && !FakeFriends.IsBotOwnedPriv(cpriv)) { _lootCorpse = null; SetDucked(false); StateName = "roam"; return false; }
                    if (Vector3.Distance(Body.transform.position, _lootCorpse.transform.position) > 2.5f)
                    {
                        if (now >= _nextPathAt)
                        {
                            _nextPathAt = now + 1f;
                            Nav.SetDestination(_lootCorpse.transform.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        }
                        return true;
                    }
                    Nav.Stop();
                    SetDucked(true);
                    _lootUntil = now + UnityEngine.Random.Range(4f, 7f);
                    StateName = "looting";
                    return true;
                }
                if (StateName == "looting")
                {
                    if (now < _lootUntil && _lootCorpse != null && !_lootCorpse.IsDestroyed) return true;
                    TakeLoot();
                    SetDucked(false);
                    _lootCorpse = null;
                    StateName = "depot"; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f; // fresh depot walk (review 2026-10-02 rec. 1)
                    _depotUntil = 0f;
                    return false;
                }
                return false;
            }

            // IN-3.4 depot walk: 0 approaching the anchor, 1 walking in, 2 dwelling inside, 3 walking out
            private int _depotStage;
            private bool _walkedIn, _walkRetried;
            private float _walkRetryAt;
            private Vector3 _walkGoal, _walkProgressPos;
            private float _walkStart, _walkProgressAt;
            private List<Door> _walkDoors;
            private Door _passDoor;

            private void EndDepot(float now)
            {
                if (Nav != null && Nav.Agent != null) Nav.Agent.canOpenDoors = false;
                FakeFriends.DriverCloseDoor(ref _passDoor);
                _depotUntil = 0f; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f;
                StateName = "roam";
                _nextRoamAt = now + 5f;
            }

            private void DepotHopIn(float now, string reason)
            {
                if (reason != null) Ff?.DriverWalkFallback(Body?.displayName, reason);
                FakeFriends.DriverCloseDoor(ref _passDoor);
                if (Nav != null && Nav.Agent != null) Nav.Agent.canOpenDoors = false;
                Nav.Stop();
                Ff?.HostileEnterBase(BotId, Body); // door swings, duck inside (like every resident visit)
                SetDucked(true); // stash the take
                _depotUntil = now + UnityEngine.Random.Range(3f, 5f);
                _depotStage = 2; _walkedIn = false;
            }


            private void IdleTick(float now)
            {
                if (StateName == "depot")
                {
                    var pos = Body.transform.position;
                    if (_depotStage == 0)
                    {
                        if (Vector3.Distance(pos, HomePos) > 3f)
                        {
                            if (now >= _nextPathAt)
                            {
                                _nextPathAt = now + 1f;
                                NavMeshHit hit;
                                if (NavSample(HomePos, out hit, 6f))
                                    Nav.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                            }
                            return;
                        }
                        // IN-3.4: at the anchor — walk in to the storage anchor through the door
                        // (BaseNavigator plans the aperture with canOpenDoors on); the hop is the fallback.
                        if (_walkRetryAt > 0f && now < _walkRetryAt) return; // IN-4.1 retry gate
                        string why = "noPlugin";
                        var goal = Ff != null ? Ff.DriverInteriorGoal(BotId, pos, "storage", out why) : Vector3.zero;
                        if (goal != Vector3.zero && Nav.Agent != null)
                        {
                            Nav.Agent.canOpenDoors = true;
                            if (Nav.SetDestination(goal, BaseNavigator.NavigationSpeed.Slow, 0f, 0f))
                            {
                                _walkGoal = goal; _walkDoors = Ff.DriverBaseDoors(BotId); _passDoor = null;
                                _walkStart = now; _walkProgressPos = pos; _walkProgressAt = now;
                                _depotStage = 1; _walkedIn = true;
                                Ff.DriverWalkStarted(Body.displayName, Vector3.Distance(pos, goal));
                                return;
                            }
                            if (!_walkRetried && FakeFriends.NavSaveInFlight()) { _walkRetried = true; _walkRetryAt = now + 3f; return; } // IN-4.1
                            why = _walkRetried ? "noPathIn-afterSave" : "noPathIn";
                        }
                        DepotHopIn(now, why);
                        return;
                    }
                    if (_depotStage == 1)
                    {
                        FakeFriends.DriverDoorPass(pos, _walkDoors, ref _passDoor);
                        if (Vector3.Distance(pos, _walkGoal) <= 1.2f)
                        {
                            FakeFriends.DriverCloseDoor(ref _passDoor); // he's in — the door shuts behind him
                            Nav.Stop();
                            SetDucked(true); // stash the take
                            _depotUntil = now + UnityEngine.Random.Range(3f, 5f);
                            _depotStage = 2;
                            Ff?.DLog($"[walk] {Body.displayName} is inside ({Vector3.Distance(pos, _walkGoal):0.0} m from the box) — stashing");
                            return;
                        }
                        if ((pos - _walkProgressPos).magnitude >= 1f) { _walkProgressPos = pos; _walkProgressAt = now; }
                        if (now - _walkProgressAt > 4f) DepotHopIn(now, "stall");
                        else if (now - _walkStart > 25f) DepotHopIn(now, "timeout");
                        return;
                    }
                    if (_depotStage == 2)
                    {
                        if (now < _depotUntil) return;
                        // Pockets empty into the base stash, then the Cobalt resupply tops
                        // the ammo back up (native bodies genuinely burn it).
                        FakeFriends.DepositHostileTake(Body, BotId);
                        var self = Ff;
                        if (self != null) self.ResupplyHostileAmmo(Body, BotId);
                        SetDucked(false);
                        if (_walkedIn && Nav.SetDestination(HomePos, BaseNavigator.NavigationSpeed.Slow, 0f, 0f))
                        {
                            _passDoor = null; _walkStart = now; _walkProgressPos = pos; _walkProgressAt = now;
                            _depotStage = 3;
                            return;
                        }
                        self?.HostileExitBase(BotId, Body); // door swings, step back out
                        if (Nav != null) Nav.PlaceOnNavMesh(0f); // re-sync the agent after the interior hop
                        EndDepot(now);
                        return;
                    }
                    // stage 3: walking out to the anchor
                    FakeFriends.DriverDoorPass(pos, _walkDoors, ref _passDoor);
                    if (Vector3.Distance(pos, HomePos) <= 1.5f)
                    {
                        Ff?.DriverWalkDone(Body.displayName);
                        EndDepot(now);
                        return;
                    }
                    if ((pos - _walkProgressPos).magnitude >= 1f) { _walkProgressPos = pos; _walkProgressAt = now; }
                    if (now - _walkProgressAt > 4f || now - _walkStart > 25f)
                    {
                        Ff?.DriverWalkFallback(Body.displayName, now - _walkProgressAt > 4f ? "stallOut" : "timeoutOut");
                        Ff?.HostileExitBase(BotId, Body);
                        if (Nav != null) Nav.PlaceOnNavMesh(0f);
                        EndDepot(now);
                    }
                    return;
                }
                if (StateName != "return") StateName = "roam";
                if (Body.modelState != null && Body.modelState.ducked) SetDucked(false); // belt-and-suspenders: never patrol crouched
                RoamTick(now);
            }

            // I-3 taunt sortie: occasionally the roam leg routes past a real, ONLINE
            // player's TC and the hostile says something unfriendly on arrival. The
            // plugin picks the target and owns the cooldown; the 5D privilege guard
            // still keeps the body out of the base itself, so worst case the line
            // lands from outside the walls.
            private Vector3 _tauntGoal;
            private float _tauntDeadline;
            private float _nextTauntTryAt;

            private void RoamTick(float now)
            {
                if (_tauntGoal != Vector3.zero)
                {
                    var td = Body.transform.position - _tauntGoal; td.y = 0f;
                    if (td.sqrMagnitude < 15f * 15f || now > _tauntDeadline)
                    {
                        if (td.sqrMagnitude < 60f * 60f) Ff?.HostileTauntNear(BotId, Body);
                        _tauntGoal = Vector3.zero;
                    }
                    else
                    {
                        if (now < _nextRoamAt) return;
                        _nextRoamAt = now + UnityEngine.Random.Range(8f, 15f);
                        NavMeshHit th;
                        if (NavSample(_tauntGoal, out th, 6f))
                            Nav.SetDestination(th.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        return;
                    }
                }
                if (now < _nextRoamAt) return;
                _nextRoamAt = now + UnityEngine.Random.Range(8f, 15f);
                if (Ff != null && now >= _nextTauntTryAt)
                {
                    _nextTauntTryAt = now + 120f; // cheap re-ask cadence; the real cooldown is plugin-side
                    Vector3 tg;
                    if (Ff.PickTauntProwl(BotId, HomePos, RoamRadius, out tg))
                    {
                        _tauntGoal = tg;
                        _tauntDeadline = now + 180f;
                        NavMeshHit th2;
                        if (NavSample(tg, out th2, 6f))
                            Nav.SetDestination(th2.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        return;
                    }
                }
                var offset = UnityEngine.Random.insideUnitCircle * RoamRadius;
                var goal = HomePos + new Vector3(offset.x, 0f, offset.y);
                goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                NavMeshHit hit;
                if (NavSample(goal, out hit, 6f))
                    Nav.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Slow, 0f, 0f);
            }

            // Facing on an AI-driven body is a TRIO — eyes.rotation + viewAngles +
            // ServerRotation together (the NPCPlayer.SetAimDirection pattern).
            // viewAngles alone leaves the networked model rotation at its spawn value:
            // the body slides along the path facing a fixed direction — observed live
            // 2026-07-21 as "walking backwards".
            private void Face(Vector3 dir)
            {
                if (dir == Vector3.zero) return;
                var rot = Quaternion.LookRotation(dir, Vector3.up);
                Body.eyes.rotation = rot;
                Body.viewAngles = rot.eulerAngles;
                Body.ServerRotation = rot;
            }

            private void SetDucked(bool v)
            {
                if (Body == null || Body.IsDestroyed || Body.modelState == null) return;
                Body.modelState.ducked = v;
                Body.SendNetworkUpdate();
            }

            private void TakeLoot()
            {
                if (_lootCorpse == null || _lootCorpse.IsDestroyed || _lootCorpse.containers == null) return;
                var taken = 0;
                foreach (var container in _lootCorpse.containers)
                {
                    if (container == null) continue;
                    var items = new List<Item>(container.itemList); // MoveToContainer mutates the list
                    foreach (var item in items)
                    {
                        if (taken >= 6) return; // strips the good stuff, not a vacuum
                        if (item != null && item.MoveToContainer(Body.inventory.containerMain)) taken++;
                    }
                }
            }
        }

        // Is this live body one of ours with the Hostile persona (either chassis)?
        private bool IsHostileBody(BasePlayer bp)
        {
            if (bp is HostileNpc) return true;
            int id;
            BotRecord r;
            return _byUserId.TryGetValue((ulong)bp.userID, out id) && _bots.TryGetValue(id, out r) && IsHostileP(r);
        }

        private void AttachHostileDriver(BotRecord rec, BasePlayer bot)
        {
            var d = bot.GetComponent<HostileDriver>();
            if (d == null) d = bot.gameObject.AddComponent<HostileDriver>();
            var season = Seasoning(rec); // veterans of the wipe shoot sharper and harder
            d.Init(bot, rec.Identity.BotId, rec.State.HomePos.V, HostilePatrolRadius(rec),
                (rec.Identity.Traits?.SkillLevel ?? 0.6f) + 0.25f * season, 1f + 0.3f * season);
            var gun = FightWeapon(bot); // shoulder the kit weapon for the prowl
            var gi = gun != null ? gun.GetItem() : null;
            if (gi != null) bot.UpdateActiveItem(gi.uid);
        }

        private void DetachHostileDriver(BasePlayer body)
        {
            if (body == null || body.IsDestroyed) return;
            var d = body.GetComponent<HostileDriver>();
            if (d != null)
            {
                d.Detach();
                body.UpdateActiveItem(default(ItemId)); // holster for the sleep
            }
        }

        // 5.5 — the depot happens INSIDE the base like every other resident visit:
        // entrance door swings open, the body ducks in (interior floors are off the
        // navmesh — the same teleport hop residents use), and it swings shut behind
        // him. No base yet → no-ops, and the depot crouch happens at the anchor.
        private void HostileEnterBase(int botId, BasePlayer body)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return;
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) return;
            FlickBaseDoors(b, body.transform.position);
            var shelter = GetShelterPoint(rec);
            if (shelter != Vector3.zero) body.Teleport(shelter);
        }

        private void HostileExitBase(int botId, BasePlayer body)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return;
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) return;
            FlickBaseDoors(b, body.transform.position);
            if (rec.State.HomePos != null) body.Teleport(GroundSnap(rec.State.HomePos.V));
        }

        // Cobalt resupply: top the pocketed ammo for the belt weapon back up to the
        // worn kit's AmmoCount (the depot just emptied the pockets into the stash —
        // the retainer covers consumables, same fiction as the scientist era's
        // never-consumed magazines).
        private void ResupplyHostileAmmo(BasePlayer body, int botId)
        {
            BotRecord rec;
            if (_bots.TryGetValue(botId, out rec)) ResupplyKitAmmo(body, rec);
        }

        // WD-4 (v0.15.2): the retainer fiction, extended to every persona. Ammo used
        // to enter the world through GiveKit alone, so winning a fight was
        // self-extinguishing — a bot that defended its yard went permanently dry
        // (GrannyMags: 25 engagements, 25 "ran out of ammo", 0 wins) and the camp
        // was never cleared. Home stashes cover consumables: pockets top back up to
        // the worn kit's AmmoCount at session start and on leaving shelter.
        private void ResupplyKitAmmo(BasePlayer body, BotRecord rec)
        {
            if (rec == null) return;
            var gun = FightWeapon(body);
            var ammoDef = gun?.primaryMagazine?.ammoType;
            if (ammoDef == null || body.inventory?.containerMain == null) return;
            var ladder = ResolveKitLadder(rec.Identity?.Persona);
            var earned = rec.Progression?.KitTier ?? 0;
            var gear = rec.Progression?.GearTier ?? -1;
            var kit = ladder[Mathf.Clamp(gear >= 0 ? gear : earned, 0, ladder.Count - 1)];
            var want = Mathf.Max(kit.AmmoCount, 32);
            var have = 0;
            foreach (var item in body.inventory.containerMain.itemList)
                if (item != null && item.info.itemid == ammoDef.itemid) have += item.amount;
            if (have >= want) return;
            var refill = ItemManager.Create(ammoDef, want - have);
            if (refill != null && !refill.MoveToContainer(body.inventory.containerMain)) refill.Remove();
            DLog($"[kit] {rec.Identity.Name} restocked {want - have} {ammoDef.shortname} from the home stash");
        }

        #endregion

        #region Hostile module probes

        // ---- 5A2-R probe: can a NATIVE-BOT BasePlayer fire a held gun server-side? ----
        // One-shot, marker-gated (data/FakeFriends/probe_fire.json): spawns two steam-id
        // probe bots 10 m apart, shooter fires 3 rounds two ways — (A) plain ServerUse
        // (muzzle/eyes aim) and (B) originOverride matrix aim — logging target health
        // after each. Cleans up and deletes the marker. Result gates the resident
        // fight-or-flight module (and Phase 6 scientist fights).
        private string ProbeFireMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_fire.json");

        // Probe results bypass Oxide logging entirely — 2026-07-21: BOTH log writers
        // (per-plugin and main oxide) wedged at once and probe v4's results vanished.
        // Direct file I/O or it didn't happen.
        private void PLog(string msg)
        {
            DLog(msg);
            try
            {
                var path = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_results.log");
                // unattended-week guard: append-only since Phase 5 — rotate at 5 MB
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 5_000_000)
                {
                    var old = path + ".old";
                    System.IO.File.Delete(old);
                    System.IO.File.Move(path, old);
                }
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // ── Phase 7 gate probes P7-1 + P7-2 ─────────────────────────────────
        // One-shot, marker-gated (data/FakeFriends/probe_slm.json). P7-1: chained
        // Oxide WebRequests POSTs (async, callbacks on main thread) to the Ollama
        // endpoint — warm latency distribution, response shape, and two failure
        // drills (closed port + black-hole timeout); persona-conditioned prompts
        // from real cast records at 60-token caps. P7-2: arms OnPlayerChat for
        // 10 min and PLogs every real-player line (payload + nearest live bot).
        // Nothing spawned — nothing to sweep. Marker doubles as config:
        // {"endpoint":"...","model":"...","calls":"8"} — all optional.
        // ETA: main sweep + drills ~90 s from probe start; chat window 10 min.
        private string ProbeSlmMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_slm.json");
        private float _probeChatUntil;
        private readonly List<float> _probeSlmLat = new List<float>();

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        private void ProbeSlm()
        {
            string endpoint = string.IsNullOrEmpty(_config.SLMEndpoint) ? "http://localhost:11434" : _config.SLMEndpoint.TrimEnd('/'),
                model = string.IsNullOrEmpty(_config.SLMModel) ? "llama3.2:3b" : _config.SLMModel;
            int calls = 8;
            try
            {
                var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(System.IO.File.ReadAllText(ProbeSlmMarker));
                if (cfg != null)
                {
                    string v;
                    if (cfg.TryGetValue("endpoint", out v) && !string.IsNullOrEmpty(v)) endpoint = v.TrimEnd('/');
                    if (cfg.TryGetValue("model", out v) && !string.IsNullOrEmpty(v)) model = v;
                    int n;
                    if (cfg.TryGetValue("calls", out v) && int.TryParse(v, out n) && n > 0 && n <= 20) calls = n;
                }
            }
            catch { }
            try { System.IO.File.Delete(ProbeSlmMarker); } catch { }
            _probeSlmLat.Clear();
            PLog($"[probe:slm] START — endpoint {endpoint}, model {model}: {calls} persona calls then 2 failure drills (ETA ~90 s). Chat hook armed 10 min — type in global chat to feed P7-2.");
            _probeChatUntil = Time.realtimeSinceStartup + 600f;
            UpdateChatHookGate();
            timer.Once(600f, () =>
            {
                if (_probeChatUntil > 0f) { _probeChatUntil = 0f; UpdateChatHookGate(); PLog("[probe:slm] chat window closed."); }
            });

            // persona pool: prefer Roleplayers (the real Phase 7 consumers), pad with any cast
            var pool = new List<BotRecord>();
            foreach (var rec in _bots.Values) if (rec.Identity.Persona == "Roleplayer") pool.Add(rec);
            foreach (var rec in _bots.Values) { if (pool.Count >= 4) break; if (!pool.Contains(rec)) pool.Add(rec); }
            if (pool.Count == 0) { PLog("[probe:slm] no bot records — aborted."); return; }
            var lines = new[]
            {
                "hey, what are you up to today?",
                "anyone seen a bear around here?",
                "nice base — you build it yourself?",
                "got anything worth trading?",
                "rough wipe so far?",
                "where's the best place to find scrap?"
            };
            ProbeSlmCall(endpoint, model, pool, lines, 0, calls);
        }

        private void ProbeSlmCall(string endpoint, string model, List<BotRecord> pool, string[] lines, int i, int total)
        {
            if (i >= total) { ProbeSlmFailureDrills(endpoint); return; }
            var rec = pool[i % pool.Count];
            var line = lines[i % lines.Length];
            var sys = $"You are {rec.Identity.Name}, a {rec.Identity.Persona} living on a Rust island. Backstory: {rec.Identity.Backstory} Reply with ONE short in-character chat line, under 25 words, plain text, no quotes, no narration, no newlines.";
            var body = Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = false,
                ["max_tokens"] = 60,
                ["messages"] = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = sys },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = line }
                }
            });
            var t0 = Time.realtimeSinceStartup;
            webrequest.Enqueue(endpoint + "/v1/chat/completions", body, (code, resp) =>
            {
                var ms = (Time.realtimeSinceStartup - t0) * 1000f;
                string content = null; int ptok = -1, ctok = -1;
                try
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(resp);
                    content = (string)jo["choices"]?[0]?["message"]?["content"];
                    ptok = (int?)jo["usage"]?["prompt_tokens"] ?? -1;
                    ctok = (int?)jo["usage"]?["completion_tokens"] ?? -1;
                }
                catch { }
                if (code == 200 && content != null) _probeSlmLat.Add(ms);
                var shown = content == null ? "<PARSE FAIL: " + Trunc(resp, 120) + ">" : Trunc(content.Replace("\n", "\\n"), 140);
                PLog($"[probe:slm] call {i + 1}/{total} [{rec.Identity.Name}] code {code}, {ms:0} ms, tok {ptok}+{ctok}: {shown}");
                timer.Once(0.5f, () => ProbeSlmCall(endpoint, model, pool, lines, i + 1, total));
            }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, 30f);
        }

        private void ProbeSlmFailureDrills(string endpoint)
        {
            // drill 1: closed port on the live host — what does a refused connection look like?
            string closedUrl = "http://localhost:11435/v1/chat/completions";
            try { var u = new Uri(endpoint); closedUrl = $"{u.Scheme}://{u.Host}:{u.Port + 1}/v1/chat/completions"; } catch { }
            var t0 = Time.realtimeSinceStartup;
            webrequest.Enqueue(closedUrl, "{}", (code, resp) =>
            {
                PLog($"[probe:slm] drill closed-port: code {code} after {(Time.realtimeSinceStartup - t0) * 1000f:0} ms, resp {Trunc(resp, 80)}");
                // drill 2: black-hole address — does the 5 s timeout actually bound the wait?
                var t1 = Time.realtimeSinceStartup;
                webrequest.Enqueue("http://10.255.255.1:11434/v1/chat/completions", "{}", (code2, resp2) =>
                {
                    PLog($"[probe:slm] drill black-hole: code {code2} after {(Time.realtimeSinceStartup - t1) * 1000f:0} ms, resp {Trunc(resp2, 80)}");
                    ProbeSlmVerdict();
                }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, 5f);
            }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, 5f);
        }

        private void ProbeSlmVerdict()
        {
            if (_probeSlmLat.Count == 0) { PLog("[probe:slm] VERDICT: FAIL — no successful persona calls."); return; }
            _probeSlmLat.Sort();
            float med = _probeSlmLat[_probeSlmLat.Count / 2];
            PLog($"[probe:slm] VERDICT: {_probeSlmLat.Count} OK, median {med:0} ms, min {_probeSlmLat[0]:0} ms, max {_probeSlmLat[_probeSlmLat.Count - 1]:0} ms — {(med < 3000f ? "PASS: mid-conversation replies viable" : "SLOW: ambient-cadence only")}. Chat window may still be open.");
        }

        // Chat listener: P7-2 probe logging while the window is open, then the real
        // 7C reply path. Subscribed only via UpdateChatHookGate (Init unsubscribes).
        // Structural note (gate report): this hook fires only for CONNECTED players —
        // our connection-less bots can never re-enter it, so no reply loops.
        private void OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            if (_probeChatUntil > 0f) ProbeChatLog(player, message, channel);
            if (!SlmEnabled || !_config.SLMChatReplies || !_config.ChatLines) return;
            if (!player.userID.IsSteamId()) return; // belt-and-braces — the hook already excludes connection-less senders
            if (channel != ConVar.Chat.ChatChannel.Global && channel != ConVar.Chat.ChatChannel.Local) return; // team/clan chat is private

            // Responder pick, in priority order:
            // 1) the bot already in conversation with this player (fast follow-ups)
            // 2) an online bot called BY NAME — reaches 2.5× earshot, beats distance
            // 3) the nearest eligible bot in plain earshot
            var ppos = player.transform.position;
            BotRecord pick = null; var isConvo = false;

            foreach (var kv in _convos)
                if (kv.Value.PlayerId == player.userID && DateTime.Now < kv.Value.Until)
                {
                    BotRecord rec; var b = GetLive(kv.Key);
                    if (b != null && !b.IsDestroyed && !b.IsDead() && !b.IsSleeping()
                        && _bots.TryGetValue(kv.Key, out rec) && rec.State.Status == "Online")
                    { pick = rec; isConvo = true; }
                    break;
                }

            if (pick == null)
            {
                var lim = _config.SLMEarshot * 2.5f; var bestD = lim * lim;
                foreach (var kv in _live)
                {
                    BotRecord rec;
                    if (!_bots.TryGetValue(kv.Key, out rec) || rec.State.Status != "Online" || !SlmChatEligible(rec)) continue;
                    if (_runnerActive.Contains(kv.Key) || EngagedElsewhere(kv.Key, player.userID)) continue;
                    var b = kv.Value;
                    if (b == null || b.IsDestroyed || b.IsDead() || b.IsSleeping()) continue;
                    if (!ContainsName(message, rec.Identity.Name)) continue;
                    var d = (b.transform.position - ppos).sqrMagnitude;
                    if (d < bestD) { bestD = d; pick = rec; }
                }
            }

            if (pick == null)
            {
                var bestD = _config.SLMEarshot * _config.SLMEarshot;
                foreach (var kv in _live)
                {
                    BotRecord rec;
                    if (!_bots.TryGetValue(kv.Key, out rec) || rec.State.Status != "Online" || !SlmChatEligible(rec)) continue;
                    if (_runnerActive.Contains(kv.Key) || EngagedElsewhere(kv.Key, player.userID)) continue;
                    var b = kv.Value;
                    if (b == null || b.IsDestroyed || b.IsDead() || b.IsSleeping()) continue;
                    var d = (b.transform.position - ppos).sqrMagnitude;
                    if (d < bestD) { bestD = d; pick = rec; }
                }
            }
            if (pick == null) return;

            var botId = pick.Identity.BotId;
            var chattiness = pick.Identity.Traits?.Chattiness ?? 0.3f;
            if (chattiness <= 0.02f) return; // mute stays mute — that's the personality
            var grudge = pick.Social?.Grudges != null && pick.Social.Grudges.ContainsKey(SteamKey(player.userID));

            if (isConvo)
            {
                if (!ChatCooldownReady("slmc", botId, player.userID)) return; // convo floor only
            }
            else
            {
                // peek both floors, THEN the willingness roll, THEN consume (ordering fix)
                if (!ChatCooldownReady("slm", botId, 0UL) || !ChatCooldownReady("slmp", botId, player.userID)) return;
                // willingness: chatty bots engage, quiet ones mostly don't — but a
                // grudge always stops to say something cold (owner's rule)
                if (!grudge && UnityEngine.Random.value > 0.1f + 0.9f * chattiness) return;
                SetChatCooldown("slm", botId, 0UL, _config.ChatCooldownSeconds / 60f);
                SetChatCooldown("slmp", botId, player.userID, _config.SLMPlayerCooldownSeconds / 60f);
            }

            SlmConvo hist;
            var turns = isConvo && _convos.TryGetValue(botId, out hist) ? hist.Turns : null;
            var userLine = Trunc(message, 200);
            var playerId = player.userID; var playerName = player.displayName;
            SlmAsk(SlmPersonaPrompt(pick, player), turns, userLine, pick.Identity.Name, reply =>
            {
                if (string.IsNullOrEmpty(reply)) reply = PickChatLine("Greet." + pick.Identity.Persona, playerName); // template fallback on ANY failure
                if (string.IsNullOrEmpty(reply)) return;
                var final = reply;
                // short jitter on top of the round-trip so the reply reads as typing
                timer.Once(UnityEngine.Random.Range(0.6f, 2.2f), () =>
                {
                    var b = GetLive(botId);
                    if (b == null || b.IsDestroyed || b.IsDead() || b.IsSleeping()) return;
                    BotRecord rec;
                    if (!_bots.TryGetValue(botId, out rec) || rec.State.Status != "Online") return;
                    PrintToChat($"{rec.Identity.Name}: {final}");
                    // open/refresh the conversation — the bot stands attending (WanderTick
                    // pins it and faces the partner until the window closes or it's hurt)
                    SlmConvo c;
                    if (_convos.TryGetValue(botId, out c) && c.PlayerId != playerId) { EndConvo(botId, c); c = null; } // partner swap closes the old thread
                    if (c == null)
                    {
                        _convos[botId] = c = new SlmConvo { PlayerId = playerId, PlayerName = playerName };
                        _convStarted++;
                        // 7F: story beat for the notable first only — the pair's first-ever
                        // conversation (no memory of them yet). Repeat chats stay quiet.
                        if (rec.Social?.MemoryNames == null || !rec.Social.MemoryNames.ContainsKey(SteamKey(playerId)))
                            Story("chat", $"{rec.Identity.Name} got to talking with {playerName}");
                    }
                    c.Until = DateTime.Now.AddSeconds(_config.SLMConversationSeconds);
                    c.LastHp = b.health;
                    c.Turns.Add(new KeyValuePair<string, string>("user", userLine));
                    c.Turns.Add(new KeyValuePair<string, string>("assistant", final));
                    while (c.Turns.Count > 20) c.Turns.RemoveAt(0); // full window transcript (~10 exchanges) — still tiny vs the 4k context
                    SetChatCooldown("slmc", botId, playerId, _config.SLMConvoReplySeconds / 60f);
                });
            });
        }

        private void ProbeChatLog(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (Time.realtimeSinceStartup > _probeChatUntil)
            { _probeChatUntil = 0f; UpdateChatHookGate(); PLog("[probe:slm] chat window closed."); return; }
            float best = -1f; string bestName = "none";
            foreach (var rec in _bots.Values)
            {
                var b = GetLive(rec.Identity.BotId);
                if (b == null || b.IsDestroyed) continue;
                var d = Vector3.Distance(b.transform.position, player.transform.position);
                if (best < 0f || d < best) { best = d; bestName = rec.Identity.Name; }
            }
            PLog($"[probe:slm] chat heard: {player.displayName} ({player.userID}) ch={channel} \"{Trunc(message, 80)}\" nearestBot={bestName}@{(best < 0f ? -1f : best):0}m");
        }

        private void ProbeNativeFire()
        {
            BasePlayer shooter = null, target = null;
            try
            {
                try { System.IO.File.Delete(ProbeFireMarker); } catch { }
                // stage the range: 30 m off the first settled home, ground-snapped
                Vector3 origin = Vector3.zero;
                foreach (var rec in _bots.Values)
                    if (rec.State.HomePos != null) { origin = rec.State.HomePos.V + new Vector3(30f, 0f, 0f); break; }
                if (origin == Vector3.zero) { PLog("[probe:fire] no settled home to stage near — aborted."); return; }
                origin = GroundSnap(origin);
                var tpos = GroundSnap(origin + new Vector3(10f, 0f, 0f));

                // sweep bodies leaked by an interrupted earlier run (same stage spot every time)
                var stale = new List<BasePlayer>();
                Vis.Entities(origin, 25f, stale);
                foreach (var s in stale)
                    if (s != null && !s.IsDestroyed && s.displayName != null && s.displayName.StartsWith("FF_PROBE", StringComparison.Ordinal))
                    { s.Kill(); PLog($"[probe:fire] swept leaked body {s.displayName}."); }

                shooter = SpawnProbeBody(origin, "FF_PROBE_SHOOTER", 990001UL);
                target = SpawnProbeBody(tpos, "FF_PROBE_TARGET", 990002UL);
                if (shooter == null || target == null) { PLog("[probe:fire] body spawn failed — aborted."); return; }

                var weaponItem = ItemManager.CreateByName("rifle.semiauto", 1);
                if (weaponItem == null || !weaponItem.MoveToContainer(shooter.inventory.containerBelt))
                { PLog("[probe:fire] weapon give failed — aborted."); return; }
                shooter.UpdateActiveItem(weaponItem.uid);
                var gun = weaponItem.GetHeldEntity() as BaseProjectile;
                if (gun == null) { PLog("[probe:fire] held entity is not a BaseProjectile — aborted."); return; }
                gun.SetAmmoCount(gun.primaryMagazine.capacity);

                var shooterRef = shooter; var targetRef = target; // capture for the timer chain
                // v2: wake both AFTER spawn settles (v1 finding: steam-id bodies spawn as
                // SLEEPERS — lying poses, rays over/under the bodies, zero damage), aim
                // at center mass, and log ammo + an explicit trace per shot.
                timer.Once(1f, () =>
                {
                    if (shooterRef == null || shooterRef.IsDestroyed || targetRef == null || targetRef.IsDestroyed) return;
                    if (shooterRef.IsSleeping()) shooterRef.EndSleeping();
                    if (targetRef.IsSleeping()) targetRef.EndSleeping();
                    var aim0 = (targetRef.CenterPoint() - shooterRef.eyes.position).normalized;
                    shooterRef.OverrideViewAngles(Quaternion.LookRotation(aim0).eulerAngles);
                    shooterRef.SendNetworkUpdate();
                    // v3: v2 proved fire+aim but zero damage — log every scale in the
                    // damage line (897: GetDamageScale * damageModifier * turretDamageScale
                    // for non-NPC owners), force turret scale to 1, and baseline-Hurt the
                    // target to prove it's damageable at all.
                    PLog($"[probe:fire] staged: hp {targetRef.health:0.#}, ammo {gun.primaryMagazine.contents}, dmgScale {gun.GetDamageScale():0.###}, turretScale {gun.turretDamageScale:0.###}, npcScale {gun.npcDamageScale:0.###}");
                    var hpb = targetRef.health;
                    targetRef.Hurt(5f, Rust.DamageType.Bullet, shooterRef);
                    PLog($"[probe:fire] baseline Hurt(5): hp {hpb:0.#} → {targetRef.health:0.#}");
                    gun.turretDamageScale = 1f;
                });
                Action<string> shot = label =>
                {
                    if (shooterRef == null || shooterRef.IsDestroyed || targetRef == null || targetRef.IsDestroyed) return;
                    var before = targetRef.health;
                    var ammoBefore = gun.primaryMagazine.contents;
                    var from = shooterRef.eyes.position;
                    var dir = (targetRef.CenterPoint() - from).normalized;
                    RaycastHit tr;
                    var traceDesc = UnityEngine.Physics.Raycast(from, dir, out tr, 300f, 1220225793)
                        ? $"{(tr.GetEntity() != null ? tr.GetEntity().ShortPrefabName : tr.collider?.name ?? "?")}@{tr.distance:0.0}m"
                        : "nothing";
                    var m = Matrix4x4.TRS(from, Quaternion.LookRotation(dir), Vector3.one);
                    gun.ServerUse(new HeldEntityServerUseParams(1f, 1f, m, true, false));
                    timer.Once(0.4f, () => PLog($"[probe:fire] {label}: hp {before:0.#} → {(targetRef.IsDestroyed ? -1f : targetRef.health):0.#}, ammo {ammoBefore}→{gun.primaryMagazine.contents}, trace {traceDesc}"));
                };
                // v6: ONE deferred block — v5's multi-timer chain died when a double
                // compile reloaded the plugin mid-sequence. ProcessHit is synchronous at
                // this range (9.5 m « the deferred-hit threshold), so shots read back
                // immediately; no chain to kill.
                Subscribe(nameof(OnEntityTakeDamage));
                timer.Once(2.5f, () =>
                {
                    try
                    {
                        if (shooterRef == null || shooterRef.IsDestroyed || targetRef == null || targetRef.IsDestroyed)
                        { PLog("[probe:fire] bodies gone before shots — aborted."); return; }
                        shot("A ServerUse");
                        PLog($"[probe:fire] A post: hp {targetRef.health:0.#}, ammo {gun.primaryMagazine.contents}");
                        var before = targetRef.health;
                        var hi = new HitInfo(shooterRef, targetRef, Rust.DamageType.Bullet, 30f);
                        hi.PointStart = shooterRef.eyes.position;
                        hi.HitPositionWorld = targetRef.CenterPoint();
                        targetRef.Hurt(hi);
                        PLog($"[probe:fire] D manual HitInfo(30): hp {before:0.#} → {(targetRef.IsDestroyed ? -1f : targetRef.health):0.#}");
                        if (!targetRef.IsDestroyed)
                        {
                            shot("C ServerUse");
                            PLog($"[probe:fire] C post: hp {(targetRef.IsDestroyed ? -1f : targetRef.health):0.#}");
                        }
                    }
                    catch (Exception e) { PLog($"[probe:fire] EXCEPTION in shots: {e.Message}"); }
                    finally
                    {
                        if (!DamageScaleArmed) Unsubscribe(nameof(OnEntityTakeDamage)); // don't strip the WD-2 dial's subscription
                        if (shooterRef != null && !shooterRef.IsDestroyed) shooterRef.Kill();
                        if (targetRef != null && !targetRef.IsDestroyed) targetRef.Kill();
                        PLog("[probe:fire] complete — probe bodies removed.");
                    }
                });
            }
            catch (Exception e)
            {
                PLog($"[probe:fire] EXCEPTION: {e.Message}");
                if (shooter != null && !shooter.IsDestroyed) shooter.Kill();
                if (target != null && !target.IsDestroyed) target.Kill();
            }
        }

        // Double duty, both dynamically subscribed: the fire-probe tap (probe window
        // only) and the WD-2 wildlife damage dial (armed with the animal guard while
        // AnimalDamageToBotScale ≠ 1 — decision 0010's seasoning knob).
        private void OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null) return;
            if (DamageScaleArmed && info.Initiator != null && IsWildlife(info.Initiator)
                && _byUserId.ContainsKey((ulong)player.userID))
                info.damageTypes?.ScaleAll(_config.AnimalDamageToBotScale);
            if (!player.displayName.StartsWith("FF_PROBE", StringComparison.Ordinal)) return;
            PLog($"[probe:fire] hook OnEntityTakeDamage: victim={player.displayName} total={info.damageTypes?.Total():0.##} initiator={(info.Initiator != null ? info.Initiator.ShortPrefabName : "null")}");
        }

        // Steam-id-range probe body (sleeper-like, invisible under occlusion — fine, no
        // audience needed): full-grade player entity outside the bot registry.
        private BasePlayer SpawnProbeBody(Vector3 pos, string name, ulong idOffset)
        {
            var ent = GameManager.server.CreateEntity(PlayerPrefab, pos, Quaternion.identity);
            var bp = ent as BasePlayer;
            if (bp == null) return null;
            var id = SteamIdBase + idOffset;
            bp.userID = id;
            bp.UserIDString = id.ToString();
            bp.displayName = name;
            bp.Spawn();
            bp.health = 100f;
            return bp;
        }

        // ---- 5A2-R probe: end-to-end fight-or-flight on a LIVE resident -------------
        // One-shot, marker-gated (data/FakeFriends/probe_fight.json): arms the first
        // online resident (rifle + ammo if his own kit has no gun; the probe flag
        // bypasses the tier/aggression gates so ANY resident can demo), spawns a boar
        // 18m out, and nips the resident with it — lastAttacker → the 1.5s threat check
        // → StartFight. Every shot PLogs; a 90s watchdog cleans up (boar + gifted gear).
        private string ProbeFightMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_fight.json");
        private int _probeFightBotId = -1;
        private BaseCombatEntity _probeBoar;
        private readonly List<Item> _probeFightGear = new List<Item>();
        private Oxide.Plugins.Timer _probeFightWatchdog;

        private void ProbeFight()
        {
            try { System.IO.File.Delete(ProbeFightMarker); } catch { }
            try
            {
                BotRecord pick = null;
                BasePlayer body = null;
                foreach (var rec in _bots.Values)
                {
                    if (rec.State.Status != "Online" || IsHostileP(rec)) continue;
                    var bp = GetLive(rec.Identity.BotId);
                    if (bp == null || bp.IsSleeping() || bp.IsDead()) continue;
                    pick = rec;
                    body = bp;
                    break;
                }
                if (pick == null) { PLog("[probe:fight] no online resident to arm — aborted (start a session, re-drop the marker)."); return; }
                if (FightWeapon(body) == null)
                {
                    var gunItem = ItemManager.CreateByName("rifle.semiauto", 1);
                    if (gunItem == null || !gunItem.MoveToContainer(body.inventory.containerBelt))
                    { PLog("[probe:fight] arming failed — aborted."); if (gunItem != null) gunItem.Remove(); return; }
                    _probeFightGear.Add(gunItem);
                }
                var ammo = ItemManager.CreateByName("ammo.rifle", 64);
                if (ammo != null)
                {
                    if (ammo.MoveToContainer(body.inventory.containerMain)) _probeFightGear.Add(ammo);
                    else ammo.Remove();
                }
                _probeFightBotId = pick.Identity.BotId;
                var dir = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
                var bpos = GroundSnap(body.transform.position + dir * 18f);
                var boar = GameManager.server.CreateEntity("assets/rust.ai/agents/boar/boar.prefab", bpos, Quaternion.identity) as BaseCombatEntity;
                if (boar == null) { PLog("[probe:fight] boar spawn failed — aborted."); ProbeFightCleanup(); return; }
                boar.Spawn();
                _probeBoar = boar;
                var pickId = pick.Identity.BotId;
                PLog($"[probe:fight] armed {pick.Identity.Name} (hp {body.health:0}, weapon {FightWeapon(body)?.GetItem()?.info?.shortname ?? "?"}) + boar at 18m.");
                timer.Once(2f, () => // the nip: deterministic aggro (boars can be shy)
                {
                    var b = GetLive(pickId);
                    if (b == null || _probeBoar == null || _probeBoar.IsDestroyed) { PLog("[probe:fight] stage lost before the poke."); ProbeFightCleanup(); return; }
                    b.Hurt(new HitInfo(_probeBoar, b, DamageType.Bite, 2f));
                    PLog("[probe:fight] poked — fight should open within ~2s (watchdog 90s).");
                });
                _probeFightWatchdog = timer.Once(90f, () =>
                {
                    PLog($"[probe:fight] window closed — {(_probeBoar == null || _probeBoar.IsDestroyed || _probeBoar.IsDead() ? "boar died (fight ran to a kill)" : "boar still alive (check the shot log)")}.");
                    ProbeFightCleanup();
                });
            }
            catch (Exception e)
            {
                PLog($"[probe:fight] EXCEPTION: {e.Message}");
                ProbeFightCleanup();
            }
        }

        private void ProbeFightCleanup()
        {
            _probeFightBotId = -1;
            if (_probeBoar != null && !_probeBoar.IsDestroyed && !_probeBoar.IsDead()) _probeBoar.Kill();
            _probeBoar = null;
            foreach (var item in _probeFightGear)
                if (item != null && item.amount > 0) item.Remove();
            _probeFightGear.Clear();
            _probeFightWatchdog?.Destroy();
            _probeFightWatchdog = null;
            PLog("[probe:fight] cleanup done.");
        }

        // ---- Phase 5.5 probe: native-body hostile session end-to-end ----------------
        // One-shot, marker-gated (data/FakeFriends/probe_hostile55.json): force-starts
        // a prowl for the first offline hostile identity (real session — nothing to
        // clean up; the scheduler owns it afterwards) and PLogs the driver's state and
        // movement every 10s for 2 minutes. PASS = driver attached + body roaming.
        private string ProbeHostile55Marker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_hostile55.json");

        private void ProbeHostile55()
        {
            try { System.IO.File.Delete(ProbeHostile55Marker); } catch { }
            try
            {
                BotRecord pick = null;
                foreach (var rec in _bots.Values)
                    if (IsHostileP(rec) && !IsOut(rec) && rec.State.Status != "Online" && rec.State.Status != "Dead" && rec.State.HomePos != null)
                    { pick = rec; break; }
                if (pick == null) { PLog("[probe:h55] no offline hostile identity to prowl — aborted."); return; }
                if (!_config.HostileNativeBodies) { PLog("[probe:h55] HostileNativeBodies is OFF — aborted."); return; }
                PLog($"[probe:h55] starting native prowl for {pick.Identity.Name} (kit tier {pick.Progression?.KitTier ?? 0}, camp {pick.State.HomePos.V})");
                StartHostileSession(pick);
                var pickId = pick.Identity.BotId;
                if (pick.State.Status != "Online") { PLog($"[probe:h55] session did not open (status {pick.State.Status}) — deferred by the prowl cap or spawn failure."); return; }
                Vector3 startPos;
                {
                    var b0 = GetLive(pickId);
                    if (b0 == null) { PLog("[probe:h55] no live body after session start — FAIL."); return; }
                    startPos = b0.transform.position;
                    PLog($"[probe:h55] body up: driver {(b0.GetComponent<HostileDriver>() != null ? "ATTACHED" : "MISSING")}, gun {(FightWeapon(b0)?.GetItem()?.info?.shortname ?? "none")}, hp {b0.health:0}");
                }
                var samples = 0;
                var maxMoved = 0f;
                timer.Repeat(10f, 12, () =>
                {
                    samples++;
                    var b = GetLive(pickId);
                    var drv = b?.GetComponent<HostileDriver>();
                    if (b == null || b.IsDestroyed)
                    { if (samples <= 12) PLog($"[probe:h55] t={samples * 10}s body gone (died or logged off — see story log)"); return; }
                    var moved = Vector3.Distance(b.transform.position, startPos);
                    if (moved > maxMoved) maxMoved = moved;
                    PLog($"[probe:h55] t={samples * 10}s state {(drv != null ? drv.StateName : "NO DRIVER")}, moved {moved:0.0}m (max {maxMoved:0.0}m), hp {b.health:0}, navType {(drv?.Nav != null ? drv.Nav.CurrentNavigationType.ToString() : "?")}");
                    if (samples >= 12)
                        PLog($"[probe:h55] VERDICT: {(drv != null && maxMoved > 8f ? "PASS — native-body hostile prowls" : "CHECK — driver or movement shortfall (see samples)")}; session left running for the scheduler.");
                });
            }
            catch (Exception e) { PLog($"[probe:h55] EXCEPTION: {e.Message}"); }
        }

        // ---- Phase 5.5 gate probe: BaseNavigator/NavMeshAgent GRAFT onto player.prefab ----
        // One-shot, marker-gated (data/FakeFriends/probe_navgraft.json). The scientist
        // chassis carries these components from its prefab; player.prefab has neither.
        // If AddComponent + Init + SetDestination drives a probe body across the
        // terrain navmesh, hostiles can migrate to native-bot bodies (perfect disguise);
        // if not, they keep the scientist chassis + display patches. Uses the 5P2
        // recipe: configure the raw Unity agent while disabled, type it to the surface
        // that answers here, enable in place. PLog-only (probe logging doctrine).
        private string ProbeNavGraftMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_navgraft.json");

        private void ProbeNavGraft()
        {
            try { System.IO.File.Delete(ProbeNavGraftMarker); } catch { }
            BasePlayer body = null;
            try
            {
                Vector3 origin = Vector3.zero;
                foreach (var rec in _bots.Values)
                    if (rec.State.HomePos != null) { origin = rec.State.HomePos.V + new Vector3(-30f, 0f, 0f); break; }
                if (origin == Vector3.zero) { PLog("[probe:navgraft] no settled home to stage near — aborted."); return; }
                origin = GroundSnap(origin);
                var stale = new List<BasePlayer>();
                Vis.Entities(origin, 25f, stale);
                foreach (var s in stale)
                    if (s != null && !s.IsDestroyed && s.displayName != null && s.displayName.StartsWith("FF_PROBE", StringComparison.Ordinal))
                    { s.Kill(); PLog($"[probe:navgraft] swept leaked body {s.displayName}."); }
                body = SpawnProbeBody(origin, "FF_PROBE_NAV", 990003UL);
                if (body == null) { PLog("[probe:navgraft] body spawn failed — aborted."); return; }
                var bodyRef = body;
                var goal = origin + new Vector3(0f, 0f, 15f);
                timer.Once(1f, () => // one settle beat, then the whole graft in one block
                {
                    try
                    {
                        if (bodyRef == null || bodyRef.IsDestroyed) { PLog("[probe:navgraft] body gone before graft."); return; }
                        if (bodyRef.IsSleeping()) bodyRef.EndSleeping();
                        var go = bodyRef.gameObject;
                        var unityAgent = go.AddComponent<UnityEngine.AI.NavMeshAgent>();
                        unityAgent.enabled = false; // 5P2: configure while disabled — raw speed is 0 otherwise
                        var typeId = TerrainAgentTypeId(bodyRef.transform.position);
                        if (typeId != int.MinValue) unityAgent.agentTypeID = typeId;
                        unityAgent.areaMask = NavMesh.AllAreas;
                        unityAgent.speed = 5f;
                        unityAgent.acceleration = 8f;
                        unityAgent.angularSpeed = 120f;
                        unityAgent.updatePosition = false; // Navigator copies nextPosition → ServerPosition
                        unityAgent.updateRotation = false;
                        NavMeshHit snap;
                        if (NavSample(bodyRef.transform.position, out snap, 6f))
                            bodyRef.transform.position = snap.position;
                        unityAgent.enabled = true;
                        PLog($"[probe:navgraft] NavMeshAgent grafted: typeId {typeId}, isOnNavMesh {unityAgent.isOnNavMesh}");
                        var rustAgent = go.AddComponent<Rust.Ai.Gen2.RustNavMeshAgent>(); // wraps the Unity agent it finds on the GO
                        var nav = go.AddComponent<BaseNavigator>();
                        nav.Init(bodyRef, rustAgent); // base CanUpdateMovement only gates on IsAlive — no NPC-class requirement (decompile 2026-07-21)
                        nav.SetNavMeshEnabled(true);
                        var placed = nav.PlaceOnNavMesh(0f);
                        goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                        NavMeshHit goalHit;
                        var goalOk = NavSample(goal, out goalHit, 6f);
                        var set = goalOk && nav.SetDestination(goalHit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        PLog($"[probe:navgraft] navigator grafted: placed {placed}, goal sampled {goalOk}, SetDestination {set}");
                        var startPos = bodyRef.transform.position;
                        var lastLog = startPos;
                        var ticks = 0;
                        // pump Think at 10Hz for 8s (a reload mid-run kills the timer and
                        // leaks the body; the FF_PROBE sweep above catches it next run)
                        timer.Repeat(0.1f, 80, () =>
                        {
                            ticks++;
                            if (bodyRef == null || bodyRef.IsDestroyed) return;
                            nav.Think(0.1f);
                            if (ticks % 20 == 0)
                            {
                                var p = bodyRef.transform.position;
                                PLog($"[probe:navgraft] t={ticks / 10f:0.0}s moved {(p - startPos).magnitude:0.0}m (+{(p - lastLog).magnitude:0.0}m) navType {nav.CurrentNavigationType} hasPath {nav.HasPath}");
                                lastLog = p;
                            }
                            if (ticks >= 80)
                            {
                                var moved = (bodyRef.transform.position - startPos).magnitude;
                                PLog($"[probe:navgraft] VERDICT: moved {moved:0.0}m of ~15m in 8s → {(moved > 5f ? "GRAFT WORKS — Phase 5.5 unblocked" : "graft did NOT drive movement")}");
                                bodyRef.Kill();
                                PLog("[probe:navgraft] complete — probe body removed.");
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        PLog($"[probe:navgraft] EXCEPTION in graft: {e.Message}");
                        if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill();
                    }
                });
            }
            catch (Exception e)
            {
                PLog($"[probe:navgraft] EXCEPTION: {e.Message}");
                if (body != null && !body.IsDestroyed) body.Kill();
            }
        }

        // ==== Phase 6: MonumentRunner embodiment (6A–6C) =============================
        // A runner's embodied session work: leave home, walk to a navmesh monument on
        // the grafted Animal-typed agent (decision 0008 — one agent, no seam logic),
        // strip crate_normal[_2]s (6P2 recipe: MoveToContainer + Kill(Gib) the shell),
        // fight or flee scientists per the 5A2-R gate, haul the take home, deposit.
        // Template: HostileDriver (FacepunchBehaviour, primitives-only Init, OnDestroy
        // cancels invokes, detach on session end/unload/reconcile).
        public class RunnerDriver : FacepunchBehaviour
        {
            public BasePlayer Body;
            public int BotId = -1;
            public Vector3 HomePos;
            public Vector3 MonuCenter;
            public Vector3 StagePos;
            public string MonuName = "?";
            public string StateName = "travel";
            public int CratesStripped;
            public int HauledUnits;
            public bool Fled;
            public BaseNavigator Nav;
            private NavMeshAgent _agent;
            private float _scanRadius = 120f;
            private float _skill = 0.5f;
            private float _dmgMul = 1f;
            private bool _canFight;
            private float _fleeFloor = 35f;
            private float _budgetEndAt;
            private float _lastPump, _thinkAcc;
            private float _nextPathAt, _nextPrivCheckAt, _nextFireAt;
            private float _stallCheckAt; private Vector3 _stallPos; private int _stalls;
            private float _lootUntil, _fleeUntil, _depotUntil;
            private float _reachFailAt; private int _crateReachFails;
            private float _engageStartAt, _lastLosAt;
            private const float EngageTimeout = 90f, NoLosTimeout = 20f; // 5A caps
            private const int MaxCratesPerRun = 6; // raids crates, doesn't vacuum the monument
            private BaseCombatEntity _target;
            private LootContainer _crate;
            private readonly List<LootContainer> _crates = new List<LootContainer>();
            private readonly HashSet<ulong> _visited = new HashSet<ulong>();

            // Primitives only — BotRecord stays plugin-private (HostileDriver pattern).
            public void Init(BasePlayer body, int botId, Vector3 home, Vector3 monuCenter, Vector3 stage,
                float scanRadius, string monuName, float skill, float dmgMul, bool canFight, float fleeFloor, float budgetSec)
            {
                Body = body;
                BotId = botId;
                HomePos = home;
                MonuCenter = monuCenter;
                StagePos = stage;
                MonuName = monuName;
                _scanRadius = scanRadius;
                _skill = Mathf.Clamp01(skill);
                _dmgMul = Mathf.Max(0.1f, dmgMul);
                _canFight = canFight;
                _fleeFloor = fleeFloor;
                _budgetEndAt = UnityEngine.Time.realtimeSinceStartup + budgetSec;
                // The graft (idempotent — a body re-used across runs keeps its components).
                _agent = gameObject.GetComponent<NavMeshAgent>();
                if (_agent == null) _agent = gameObject.AddComponent<NavMeshAgent>();
                _agent.enabled = false;
                var typeId = TerrainAgentTypeId(body.transform.position);
                if (typeId != int.MinValue) _agent.agentTypeID = typeId;
                _agent.areaMask = NavMesh.AllAreas;
                _agent.speed = 5f;
                _agent.acceleration = 8f;
                _agent.angularSpeed = 120f;
                _agent.updatePosition = false;
                _agent.updateRotation = false;
                NavMeshHit snap;
                if (NavSample(body.transform.position, out snap, 6f))
                    body.transform.position = snap.position;
                if (ConVar.AI.useUnityNavmesh) _agent.enabled = true; // RustNav drives RustNavMeshAgent directly; an enabled Unity agent with no Unity mesh just logs errors
                var rustAgent = gameObject.GetComponent<Rust.Ai.Gen2.RustNavMeshAgent>();
                if (rustAgent == null) rustAgent = gameObject.AddComponent<Rust.Ai.Gen2.RustNavMeshAgent>();
                Nav = gameObject.GetComponent<BaseNavigator>();
                if (Nav == null) Nav = gameObject.AddComponent<BaseNavigator>();
                Nav.Init(body, rustAgent);
                Nav.MaxWaterDepth = 0.5f;
                Nav.SetNavMeshEnabled(true);
                Nav.PlaceOnNavMesh(0f);
                StateName = "travel";
                CratesStripped = 0; HauledUnits = 0; Fled = false;
                _target = null; _crate = null;
                _crates.Clear(); _visited.Clear();
                _stalls = 0; _stallCheckAt = 0f;
                _lastPump = UnityEngine.Time.realtimeSinceStartup;
                InvokeRandomized(Pump, 1f, 0.1f, 0.01f);
            }

            public void Detach()
            {
                CancelInvoke(Pump);
                if (Nav != null) { Nav.Stop(); Nav.SetNavMeshEnabled(false); }
                if (_agent != null) _agent.enabled = false;
                if (Body != null && !Body.IsDestroyed && Body.modelState != null && Body.modelState.ducked) SetDucked(false);
                UnityEngine.Object.Destroy(this);
            }

            private void OnDestroy() { CancelInvoke(Pump); } // invokes must not outlive a killed entity

            // 10 Hz: navigator think + ground glide + client-smooth MovePosition —
            // verbatim HostileDriver pump (the proven native-body movement recipe).
            private void Pump()
            {
                if (Body == null || Body.IsDestroyed || Body.IsDead()) { CancelInvoke(Pump); return; }
                if (Body.IsWounded()) { Body.Die(); return; } // connection-less bodies never tick wounded recovery
                if (Body.IsSleeping()) return;
                var now = UnityEngine.Time.realtimeSinceStartup;
                var delta = now - _lastPump;
                _lastPump = now;
                var before = Body.transform.position;
                Nav?.Think(delta);
                var p = Body.ServerPosition;
                RaycastHit ground;
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out ground, 4f, 10551296))
                {
                    var dy = ground.point.y - p.y;
                    if (Mathf.Abs(dy) > 0.03f)
                        p.y = Mathf.Abs(dy) > 0.5f ? ground.point.y : Mathf.MoveTowards(p.y, ground.point.y, 1.5f * delta);
                }
                var stepped = p - before;
                if (stepped.sqrMagnitude > 0.0001f)
                {
                    var flat = new Vector3(stepped.x, 0f, stepped.z);
                    if (_target == null && flat.sqrMagnitude > 0.001f)
                        Face(flat.normalized);
                    Body.MovePosition(p);
                }
                _thinkAcc += delta;
                if (_thinkAcc >= 0.25f) { _thinkAcc = 0f; Think(now); } // 4 Hz think
            }

            private void Think(float now)
            {
                if (!ConVar.AI.think) return;
                var self = Ff;
                if (self == null) return;

                // Time budget: overrun → head home with whatever's in the pockets.
                if (now > _budgetEndAt && StateName != "haul" && StateName != "depot" && StateName != "fight" && StateName != "flee")
                    StartHaul("time budget");

                // Attacked (scientists, players, hostiles, animals) — poll lastAttacker,
                // no hot hooks. Fight only per the 5A2-R gate AND above the flee floor;
                // a runner raids crates, he doesn't siege scientists.
                var att = Body.lastAttacker as BaseCombatEntity;
                if (att != null && !ReferenceEquals(att, Body) && !att.IsDestroyed && !att.IsDead() &&
                    UnityEngine.Time.time - Body.lastAttackedTime < 6f && StateName != "depot")
                {
                    // Heli/Bradley are never fight targets (5A2-R doctrine — rifles
                    // don't argue with those; QuietFox learned this at Airfield).
                    if (att is PatrolHelicopter || att is BradleyAPC)
                    { if (StateName != "flee") StartFlee(now); }
                    else if (_canFight && Body.health > _fleeFloor && self.FightWeapon(Body) != null &&
                        Vector3.Distance(Body.transform.position, att.transform.position) < 50f)
                    {
                        if (!ReferenceEquals(_target, att))
                        {
                            _target = att;
                            _engageStartAt = now;
                            _lastLosAt = now;
                            StateName = "fight";
                        }
                    }
                    else if (StateName != "flee" && StateName != "fight") StartFlee(now);
                }

                if (StateName == "fight") { FightTick(now); return; }

                // 5D self-guard (1 Hz): a runner must never end up working inside a
                // player's building privilege (monument crates can't be built on, but a
                // flee/stall path could stray) — leave for home the moment it happens.
                if (now >= _nextPrivCheckAt)
                {
                    _nextPrivCheckAt = now + 1f;
                    var priv = Body.GetBuildingPrivilege(true);
                    if (priv != null && !FakeFriends.IsBotOwnedPriv(priv) && StateName != "haul" && StateName != "depot")
                        StartHaul("player TC privilege");
                }

                switch (StateName)
                {
                    case "travel":
                        if (TravelTick(now, StagePos, 12f, BaseNavigator.NavigationSpeed.Fast))
                        {
                            ScanCrates();
                            StateName = "seek";
                        }
                        return;
                    case "seek":
                        SeekTick(now);
                        return;
                    case "to-crate":
                        ToCrateTick(now);
                        return;
                    case "loot":
                        LootTick(now);
                        return;
                    case "flee":
                        if (now >= _fleeUntil) { StateName = "haul"; _nextPathAt = 0f; }
                        else
                        {
                            TravelTick(now, HomePos, 4f, BaseNavigator.NavigationSpeed.Fast);
                            // Don't sprint a kilometre home at 30 hp with a pocket full
                            // of meds (live, 2026-07-24): once contact is broken for
                            // ~6 s, stop and patch up where you stand instead.
                            if (Pursued()) _clearSince = 0f;
                            else if (_clearSince <= 0f) _clearSince = now;
                            else if (now - _clearSince >= 6f && FindMed() != null)
                            {
                                Nav.Stop();
                                StateName = "medic"; _nextMedAt = 0f;
                                Ff?.DLog($"[run] {Body?.displayName} broke contact — patching up");
                            }
                        }
                        return;
                    case "medic":
                        MedicTick(now);
                        return;
                    case "haul":
                        if (TravelTick(now, HomePos, 3f, BaseNavigator.NavigationSpeed.Normal))
                        {
                            StateName = "depot";
                            _depotUntil = 0f; _depotStage = 0; _walkedIn = false; _walkRetried = false; _walkRetryAt = 0f;
                        }
                        return;
                    case "depot":
                        DepotTick(now);
                        return;
                }
            }

            // Leg-based long-haul travel: BaseNavigator paths ~150 m legs reliably on
            // the Animal bake (6P1); re-leg every 3 s, jitter on stall, abort-to-haul
            // when hopelessly stuck. Returns true on arrival.
            private bool TravelTick(float now, Vector3 goal, float arriveDist, BaseNavigator.NavigationSpeed speed)
            {
                var pos = Body.transform.position;
                var flatGoal = goal - pos; flatGoal.y = 0f;
                if (flatGoal.magnitude <= arriveDist) { Nav.Stop(); return true; }
                if (now >= _stallCheckAt)
                {
                    if (_stallCheckAt > 0f && (pos - _stallPos).magnitude < 2f)
                    {
                        _stalls++;
                        if (_stalls >= 6)
                        {
                            // Truly stuck. Mid-outbound → give up on the run; stuck on
                            // the way HOME → let the session-end teleport fallback catch
                            // it (the plugin's finish path handles both).
                            Ff?.AbortMonumentRun(BotId, "stuck en route");
                            return false;
                        }
                    }
                    else _stalls = 0;
                    _stallPos = pos;
                    _stallCheckAt = now + 12f;
                }
                if (now >= _nextPathAt)
                {
                    _nextPathAt = now + 3f;
                    var dir = flatGoal.normalized;
                    var legLen = Mathf.Min(120f, flatGoal.magnitude);
                    var leg = pos + dir * legLen;
                    if (_stalls > 0) // lateral jitter walks around navmesh pinches
                    {
                        var side = Vector3.Cross(Vector3.up, dir);
                        leg += side * UnityEngine.Random.Range(-30f, 30f);
                    }
                    leg.y = TerrainMeta.HeightMap.GetHeight(leg);
                    NavMeshHit hit;
                    if (NavSample(leg, out hit, 25f))
                        Nav.SetDestination(hit.position, speed, 0f, 0f);
                }
                return false;
            }

            // One Vis scan at arrival fills the run's crate list (6P2: crate_normal[_2]).
            private void ScanCrates()
            {
                _crates.Clear();
                var found = new List<LootContainer>();
                Vis.Entities(MonuCenter, _scanRadius, found);
                foreach (var lc in found)
                    if (lc != null && !lc.IsDestroyed &&
                        (lc.ShortPrefabName == "crate_normal" || lc.ShortPrefabName == "crate_normal_2"))
                        _crates.Add(lc);
            }

            private void SeekTick(float now)
            {
                if (CratesStripped >= MaxCratesPerRun || FreeSlots() < 3) { StartHaul("pockets full"); return; }
                LootContainer best = null;
                var bestD = float.MaxValue;
                var terrainType = TerrainAgentTypeId(Body.transform.position);
                foreach (var lc in _crates)
                {
                    if (lc == null || lc.IsDestroyed || _visited.Contains(lc.net?.ID.Value ?? 0)) continue;
                    if (lc.inventory == null || lc.inventory.itemList == null || lc.inventory.itemList.Count == 0) continue;
                    // Surface-only this phase (decision 0008): the crate must answer the
                    // Animal bake — Humanoid-only pockets wait for the re-type excursion.
                    NavMeshHit reach;
                    var filter = new NavMeshQueryFilter { agentTypeID = terrainType, areaMask = NavMesh.AllAreas };
                    if (!NavSampleTyped(lc.transform.position, out reach, 4f, filter)) continue;
                    var d = Vector3.Distance(Body.transform.position, lc.transform.position);
                    if (d < bestD) { bestD = d; best = lc; }
                }
                if (best == null) { StartHaul(CratesStripped > 0 ? "crates cleared" : "no reachable crates"); return; }
                _crate = best;
                StateName = "to-crate";
                _nextPathAt = 0f;
                _stallCheckAt = 0f; _stalls = 0;
                _crateReachFails = 0; _reachFailAt = 0f;
            }

            private void ToCrateTick(float now)
            {
                if (_crate == null || _crate.IsDestroyed) { StateName = "seek"; return; }
                var dist = Vector3.Distance(Body.transform.position, _crate.transform.position);
                // The navigator's walkable endpoint can sit meters short of the crate
                // (hangar-interior crates hang off the Animal bake — observed live at
                // Airfield as a 100↔115 m oscillation). Close enough → loot; a leg that
                // ends short too often → the crate is unreachable, skip it.
                var navDone = Nav != null && Nav.CurrentNavigationType == BaseNavigator.NavigationType.None;
                if (navDone && dist <= 7f)
                {
                    Nav.Stop();
                    SetDucked(true);
                    _lootUntil = now + UnityEngine.Random.Range(3f, 5f);
                    StateName = "loot";
                    return;
                }
                if (navDone && now >= _reachFailAt)
                {
                    _reachFailAt = now + 5f; // count once per settled leg, not per think
                    if (++_crateReachFails >= 3)
                    {
                        _visited.Add(_crate.net?.ID.Value ?? 0);
                        _crate = null;
                        _crateReachFails = 0;
                        StateName = "seek";
                        return;
                    }
                }
                if (TravelTick(now, _crate.transform.position, 2.5f, BaseNavigator.NavigationSpeed.Normal))
                {
                    SetDucked(true);
                    _lootUntil = now + UnityEngine.Random.Range(3f, 5f);
                    StateName = "loot";
                }
            }

            private void LootTick(float now)
            {
                if (_crate == null || _crate.IsDestroyed) { SetDucked(false); StateName = "seek"; return; }
                if (now < _lootUntil) return;
                // 6P2 recipe: strip server-side, then Kill(Gib) the shell — the vanilla
                // emptied path; the SpawnGroup link hands it to the respawn handler.
                var units = 0;
                if (_crate.inventory != null)
                {
                    var items = new List<Item>(_crate.inventory.itemList); // MoveToContainer mutates
                    foreach (var it in items)
                        if (it != null && it.MoveToContainer(Body.inventory.containerMain)) units += it.amount;
                }
                _visited.Add(_crate.net?.ID.Value ?? 0);
                if (!_crate.IsDestroyed) _crate.Kill(BaseNetworkable.DestroyMode.Gib);
                CratesStripped++;
                HauledUnits += units;
                _crate = null;
                SetDucked(false);
                StateName = "seek";
            }

            // Stand-and-fight per 5A2-R: stationary (no chasing scientists into their
            // spawn), crouch at range, native-fire; caps + flee valve end it.
            private void FightTick(float now)
            {
                var self = Ff;
                if (_target == null || _target.IsDestroyed || _target.IsDead())
                { _target = null; SetDucked(false); ResumeAfterFight(now); return; }
                if (Body.health <= _fleeFloor) { _target = null; StartFlee(now); return; } // flee valve
                var aimPoint = _target is BasePlayer bp && !bp.IsWounded() && bp.eyes != null
                    ? bp.eyes.position - Vector3.up * 0.15f : _target.CenterPoint();
                var dist = Vector3.Distance(Body.transform.position, _target.transform.position);
                var visible = Body.IsVisible(aimPoint);
                if (visible) _lastLosAt = now;
                if (now - _engageStartAt > EngageTimeout || now - _lastLosAt > NoLosTimeout || dist > 60f)
                { _target = null; StartHaul("disengaged"); return; }
                Nav.Stop();
                if (Body.modelState != null && Body.modelState.ducked != dist > 12f) SetDucked(dist > 12f);
                var dir = (aimPoint - Body.eyes.position).normalized;
                Face(dir);
                Body.SendNetworkUpdate();
                if (!visible || now < _nextFireAt) return;
                var gun = self?.FightWeapon(Body);
                if (gun == null) { _target = null; StartFlee(now); return; }
                if (gun.primaryMagazine.contents <= 0)
                {
                    if (!self.FightReload(Body, gun)) { _target = null; StartFlee(now); return; } // dry — run for it
                    _nextFireAt = now + 2.5f;
                    return;
                }
                float dmg, interval;
                FightWeaponProfile(gun, out dmg, out interval);
                self.NativeShot(Body, gun, _target, aimPoint, dist, _skill, dmg * _dmgMul);
                _nextFireAt = now + interval * Mathf.Lerp(1.35f, 0.75f, _skill) * UnityEngine.Random.Range(0.85f, 1.25f);
            }

            private void ResumeAfterFight(float now)
            {
                // Won (or lost track of) the fight: keep working if there's monument
                // left in the visit, else take the win home.
                if (CratesStripped >= MaxCratesPerRun || FreeSlots() < 3 || now > _budgetEndAt) StartHaul("after the fight");
                else { StateName = "seek"; _nextPathAt = 0f; }
            }

            private void StartFlee(float now)
            {
                Fled = true;
                _target = null;
                SetDucked(false);
                StateName = "flee";
                _fleeUntil = now + UnityEngine.Random.Range(18f, 28f);
                _clearSince = 0f;
                _nextPathAt = 0f; _stallCheckAt = 0f; _stalls = 0;
            }

            // ── Field medicine (2026-07-24) ─────────────────────────────────
            private float _clearSince, _nextMedAt;

            private bool Pursued()
            {
                if (Body.lastAttacker != null && !Body.lastAttacker.IsDestroyed && UnityEngine.Time.time - Body.lastAttackedTime < 5f) return true;
                return Ff != null && Ff.NearestScientist(Body.transform.position, 40f) != null;
            }

            private static float MedHeal(string shortname)
            {
                switch (shortname)
                {
                    case "largemedkit": return 60f;
                    case "syringe.medical": return 35f;
                    case "bandage": return 20f;
                    default: return 0f;
                }
            }

            private Item FindMed()
            {
                Item best = null; var bestHeal = 0f;
                var inv = Body.inventory;
                if (inv == null) return null;
                for (var c = 0; c < 2; c++)
                {
                    var container = c == 0 ? inv.containerBelt : inv.containerMain;
                    if (container?.itemList == null) continue;
                    foreach (var it in container.itemList)
                    {
                        var heal = MedHeal(it?.info?.shortname);
                        if (heal > bestHeal) { bestHeal = heal; best = it; }
                    }
                }
                return best;
            }

            // Stand still, burn real meds (biggest first) every ~3 s until 75% hp,
            // then get back to the run if budget and the crate cap allow — else haul.
            // Fresh damage means they found us: flee again.
            private void MedicTick(float now)
            {
                if (UnityEngine.Time.time - Body.lastAttackedTime < 2f) { StartFlee(now); return; }
                var maxHp = Body.MaxHealth();
                if (Body.health >= maxHp * 0.75f)
                {
                    if (now < _budgetEndAt && CratesStripped < 6)
                    {
                        Fled = false;
                        StateName = "seek"; _crate = null;
                        _nextPathAt = 0f; _stallCheckAt = 0f; _stalls = 0;
                        Ff?.DLog($"[run] {Body?.displayName} patched up ({Body.health:0}hp) — back to work at {MonuName}");
                    }
                    else StartHaul("patched up, but the run is done");
                    return;
                }
                if (now < _nextMedAt) return;
                var med = FindMed();
                if (med == null) { StartHaul("hurt and out of meds"); return; }
                var heal = MedHeal(med.info.shortname);
                Ff?.DLog($"[run] {Body?.displayName} used a {med.info.shortname} ({Body.health:0} → {Mathf.Min(maxHp, Body.health + heal):0}hp)");
                med.UseItem(1);
                Body.Heal(heal);
                Body.SendNetworkUpdate();
                _nextMedAt = now + 3f;
            }

            private void StartHaul(string why)
            {
                if (StateName == "haul" || StateName == "depot") return;
                SetDucked(false);
                _target = null; _crate = null;
                StateName = "haul";
                _nextPathAt = 0f; _stallCheckAt = 0f; _stalls = 0;
                Ff?.DLog($"[run] {Body?.displayName} heading home from {MonuName}: {why} ({CratesStripped} crates, {HauledUnits} units)");
            }

            // IN-3.4 depot walk: 0 not started, 1 walking in, 2 dwelling inside, 3 walking out
            private int _depotStage;
            private bool _walkedIn, _walkRetried;
            private float _walkRetryAt;
            private Vector3 _walkGoal, _walkProgressPos;
            private float _walkStart, _walkProgressAt;
            private List<Door> _walkDoors;
            private Door _passDoor;

            private void DepotHopIn(float now, string reason)
            {
                if (reason != null) Ff?.DriverWalkFallback(Body?.displayName, reason);
                FakeFriends.DriverCloseDoor(ref _passDoor);
                if (Nav != null && Nav.Agent != null) Nav.Agent.canOpenDoors = false;
                Nav.Stop();
                Ff?.HostileEnterBase(BotId, Body); // same door-swing + duck-inside visit residents use
                SetDucked(true);
                _depotUntil = now + UnityEngine.Random.Range(3f, 5f);
                _depotStage = 2; _walkedIn = false;
            }

            private void DepotTick(float now)
            {
                var pos = Body.transform.position;
                if (_depotStage == 0)
                {
                    if (_walkRetryAt > 0f && now < _walkRetryAt) return; // IN-4.1 retry gate
                    string why = "noPlugin";
                    var goal = Ff != null ? Ff.DriverInteriorGoal(BotId, pos, "storage", out why) : Vector3.zero;
                    if (goal != Vector3.zero && Nav.Agent != null)
                    {
                        Nav.Agent.canOpenDoors = true;
                        if (Nav.SetDestination(goal, BaseNavigator.NavigationSpeed.Slow, 0f, 0f))
                        {
                            _walkGoal = goal; _walkDoors = Ff.DriverBaseDoors(BotId); _passDoor = null;
                            _walkStart = now; _walkProgressPos = pos; _walkProgressAt = now;
                            _depotStage = 1; _walkedIn = true;
                            Ff.DriverWalkStarted(Body.displayName, Vector3.Distance(pos, goal));
                            return;
                        }
                        if (!_walkRetried && FakeFriends.NavSaveInFlight()) { _walkRetried = true; _walkRetryAt = now + 3f; return; } // IN-4.1
                        why = _walkRetried ? "noPathIn-afterSave" : "noPathIn";
                    }
                    DepotHopIn(now, why);
                    return;
                }
                if (_depotStage == 1)
                {
                    FakeFriends.DriverDoorPass(pos, _walkDoors, ref _passDoor);
                    if (Vector3.Distance(pos, _walkGoal) <= 1.2f)
                    {
                        FakeFriends.DriverCloseDoor(ref _passDoor);
                        Nav.Stop();
                        SetDucked(true);
                        _depotUntil = now + UnityEngine.Random.Range(3f, 5f);
                        _depotStage = 2;
                        Ff?.DLog($"[walk] {Body.displayName} is inside ({Vector3.Distance(pos, _walkGoal):0.0} m from the box) — stashing the haul");
                        return;
                    }
                    if ((pos - _walkProgressPos).magnitude >= 1f) { _walkProgressPos = pos; _walkProgressAt = now; }
                    if (now - _walkProgressAt > 4f) DepotHopIn(now, "stall");
                    else if (now - _walkStart > 25f) DepotHopIn(now, "timeout");
                    return;
                }
                if (_depotStage == 2)
                {
                    if (now < _depotUntil) return;
                    if (_walkedIn)
                    {
                        Ff?.DepositRunnerTakeFor(BotId); // stash inside, then walk back out before the run closes
                        SetDucked(false);
                        if (Nav.SetDestination(HomePos, BaseNavigator.NavigationSpeed.Slow, 0f, 0f))
                        {
                            _passDoor = null; _walkStart = now; _walkProgressPos = pos; _walkProgressAt = now;
                            _depotStage = 3;
                            return;
                        }
                        Ff?.DriverWalkFallback(Body?.displayName, "noPathOut");
                        if (Nav != null && Nav.Agent != null) Nav.Agent.canOpenDoors = false;
                        Ff?.FinishMonumentRun(BotId, true); // deposited already; exit hop inside FinishMonumentRun is skipped → hop here
                        return;
                    }
                    Ff?.FinishMonumentRun(BotId); // deposits, credits career, detaches this driver
                    return;
                }
                // stage 3: walking out
                FakeFriends.DriverDoorPass(pos, _walkDoors, ref _passDoor);
                var done = Vector3.Distance(pos, HomePos) <= 1.5f;
                if (!done)
                {
                    if ((pos - _walkProgressPos).magnitude >= 1f) { _walkProgressPos = pos; _walkProgressAt = now; }
                    if (now - _walkProgressAt > 4f || now - _walkStart > 25f)
                    {
                        Ff?.DriverWalkFallback(Body?.displayName, now - _walkProgressAt > 4f ? "stallOut" : "timeoutOut");
                        Ff?.HostileExitBase(BotId, Body);
                        done = true;
                    }
                    else return;
                }
                else Ff?.DriverWalkDone(Body.displayName);
                FakeFriends.DriverCloseDoor(ref _passDoor);
                if (Nav != null && Nav.Agent != null) Nav.Agent.canOpenDoors = false;
                Ff?.FinishMonumentRun(BotId, true); // already deposited and outside: credits + story + detach
            }

            private int FreeSlots()
            {
                var c = Body.inventory?.containerMain;
                return c == null ? 0 : c.capacity - c.itemList.Count;
            }

            private void Face(Vector3 dir)
            {
                if (dir == Vector3.zero) return;
                var rot = Quaternion.LookRotation(dir, Vector3.up);
                Body.eyes.rotation = rot;
                Body.viewAngles = rot.eulerAngles;
                Body.ServerRotation = rot;
            }

            private void SetDucked(bool v)
            {
                if (Body == null || Body.IsDestroyed || Body.modelState == null) return;
                Body.modelState.ducked = v;
                Body.SendNetworkUpdate();
            }
        }

        // ---- Phase 6 plugin-side wiring ---------------------------------------------
        private static bool IsRunnerP(BotRecord rec) => rec?.Identity?.Persona == "MonumentRunner";
        private readonly HashSet<int> _runnerActive = new HashSet<int>();
        private readonly Dictionary<int, float> _nextRunAt = new Dictionary<int, float>(); // realtime; in-memory (reload = fresh cooldown)
        private const int RunnerCrateCareer = 150; // career credit per crate — paces a ~5-crate run like ~25 min of abstract production

        private bool GroupHasActiveRunner(int gid)
        {
            foreach (var id in _runnerActive)
            {
                BotRecord r;
                if (_bots.TryGetValue(id, out r) && r.Identity.GroupId == gid) return true;
            }
            return false;
        }

        // SchedulerTick (30 s) hook: an online runner starts a run when observed
        // (Stage-B switch — unwatched sessions leave it to the abstract profile),
        // cooled down, and not busy retreating/fighting/logging off.
        private void MaybeStartMonumentRun(BotRecord rec)
        {
            var botId = rec.Identity.BotId;
            if (_runnerActive.Contains(botId) || rec.State.HomePos == null) return;
            var bot = GetLive(botId);
            if (bot == null || bot.IsSleeping() || bot.IsDead() || bot.IsWounded()) return;
            WanderState ws;
            if (_wander.TryGetValue(botId, out ws) && (ws.Retreating || ws.GoingHome || ws.FightTarget != null)) return;
            float nra;
            if (_nextRunAt.TryGetValue(botId, out nra) && Time.realtimeSinceStartup < nra) return;
            if (!IsObserved(bot.transform.position)) return;
            StartMonumentRun(rec, bot, null);
        }

        private string StartMonumentRun(BotRecord rec, BasePlayer bot, string filter)
        {
            var botId = rec.Identity.BotId;
            MonumentInfo m;
            Vector3 stage;
            if (!PickRunMonument(rec, filter, out m, out stage))
            {
                _nextRunAt[botId] = Time.realtimeSinceStartup + 600f; // nothing viable — re-check in 10 min
                return "no viable monument (navmesh + crates + stage point) in range";
            }
            _wander.Remove(botId); // the driver owns movement now
            _work.Remove(botId);
            var d = bot.GetComponent<RunnerDriver>();
            if (d == null) d = bot.gameObject.AddComponent<RunnerDriver>();
            var season = Seasoning(rec);
            var caution = Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f);
            var scanRadius = Mathf.Clamp(m.Bounds.extents.magnitude * 0.7f, 40f, 180f);
            d.Init(bot, botId, rec.State.HomePos.V, m.transform.position, stage, scanRadius, MonuName(m),
                (rec.Identity.Traits?.SkillLevel ?? 0.5f) + 0.25f * season, 1f + 0.3f * season,
                CanFight(rec, bot), Mathf.Lerp(25f, 45f, caution), _config.MonumentRunBudgetMinutes * 60f);
            var gun = FightWeapon(bot); // shoulder the kit weapon for the road
            var gi = gun != null ? gun.GetItem() : null;
            if (gi != null) bot.UpdateActiveItem(gi.uid);
            _runnerActive.Add(botId);
            _nextRunAt[botId] = Time.realtimeSinceStartup + RunCooldownSeconds(); // provisional; refreshed at finish
            Story("run", $"{rec.Identity.Name} set out for {MonuName(m)}");
            DLog($"[run] {rec.Identity.Name} → {MonuName(m)} ({Vector3.Distance(rec.State.HomePos.V, m.transform.position):0}m out, stage {stage})");
            return null;
        }

        private float RunCooldownSeconds() =>
            Mathf.Max(300f, _config.MonumentRunCooldownMinutes * 60f * UnityEngine.Random.Range(0.5f, 1.5f));

        // 6E monument selection: navmesh + crates, distance-discounted, risk-checked
        // against the worn kit (a naked runner doesn't stroll into 30 scientists).
        private bool PickRunMonument(BotRecord rec, string filter, out MonumentInfo best, out Vector3 stage)
        {
            best = null;
            stage = Vector3.zero;
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null || rec.State.HomePos == null) return false;
            var home = rec.State.HomePos.V;
            // Terrain-bake reference MUST be a known off-monument point (the home
            // anchor is validated away from monuments). A monument-adjacent first call
            // would poison the session cache with the Humanoid bake (typeId 0 sits
            // first in the settings enumeration) and fail every stage sample after it.
            var terrainType = TerrainAgentTypeId(home);
            var tier = rec.Progression == null ? 0 : (rec.Progression.GearTier >= 0 ? rec.Progression.GearTier : rec.Progression.KitTier);
            var scored = new List<KeyValuePair<float, MonumentInfo>>();
            var lcBuf = new List<LootContainer>();
            var sciBuf = new List<BaseCombatEntity>(); // both scientist gens via IsScientist
            var diag = new System.Text.StringBuilder(); // populated for the no-candidate PLog
            foreach (var m in monuments)
            {
                if (m == null || !m.HasNavmesh || m.IsSafeZone || m.IsOilRig()) continue;
                var dist = Vector3.Distance(home, m.transform.position);
                if (!string.IsNullOrEmpty(filter) && MonuName(m).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // A NAMED monument (ff.run <bot> <monument> / probe harness) is an
                // explicit order — range only vetoes organic selection (2026-07-24:
                // WildSadie67's home sits >1500 m from every qualifying monument).
                if (string.IsNullOrEmpty(filter) && dist > _config.MonumentRunRange) { diag.Append($"{MonuName(m)}:{dist:0}m OUT-OF-RANGE; "); continue; }
                var scanR = Mathf.Clamp(m.Bounds.extents.magnitude * 0.7f, 40f, 180f);
                lcBuf.Clear();
                Vis.Entities(m.transform.position, scanR, lcBuf);
                var crates = 0;
                foreach (var lc in lcBuf)
                    if (lc != null && !lc.IsDestroyed && lc.inventory != null && lc.inventory.itemList != null && lc.inventory.itemList.Count > 0 &&
                        (lc.ShortPrefabName == "crate_normal" || lc.ShortPrefabName == "crate_normal_2")) crates++;
                if (crates == 0) { diag.Append($"{MonuName(m)}:{dist:0}m NO-CRATES; "); continue; }
                sciBuf.Clear();
                Vis.Entities(m.transform.position, 150f, sciBuf);
                var sci = 0;
                foreach (var s in sciBuf) if (s != null && !s.IsDestroyed && !s.IsDead() && IsScientist(s)) sci++;
                var excessSci = Mathf.Max(0, sci - (4 + 8 * tier)); // what the kit can't argue with
                var score = crates / (1f + dist / 400f) / (1f + excessSci * 0.2f);
                diag.Append($"{MonuName(m)}:{dist:0}m {crates}cr {sci}sci s={score:0.00}; ");
                scored.Add(new KeyValuePair<float, MonumentInfo>(score, m));
            }
            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (var kv in scored) // best first; a monument with no stage point falls through to the next
            {
                var s = StageForMonument(kv.Value, terrainType);
                if (s == Vector3.zero) { diag.Append($"{MonuName(kv.Value)} NO-STAGE-POINT; "); continue; }
                best = kv.Value;
                stage = s;
                return true;
            }
            PLog($"[run] {rec.Identity.Name}: no viable monument. home {home}, range {_config.MonumentRunRange:0}, terrainType {terrainType} — {diag}");
            return false;
        }

        // A terrain-navmesh walk-in point just outside the bounds (the 6P1 staging
        // logic, promoted from the probe). terrainType arrives from the caller's
        // known-safe off-monument reference — never probed near the monument here.
        private Vector3 StageForMonument(MonumentInfo m, int terrainType)
        {
            for (var dgr = 0; dgr < 8; dgr++)
            {
                var dir = Quaternion.Euler(0f, dgr * 45f, 0f) * Vector3.forward;
                var edge = m.ClosestPointOnBounds(m.transform.position + dir * 4000f);
                var cpt = edge + new Vector3(dir.x, 0f, dir.z).normalized * 25f;
                if (TerrainMeta.HeightMap.GetHeight(cpt) < 0.5f) continue; // water side
                cpt = GroundSnap(cpt);
                var tf = new NavMeshQueryFilter { agentTypeID = terrainType, areaMask = NavMesh.AllAreas };
                NavMeshHit sh;
                if (NavSampleTyped(cpt, out sh, 6f, tf)) return sh.position;
            }
            return Vector3.zero;
        }

        // Depot complete: empty the pockets into the base stash with career credit
        // (physical goods are the economy — decision 0005; career paces the base/kit
        // ladder like every other persona).
        // IN-3.4: a runner that walked in deposits inside (before the walk out), so the
        // finish must not deposit again or teleport him.
        public void DepositRunnerTakeFor(int botId)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return;
            var body = GetLive(botId);
            if (body == null || body.IsDestroyed) return;
            var d = body.GetComponent<RunnerDriver>();
            DepositRunnerTake(body, rec, d != null ? d.CratesStripped : 0);
        }

        public void FinishMonumentRun(int botId, bool walkedOut = false)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) { DetachRunnerDriver(botId); return; }
            var body = GetLive(botId);
            var d = body != null ? body.GetComponent<RunnerDriver>() : null;
            var crates = d != null ? d.CratesStripped : 0;
            var units = d != null ? d.HauledUnits : 0;
            var fled = d != null && d.Fled;
            var monu = d != null ? d.MonuName : "?";
            if (body != null && !body.IsDestroyed && !walkedOut)
            {
                DepositRunnerTake(body, rec, crates);
                HostileExitBase(botId, body); // step back out of the cabin, door swinging
            }
            DetachRunnerDriver(botId);
            _nextRunAt[botId] = Time.realtimeSinceStartup + RunCooldownSeconds();
            if (crates > 0)
                Story("run", fled
                    ? $"{rec.Identity.Name} fled {monu} but got away with the loot ({crates} crates)"
                    : $"{rec.Identity.Name} hauled the take home from {monu} ({crates} crates)");
            else if (fled) Story("run", $"{rec.Identity.Name} came back from {monu} empty-handed");
            DLog($"[run] {rec.Identity.Name} finished {monu}: {crates} crates, {units} units{(fled ? ", fled" : "")}.");
        }

        // Mid-run failure (stuck, etc.): drop the driver where it stands; the wander
        // system takes back over and the session flow (or retreat) brings him home.
        // Pocketed loot rides along and deposits at the next run's depot.
        public void AbortMonumentRun(int botId, string why)
        {
            BotRecord rec;
            _bots.TryGetValue(botId, out rec);
            DLog($"[run] {(rec != null ? rec.Identity.Name : botId.ToString())}: run aborted — {why}.");
            DetachRunnerDriver(botId);
            _nextRunAt[botId] = Time.realtimeSinceStartup + RunCooldownSeconds();
            if (rec != null) Story("run", $"{rec.Identity.Name} gave up on the monument run ({why})");
        }

        private void DepositRunnerTake(BasePlayer body, BotRecord rec, int crates)
        {
            if (body?.inventory?.containerMain == null) return;
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) return; // no base yet — keep the loot in the pockets
            var buckets = FindBaseContainers(b);
            var box = PickContainer(buckets, "storage");
            if (box == null) return;
            var moved = 0;
            long units = 0;
            var items = new List<Item>(body.inventory.containerMain.itemList); // MoveToContainer mutates
            foreach (var item in items)
            {
                if (item == null) continue;
                var amt = item.amount;
                if (item.MoveToContainer(box.inventory)) { moved++; units += amt; }
            }
            // v0.20.0: the hauled units no longer credit career (time-based); the
            // per-crate bonus stays — real crates are the runner's "kill bounty".
            if (crates > 0)
            {
                var ge = Econ(rec.Identity.GroupId);
                if (ge != null)
                {
                    ge.CareerProduced += (long)crates * RunnerCrateCareer;
                    MarkEconomyDirty();
                }
            }
            if (units > 0) DLog($"[run] deposited {units} looted unit(s) from {crates} crate(s)");
        }

        private void DetachRunnerDriver(int botId)
        {
            _runnerActive.Remove(botId);
            var body = GetLive(botId);
            if (body == null || body.IsDestroyed) return;
            var d = body.GetComponent<RunnerDriver>();
            if (d != null)
            {
                d.Detach();
                body.UpdateActiveItem(default(ItemId)); // holster
            }
        }

        [ConsoleCommand("ff.run")]
        private void CmdRun(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0));
            if (rec == null) { arg.ReplyWith("Usage: ff.run <bot> [monument name filter] — forces a monument run (bot must be Online)."); return; }
            if (!IsRunnerP(rec)) { arg.ReplyWith($"{rec.Identity.Name} is a {rec.Identity.Persona}, not a MonumentRunner."); return; }
            if (_runnerActive.Contains(rec.Identity.BotId)) { arg.ReplyWith($"{rec.Identity.Name} is already on a run (ff.run.state)."); return; }
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null || bot.IsSleeping() || rec.State.Status != "Online")
            { arg.ReplyWith($"{rec.Identity.Name} is not online — 'ff.session start {rec.Identity.Name}' first."); return; }
            _nextRunAt.Remove(rec.Identity.BotId); // harness bypasses the cooldown + observation gate
            var err = StartMonumentRun(rec, bot, arg.GetString(1));
            arg.ReplyWith(err == null ? $"{rec.Identity.Name} is heading out — watch ff.run.state." : $"Run not started: {err}");
        }

        [ConsoleCommand("ff.run.state")]
        private void CmdRunState(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_runnerActive.Count == 0) { arg.ReplyWith("No monument runs in progress."); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var id in _runnerActive)
            {
                BotRecord rec;
                var body = GetLive(id);
                var d = body != null ? body.GetComponent<RunnerDriver>() : null;
                if (!_bots.TryGetValue(id, out rec) || d == null) { sb.AppendLine($"#{id}: (no driver?)"); continue; }
                sb.AppendLine($"{rec.Identity.Name}: {d.StateName} @ {d.MonuName} — hp {body.health:0}, {d.CratesStripped} crates, {d.HauledUnits} units, " +
                    $"{Vector3.Distance(body.transform.position, d.MonuCenter):0}m from monument, {Vector3.Distance(body.transform.position, d.HomePos):0}m from home, navType {d.Nav?.CurrentNavigationType}");
            }
            arg.ReplyWith(sb.ToString().TrimEnd());
        }

        // ---- Phase 6 end-to-end probe: one real monument run, observed to the log ----
        // One-shot, marker-gated (data/FakeFriends/probe_run.json): wakes (or uses) the
        // first MonumentRunner identity, force-starts a run (bypassing the observation
        // gate + cooldown, exactly what ff.run does), then samples state every 10 s for
        // up to 12 min. The bot is a real resident doing his job — nothing is killed or
        // cleaned up; the session hands back to the scheduler (probe_h55 pattern).
        // ETA: 30 s arm + session start + a real run (travel is minutes — be patient).
        private string ProbeRunMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_run.json");

        private void ProbeRun()
        {
            // Marker doubles as config: {"bot":"QuietFox","monument":"Airfield"} —
            // both optional (default: first live runner, best-scored monument).
            string wantBot = null, wantMonu = null;
            try
            {
                var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(System.IO.File.ReadAllText(ProbeRunMarker));
                if (cfg != null) { cfg.TryGetValue("bot", out wantBot); cfg.TryGetValue("monument", out wantMonu); }
            }
            catch { }
            try { System.IO.File.Delete(ProbeRunMarker); } catch { }
            try
            {
                BotRecord pick = null;
                if (!string.IsNullOrEmpty(wantBot)) pick = FindBot(wantBot);
                if (pick != null && !IsRunnerP(pick)) { PLog($"[probe:run] {pick.Identity.Name} is not a MonumentRunner — aborted."); return; }
                if (pick == null)
                    foreach (var rec in _bots.Values)
                        if (IsRunnerP(rec) && !IsOut(rec)) { pick = rec; if (rec.State.Status == "Online") break; }
                if (pick == null) { PLog("[probe:run] no MonumentRunner identity on the roster — aborted."); return; }
                if (_runnerActive.Contains(pick.Identity.BotId)) { PLog($"[probe:run] {pick.Identity.Name} is already mid-run — aborted (never re-Init a live driver)."); return; }
                if (pick.State.Status != "Online")
                {
                    PLog($"[probe:run] starting a session for {pick.Identity.Name} (was {pick.State.Status})");
                    StartSession(pick);
                    if (pick.State.Status != "Online") { PLog($"[probe:run] session did not open (status {pick.State.Status}) — aborted."); return; }
                }
                var bot = GetLive(pick.Identity.BotId);
                if (bot == null) { PLog("[probe:run] no live body after session start — FAIL."); return; }
                _nextRunAt.Remove(pick.Identity.BotId);
                var err = StartMonumentRun(pick, bot, wantMonu);
                if (err != null) { PLog($"[probe:run] run not started: {err} — FAIL."); return; }
                var botId = pick.Identity.BotId;
                var name = pick.Identity.Name;
                var samples = 0;
                Oxide.Plugins.Timer watchT = null;
                watchT = timer.Repeat(10f, 72, () =>
                {
                    try
                    {
                        samples++;
                        var b2 = GetLive(botId);
                        if (b2 == null || b2.IsDestroyed)
                        { PLog($"[probe:run] t={samples * 10}s body gone (died mid-run — see story log). Probe over."); watchT?.Destroy(); return; }
                        var d = b2.GetComponent<RunnerDriver>();
                        if (d == null)
                        {
                            PLog($"[probe:run] t={samples * 10}s run ENDED — driver detached (finished or aborted; see [run] log lines). VERDICT above the last state lines.");
                            watchT?.Destroy();
                            return;
                        }
                        PLog($"[probe:run] t={samples * 10}s {name}: {d.StateName} @ {d.MonuName}, hp {b2.health:0}, {d.CratesStripped} crates, {d.HauledUnits} units, {Vector3.Distance(b2.transform.position, d.MonuCenter):0}m→monu, {Vector3.Distance(b2.transform.position, d.HomePos):0}m→home, navType {d.Nav?.CurrentNavigationType}");
                        if (samples >= 72) { PLog("[probe:run] watch window closed with the run still going — check ff.run.state / story log later."); watchT?.Destroy(); }
                    }
                    catch (Exception e) { PLog($"[probe:run] EXCEPTION in watch: {e.Message}"); watchT?.Destroy(); }
                });
            }
            catch (Exception e) { PLog($"[probe:run] EXCEPTION: {e.Message}"); }
        }

        // ---- Phase 6 gate probe 6P1: monument navmesh + terrain→monument seam -------
        // One-shot, marker-gated (data/FakeFriends/probe_monunav.json). TerrainAgentTypeId
        // caches the off-monument 'Animal' bake; this probe answers what lives INSIDE
        // monuments (Phase 0: the Humanoid bake is monument-only) and whether ONE grafted
        // agent can path across the terrain→monument seam, or the RunnerDriver needs
        // per-surface re-typing / a seam handoff. Decides the Phase 6 movement
        // architecture (→ decision 0008). ETA: 30 s arm + ~5 s census + ≤120 s walk legs.
        private string ProbeMonuNavMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_monunav.json");

        private static string MonuName(MonumentInfo m)
        {
            var n = m.displayPhrase.english;
            return string.IsNullOrEmpty(n) ? m.name : n.Trim();
        }

        private void ProbeMonuNav()
        {
            try { System.IO.File.Delete(ProbeMonuNavMarker); } catch { }
            BasePlayer body = null;
            try
            {
                Vector3 terrainRef = Vector3.zero;
                foreach (var rec in _bots.Values)
                    if (rec.State.HomePos != null) { terrainRef = rec.State.HomePos.V; break; }
                if (terrainRef == Vector3.zero) { PLog("[probe:monunav] no settled home for a terrain reference — aborted."); return; }
                var terrainType = TerrainAgentTypeId(terrainRef);

                // 1) every bake the server holds (5P2 doctrine: NEVER guess typeIDs)
                var settings = new List<NavMeshBuildSettings>();
                for (var i = 0; i < NavMesh.GetSettingsCount(); i++) settings.Add(NavMesh.GetSettingsByIndex(i));
                foreach (var s in settings)
                    PLog($"[probe:monunav] bake: typeId {s.agentTypeID} radius {s.agentRadius:0.##} height {s.agentHeight:0.##}{(s.agentTypeID == terrainType ? "  ← terrain ('Animal') bake" : "")}");

                // 2) monument census: navmesh flag + live scientist count
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments == null || monuments.Count == 0) { PLog("[probe:monunav] no monuments listed — aborted."); return; }
                var cands = new List<MonumentInfo>();
                var sciCount = new Dictionary<MonumentInfo, int>();
                foreach (var m in monuments)
                {
                    if (m == null) continue;
                    var sci = new List<ScientistNPC>();
                    Vis.Entities(m.transform.position, 150f, sci);
                    var alive = 0;
                    foreach (var s in sci) if (s != null && !s.IsDestroyed && !s.IsDead()) alive++;
                    PLog($"[probe:monunav] monument: {MonuName(m)} pos {m.transform.position} navmesh {m.HasNavmesh} safezone {m.IsSafeZone} scientists {alive}");
                    if (m.HasNavmesh && !m.IsSafeZone) { cands.Add(m); sciCount[m] = alive; }
                }
                if (cands.Count == 0) { PLog("[probe:monunav] no HasNavmesh non-safezone monuments — aborted."); return; }
                // scientists first (they prove the Humanoid bake + give 6P2 a stage), then nearest
                cands.Sort((a, b) =>
                {
                    var d = sciCount[b].CompareTo(sciCount[a]);
                    if (d != 0) return d;
                    return Vector3.Distance(a.transform.position, terrainRef).CompareTo(Vector3.Distance(b.transform.position, terrainRef));
                });
                if (cands.Count > 3) cands.RemoveRange(3, cands.Count - 3);

                // 3) interior sampling: which typeIDs answer inside each candidate
                MonumentInfo seamM = null;
                var terrainPts = new List<Vector3>();          // interior pts answering the terrain typeID
                var monuPts = new List<Vector3>();             // interior pts answering some OTHER typeID
                var monuType = int.MinValue;                   // that other typeID (first seen)
                foreach (var m in cands)
                {
                    var e = m.Bounds.extents;
                    var c = m.Bounds.center;
                    var offs = e.magnitude > 15f
                        ? new[] { Vector3.zero, new Vector3(e.x * 0.35f, 0f, 0f), new Vector3(-e.x * 0.35f, 0f, 0f), new Vector3(0f, 0f, e.z * 0.35f), new Vector3(0f, 0f, -e.z * 0.35f) }
                        : new[] { Vector3.zero, new Vector3(12f, 0f, 0f), new Vector3(-12f, 0f, 0f), new Vector3(0f, 0f, 12f), new Vector3(0f, 0f, -12f) };
                    var mTerrainPts = new List<Vector3>();
                    var mMonuPts = new List<Vector3>();
                    var mMonuType = int.MinValue;
                    for (var j = 0; j < offs.Length; j++)
                    {
                        var pt = GroundSnap(m.transform.TransformPoint(c + offs[j]));
                        var ids = "";
                        foreach (var s in settings)
                        {
                            var filter = new NavMeshQueryFilter { agentTypeID = s.agentTypeID, areaMask = NavMesh.AllAreas };
                            NavMeshHit hit;
                            if (!NavSampleTyped(pt, out hit, 6f, filter)) continue;
                            ids += (ids.Length > 0 ? ", " : "") + s.agentTypeID;
                            if (s.agentTypeID == terrainType) mTerrainPts.Add(hit.position);
                            else { mMonuPts.Add(hit.position); if (mMonuType == int.MinValue) mMonuType = s.agentTypeID; }
                        }
                        NavMeshHit fl;
                        var flOk = NavSample(pt, out fl, 6f);
                        PLog($"[probe:monunav] {MonuName(m)} pt{j} {pt}: typed [{(ids.Length > 0 ? ids : "NONE")}] filterless {flOk}");
                    }
                    if (seamM == null && (mTerrainPts.Count > 0 || mMonuPts.Count > 0))
                    { seamM = m; terrainPts = mTerrainPts; monuPts = mMonuPts; monuType = mMonuType; }
                }
                if (seamM == null) { PLog("[probe:monunav] VERDICT: NO typed navmesh answered inside any candidate — RunnerDriver needs a hand-walk (MovePosition) monument mode."); return; }
                PLog($"[probe:monunav] seam test target: {MonuName(seamM)} — terrain-typed interior pts {terrainPts.Count}, other-typed pts {monuPts.Count} (typeId {(monuType == int.MinValue ? "n/a" : monuType.ToString())})");

                // 4) stage a walk-in point on plain terrain outside the bounds
                var stagePos = Vector3.zero;
                for (var d = 0; d < 8 && stagePos == Vector3.zero; d++)
                {
                    var dir = Quaternion.Euler(0f, d * 45f, 0f) * Vector3.forward;
                    var edge = seamM.ClosestPointOnBounds(seamM.transform.position + dir * 4000f);
                    var cpt = edge + new Vector3(dir.x, 0f, dir.z).normalized * 25f;
                    if (TerrainMeta.HeightMap.GetHeight(cpt) < 0.5f) continue; // water side
                    cpt = GroundSnap(cpt);
                    var tf = new NavMeshQueryFilter { agentTypeID = terrainType, areaMask = NavMesh.AllAreas };
                    NavMeshHit sh;
                    if (NavSampleTyped(cpt, out sh, 6f, tf)) stagePos = sh.position;
                }
                if (stagePos == Vector3.zero) { PLog("[probe:monunav] no terrain-navmesh stage point around the target — aborted (pick another monument next run)."); return; }
                var goalA = terrainPts.Count > 0 ? terrainPts[0] : monuPts[0];
                var goalAOnTerrainBake = terrainPts.Count > 0;

                var stale = new List<BasePlayer>();
                Vis.Entities(stagePos, 25f, stale);
                foreach (var s in stale)
                    if (s != null && !s.IsDestroyed && s.displayName != null && s.displayName.StartsWith("FF_PROBE", StringComparison.Ordinal))
                    { s.Kill(); PLog($"[probe:monunav] swept leaked body {s.displayName}."); }
                body = SpawnProbeBody(stagePos, "FF_PROBE_MNAV", 990005UL);
                if (body == null) { PLog("[probe:monunav] body spawn failed — aborted."); return; }
                var bodyRef = body;
                PLog($"[probe:monunav] staged at {stagePos}, leg A goal {goalA} ({(goalAOnTerrainBake ? "terrain-bake" : "monument-bake")} pt, {Vector3.Distance(stagePos, goalA):0.0}m)");

                timer.Once(1f, () =>
                {
                    try
                    {
                        if (bodyRef == null || bodyRef.IsDestroyed) { PLog("[probe:monunav] body gone before graft."); return; }
                        if (bodyRef.IsSleeping()) bodyRef.EndSleeping();
                        var go = bodyRef.gameObject;
                        var agent = go.AddComponent<UnityEngine.AI.NavMeshAgent>();
                        agent.enabled = false;
                        agent.agentTypeID = terrainType;
                        agent.areaMask = NavMesh.AllAreas;
                        agent.speed = 5f; agent.acceleration = 8f; agent.angularSpeed = 120f;
                        agent.updatePosition = false; agent.updateRotation = false;
                        NavMeshHit snap;
                        if (NavSample(bodyRef.transform.position, out snap, 6f))
                            bodyRef.transform.position = snap.position;
                        agent.enabled = true;
                        var rustAgent = go.AddComponent<Rust.Ai.Gen2.RustNavMeshAgent>();
                        var nav = go.AddComponent<BaseNavigator>();
                        nav.Init(bodyRef, rustAgent);
                        nav.MaxWaterDepth = 0.5f;
                        nav.SetNavMeshEnabled(true);
                        nav.PlaceOnNavMesh(0f);
                        var setA = nav.SetDestination(goalA, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                        PLog($"[probe:monunav] grafted (typeId {terrainType}): isOnNavMesh {agent.isOnNavMesh}, leg A SetDestination {setA}");

                        // legs: 0 = A terrain-typed seam crossing; 1 = B interior re-typed;
                        // 2 = C outbound re-typed BACK to terrain (run 1 proved typeId-0
                        // cannot leave: hasPath False for any off-monument goal)
                        var leg = 0; var legTicks = 0; var legAPass = false; var legBPass = false; var legBRan = false; var legCPass = false;
                        Oxide.Plugins.Timer pumpT = null;
                        Action finish = () =>
                        {
                            var arch = legAPass && legCPass
                                ? "ONE Animal-typed agent walks in and out of surface monument areas; deep (Humanoid-only) pockets via in-place re-type"
                                : legAPass ? "Animal-typed agent enters fine; outbound re-type failed — investigate before 6A"
                                : legBPass ? "seam handoff required: re-type the agent at the monument edge"
                                : "no typed path worked — RunnerDriver needs a hand-walk (MovePosition) monument mode";
                            PLog($"[probe:monunav] VERDICT: legA(seam-in, terrain-typed) {(legAPass ? "PASS" : "FAIL")}; legB(interior re-typed) {(legBRan ? (legBPass ? "PASS" : "FAIL") : "SKIP")}; legC(outbound re-typed to terrain) {(legCPass ? "PASS" : "FAIL")} → {arch}");
                            if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill();
                            PLog("[probe:monunav] complete — probe body removed.");
                            pumpT?.Destroy();
                        };
                        Action<int> startLeg = null;
                        startLeg = l =>
                        {
                            leg = l; legTicks = 0;
                            if (l == 1)
                            {
                                if (monuType == int.MinValue || monuPts.Count == 0)
                                { PLog("[probe:monunav] leg B skipped (no distinct monument bake answered)."); startLeg(2); return; }
                                // teleport inside, re-type to the monument bake, walk an interior leg
                                nav.Stop();
                                agent.enabled = false;
                                bodyRef.transform.position = monuPts[0];
                                agent.agentTypeID = monuType;
                                agent.enabled = true;
                                nav.SetNavMeshEnabled(true);
                                nav.PlaceOnNavMesh(0f);
                                var goalB = monuPts.Count > 1 ? monuPts[monuPts.Count - 1] : monuPts[0] + new Vector3(12f, 0f, 0f);
                                NavMeshHit bh;
                                var bf = new NavMeshQueryFilter { agentTypeID = monuType, areaMask = NavMesh.AllAreas };
                                if (NavSampleTyped(goalB, out bh, 8f, bf)) goalB = bh.position;
                                legBRan = true;
                                var setB = nav.SetDestination(goalB, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                                PLog($"[probe:monunav] leg B: re-typed to {monuType}, isOnNavMesh {agent.isOnNavMesh}, goal {goalB} ({Vector3.Distance(bodyRef.transform.position, goalB):0.0}m), SetDestination {setB}");
                            }
                            else if (l == 2)
                            {
                                // outbound: re-type back to the terrain bake in place (run 1:
                                // leaving on typeId 0 is impossible — hasPath False), then walk out
                                nav.Stop();
                                agent.enabled = false;
                                agent.agentTypeID = terrainType;
                                agent.enabled = true;
                                nav.SetNavMeshEnabled(true);
                                nav.PlaceOnNavMesh(0f);
                                var setC = nav.SetDestination(stagePos, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                                PLog($"[probe:monunav] leg C: re-typed to terrain {terrainType}, isOnNavMesh {agent.isOnNavMesh}, outbound to stage {stagePos} ({Vector3.Distance(bodyRef.transform.position, stagePos):0.0}m), SetDestination {setC}");
                            }
                        };
                        pumpT = timer.Repeat(0.1f, 2600, () =>
                        {
                            try
                            {
                                if (bodyRef == null || bodyRef.IsDestroyed) { pumpT?.Destroy(); return; }
                                nav.Think(0.1f);
                                legTicks++;
                                var pos = bodyRef.transform.position;
                                var goal = leg == 0 ? goalA : leg == 1 ? nav.Destination : stagePos;
                                var dist = Vector3.Distance(pos, goal);
                                if (legTicks % 30 == 0)
                                    PLog($"[probe:monunav] leg {(leg == 0 ? "A" : leg == 1 ? "B" : "C")} t={legTicks / 10f:0.0}s dist {dist:0.0}m navType {nav.CurrentNavigationType} hasPath {nav.HasPath} inBounds {seamM.IsInBounds(pos)}");
                                // run 1 lesson: 450 ticks capped a 162 m route mid-stride
                                // (~2.3 m/s observed) and mislabeled a live walk as FAIL —
                                // budget legs generously, arrival or bust
                                var cap = leg == 0 ? 1200 : leg == 1 ? 250 : 900;
                                var arrived = dist < 3f;
                                if (arrived || legTicks >= cap)
                                {
                                    if (leg == 0)
                                    {
                                        legAPass = arrived;
                                        PLog($"[probe:monunav] leg A {(arrived ? "PASS — arrived inside" : $"FAIL — stalled at {dist:0.0}m from goal, inBounds {seamM.IsInBounds(pos)}, hasPath {nav.HasPath}")}");
                                        // A pass proves the single-agent architecture; only re-type-test on failure
                                        startLeg(legAPass ? 2 : 1);
                                    }
                                    else if (leg == 1)
                                    {
                                        legBPass = arrived;
                                        PLog($"[probe:monunav] leg B {(arrived ? "PASS — interior leg walked on the monument bake" : $"FAIL — stalled at {dist:0.0}m, hasPath {nav.HasPath}")}");
                                        startLeg(2);
                                    }
                                    else
                                    {
                                        legCPass = dist < 4f;
                                        PLog($"[probe:monunav] leg C {(legCPass ? "PASS — walked back out" : $"FAIL — stalled at {dist:0.0}m from stage, hasPath {nav.HasPath}")}");
                                        finish();
                                    }
                                }
                            }
                            catch (Exception e) { PLog($"[probe:monunav] EXCEPTION in pump: {e.Message}"); pumpT?.Destroy(); if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill(); }
                        });
                    }
                    catch (Exception e)
                    {
                        PLog($"[probe:monunav] EXCEPTION in graft: {e.Message}");
                        if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill();
                    }
                });
            }
            catch (Exception e)
            {
                PLog($"[probe:monunav] EXCEPTION: {e.Message}");
                if (body != null && !body.IsDestroyed) body.Kill();
            }
        }

        // ---- Scientist-generation census probe (2026-08-01) --------------------------
        // One-shot, marker-gated (data/FakeFriends/probe_scicensus.json), read-only.
        // Settles the humanoid half of the Gen2 migration question the wolves forced
        // (decision 0010): the bundle ships BOTH scientist prefab families and no code
        // path in Assembly-CSharp references gen2/scientist2*.prefab — the wiring is
        // GUID-level inside monument spawner prefabs, invisible to static analysis.
        // So: sweep serverEntities live, count old ScientistNPC (minus our own
        // HostileNpc subclass) vs Rust.Ai.Gen2.ScientistNPC2, break down by prefab
        // and nearest monument. ETA: 30 s arm + one sweep.
        private string ProbeSciCensusMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_scicensus.json");

        private void ProbeSciCensus()
        {
            try { System.IO.File.Delete(ProbeSciCensusMarker); } catch { }
            try
            {
                var oldGen = 0; var gen2 = 0; var ours = 0;
                var byPrefab = new Dictionary<string, int>();
                var byMonu = new Dictionary<string, int>();
                var monuments = TerrainMeta.Path?.Monuments;
                foreach (var net in BaseNetworkable.serverEntities)
                {
                    BaseEntity e = null;
                    if (net is HostileNpc) { ours++; continue; }        // our own scientist-chassis hostiles: not vanilla
                    if (net is ScientistNPC sOld) { oldGen++; e = sOld; }
                    else if (net is Rust.Ai.Gen2.ScientistNPC2 s2) { gen2++; e = s2; }
                    if (e == null) continue;
                    var key = (e is Rust.Ai.Gen2.ScientistNPC2 ? "gen2:" : "old:") + e.ShortPrefabName;
                    int n; byPrefab.TryGetValue(key, out n); byPrefab[key] = n + 1;
                    if (monuments != null)
                    {
                        MonumentInfo nearest = null; var bestSq = float.MaxValue;
                        foreach (var m in monuments)
                        {
                            if (m == null) continue;
                            var d = (m.transform.position - e.transform.position).sqrMagnitude;
                            if (d < bestSq) { bestSq = d; nearest = m; }
                        }
                        var mk = (e is Rust.Ai.Gen2.ScientistNPC2 ? "gen2@" : "old@") +
                                 (nearest != null && bestSq < 250f * 250f ? MonuName(nearest) : "roam/other");
                        byMonu.TryGetValue(mk, out n); byMonu[mk] = n + 1;
                    }
                }
                PLog($"[probe:scicensus] live sweep: {oldGen} old-gen ScientistNPC, {gen2} Gen2 ScientistNPC2 ({ours} FF hostiles excluded).");
                var sb = new System.Text.StringBuilder();
                foreach (var kv in byPrefab) sb.Append($"{kv.Key} x{kv.Value}; ");
                PLog($"[probe:scicensus] by prefab: {(sb.Length > 0 ? sb.ToString() : "none alive")}");
                sb.Length = 0;
                foreach (var kv in byMonu) sb.Append($"{kv.Key} x{kv.Value}; ");
                PLog($"[probe:scicensus] by monument: {(sb.Length > 0 ? sb.ToString() : "n/a")}");
                PLog($"[probe:scicensus] VERDICT: {(gen2 > 0 && oldGen > 0 ? "MIXED — both generations live; both-class checks mandatory" : gen2 > 0 ? "MIGRATED — Gen2 only; old-class checks were blind" : oldGen > 0 ? "OLD-GEN only today; Gen2 coverage is future-proofing" : "no vanilla scientists alive at sweep time — rerun when monuments are populated")}");
            }
            catch (Exception e)
            {
                PLog($"[probe:scicensus] EXCEPTION: {e.Message}");
            }
        }

        // ---- Phase 6 gate probe 6P2: crate loot + scientist aggro --------------------
        // One-shot, marker-gated (data/FakeFriends/probe_mloot.json). Can a native-bot
        // body find and strip crate_normal/crate_normal_2 server-side, and what does the
        // emptied crate do? (Decompile 2026-07-22: server-side removal only sets
        // HasBeenLooted; the self-kill lives in PlayerStoppedLooting — so the runner
        // must Kill(Gib) the shell itself, which is exactly the vanilla emptied path and
        // lets the spawn handler respawn it.) Plus: do monument scientists aggro the
        // runner body (Phase 0 observation, re-verified on current build)?
        // ETA: 30 s arm + ~5 s census + ~65 s watch.
        private string ProbeMonuLootMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_mloot.json");

        private void ProbeMonuLoot()
        {
            try { System.IO.File.Delete(ProbeMonuLootMarker); } catch { }
            BasePlayer body = null;
            try
            {
                // census: crates + scientists in one serverEntities sweep (one-shot, cold path)
                var crates = new List<LootContainer>();
                var scientists = new List<ScientistNPC>();
                foreach (var net in BaseNetworkable.serverEntities)
                {
                    var lc = net as LootContainer;
                    if (lc != null && !lc.IsDestroyed && (lc.ShortPrefabName == "crate_normal" || lc.ShortPrefabName == "crate_normal_2")) { crates.Add(lc); continue; }
                    var sn = net as ScientistNPC;
                    if (sn != null && !sn.IsDestroyed && !sn.IsDead()) scientists.Add(sn);
                }
                PLog($"[probe:mloot] census: {crates.Count} crate_normal[_2], {scientists.Count} scientists on the map.");
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments == null) { PLog("[probe:mloot] no monuments listed — aborted."); return; }
                MonumentInfo best = null; var bestSci = -1; LootContainer crate = null;
                foreach (var m in monuments)
                {
                    if (m == null || !m.HasNavmesh || m.IsSafeZone) continue;
                    var cCount = 0; LootContainer first = null;
                    foreach (var lc in crates)
                        if (lc.inventory != null && lc.inventory.itemList != null && lc.inventory.itemList.Count > 0 && m.IsInBounds(lc.transform.position))
                        { cCount++; if (first == null) first = lc; }
                    if (cCount == 0) continue;
                    var sCount = 0;
                    foreach (var sn in scientists)
                        if (Vector3.Distance(sn.transform.position, m.transform.position) < 150f) sCount++;
                    PLog($"[probe:mloot] {MonuName(m)}: {cCount} filled crates, {sCount} scientists");
                    if (sCount > bestSci) { bestSci = sCount; best = m; crate = first; }
                }
                if (best == null || crate == null) { PLog("[probe:mloot] no navmesh monument holds a filled crate — aborted (crates looted out? re-run later)."); return; }
                // prefer a crate with a scientist near it (aggro test co-located)
                foreach (var lc in crates)
                {
                    if (lc.inventory == null || lc.inventory.itemList == null || lc.inventory.itemList.Count == 0 || !best.IsInBounds(lc.transform.position)) continue;
                    foreach (var sn in scientists)
                        if (Vector3.Distance(sn.transform.position, lc.transform.position) < 60f) { crate = lc; break; }
                }
                var crateRef = crate;
                var cratePos = crate.transform.position;
                var inv = "";
                foreach (var it in crate.inventory.itemList)
                    if (it != null) inv += (inv.Length > 0 ? ", " : "") + it.info.shortname + "x" + it.amount;
                var spi = crate.GetComponent<SpawnPointInstance>();
                PLog($"[probe:mloot] target: {crate.ShortPrefabName} at {cratePos} ({MonuName(best)}) — [{inv}]; destroyOnEmpty {crate.destroyOnEmpty}, refresh {crate.minSecondsBetweenRefresh:0}-{crate.maxSecondsBetweenRefresh:0}s, HasBeenLooted {crate.HasBeenLooted}, spawn-link {(spi != null ? (spi.parentSpawnPointUser != null ? spi.parentSpawnPointUser.GetType().Name : "user:null") + "/" + (spi.parentSpawnPoint != null ? spi.parentSpawnPoint.GetType().Name : "point:null") : "NONE")}");

                var stale = new List<BasePlayer>();
                Vis.Entities(cratePos, 25f, stale);
                foreach (var s in stale)
                    if (s != null && !s.IsDestroyed && s.displayName != null && s.displayName.StartsWith("FF_PROBE", StringComparison.Ordinal))
                    { s.Kill(); PLog($"[probe:mloot] swept leaked body {s.displayName}."); }
                body = SpawnProbeBody(GroundSnap(cratePos + new Vector3(2.5f, 0f, 0f)), "FF_PROBE_LOOTER", 990006UL);
                if (body == null) { PLog("[probe:mloot] body spawn failed — aborted."); return; }
                var bodyRef = body;
                var bestRef = best;

                timer.Once(1f, () =>
                {
                    try
                    {
                        if (bodyRef == null || bodyRef.IsDestroyed) { PLog("[probe:mloot] body gone before strip."); return; }
                        if (bodyRef.IsSleeping()) bodyRef.EndSleeping();
                        var sciNear = new List<ScientistNPC>();
                        Vis.Entities(bodyRef.transform.position, 100f, sciNear);
                        var nearest = float.MaxValue;
                        foreach (var sn in sciNear)
                            if (sn != null && !sn.IsDestroyed && !sn.IsDead())
                                nearest = Mathf.Min(nearest, Vector3.Distance(sn.transform.position, bodyRef.transform.position));
                        PLog($"[probe:mloot] staged: {sciNear.Count} scientists within 100m (nearest {(nearest < float.MaxValue ? nearest.ToString("0.0") + "m" : "n/a")}), body hp {bodyRef.health:0}");

                        // strip: server-side MoveToContainer, timed, no looting session
                        if (crateRef == null || crateRef.IsDestroyed) { PLog("[probe:mloot] crate died before the strip (looted/respawned?) — aborted."); bodyRef.Kill(); return; }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var items = new List<Item>(crateRef.inventory.itemList);
                        var moved = 0;
                        foreach (var it in items)
                            if (it != null && it.MoveToContainer(bodyRef.inventory.containerMain)) moved++;
                        sw.Stop();
                        PLog($"[probe:mloot] STRIP: moved {moved}/{items.Count} stacks in {sw.ElapsedMilliseconds}ms; crate now {crateRef.inventory.itemList.Count} items, HasBeenLooted {crateRef.HasBeenLooted}, alive {!crateRef.IsDestroyed}");

                        var tick = 0;
                        Oxide.Plugins.Timer watchT = null;
                        watchT = timer.Repeat(2f, 33, () =>
                        {
                            try
                            {
                                tick++;
                                if (bodyRef == null || bodyRef.IsDestroyed)
                                { PLog("[probe:mloot] body destroyed mid-watch (killed?) — see last aggro lines."); watchT?.Destroy(); return; }
                                if (tick == 3)
                                {
                                    if (crateRef != null && !crateRef.IsDestroyed)
                                    {
                                        PLog("[probe:mloot] crate did NOT self-kill after server-side strip (expected — PlayerStoppedLooting only). Killing it now (vanilla emptied path).");
                                        crateRef.Kill(BaseNetworkable.DestroyMode.Gib);
                                    }
                                    else PLog("[probe:mloot] crate SELF-KILLED after strip — no manual RemoveMe needed.");
                                }
                                if (tick % 5 == 0)
                                {
                                    var sciNow = new List<ScientistNPC>();
                                    Vis.Entities(bodyRef.transform.position, 100f, sciNow);
                                    var nd = float.MaxValue;
                                    foreach (var sn in sciNow)
                                        if (sn != null && !sn.IsDestroyed && !sn.IsDead())
                                            nd = Mathf.Min(nd, Vector3.Distance(sn.transform.position, bodyRef.transform.position));
                                    var la = bodyRef.lastAttacker;
                                    var respawned = false;
                                    var lcNear = new List<LootContainer>();
                                    Vis.Entities(cratePos, 8f, lcNear);
                                    foreach (var lc in lcNear)
                                        if (lc != null && !lc.IsDestroyed && (lc.ShortPrefabName == "crate_normal" || lc.ShortPrefabName == "crate_normal_2")) respawned = true;
                                    PLog($"[probe:mloot] t={tick * 2}s hp {bodyRef.health:0.#}, lastAttacker {(la != null ? la.ShortPrefabName : "none")}, nearest scientist {(nd < float.MaxValue ? nd.ToString("0.0") + "m" : "n/a")}, crate respawned {respawned}");
                                }
                                if (tick >= 33)
                                {
                                    var shot = bodyRef.health < 99.5f || bodyRef.lastAttacker is ScientistNPC;
                                    PLog($"[probe:mloot] VERDICT: strip WORKS (server-side MoveToContainer, no looting session, kill shell for respawn); scientist aggro {(shot ? "CONFIRMED — body was engaged" : "NOT OBSERVED in 60s (nearest may be out of sense range — check distances above)")}");
                                    bodyRef.Kill();
                                    PLog("[probe:mloot] complete — probe body removed (loot went with it).");
                                    watchT?.Destroy();
                                }
                            }
                            catch (Exception e) { PLog($"[probe:mloot] EXCEPTION in watch: {e.Message}"); watchT?.Destroy(); if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill(); }
                        });
                    }
                    catch (Exception e)
                    {
                        PLog($"[probe:mloot] EXCEPTION in strip: {e.Message}");
                        if (bodyRef != null && !bodyRef.IsDestroyed) bodyRef.Kill();
                    }
                });
            }
            catch (Exception e)
            {
                PLog($"[probe:mloot] EXCEPTION: {e.Message}");
                if (body != null && !body.IsDestroyed) body.Kill();
            }
        }

        private static bool IsHostileP(BotRecord rec) => rec?.Identity?.Persona == "Hostile";

        // Static bridge for the nested brains (MonoBehaviours have no plugin instance).
        private static FakeFriends Ff;

        // 5C — does this hostile's roster record hold a grudge against the candidate?
        // (People who killed him before, or whose crew did.) Used as a targeting weight.
        public static bool HostileHasGrudge(int botId, BasePlayer candidate)
        {
            var self = Ff;
            if (self == null || botId < 0 || candidate == null) return false;
            BotRecord rec;
            if (!self._bots.TryGetValue(botId, out rec) || rec.Social?.Grudges == null) return false;
            int cbid;
            var key = self._byUserId.TryGetValue((ulong)candidate.userID, out cbid)
                ? BotKey(cbid) : SteamKey((ulong)candidate.userID);
            return rec.Social.Grudges.ContainsKey(key);
        }

        // 5B — depot: the hostile's pockets (loot from kills, plus expendables he won't
        // miss — scientists never consume inventory ammo/meds) empty into the group's
        // base stash. Physical goods only; the scrap bounty banked at kill time.
        // Works for either chassis (5.5): the scientist overload forwards its binding.
        public static void DepositHostileTake(HostileNpc npc) => DepositHostileTake(npc, npc != null ? npc.BoundBotId : -1);

        public static void DepositHostileTake(BasePlayer npc, int boundBotId)
        {
            var self = Ff;
            if (self == null || npc == null || npc.IsDestroyed || boundBotId < 0 || npc.inventory?.containerMain == null) return;
            BotRecord rec;
            if (!self._bots.TryGetValue(boundBotId, out rec)) return;
            BotRecord holder;
            var b = self.GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) return;
            var buckets = self.FindBaseContainers(b);
            var box = PickContainer(buckets, "storage");
            if (box == null) return;
            var moved = 0;
            var items = new List<Item>(npc.inventory.containerMain.itemList); // MoveToContainer mutates
            foreach (var item in items)
                if (item != null && item.MoveToContainer(box.inventory)) moved++;
            if (moved > 0)
            {
                self.Story("depot", $"{rec.Identity.Name} stashed the take from the last hunt");
                self.DLog($"[depot] {rec.Identity.Name} stashed {moved} item stack(s) at the base.");
            }
        }

        // 5D — is this building privilege one of OUR pasted bases? Bot cabins are fair
        // ground for the hunt (bot-vs-bot symmetry); anything else is a PLAYER's base
        // and the never-enter promise applies.
        public static bool IsBotOwnedPriv(BuildingPrivlidge priv)
        {
            var self = Ff;
            if (self == null || priv == null) return false;
            foreach (var rec in self._bots.Values)
            {
                var b = self.GetBase(rec);
                if (b != null && b.OwnerId != 0 && b.OwnerId == priv.OwnerID) return true;
            }
            return false;
        }

        // Patrol radius scales with the base stage (decision 0007): a fresh camp is a
        // local menace, a stage-3 compound is a regional one.
        private float HostilePatrolRadius(BotRecord rec)
        {
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            switch (b?.Stage ?? 0)
            {
                case 3: return 300f;
                case 2: return 150f;
                case 1: return 75f;
                default: return 60f;
            }
        }

        // kits.json Hostile ladder, minus the resident flavor items (no torch/rock —
        // EquipWeapon must find the weapon first in the belt).
        private void GiveHostileKit(HostileNpc npc, BotRecord rec)
        {
            if (npc.inventory == null) return;
            var ladder = ResolveKitLadder("Hostile");
            var earned = rec?.Progression?.KitTier ?? 0;
            var gear = rec?.Progression?.GearTier ?? -1;
            var kit = ladder[Mathf.Clamp(gear >= 0 ? gear : earned, 0, ladder.Count - 1)];
            if (kit.Wear != null) foreach (var s in kit.Wear) GiveItem(npc, s, 1, npc.inventory.containerWear);
            if (!string.IsNullOrEmpty(kit.Weapon)) GiveItem(npc, kit.Weapon, 1, npc.inventory.containerBelt);
            if (!string.IsNullOrEmpty(kit.Ammo) && kit.AmmoCount > 0) GiveItem(npc, kit.Ammo, kit.AmmoCount, npc.inventory.containerMain);
            if (!string.IsNullOrEmpty(kit.Med) && kit.Meds > 0) GiveItem(npc, kit.Med, kit.Meds, npc.inventory.containerMain);
        }

        private HostileNpc SpawnHostile(Vector3 pos, BotRecord rec = null)
        {
            var proto = GameManager.server.CreateEntity(HostileScientistPrefab, pos, Quaternion.identity, startActive: false) as ScientistNPC;
            if (proto == null) return null;
            var protoBrain = proto.GetComponent<ScientistBrain>();
            var npc = proto.gameObject.AddComponent<HostileNpc>();
            proto.gameObject.AddComponent<HostileBrain>();
            // Serialized fields lost in the swap, copied from the proto (public fields —
            // still no reflection). prefabID=0 → "Couldn't create network entity" and
            // the entity never networks to clients (and Kill()'s ResetState→LookupPrefab
            // NRE — the probe's kill-path caveat — is the same missing id).
            npc.prefabID = proto.prefabID;
            npc.bounds = proto.bounds;
            UnityEngine.Object.DestroyImmediate(proto, true);
            UnityEngine.Object.DestroyImmediate(protoBrain, true);
            npc.enableSaving = false;      // lifecycle is ours; never engine-persisted
            npc.LegacyNavigation = false;  // lost serialized field #1 — gates Navigator.Init
            npc.syncPosition = true;       // lost serialized field #2 — gates movement + client sync
            npc.HomePos = pos;
            npc.SpawnPos = pos;
            npc.gameObject.AwakeFromInstantiate();
            npc.Spawn();
            npc.InitializeHealth(120f, 120f);
            if (rec != null)
            {
                // Roster-bound (5B): real identity, kits.json disguise, staged patrol.
                npc.BoundBotId = rec.Identity.BotId;
                npc.HostileName = rec.Identity.Name;
                npc.displayName = rec.Identity.Name;
                npc.RoamRadius = HostilePatrolRadius(rec);
                GiveHostileKit(npc, rec);
            }
            else
            {
                // Test-harness spawn: the component swap loses the prefab belt — arm explicitly.
                npc.displayName = "Hostile (test)";
                var rifle = ItemManager.CreateByName("rifle.ak", 1);
                if (rifle != null && !rifle.MoveToContainer(npc.inventory.containerBelt)) rifle.Remove();
            }
            return npc;
        }

        private void DespawnHostile(HostileNpc h)
        {
            if (h == null) return; // Unity null-overload also covers destroyed
            h.GetComponent<HostileBrain>()?.StopPump();
            try { if (!h.IsDestroyed) h.Kill(); }
            catch { /* ResetState→LookupPrefab NRE on swapped scientists (probe caveat) */ }
            if (h != null && h.gameObject != null) UnityEngine.Object.DestroyImmediate(h.gameObject);
        }

        // Watchable prey: a throwaway native-bot body for observing a hunt end to end
        // (chase → combat → loot crouch → depot) without risking a roster identity.
        // Never engine-persisted (crash-safe); despawned on unload and by 'kill'.
        // It stands still and dies — for a real two-sided fight, ff.session start a
        // resident near a hostile camp instead.
        private readonly List<BasePlayer> _preyBodies = new List<BasePlayer>();
        private static readonly string[] _preyNames = { "Dusty", "Roach", "Pockets", "Lenny", "Sunburn", "Grubs" };

        private BasePlayer SpawnPreyBody(Vector3 pos)
        {
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.15f;
            var body = GameManager.server.CreateEntity(PlayerPrefab, pos, Quaternion.identity) as BasePlayer;
            if (body == null) return null;
            body.enableSaving = false;
            body.limitNetworking = true; // silent until named/dressed (numeric-name cache)
            body.Spawn();
            body.displayName = _preyNames[UnityEngine.Random.Range(0, _preyNames.Length)];
            body.health = 100f;
            if (body.metabolism != null)
            {
                body.metabolism.calories.value = 500f;
                body.metabolism.hydration.value = 250f;
            }
            GiveItem(body, "burlap.shirt", 1, body.inventory.containerWear);
            GiveItem(body, "burlap.trousers", 1, body.inventory.containerWear);
            GiveItem(body, "torch", 1, body.inventory.containerBelt);
            GiveItem(body, "cloth", 20, body.inventory.containerMain);   // something worth
            GiveItem(body, "scrap", 15, body.inventory.containerMain);   // stripping at the
            GiveItem(body, "apple", 3, body.inventory.containerMain);    // loot crouch
            body.limitNetworking = false;
            body.SendNetworkUpdateImmediate();
            if (body.IsSleeping()) body.EndSleeping(); // sleepers are never targeted
            return body;
        }

        [ConsoleCommand("ff.hostile.test")]
        private void CmdHostileTest(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sub = arg.GetString(0, "list").ToLowerInvariant();
            if (sub == "prey")
            {
                Vector3 pos;
                var caller = arg.Player();
                var px = arg.GetFloat(1, float.NaN);
                var pz = arg.GetFloat(2, float.NaN);
                if (!float.IsNaN(px) && !float.IsNaN(pz)) pos = new Vector3(px, 0f, pz);
                else if (caller != null) pos = caller.transform.position + caller.transform.forward * 25f;
                else
                {
                    // RCON with no coords: stage on the nearest prowler's ring.
                    BasePlayer prowler = null;
                    foreach (var rec in _bots.Values)
                        if (IsHostileP(rec) && rec.State.Status == "Online" && (prowler = GetLive(rec.Identity.BotId)) != null) break;
                    if (prowler == null) { arg.ReplyWith("No prowling hostile to stage near — run from in-game (spawns 25m in front of you) or pass coords: ff.hostile.test prey <x> <z>"); return; }
                    var ring = UnityEngine.Random.insideUnitCircle.normalized * 35f;
                    pos = prowler.transform.position + new Vector3(ring.x, 0f, ring.y);
                }
                var prey = SpawnPreyBody(pos);
                if (prey == null) { arg.ReplyWith("Prey spawn failed (CreateEntity returned null)."); return; }
                _preyBodies.Add(prey);
                var best = float.MaxValue;
                string bestName = null;
                foreach (var rec in _bots.Values)
                {
                    if (!IsHostileP(rec) || rec.State.Status != "Online") continue;
                    var h = GetLive(rec.Identity.BotId);
                    if (h == null) continue;
                    var d = Vector3.Distance(h.transform.position, prey.transform.position);
                    if (d < best) { best = d; bestName = rec.Identity.Name; }
                }
                var ctx = bestName != null
                    ? $" Nearest prowler: {bestName} at {best:0}m ({(best <= HostileDriver.SenseRange ? "inside his 60m senses — he'll pick it up within seconds" : "out of sense range — it gets found when a patrol swings by")})."
                    : " No hostile is prowling right now — ff.session start <hostileId> to send one out.";
                arg.ReplyWith($"Prey '{prey.displayName}' spawned at {prey.transform.position}.{ctx} ff.hostile.test kill cleans up. (Heads-up: the kill pays the hostile's normal Cobalt bounty and lands in the story log.)");
                DLog($"[hostile] prey body '{prey.displayName}' spawned at {prey.transform.position}");
                return;
            }
            if (sub == "spawn")
            {
                Vector3 pos;
                var player = arg.Player();
                if (player != null)
                {
                    pos = player.transform.position + player.transform.forward * 20f;
                }
                else
                {
                    // RCON: 'ff.hostile.test spawn x z' or a random off-monument spot
                    var x = arg.GetFloat(1, float.NaN);
                    var z = arg.GetFloat(2, float.NaN);
                    if (!float.IsNaN(x) && !float.IsNaN(z)) pos = new Vector3(x, 0f, z);
                    else if (!TryRandomTerrainSpot(out pos))
                    {
                        arg.ReplyWith("No valid terrain spot found — pass coordinates: ff.hostile.test spawn <x> <z>");
                        return;
                    }
                }
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                NavMeshHit hit;
                if (!NavSample(pos, out hit, 10f))
                {
                    arg.ReplyWith($"No navmesh within 10m of {pos} — pick another spot.");
                    return;
                }
                var h = SpawnHostile(hit.position);
                if (h == null) { arg.ReplyWith("Spawn failed (CreateEntity returned null)."); return; }
                _testHostiles.Add(h);
                arg.ReplyWith($"Hostile test scientist spawned at {hit.position} (roam radius {h.RoamRadius:0}m). ff.hostile.test list to watch it move; ff.hostile.test kill to clean up.");
                DLog($"[hostile] test spawn at {hit.position}");
                return;
            }
            if (sub == "kill")
            {
                var n = 0;
                foreach (var h in _testHostiles) { if (h != null) { DespawnHostile(h); n++; } }
                _testHostiles.Clear();
                foreach (var p in _preyBodies) { if (p != null && !p.IsDestroyed) { p.Kill(); n++; } }
                _preyBodies.Clear();
                arg.ReplyWith($"Despawned {n} test hostile(s)/prey bod(ies).");
                return;
            }
            // list (default)
            _testHostiles.RemoveAll(h => h == null || h.IsDestroyed);
            _preyBodies.RemoveAll(p => p == null || p.IsDestroyed);
            if (_testHostiles.Count == 0 && _preyBodies.Count == 0) { arg.ReplyWith("No test hostiles or prey alive. ff.hostile.test spawn [x z] | prey [x z]."); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"=== {_testHostiles.Count} test hostile(s), {_preyBodies.Count} prey ===");
            foreach (var h in _testHostiles)
            {
                var moved = Vector3.Distance(h.SpawnPos, h.transform.position);
                var state = h.GetComponent<HostileBrain>()?.StateName ?? "?";
                sb.AppendLine($"  hp {h.health:0} [{state}] kills {h.Kills} at {h.transform.position} — moved {moved:0.0}m from spawn, {Vector3.Distance(h.HomePos, h.transform.position):0.0}m from home");
            }
            foreach (var p in _preyBodies)
                sb.AppendLine($"  prey {p.displayName}: hp {p.health:0} at {p.transform.position}");
            arg.ReplyWith(sb.ToString());
        }

        // 5.5 observability: every hostile's chassis, state, and whereabouts.
        [ConsoleCommand("ff.hostile.state")]
        private void CmdHostileState(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sb = new StringBuilder();
            var n = 0;
            foreach (var rec in _bots.Values)
            {
                if (!IsHostileP(rec)) continue;
                n++;
                var live = GetLive(rec.Identity.BotId);
                if (live == null) { sb.AppendLine($"  {rec.Identity.Name}: {rec.State.Status} (no body)"); continue; }
                var legacy = live as HostileNpc;
                var drv = live.GetComponent<HostileDriver>();
                var chassis = legacy != null ? "scientist" : drv != null ? "native+driver" : "native (sleeper)";
                var state = legacy != null ? legacy.GetComponent<HostileBrain>()?.StateName ?? "?" : drv != null ? drv.StateName : "-";
                var homeDist = rec.State.HomePos != null ? Vector3.Distance(live.transform.position, rec.State.HomePos.V) : -1f;
                sb.AppendLine($"  {rec.Identity.Name}: {rec.State.Status} [{chassis}] state {state}, hp {live.health:0}, kills {rec.State.Kills}, {homeDist:0}m from camp, gun {(FightWeapon(live)?.GetItem()?.info?.shortname ?? "none")}");
            }
            arg.ReplyWith(n == 0 ? "No hostile identities in the roster." : $"=== {n} hostile(s) ===\n{sb}");
        }

        private bool TryRandomTerrainSpot(out Vector3 spot)
        {
            var half = TerrainMeta.Size.x * 0.5f - 200f;
            var blockMask = TerrainTopology.CLIFF | TerrainTopology.CLIFFSIDE | TerrainTopology.OCEAN |
                            TerrainTopology.MONUMENT | TerrainTopology.RIVER | TerrainTopology.LAKE | TerrainTopology.BUILDING;
            for (var i = 0; i < 60; i++)
            {
                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 1f) continue;
                if ((TerrainMeta.TopologyMap.GetTopology(pos) & (int)blockMask) != 0) continue;
                spot = pos;
                return true;
            }
            spot = Vector3.zero;
            return false;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            Ff = this; // nested brains (MonoBehaviours) reach plugin state through this
            permission.RegisterPermission(_config.AdminPermission, this);
            Unsubscribe(nameof(OnEntityDeath));  // armed only while bots are live
            Unsubscribe(nameof(OnPlayerInput));  // armed only while someone stands graveside
            Unsubscribe(nameof(OnEntityTakeDamage)); // armed only during the fire probe (v4)
            Unsubscribe(nameof(OnPlayerChat));   // armed only during the P7-2 probe window
            Unsubscribe(nameof(OnNpcTarget));    // armed only while bots are live (fires ~1300x/sec in a hunt)
        }

        private void OnServerInitialized()
        {
            LoadAllData();
            LoadStory();
            LoadCast();
            LoadBaseCatalog();
            LoadEconomy();      // before DetectWipe — wipe clears it (decision 0005)
            LoadEconomyProfiles();
            LoadKits();         // tiered loadouts (data/FakeFriends/kits.json)
            DetectWipe();
            if (_index.Headstones == null) _index.Headstones = new List<HeadstoneRec>(); // pre-0.7.4 index files
            SeedLostHeadstones(); // one-off: this wipe's decayed graves, from the log
            // Backfill: generated identities predating v0.7.9 have empty backstories —
            // give them template ones so graves, chat, and epitaphs have material.
            foreach (var rec in _bots.Values)
                if (string.IsNullOrEmpty(rec.Identity.Backstory))
                {
                    rec.Identity.Backstory = GenerateBackstory(rec.Identity.Persona);
                    MarkDirty(rec.Identity.BotId);
                }
            var created = FillRoster();
            if (created > 0) DLog($"Roster fill: created {created} identities ({ActiveRosterCount()}/{_config.MaxBots} active, {_bots.Count} total incl. fallen).");
            ReconcileEntities();
            KillStrayBots();    // sweep orphaned duplicates left by a base relocation + reload
            PruneEconomy();     // drop economy for groups no longer in the roster
            SweepOrphanNightFires();
            EnforceShopBroadcast(); // stale map pins die at load, not at each shop's next restock
            EnforceDoorNav();       // N-2 Phase 1: bot doors NPC-openable in the mesh + one path proof line
            SaveDirty();
            _saveTimer = timer.Every(Mathf.Max(30f, _config.SaveIntervalSeconds), SaveDirty);
            _tickTimer = timer.Every(30f, SchedulerTick);
            _wanderTimer = timer.Every(WanderTickSeconds, WanderTick);
            _economyTimer = timer.Every(Mathf.Max(10f, _config.EconomyTickSeconds), EconomyTick);
            _socialTimer = timer.Every(300f, SocialTick); // 4D — coarse: warnings spread on a 5-min cadence
            LoadPhotoCache();                             // roleplayer camera prints (data/FakeFriends/photos)
            LoadGestureList();                            // v0.15: validate busk gestures against the live collection
            _chatTimer = timer.Every(15f, ChatTick);      // proximity chat + roleplayer antics
            // Graves on their own 1 s tick: the E-hook arms the moment someone walks up
            // (riding the 15 s ChatTick left a 3–5 s dead stone — live, 2026-07-24);
            // whisper spam is already bounded by its 10-min per-player cooldown.
            _graveTimer = timer.Every(1f, GraveWhisperTick);
            MaintainGraveMarkers(); // markers NOW, not at the first 5-min warden pass — every reload left a bare map window
            MaintainHamletMarkers(); // same lesson: the market pin comes back with the reload, not 5 min later
            if (_config.MetricsIntervalSeconds > 0f)
                _metricsTimer = timer.Every(Mathf.Max(15f, _config.MetricsIntervalSeconds), WriteMetrics); // Telegraf/Grafana feed
            if (System.IO.File.Exists(ProbeFireMarker)) timer.Once(30f, ProbeNativeFire); // 5A2-R one-shot
            if (System.IO.File.Exists(ProbeFightMarker)) timer.Once(30f, ProbeFight); // 5A2-R end-to-end demo
            if (System.IO.File.Exists(ProbeNavGraftMarker)) timer.Once(30f, ProbeNavGraft); // Phase 5.5 gate
            if (System.IO.File.Exists(ProbeWalkInMarker)) timer.Once(30f, ProbeWalkIn); // N-2 Phase 2 harness
            if (System.IO.File.Exists(ProbeHostile55Marker)) timer.Once(30f, ProbeHostile55); // Phase 5.5 end-to-end
            if (System.IO.File.Exists(ProbeMonuNavMarker)) timer.Once(30f, ProbeMonuNav); // Phase 6 gate 6P1
            if (System.IO.File.Exists(ProbeMonuLootMarker)) timer.Once(30f, ProbeMonuLoot); // Phase 6 gate 6P2
            if (System.IO.File.Exists(ProbeRunMarker)) timer.Once(30f, ProbeRun); // Phase 6 end-to-end run
            if (System.IO.File.Exists(ProbeSlmMarker)) timer.Once(30f, ProbeSlm); // Phase 7 gate P7-1/P7-2
            if (System.IO.File.Exists(ProbeSciCensusMarker)) timer.Once(30f, ProbeSciCensus); // Gen2 scientist migration census
            DLog($"FakeFriends {Version} up — roster {ActiveRosterCount()}/{_config.MaxBots} active ({_bots.Count} total incl. fallen), {_live.Count} live. Scheduler running (30s tick). ff.list to view.");
            NotifyPresence(); // a HUD redraws its Online count from our resumed sessions
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            _tickTimer?.Destroy();
            _socialTimer?.Destroy();
            _wanderTimer?.Destroy();
            _economyTimer?.Destroy();
            _chatTimer?.Destroy();
            Ff = null;
            // StartSleeping fires hooks other plugins may react to (even killing the
            // bot) — disarm our death hook and iterate a snapshot so re-entrant
            // UnlinkLive can't mutate _live mid-enumeration.
            Unsubscribe(nameof(OnEntityDeath));
            _deathHookArmed = false;
            var snapshot = new List<KeyValuePair<int, BasePlayer>>(_live);
            // Leave bot entities in the world as sleepers (they persist in the save);
            // put anyone still standing to sleep so they're in a safe, natural state.
            foreach (var kv in snapshot)
            {
                var bp = kv.Value;
                if (bp == null || bp.IsDestroyed) continue;
                DetachHostileDriver(bp); // 5.5: stop driving before the body sleeps (revert-on-unload)
                DetachRunnerDriver(kv.Key); // 6A: same doctrine for runners mid-run
                // 5B: a bound hostile can't sleep (scientist entity) — despawn it and
                // mark the identity Unspawned; the scheduler logs him back in later.
                var hostileEnt = bp as HostileNpc;
                if (hostileEnt != null)
                {
                    BotRecord hrec;
                    if (_bots.TryGetValue(kv.Key, out hrec))
                    {
                        hrec.State.Status = "Unspawned";
                        hrec.State.SessionEndAt = "";
                        hrec.State.EngineUserId = 0;
                        MarkDirty(kv.Key);
                    }
                    DespawnHostile(hostileEnt);
                    continue;
                }
                if (!bp.IsSleeping()) bp.StartSleeping();
                BotRecord rec;
                if (_bots.TryGetValue(kv.Key, out rec) && rec.State.Status == "Online")
                {
                    rec.State.Status = "Sleeping";
                    MarkDirty(kv.Key);
                }
            }
            // Test hostiles and prey bodies are never persisted — despawn on unload
            // (uMod guideline).
            foreach (var h in _testHostiles) DespawnHostile(h);
            _testHostiles.Clear();
            foreach (var p in _preyBodies) if (p != null && !p.IsDestroyed) p.Kill();
            _preyBodies.Clear();
            if (_probeFightBotId >= 0 || _probeBoar != null) ProbeFightCleanup(); // don't leak the sparring boar
            KillAllNightFires(); // revert world changes on unload (uMod guideline)
            KillGraveMarkers();  // markers are ours; graves themselves stay
            KillHamletMarkers(); // ditto — the hamlet record survives, the pin is runtime-only
            SaveDirty();
            Interface.CallHook("OnFakeFriendsPresenceChanged", 0); // nobody is "online" once we're gone
        }

        #endregion

        #region Presence API (for other plugins — a server HUD, a player list)

        // Bots never join BasePlayer.activePlayerList: the engine fills that list only
        // from a real network connection (PlayerInit), so any plugin that counts
        // "online players" from it never sees a resident. Offline residents ARE
        // ordinary sleepers (StartSleeping adds every player to sleepingPlayerList),
        // so a Sleeping count already includes them. This is the other half: how
        // many residents are up right now — Status Online with a live, non-dead body.
        // Call as `FakeFriends.Call("GetOnlineBotCount")` (boxed int).
        [HookMethod("GetOnlineBotCount")]
        public int GetOnlineBotCount()
        {
            var n = 0;
            foreach (var rec in _bots.Values)
            {
                if (rec.State.Status != "Online") continue;
                var bp = GetLive(rec.Identity.BotId);
                if (bp == null || bp.IsDestroyed || bp.IsDead()) continue;
                n++;
            }
            return n;
        }

        private int _lastPresenceCount = -1;

        // Fires OnFakeFriendsPresenceChanged(int onlineCount) to every plugin whenever
        // the online count moves. Cheap (one roster pass, no allocation), so it is
        // called liberally: session start/end, death, respawn, and the 30 s scheduler
        // tick as a catch-all. Log-on/off already fire OnPlayerSleepEnded/OnPlayerSleep
        // through the engine; this covers the awake spawns and deaths those miss.
        private void NotifyPresence()
        {
            var n = GetOnlineBotCount();
            if (n == _lastPresenceCount) return;
            _lastPresenceCount = n;
            Interface.CallHook("OnFakeFriendsPresenceChanged", n);
        }

        #endregion

        #region Admin commands

        // Phase 7 observability: endpoint health + budget at a glance, and a live
        // persona round-trip without needing a bot online or a player nearby.
        [ConsoleCommand("ff.slm")]
        private void CmdSlm(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!SlmEnabled) { arg.ReplyWith("SLM disabled (SLMEndpoint empty) — template behavior everywhere."); return; }
            var breaker = _slmBreakUntil > DateTime.Now ? $", BREAKER OPEN {(_slmBreakUntil - DateTime.Now).TotalSeconds:0}s" : "";
            var budget = _config.SLMHourlyBudget <= 0 ? "unlimited" : $"{_slmCallsHour}/{_config.SLMHourlyBudget}";
            arg.ReplyWith($"SLM {_config.SLMEndpoint} ({_config.SLMModel}): ok {_slmOk}, fallbacks {_slmFall}, last {_slmLastMs:0} ms, hour budget {budget}, chat hook {(_chatHookArmed ? "ARMED" : "off")}{breaker}");
        }

        [ConsoleCommand("ff.slm.test")]
        private void CmdSlmTest(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("usage: ff.slm.test <bot> <message...> (any cast member, online or not)"); return; }
            if (!SlmEnabled) { arg.ReplyWith("SLMEndpoint is empty — nothing to test."); return; }
            var sb = new StringBuilder();
            for (var i = 1; i < (arg.Args?.Length ?? 0); i++) { if (sb.Length > 0) sb.Append(' '); sb.Append(arg.GetString(i, "")); }
            var msg = sb.Length > 0 ? sb.ToString() : "hello there";
            var t0 = Time.realtimeSinceStartup;
            SlmAsk(SlmPersonaPrompt(rec, null), Trunc(msg, 200), rec.Identity.Name, reply =>
                Puts($"[ff.slm.test] {rec.Identity.Name} ({(Time.realtimeSinceStartup - t0) * 1000f:0} ms): {reply ?? "<failure — template fallback would fire>"}"));
            arg.ReplyWith($"asked {rec.Identity.Name} — reply lands in server console in ~2 s.");
        }

        private bool Allowed(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null) return true; // server console / RCON
            if (arg.Connection.authLevel >= 2) return true;
            var player = arg.Player();
            if (player != null && permission.UserHasPermission(player.UserIDString, _config.AdminPermission)) return true;
            arg.ReplyWith(L("Admin.NoPermission"));
            return false;
        }

        [ConsoleCommand("ff.list")]
        private void CmdList(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_bots.Count == 0)
            {
                arg.ReplyWith("Roster empty. Run ff.roster.fill to create identities from the cast file + generator.");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"=== FakeFriends roster ({ActiveRosterCount()}/{_config.MaxBots} active, {_bots.Count} total) — wipe '{_index.WipeId}' ===");
            foreach (var rec in _bots.Values)
            {
                var idn = rec.Identity;
                var next = "";
                if (!string.IsNullOrEmpty(rec.State.NextSessionAt))
                {
                    var t = ParseTime(rec.State.NextSessionAt);
                    if (t != DateTime.MaxValue) next = $" next {t:ddd HH:mm}";
                }
                var b = GetBase(rec);
                var baseTag = b == null ? "" : $" base s{b.Stage}({b.Anchors.FindAll(a => a.Filled).Count}/{b.Anchors.Count})";
                sb.AppendLine($"  #{idn.BotId,-3} {idn.Name,-16} {idn.Persona,-14} grp {idn.GroupId,-3} {(idn.FromCast ? "cast" : "gen "),-4} " +
                              $"{rec.State.Status,-10}{(GetLive(idn.BotId) != null ? " LIVE" : "")} d{rec.State.Deaths}/k{rec.State.Kills} L{Mathf.Max(0, LivesFor(rec) - rec.State.WipeDeaths)} {idn.Traits.ScheduleProfile}{baseTag}{next}");
            }
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("ff.info")]
        private void CmdInfo(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null)
            {
                arg.ReplyWith("Usage: ff.info <botId|name> — unknown bot.");
                return;
            }
            arg.ReplyWith(JsonConvert.SerializeObject(rec, Formatting.Indented));
        }

        [ConsoleCommand("ff.roster.fill")]
        private void CmdRosterFill(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var created = FillRoster();
            arg.ReplyWith(created > 0
                ? $"Created {created} identities ({_bots.Count}/{_config.MaxBots}). ff.list to view."
                : $"Roster already at {_bots.Count}/{_config.MaxBots}.");
        }

        [ConsoleCommand("ff.cast.reload")]
        private void CmdCastReload(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            LoadCast();
            arg.ReplyWith($"Cast reloaded: {_cast.Identities.Count} authored identities. (Existing bots keep their identities; new spawns use the updated cast.)");
        }

        [ConsoleCommand("ff.session")]
        private void CmdSession(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var action = (arg.GetString(0, "") ?? "").ToLowerInvariant();
            var rec = FindBot(arg.GetString(1, ""));
            if (rec == null || (action != "start" && action != "end"))
            {
                arg.ReplyWith("Usage: ff.session <start|end> <botId|name>");
                return;
            }
            if (action == "start")
            {
                if (rec.State.Status == "Dead") { arg.ReplyWith($"{rec.Identity.Name} is dead (respawns {rec.State.RespawnAt})."); return; }
                if (rec.State.Status == "Retired") { arg.ReplyWith($"{rec.Identity.Name} is retired — use ff.revive first."); return; }
                if (rec.State.Status == "Fallen") { arg.ReplyWith($"{rec.Identity.Name} has fallen (permadeath) — ff.revive grants fresh lives, or wait for the wipe."); return; }
                if (rec.State.Status == "Online") { arg.ReplyWith($"{rec.Identity.Name} is already online."); return; }
                StartSession(rec);
                arg.ReplyWith($"{rec.Identity.Name}: {rec.State.Status}, home {(rec.State.HomePos != null ? rec.State.HomePos.V.ToString() : "<none>")}");
            }
            else
            {
                if (rec.State.Status != "Online") { arg.ReplyWith($"{rec.Identity.Name} is not online ({rec.State.Status})."); return; }
                EndSession(rec);
                arg.ReplyWith($"{rec.Identity.Name}: now {rec.State.Status}, next session {rec.State.NextSessionAt}");
            }
        }

        [ConsoleCommand("ff.anchor.reset")]
        private void CmdAnchorReset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.anchor.reset <botId|name> [x z] — re-roll (or set) the bot's home region. Removes any existing base."); return; }

            // Clear any placed base first (owned-entity sweep at the old origin).
            var b = GetBase(rec);
            if (b != null)
            {
                SweepBaseEntities(b);
                SetBase(rec, null);
            }

            // Group members share the anchor — clear the whole group so it re-rolls together.
            foreach (var member in _bots.Values)
                if (member.Identity.GroupId == rec.Identity.GroupId)
                {
                    member.State.HomePos = null;
                    member.State.HomeBiome = "";
                    MarkDirty(member.Identity.BotId);
                }

            // Optional explicit placement: ff.anchor.reset <bot> <x> <z>
            if (arg.HasArgs(3))
            {
                var pos = new Vector3(arg.GetFloat(1), 0f, arg.GetFloat(2));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                rec.State.HomePos = new Vec3(pos);
                rec.State.HomeRadius = _config.WanderRadius;
                rec.State.HomeBiome = BiomeName(TerrainMeta.BiomeMap.GetBiomeMaxType(pos));
                MarkDirty(rec.Identity.BotId);
                arg.ReplyWith($"{rec.Identity.Name} anchored at your coordinates {pos} (biome {rec.State.HomeBiome}).");
            }
            else if (EnsureAnchor(rec))
                arg.ReplyWith($"{rec.Identity.Name} re-anchored at {rec.State.HomePos.V} (biome {rec.State.HomeBiome}). Next session spawns there.");
            else
                arg.ReplyWith($"Could not find a valid new anchor for {rec.Identity.Name} — try again or pass explicit coords.");

            // A live bot should relocate to its new home on its next session; if online now, send it home.
            var bot = GetLive(rec.Identity.BotId);
            if (bot != null && rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
        }

        [ConsoleCommand("ff.respawn")]
        private void CmdRespawn(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.respawn <botId|name>"); return; }
            if (rec.State.Status != "Dead") { arg.ReplyWith($"{rec.Identity.Name} is not dead ({rec.State.Status})."); return; }
            RespawnBot(rec);
            arg.ReplyWith($"{rec.Identity.Name}: {rec.State.Status}");
        }

        [ConsoleCommand("ff.tp")]
        private void CmdTp(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var caller = arg.Player();
            if (caller == null) { arg.ReplyWith("Run in-game."); return; }
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.tp <botId|name>"); return; }
            var bot = GetLive(rec.Identity.BotId);
            var target = bot != null ? bot.transform.position : rec.State.HomePos?.V ?? Vector3.zero;
            if (target == Vector3.zero) { arg.ReplyWith($"{rec.Identity.Name} has no entity or anchor yet."); return; }
            caller.Teleport(target + new Vector3(2f, 0.2f, 2f));
            arg.ReplyWith($"Teleported to {rec.Identity.Name} ({rec.State.Status}) at {target}.");
        }

        [ConsoleCommand("ff.retire")]
        private void CmdRetire(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.retire <botId|name>"); return; }
            RetireBot(rec);
            arg.ReplyWith($"{rec.Identity.Name} retired (entity despawned, excluded from scheduling). ff.revive to bring back.");
        }

        [ConsoleCommand("ff.revive")]
        private void CmdRevive(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.revive <botId|name>"); return; }
            if (!IsOut(rec)) { arg.ReplyWith($"{rec.Identity.Name} is not retired or fallen ({rec.State.Status})."); return; }
            ReviveBot(rec);
            arg.ReplyWith($"{rec.Identity.Name} revived — scheduler will bring them online.");
        }

        // Single owner of un-retiring (mirrors RetireBot). Restores base upkeep so a
        // retire -> revive round-trip is genuinely reversible for the base too.
        private void ReviveBot(BotRecord rec)
        {
            rec.State.Status = "Unspawned";
            rec.State.WipeDeaths = 0; // admin grace — a revived bot gets fresh lives
            MarkDirty(rec.Identity.BotId);
            var b = GetBase(rec);
            if (b != null && b.TcNetId != 0)
            {
                var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
                if (tc != null && !tc.IsDestroyed) StockTc(tc); // ReauthBase re-adds auth on spawn
            }
            Story("return", $"{rec.Identity.Name} returned to the island");
        }

        private void RetireBot(BotRecord rec)
        {
            var bot = GetLive(rec.Identity.BotId);
            // Unlink and mark Retired BEFORE Kill(): if anything routes the destroy
            // through the death flow, our death handler must see a non-bot entity.
            UnlinkLive(rec.Identity.BotId);
            rec.State.Status = "Retired";
            rec.State.EngineUserId = 0;
            rec.State.SessionEndAt = "";
            rec.State.NextSessionAt = "";
            rec.State.RespawnAt = "";
            MarkDirty(rec.Identity.BotId);
            if (bot != null && !bot.IsDestroyed) bot.Kill();
            StopBaseUpkeep(rec); // abandoned base decays naturally (handoff §3.2 lifecycle)
            Story("retire", $"{rec.Identity.Name} left the island");
        }

        // Empty the base's TC so upkeep can't be paid — Rust's decay then rots the
        // abandoned structure over time, leaving a ruin players can stumble on, rather
        // than despawning it instantly. Used on retire (and Phase 4 permadeath).
        private void StopBaseUpkeep(BotRecord rec)
        {
            var b = GetBase(rec);
            if (b == null || b.TcNetId == 0) return;
            var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
            if (tc == null || tc.IsDestroyed || tc.inventory == null) return;
            while (tc.inventory.itemList.Count > 0)
            {
                var item = tc.inventory.itemList[0];
                item.RemoveFromContainer();
                item.Remove();
            }
            tc.authorizedPlayers.Clear();
            tc.SendNetworkUpdate();
            DLog($"[base] stopped upkeep for {rec.Identity.Name}'s base — it will decay naturally.");
        }

        [ConsoleCommand("ff.story")]
        private void CmdStory(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var n = Mathf.Clamp(arg.GetInt(0, 25), 1, 200);
            var sb = new StringBuilder();
            sb.AppendLine($"=== story log (last {Mathf.Min(n, _story.Count)} of {_story.Count}) ===");
            for (var i = Mathf.Max(0, _story.Count - n); i < _story.Count; i++)
                sb.AppendLine($"  Day {_story[i].Day}: {_story[i].Text}");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("ff.wipe.status")]
        private void CmdWipeStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith($"Current wipe id: '{CurrentWipeId()}' | stored: '{_index.WipeId}' | WipeCastReset={_config.WipeCastReset} WipeGrudgeReset={_config.WipeGrudgeReset}");
        }

        private BotRecord FindBot(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            int id;
            if (int.TryParse(idOrName, out id))
            {
                BotRecord rec;
                return _bots.TryGetValue(id, out rec) ? rec : null;
            }
            foreach (var rec in _bots.Values)
                if (string.Equals(rec.Identity.Name, idOrName, StringComparison.OrdinalIgnoreCase)) return rec;
            return null;
        }

        #endregion

        #region Base system (Phase 2)

        // Capture: `ff.base.capture <persona> <stage>` wraps CopyPaste's copy (in-game,
        // aim at the build). Paste: shell goes down via CopyPaste; each spawned interior
        // deployable is recorded as a purpose-tagged anchor at its exact world transform
        // and removed — the base starts bare and the interior fill re-places the same
        // furniture over time (handoff §3.2.1: authored anchors, emergent fill).

        private class BaseCatalog
        {
            // persona → ordered stage filenames (copypaste data names)
            public Dictionary<string, List<string>> Stages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private BaseCatalog _baseCatalog = new BaseCatalog();

        // Base entities are stamped with a STABLE per-bot ownership id (not the volatile
        // engine userID, which changes each respawn) so removal/decay sweeps always
        // match. TC building-privilege auth is separate and uses live engine ids.
        private const ulong BaseOwnerBase = 76561199992000000UL;
        private static ulong BaseOwnerId(int botId) => BaseOwnerBase + (ulong)botId;

        private class AnchorDef
        {
            public string Prefab;     // full prefab path to re-place later
            public string Purpose;    // bed | storage | locker | furnace | workbench | shop | mixing | decor
            public Vec3 Pos;          // world position (recorded at paste time)
            public Vec3 Rot;          // world euler
            public bool Filled;
        }

        private class BotBase
        {
            public int Stage;               // 1-based; 0 = no base
            public string File = "";
            public Vec3 Origin;
            public ulong TcNetId;
            public ulong OwnerId;           // OwnerID stamped on pasted entities (for removal sweeps)
            public int BlockCount;          // WG-4: building blocks in the intact shell (census baseline)
            public string NextFillAt = "";  // when the bot places its next interior piece
            public List<AnchorDef> Anchors = new List<AnchorDef>();
            public List<DoorDef> Doors = new List<DoorDef>(); // shell-door snapshot for repair (2026-07-23)
        }

        private class DoorDef
        {
            public Vec3 Pos;
            public string Prefab = "";
            public float Yaw;
        }

        // Order the bot furnishes anchors in: survival first, production, then flavor.
        private static readonly Dictionary<string, int> FillPriority = new Dictionary<string, int>
        {
            ["bed"] = 0, ["storage"] = 1, ["furnace"] = 2, ["planter"] = 3, ["workbench"] = 4,
            ["refinery"] = 4, ["mixing"] = 5, ["locker"] = 6, ["shop"] = 7, ["decor"] = 8,
        };

        private void LoadBaseCatalog()
        {
            _baseCatalog = SafeRead<BaseCatalog>($"{DataRoot}/bases") ?? new BaseCatalog();
        }

        private void SaveBaseCatalog() => Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/bases", _baseCatalog);

        // Interior deployables that become anchors instead of pre-placed furniture.
        // Doors, locks, TC, shutters etc. stay in the shell.
        private static readonly Dictionary<string, string> AnchorPurposes = new Dictionary<string, string>
        {
            ["sleepingbag"] = "bed", ["bed_deployed"] = "bed",
            ["box.wooden.large"] = "storage", ["woodbox_deployed"] = "storage", ["coffinstorage"] = "storage",
            ["locker.deployed"] = "locker",
            ["furnace"] = "furnace", ["furnace.large"] = "furnace", ["bbq.deployed"] = "furnace",
            ["refinery"] = "refinery", ["refinery_small_deployed"] = "refinery", // own bucket: gates @refinery smelt recipes
            ["workbench1.deployed"] = "workbench", ["workbench2.deployed"] = "workbench", ["workbench3.deployed"] = "workbench",
            ["vendingmachine.deployed"] = "shop",
            ["mixingtable.deployed"] = "mixing",
            ["rug.deployed"] = "decor", ["rug.bear.deployed"] = "decor", ["chair.deployed"] = "decor",
            ["table.deployed"] = "decor", ["lantern.deployed"] = "decor", ["tunalight.deployed"] = "decor",
            ["shelves"] = "decor", ["spinner.wheel.deployed"] = "decor", ["fridge.deployed"] = "storage",
            // Planters are the Farmer's production anchor (crop rules gate on one standing).
            ["planter.small.deployed"] = "planter", ["planter.large.deployed"] = "planter",
        };

        private static string AnchorPurposeFor(BaseEntity ent)
        {
            if (ent is BuildingPrivlidge) return null;         // TC stays
            var shortname = ent.ShortPrefabName ?? "";
            string purpose;
            if (AnchorPurposes.TryGetValue(shortname, out purpose)) return purpose;
            // fall back on category sniffing for skinned/variant prefabs
            if (shortname.Contains("sleepingbag") || shortname.Contains("bed_")) return "bed";
            if (shortname.Contains("box.") || shortname.Contains("woodbox")) return "storage";
            if (shortname.Contains("refinery")) return "refinery";
            if (shortname.Contains("furnace") || shortname.Contains("bbq")) return "furnace";
            if (shortname.Contains("workbench")) return "workbench";
            if (shortname.Contains("vendingmachine")) return "shop";
            if (shortname.Contains("locker")) return "locker";
            if (shortname.Contains("mixingtable")) return "mixing";
            if (shortname.Contains("planter")) return "planter";
            if (shortname.Contains("rug") || shortname.Contains("chair") || shortname.Contains("table.") ||
                shortname.Contains("light") || shortname.Contains("lantern")) return "decor";
            return null; // unknown → stays in the shell (doors, walls, campfire, TC)
        }

        [ConsoleCommand("ff.base.capture")]
        private void CmdBaseCapture(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var caller = arg.Player();
            if (caller == null) { arg.ReplyWith("Run in-game while aiming at the build."); return; }
            if (CopyPaste == null) { arg.ReplyWith("CopyPaste plugin not loaded — capture unavailable."); return; }
            var persona = arg.GetString(0, "");
            var stage = arg.GetInt(1, 0);
            if (Array.IndexOf(Personas, persona) < 0 || stage < 1 || stage > 5)
            {
                arg.ReplyWith("Usage: ff.base.capture <Trapper|Farmer|Smelter|MonumentRunner|Hostile|Roleplayer> <stage 1-3>. Aim at the build.");
                return;
            }
            var filename = $"ff_{persona.ToLowerInvariant()}_s{stage}";
            var callerId = (ulong)caller.userID;
            var result = CopyPaste.Call("TryCopyFromSteamId", callerId, filename, new string[0], new Action(() =>
            {
                List<string> stages;
                if (!_baseCatalog.Stages.TryGetValue(persona, out stages))
                    _baseCatalog.Stages[persona] = stages = new List<string>();
                while (stages.Count < stage) stages.Add("");
                stages[stage - 1] = filename;
                SaveBaseCatalog();
                DLog($"[base] captured {persona} stage {stage} -> copypaste/{filename} (by {caller.displayName})");
                SendReply(caller, $"Captured {persona} stage {stage} as '{filename}'. Catalog updated — ff.base.list to view.");
            }));
            if (result is string) arg.ReplyWith($"Capture failed: {result}");
            else arg.ReplyWith($"Capturing {persona} stage {stage}…");
        }

        // Kill every entity stamped with this base's stable owner id near its origin.
        // Shared by remove, anchor-reset, and stage-upgrade.
        private int SweepBaseEntities(BotBase b)
        {
            if (b == null || b.OwnerId == 0) return 0;
            var killed = 0;
            var near = new List<BaseEntity>();
            Vis.Entities(b.Origin.V, 60f, near);
            foreach (var ent in near)
            {
                if (ent == null || ent.IsDestroyed || ent is BasePlayer) continue;
                if (ent.OwnerID != b.OwnerId) continue;
                ent.Kill();
                killed++;
            }
            return killed;
        }

        [ConsoleCommand("ff.base.remove")]
        private void CmdBaseRemove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.base.remove <botId|name>"); return; }
            var b = GetBase(rec);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no recorded base."); return; }
            var killed = SweepBaseEntities(b);
            SetBase(rec, null);
            MarkDirty(rec.Identity.BotId);
            DLog($"[base] removed {rec.Identity.Name}'s base at {b.Origin.V} ({killed} entities)");
            arg.ReplyWith($"Removed {rec.Identity.Name}'s base ({killed} entities).");
        }

        [ConsoleCommand("ff.base.stage")]
        private void CmdBaseStage(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.base.stage <botId|name> [targetStage] — advance the bot's base to the next (or given) stage."); return; }
            var b = GetBase(rec);
            var current = b?.Stage ?? 0;
            var target = arg.HasArgs(2) ? arg.GetInt(1) : current + 1;
            if (target < 1) { arg.ReplyWith("Target stage must be >= 1."); return; }

            List<string> stages;
            if (!_baseCatalog.Stages.TryGetValue(rec.Identity.Persona, out stages) || stages.Count < target || string.IsNullOrEmpty(stages[target - 1]))
            {
                arg.ReplyWith($"No captured stage {target} blueprint for {rec.Identity.Persona}. ff.base.capture {rec.Identity.Persona} {target} first.");
                return;
            }
            if (target == current) { arg.ReplyWith($"{rec.Identity.Name} is already at stage {current}."); return; }

            // PlaceBase demolishes the old base itself, only after its own validation
            // passes — so a failed upgrade never leaves the bot homeless. A fresh stage
            // = a new interior plan, so the "just moved in" sparse period is intended.
            var err = PlaceBase(rec, target, float.NaN);
            if (err != null) { arg.ReplyWith($"Stage change failed: {err}"); return; }
            arg.ReplyWith($"{rec.Identity.Name}: upgrading base {current} -> {target} at {rec.State.HomePos?.V}. Interior re-furnishes over time (ff.base.fill to force).");
        }

        [ConsoleCommand("ff.base.list")]
        private void CmdBaseList(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sb = new StringBuilder();
            sb.AppendLine($"=== base catalog (CopyPaste {(CopyPaste != null ? "loaded" : "MISSING")}) ===");
            foreach (var p in Personas)
            {
                List<string> stages;
                _baseCatalog.Stages.TryGetValue(p, out stages);
                var have = stages == null ? "none" : string.Join(", ", stages);
                sb.AppendLine($"  {p,-14} stages: {have}");
            }
            sb.AppendLine("Capture: build in-game, then ff.base.capture <persona> <stage> while aiming at it.");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("ff.base.paste")]
        private void CmdBasePaste(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var stage = arg.GetInt(1, 1);
            if (rec == null) { arg.ReplyWith("Usage: ff.base.paste <botId|name> [stage] [heightNudge] — pastes at the bot's home anchor."); return; }
            // Optional live height override so a good value can be dialed in, then
            // baked into config BasePasteHeightOffset. Default -1 means "use config".
            var nudge = arg.HasArgs(3) ? arg.GetFloat(2) : float.NaN;
            var err = PlaceBase(rec, stage, nudge);
            arg.ReplyWith(err ?? $"Pasting stage {stage} base for {rec.Identity.Name} at {rec.State.HomePos?.V} (height nudge {(float.IsNaN(nudge) ? _config.BasePasteHeightOffset : nudge):0.00})…");
        }

        // Reads the CopyPaste blueprint and returns the rigid `height` value that seats
        // the foundations on the highest ground under their footprint. With autoheight
        // OFF, a pasted entity's world Y = localOffset.y + origin.y + height, so to put
        // the most-buried foundation's origin on its highest ground corner:
        //   height = max_over_foundations( groundMax_corner - localOffset.y ) - origin.y
        // Returns NaN if the blueprint has no foundations or can't be parsed.
        private float ComputeFoundationFitHeight(string file, Vector3 origin)
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.GetDatafile($"copypaste/{file}");
                var entities = data["entities"] as List<object>;
                if (entities == null) return float.NaN;
                var need = float.NegativeInfinity;
                var found = false;
                foreach (var raw in entities)
                {
                    var ent = raw as Dictionary<string, object>;
                    if (ent == null) continue;
                    var prefab = ent.ContainsKey("prefabname") ? ent["prefabname"] as string : null;
                    if (prefab == null || prefab.IndexOf("/foundation", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var pos = ent["pos"] as Dictionary<string, object>;
                    if (pos == null) continue;
                    var lx = Convert.ToSingle(pos["x"]);
                    var ly = Convert.ToSingle(pos["y"]);
                    var lz = Convert.ToSingle(pos["z"]);
                    // We paste with rotationCorrection 0, so world XZ = origin + local XZ.
                    var cx = origin.x + lx;
                    var cz = origin.z + lz;
                    var groundMax = float.NegativeInfinity;
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            var h = TerrainMeta.HeightMap.GetHeight(new Vector3(cx + dx * 1.5f, 0f, cz + dz * 1.5f));
                            if (h > groundMax) groundMax = h;
                        }
                    var h_i = groundMax - ly; // origin.y subtracted once at the end
                    if (h_i > need) need = h_i;
                    found = true;
                }
                if (!found) return float.NaN;
                return need - origin.y;
            }
            catch (Exception e)
            {
                PrintWarning($"ComputeFoundationFitHeight({file}) failed: {e.Message} — using autoheight fallback.");
                return float.NaN;
            }
        }

        // Horizontal bounding radius of a blueprint (max distance from its origin to any
        // entity), plus wall-thickness margin. Cached per file. Used to size the clear
        // area a stage needs so a bigger stage doesn't push into terrain.
        private readonly Dictionary<string, float> _blueprintRadius = new Dictionary<string, float>();

        private float BlueprintClearRadius(string file)
        {
            float cached;
            if (_blueprintRadius.TryGetValue(file, out cached)) return cached;
            var radius = _config.BaseFootprint;
            try
            {
                var data = Interface.Oxide.DataFileSystem.GetDatafile($"copypaste/{file}");
                var entities = data["entities"] as List<object>;
                if (entities != null)
                {
                    var maxSq = 0f;
                    foreach (var raw in entities)
                    {
                        var ent = raw as Dictionary<string, object>;
                        var pos = ent?["pos"] as Dictionary<string, object>;
                        if (pos == null) continue;
                        var lx = Convert.ToSingle(pos["x"]);
                        var lz = Convert.ToSingle(pos["z"]);
                        var d = lx * lx + lz * lz;
                        if (d > maxSq) maxSq = d;
                    }
                    radius = Mathf.Sqrt(maxSq) + 2.5f; // + wall thickness / clearance
                }
            }
            catch (Exception e) { PrintWarning($"BlueprintClearRadius({file}): {e.Message}"); }
            _blueprintRadius[file] = radius;
            return radius;
        }

        private float PersonaMaxBaseRadius(string persona)
        {
            List<string> stages;
            if (!_baseCatalog.Stages.TryGetValue(persona, out stages) || stages == null) return _config.BaseFootprint;
            var max = _config.BaseFootprint;
            foreach (var f in stages)
                if (!string.IsNullOrEmpty(f)) max = Mathf.Max(max, BlueprintClearRadius(f));
            return max;
        }

        // Search outward from a preferred origin for a spot where the given footprint is
        // clear (rings at increasing radius). Returns Vector3.zero if none found nearby.
        private Vector3 FindClearBaseSpotNear(Vector3 preferred, float footprint, int myGroup)
        {
            if (IsBaseSpotClear(preferred, footprint) && !NearOtherAnchor(preferred, 150f, myGroup)) return preferred;
            for (var ring = 1; ring <= 6; ring++)
            {
                var r = ring * footprint * 0.6f;
                for (var a = 0; a < 12; a++)
                {
                    var ang = a * 30f * Mathf.Deg2Rad;
                    var p = preferred + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                    p.y = TerrainMeta.HeightMap.GetHeight(p);
                    if (p.y < 3f) continue;
                    if (NearMonument(p, 100f) || NearOtherAnchor(p, 150f, myGroup)) continue;
                    if (IsBaseSpotClear(p, footprint)) return p;
                }
            }
            return Vector3.zero;
        }

        private readonly Dictionary<int, List<BaseEntity>> _pasteBuffers = new Dictionary<int, List<BaseEntity>>();

        private string PlaceBase(BotRecord rec, int stage, float heightNudge)
        {
            if (CopyPaste == null) return "CopyPaste plugin not loaded.";
            if (rec.State.HomePos == null) return $"{rec.Identity.Name} has no home anchor yet (starts with first session).";
            List<string> stages;
            if (!_baseCatalog.Stages.TryGetValue(rec.Identity.Persona, out stages) || stages.Count < stage || string.IsNullOrEmpty(stages[stage - 1]))
                return $"No captured stage {stage} blueprint for persona {rec.Identity.Persona} — ff.base.capture first.";
            var file = stages[stage - 1];
            var botId = rec.Identity.BotId;
            if (_pasteBuffers.ContainsKey(botId)) return $"A base paste is already in progress for {rec.Identity.Name}.";
            var origin = rec.State.HomePos.V;

            // Re-evaluate the spot for THIS stage's actual footprint (a bigger stage can
            // push into rocks/terrain the smaller anchor cleared). If blocked, relocate
            // to the nearest clear spot and move the home there so the base and the bot's
            // wander stay together (answers "shift the base if it runs into terrain").
            var footprint = BlueprintClearRadius(file);
            var blockReason = SpotBlockReason(origin, footprint);
            if (blockReason != null)
            {
                var shifted = FindClearBaseSpotNear(origin, footprint, rec.Identity.GroupId);
                if (shifted == Vector3.zero)
                    return $"Stage {stage} footprint ({footprint:0.#}m) doesn't fit at {rec.Identity.Name}'s home — blocked by {blockReason} — and no clear spot found nearby. Try ff.anchor.reset.";
                DLog($"[base] {rec.Identity.Name}: stage {stage} footprint blocked at home ({blockReason}) — relocating {Vector3.Distance(origin, shifted):0.#}m to {shifted}.");
                origin = shifted;
                // Move the whole group's home to the new base spot.
                foreach (var member in _bots.Values)
                    if (member.Identity.GroupId == rec.Identity.GroupId)
                    {
                        member.State.HomePos = new Vec3(origin);
                        member.State.HomeBiome = BiomeName(TerrainMeta.BiomeMap.GetBiomeMaxType(origin));
                        MarkDirty(member.Identity.BotId);
                    }
                Story("relocate", $"{rec.Identity.Name} moved home to clearer ground near {GridRef(origin)}");
            }

            // Replacement is atomic: all synchronous validation has passed, so it's now
            // safe to demolish any existing base (at its own recorded origin) before the
            // new one goes down. Doing it here — not in the callers — means a failed
            // placement never leaves a bot without a home, and no path can orphan a base.
            var existing = GetBase(rec);
            if (existing != null)
            {
                // Preserve the stored economy across the upgrade — snapshot the boxes'
                // goods before the old base is demolished, re-deposited into the new base
                // once it pastes (RestoreCarryover). The bot "moves house", keeps its stock.
                SnapshotCarryover(rec.Identity.GroupId, existing);
                SweepBaseEntities(existing);
            }

            _pasteBuffers[botId] = new List<BaseEntity>();

            // Compute the vertical fit ourselves from the foundation footprint, because
            // CopyPaste's autoheight is AIM-POINT relative (it lifts the build so the
            // point the capturer aimed at sits at ground+1) — aim at a wall and the
            // foundations end up buried. We instead seat the foundations on the highest
            // ground under their corners, aim-independent, and pass it as a rigid height
            // with autoheight OFF. Falls back to autoheight if the blueprint can't be read.
            var autoFit = ComputeFoundationFitHeight(file, origin);
            var trim = float.IsNaN(heightNudge) ? _config.BasePasteHeightOffset : heightNudge;
            string[] pasteArgs;
            if (float.IsNaN(autoFit))
            {
                DLog($"[base] {file}: no foundations found in blueprint — falling back to CopyPaste autoheight.");
                pasteArgs = new[]
                {
                    "deployables", "true", "inventories", "false",
                    "autoheight", "true", "height", trim.ToString("0.###"),
                    "stability", "true", "auth", "false", "entityowner", "false"
                };
            }
            else
            {
                var height = autoFit + trim;
                DLog($"[base] {file}: footprint-fit height {height:0.00} (autoFit {autoFit:0.00} + trim {trim:0.00})");
                pasteArgs = new[]
                {
                    "deployables", "true", "inventories", "false",
                    "autoheight", "false", "height", height.ToString("0.###"),
                    "stability", "true", "auth", "false", "entityowner", "false"
                };
            }
            var result = CopyPaste.Call("TryPasteFromVector3", origin, 0f, file, pasteArgs,
                new Action(() => FinishBasePaste(botId, stage, file, origin)),
                new Action<BaseEntity>(ent =>
                {
                    List<BaseEntity> buf;
                    if (ent != null && _pasteBuffers.TryGetValue(botId, out buf)) buf.Add(ent);
                }));
            if (result is string)
            {
                _pasteBuffers.Remove(botId);
                return $"Paste failed: {result}";
            }
            return null;
        }

        private void FinishBasePaste(int botId, int stage, string file, Vector3 origin)
        {
            BotRecord rec;
            List<BaseEntity> ents;
            if (!_bots.TryGetValue(botId, out rec) || !_pasteBuffers.TryGetValue(botId, out ents)) return;
            _pasteBuffers.Remove(botId);

            var ownerStamp = BaseOwnerId(botId);
            var newBase = new BotBase { Stage = stage, File = file, Origin = new Vec3(origin), OwnerId = ownerStamp };

            // NOTE: vertical positioning is handled rigidly by CopyPaste (autoheight +
            // a config height nudge) BEFORE spawn. We never move pasted building blocks
            // afterward — Rust re-runs stability/socket settling on a moved block, which
            // shears foundations and walls apart (live finding: floating walls).

            BuildingPrivlidge tc = null;
            var anchors = 0;
            foreach (var ent in ents)
            {
                if (ent == null || ent.IsDestroyed) continue;
                if (ent is BuildingBlock) newBase.BlockCount++; // WG-4 census baseline
                var purpose = AnchorPurposeFor(ent);
                if (purpose != null)
                {
                    newBase.Anchors.Add(new AnchorDef
                    {
                        Prefab = ent.PrefabName,
                        Purpose = purpose,
                        Pos = new Vec3(ent.transform.position),
                        Rot = new Vec3(ent.transform.rotation.eulerAngles),
                    });
                    anchors++;
                    ent.Kill(); // base starts as an empty shell; interior fills over time
                    continue;
                }
                ent.OwnerID = ownerStamp;
                var priv = ent as BuildingPrivlidge;
                if (priv != null) tc = priv;
            }
            if (tc != null)
            {
                newBase.TcNetId = tc.net?.ID.Value ?? 0;
                AuthorizeGroupOnTc(tc, rec);
                StockTc(tc);
            }
            newBase.NextFillAt = DateTime.Now.AddMinutes(2f).ToString("o"); // start furnishing soon
            // The vending machine is mounted in a wall frame / doorway in the blueprints,
            // so leaving it need-gated exposes an open shopfront into the base. Place any
            // shop anchor immediately to seal the opening; the rest of the interior still
            // fills over time (ServiceShop configures the shop later when goods exist).
            if (newBase.Anchors != null)
                foreach (var a in newBase.Anchors)
                    if (a.Purpose == "shop") FillAnchor(rec, rec, a);
            RestoreCarryover(rec, newBase); // re-deposit goods preserved from the old base (upgrade)
            LockBaseDoors(newBase, rec); // F5 — seal the shell (bots open via SetOpen anyway)
            MarkBotDoors(newBase);       // N-2 — NPC-openable in the mesh (decision 0011)
            SetBase(rec, newBase);
            MarkDirty(botId);
            DLog($"[base] {rec.Identity.Name}: stage {stage} '{file}' pasted at {origin} — {anchors} interior anchors recorded, {newBase.BlockCount} shell blocks, TC {(tc != null ? "authorized+stocked" : "NOT FOUND")}");
            if (stage >= 2)
            {
                // IN-4.3 — the first stage-2/3 shells of the wipe report their own mesh fit
                // once the tiles have rebuilt: anchor heights above the yard and the
                // doors-allowed paths. Anchors > 2.5 m up keep the hop until this reads clean.
                var r2 = rec; var st = stage;
                timer.Once(45f, () =>
                {
                    if (r2 == null || r2.State.HomePos == null) return;
                    var bb = GetBase(r2);
                    if (bb == null || bb.Anchors == null) return;
                    var yard = GroundSnap(r2.State.HomePos.V);
                    var sb = new StringBuilder();
                    foreach (var purpose in new[] { "bed", "storage", "furnace", "workbench" })
                    {
                        AnchorDef a = null;
                        foreach (var x in bb.Anchors) if (x.Purpose == purpose && x.Pos != null) { a = x; break; }
                        if (a == null) continue;
                        sb.Append($" {purpose}: +{(a.Pos.V.y - yard.y):0.0} m, doors allowed {DescribePath(yard, a.Pos.V, true)};");
                    }
                    DLog($"[door] {r2.Identity.Name}: stage {st} shell mesh fit —{sb}");
                });
            }
            if (_repairPaste.Remove(botId)) // WG-4: same-stage shell repair, not a new build
                Story("repair", $"{rec.Identity.Name} patched the cabin back up after the attack");
            else
                Story("build", $"{rec.Identity.Name} {(stage == 1 ? "built a home" : $"expanded to stage {stage}")} near {GridRef(origin)}");
        }

        // Goods lifted from a base's boxes just before it's demolished for an upgrade,
        // held per group until the new base pastes. Transient (memory-only): a reload in
        // the ~1-2s paste window forfeits it, which is acceptable for an upgrade edge.
        private readonly Dictionary<int, Dictionary<string, int>> _baseCarryover = new Dictionary<int, Dictionary<string, int>>();

        private void SnapshotCarryover(int gid, BotBase b)
        {
            var buckets = FindBaseContainers(b);
            if (buckets.Count == 0) return;
            var tally = new Dictionary<string, int>();
            foreach (var kv in buckets)
                foreach (var sc in kv.Value)
                    foreach (var it in sc.inventory.itemList)
                    {
                        if (it?.info == null || it.info.shortname == "scrap") continue; // scrap lives in the ledger, not the boxes
                        int n; tally.TryGetValue(it.info.shortname, out n);
                        tally[it.info.shortname] = n + it.amount;
                    }
            if (tally.Count > 0) _baseCarryover[gid] = tally;
        }

        private void RestoreCarryover(BotRecord rec, BotBase newBase)
        {
            Dictionary<string, int> tally;
            if (!_baseCarryover.TryGetValue(rec.Identity.GroupId, out tally)) return;
            _baseCarryover.Remove(rec.Identity.GroupId);
            if (tally.Count == 0 || newBase.Anchors == null) return;

            // Place the stash boxes now so the carried-over stock has homes (an upgrade
            // keeps the stockpile), then re-deposit each good into its routed box.
            foreach (var a in newBase.Anchors)
                if (!a.Filled && (a.Purpose == "storage" || a.Purpose == "food" || a.Purpose == "locker"))
                    FillAnchor(rec, rec, a);
            var buckets = FindBaseContainers(newBase);
            int restored = 0, lost = 0;
            foreach (var kv in tally)
            {
                var box = PickContainer(buckets, DepositRouteFor(kv.Key));
                var got = box != null ? DepositToContainer(box.inventory, kv.Key, kv.Value) : 0;
                restored += got; lost += kv.Value - got;
            }
            DLog($"[base] {rec.Identity.Name}: carried {restored} stored item(s) into the upgraded base{(lost > 0 ? $" ({lost} didn't fit)" : "")}.");
        }

        private void AuthorizeGroupOnTc(BuildingPrivlidge tc, BotRecord rec)
        {
            tc.authorizedPlayers.Clear();
            foreach (var member in _bots.Values)
            {
                if (member.Identity.GroupId != rec.Identity.GroupId) continue;
                if (member.State.EngineUserId != 0) tc.authorizedPlayers.Add(member.State.EngineUserId);
            }
            tc.SendNetworkUpdate();
        }

        private void StockTc(BuildingPrivlidge tc)
        {
            if (tc.inventory == null) return;
            var stock = _config.TcInitialStock;
            if (stock == null || stock.Count == 0) return;
            foreach (var kv in stock)
                if (kv.Value > 0) GiveContainerItem(tc.inventory, kv.Key, kv.Value);
        }

        private void GiveContainerItem(ItemContainer container, string shortname, int amount)
        {
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            if (!item.MoveToContainer(container)) item.Remove();
        }

        // BotBase lives in the record's Social-safe extra bag to avoid a schema bump
        // mid-phase; serialized as its own JSON node.
        private BotBase GetBase(BotRecord rec)
        {
            object raw;
            if (rec.Extra == null || !rec.Extra.TryGetValue("Base", out raw) || raw == null) return null;
            var jt = raw as Newtonsoft.Json.Linq.JToken;
            if (jt != null) return jt.ToObject<BotBase>();
            return raw as BotBase;
        }

        private void SetBase(BotRecord rec, BotBase b)
        {
            rec.Extra = rec.Extra ?? new Dictionary<string, object>();
            rec.Extra["Base"] = b;
        }

        // A destroyed base's rebuild note (WB-4). Same Extra-bag pattern as BotBase so
        // it survives reloads; lives on whichever record held the lost base.
        private class BaseRebuild
        {
            public int Stage = 1;
            public string At = "";     // when the group starts rebuilding ("" = never)
            public string LostAt = ""; // when the loss was detected
        }

        private BaseRebuild GetRebuild(BotRecord rec)
        {
            object raw;
            if (rec.Extra == null || !rec.Extra.TryGetValue("BaseRebuild", out raw) || raw == null) return null;
            var jt = raw as Newtonsoft.Json.Linq.JToken;
            if (jt != null) return jt.ToObject<BaseRebuild>();
            return raw as BaseRebuild;
        }

        private void SetRebuild(BotRecord rec, BaseRebuild rb)
        {
            rec.Extra = rec.Extra ?? new Dictionary<string, object>();
            rec.Extra["BaseRebuild"] = rb;
        }

        private BaseRebuild GetGroupRebuild(int groupId, out BotRecord holder)
        {
            holder = null;
            foreach (var rec in _bots.Values)
            {
                if (rec.Identity.GroupId != groupId) continue;
                var rb = GetRebuild(rec);
                if (rb != null) { holder = rec; return rb; }
            }
            return null;
        }

        // Interior fill: while a bot is online and near home, it places one recorded
        // anchor at a time in priority order — bag + box first, flavor last. Bases arrive
        // as empty shells and furnish over the bot's life (handoff §3.2.1). Driven from
        // SchedulerTick; the real economy gates this in Phase 3.
        private void InteriorFillTick()
        {
            var now = DateTime.Now;
            foreach (var botId in _live.Keys)
            {
                BotRecord rec;
                if (!_bots.TryGetValue(botId, out rec) || rec.State.Status != "Online") continue;
                // Any online member furnishes the GROUP base — not just the record
                // holder, who may be out for the wipe (Hank fell with the bed still
                // unplaced, so groupmate Sadie could never respawn indoors).
                BotRecord holder;
                var b = GetGroupBase(rec.Identity.GroupId, out holder);
                if (b == null || holder == null || b.Anchors == null || b.Anchors.Count == 0) continue;
                if (_pasteBuffers.ContainsKey(holder.Identity.BotId)) continue; // mid-upgrade: GetBase returns the doomed old base until FinishBasePaste
                if (!string.IsNullOrEmpty(b.NextFillAt) && ParseTime(b.NextFillAt) > now) continue;

                var anyUnfilled = false;
                foreach (var a in b.Anchors) if (!a.Filled) { anyUnfilled = true; break; }
                if (!anyUnfilled) continue; // fully furnished

                // F3 — need/resource gating: survival pieces (bed, box) go in first; a
                // furnace waits until there's meat/ore to cook, a shop until there are
                // goods to sell, a locker until a spare kit is affordable to bank. The
                // ledger + real box contents drive the gate. ff.base.fill overrides it.
                var ge = Econ(rec.Identity.GroupId);
                var buckets = FindBaseContainers(b);
                AnchorDef next = null;
                var bestPriority = int.MaxValue;
                foreach (var a in b.Anchors)
                {
                    if (a.Filled || !AnchorNeedMet(rec, ge, buckets, a.Purpose)) continue;
                    int pri;
                    if (!FillPriority.TryGetValue(a.Purpose, out pri)) pri = 9;
                    if (pri < bestPriority) { bestPriority = pri; next = a; }
                }

                // Advance the pace timer whether or not anything was placeable, so a bot
                // waiting on a need rechecks at the fill cadence rather than every tick.
                b.NextFillAt = now.AddMinutes(Mathf.Max(0.5f, _config.InteriorFillMinutes)).ToString("o");
                if (next != null)
                {
                    var placed = FillAnchor(rec, holder, next);
                    if (placed && (next.Purpose == "shop" || next.Purpose == "furnace"))
                        Story("furnish", $"{rec.Identity.Name} set up a {(next.Purpose == "shop" ? "shop" : "furnace")} at home");
                }
                SetBase(holder, b); // GetBase returns a copy — write mutations back
                MarkDirty(holder.Identity.BotId);
            }
        }

        // Whether a bot has a reason to place this anchor yet (F3). Survival and crafting
        // stations are always allowed; production/sales anchors wait for the resource or
        // economic condition that gives them a purpose.
        private bool AnchorNeedMet(BotRecord rec, GroupEconomy ge, Dictionary<string, List<IItemContainerEntity>> buckets, string purpose)
        {
            switch (purpose)
            {
                case "furnace": return HasSmeltable(buckets);          // raw meat or ore to cook/smelt
                case "shop":    return HasSellableGoods(rec, buckets); // finished goods exist to sell
                case "locker":  return SpareKitReady(rec, ge);         // a spare kit is affordable to bank
                default:        return true;                           // bed, storage, workbench, mixing, decor
            }
        }

        private bool HasSmeltable(Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            foreach (var sc in Stashes(buckets))
                foreach (var it in sc.inventory.itemList)
                {
                    var sn = it?.info?.shortname;
                    if (sn == null) continue;
                    if (sn.EndsWith(".ore")) return true;
                    if ((sn.Contains("meat") || sn.Contains("chicken")) && !sn.Contains("cooked") && !sn.Contains("burned")) return true;
                }
            return false;
        }

        private bool HasSellableGoods(BotRecord rec, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            var profile = ProfileFor(rec);
            if (profile == null) return false;
            foreach (var s in profile.Sell)
                if (CountAcross(buckets, s.Item) > 0) return true;
            return false;
        }

        // `holder` owns the base record — placed pieces are stamped with HIS stable
        // owner id so ownership scans (respawn bag search, removal sweeps) match the
        // base's OwnerId even when a groupmate does the placing.
        private bool FillAnchor(BotRecord rec, BotRecord holder, AnchorDef a)
        {
            var ent = GameManager.server.CreateEntity(a.Prefab, a.Pos.V, Quaternion.Euler(a.Rot.V));
            if (ent == null)
            {
                PrintWarning($"Interior fill: could not create '{a.Prefab}' for {rec.Identity.Name}.");
                a.Filled = true; // don't retry a bad prefab forever
                return false;
            }
            ent.OwnerID = BaseOwnerId(holder.Identity.BotId);
            ent.Spawn();
            var bag = ent as SleepingBag;
            if (bag != null) bag.deployerUserID = rec.State.EngineUserId;
            // v0.16.3: a deployed vending machine broadcasts by default — apply the
            // ShopBroadcast policy at placement rather than waiting for the next
            // economy pass (which never touches a no-Sell persona's machine).
            var placedShop = ent as VendingMachine;
            if (placedShop != null) ApplyShopBroadcast(placedShop);
            // F5 — lock storage the bot owns so players must raid for its loot (no-ops on
            // anything without a lock slot, e.g. the vending machine, which is already
            // access-controlled and swept of its scrap each tick).
            if (a.Purpose == "storage" || a.Purpose == "locker" || a.Purpose == "shop")
                PlaceBotLock(ent, rec);
            a.Filled = true;
            DLog($"[fill] {rec.Identity.Name} placed {a.Purpose} ({ent.ShortPrefabName})");
            return true;
        }

        private const string CodeLockPrefab = "assets/prefabs/locks/keypad/lock.code.prefab";

        // F5 — code-lock an entity to the bot's group. Bots deposit/withdraw server-side
        // and open doors via SetOpen (both bypass the lock's player RPCs), so the lock never
        // impedes the economy — it only gates humans, forcing a raid. Group engine ids are
        // whitelisted belt-and-suspenders (they drift across native-bot respawns, but the
        // server-side bypass is what actually matters). Skips anything without a lock slot.
        private void PlaceBotLock(BaseEntity ent, BotRecord rec)
        {
            if (!_config.LockBotStorage || ent == null || ent.IsDestroyed) return;
            if (!ent.HasSlot(BaseEntity.Slot.Lock) || ent.GetSlot(BaseEntity.Slot.Lock) != null) return;
            var lockEnt = GameManager.server.CreateEntity(CodeLockPrefab, ent.transform.position) as CodeLock;
            if (lockEnt == null) return;
            lockEnt.OwnerID = ent.OwnerID;
            lockEnt.SetParent(ent, ent.GetSlotAnchorName(BaseEntity.Slot.Lock));
            lockEnt.Spawn();
            lockEnt.code = UnityEngine.Random.Range(0, 10000).ToString("0000");
            lockEnt.hasCode = true;
            foreach (var member in _bots.Values)
                if (member.Identity.GroupId == rec.Identity.GroupId && member.State.EngineUserId != 0 &&
                    !lockEnt.whitelistPlayers.Contains(member.State.EngineUserId))
                    lockEnt.whitelistPlayers.Add(member.State.EngineUserId);
            SetFlagNet(lockEnt, BaseEntity.Flags.Locked, true);
            ent.SetSlot(BaseEntity.Slot.Lock, lockEnt);
            lockEnt.SendNetworkUpdate();
        }

        // F5 (optional) — seal the shell by code-locking the base's own doors at paste
        // time. The bot's entry flick uses server-side SetOpen, which ignores the lock.
        private void LockBaseDoors(BotBase b, BotRecord rec)
        {
            if (!_config.LockBotStorage || b == null || b.OwnerId == 0) return;
            _doorScan.Clear();
            Vis.Entities(b.Origin.V, 20f, _doorScan);
            foreach (var e in _doorScan)
            {
                var d = e as Door;
                if (d == null || d.IsDestroyed || d.OwnerID != b.OwnerId) continue;
                PlaceBotLock(d, rec);
            }
        }

        // ---- Interior navigation, Phase 1 (N-2, decision 0011): own doors are NPC-openable ----
        // RustNav classifies a door aperture Open / NpcOpenable / Blocked from
        // Door.IsNpcOpenable (= canNpcOpen && !isSecurityDoor). Player-placed doors ship
        // canNpcOpen=false, so a closed door is a wall to every path query. Flipping it on
        // OUR shell doors lets a doors-allowed query (PlanPathTo allowNpcDoors:true, Phase 2)
        // path through the closed door; the engine's own trigger box still never opens it
        // (it refuses locked doors, and bots aren't IsNpc) — FF opens via SetOpen as today.
        // The flag persists in the door's save data; OnDoorToggled re-applies the aperture
        // flags (deferred while a navmesh save is in flight — gate probe 2026-10-02). Player
        // doors are never touched, so residents cannot route through someone else's base.
        // No-op under -useOldNavmesh (the Unity bake has no door apertures).
        private static bool MarkBotDoor(Door d)
        {
            if (d == null || d.IsDestroyed || ConVar.AI.useUnityNavmesh || d.canNpcOpen) return false;
            d.canNpcOpen = true;
            Rust.Ai.Gen2.Nav.RustNavDoorGates.OnDoorToggled(d);
            return true;
        }

        private int MarkBotDoors(BotBase b)
        {
            if (b == null || b.OwnerId == 0) return 0;
            _doorSweep.Clear();
            Vis.Entities(b.Origin.V, 40f, _doorSweep);
            var n = 0;
            foreach (var d in _doorSweep)
                if (d != null && !d.IsDestroyed && d.OwnerID == b.OwnerId && MarkBotDoor(d)) n++;
            return n;
        }

        // One-shot sweep at load (same pattern as EnforceShopBroadcast): every bot-owned
        // door gets the flag now, and one base logs its anchor→bed path so the log carries
        // proof that the mesh agrees — the gate probe's line, permanently.
        private void EnforceDoorNav()
        {
            if (ConVar.AI.useUnityNavmesh) return;
            var n = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var d = ent as Door;
                if (d == null || d.IsDestroyed || !IsOurOwnedEntity(d.OwnerID)) continue;
                if (MarkBotDoor(d)) n++;
            }
            if (n > 0) DLog($"[door] marked {n} bot door(s) NPC-openable for the navmesh (interior navigation, decision 0011).");
            foreach (var rec in _bots.Values)
            {
                if (rec.State.HomePos == null) continue;
                var b = GetBase(rec);
                if (b == null || b.Anchors == null) continue;
                AnchorDef bed = null;
                foreach (var a in b.Anchors) if (a.Purpose == "bed" && a.Pos != null) { bed = a; break; }
                if (bed == null) continue;
                DLog($"[door] {rec.Identity.Name}: anchor→bed path, doors allowed: {DescribePath(rec.State.HomePos.V, bed.Pos.V, true)}; doors off: {DescribePath(rec.State.HomePos.V, bed.Pos.V, false)}.");
                break;
            }
        }

        private static string DescribePath(Vector3 from, Vector3 to, bool allowNpcDoors)
        {
            var path = new Rust.Ai.Gen2.RustNavMeshPath();
            if (!Rust.Ai.Gen2.RustNavMeshHelpers.CalculatePath(from, to, Rust.Ai.Gen2.RustNavMeshHelpers.AllAreas, path, allowNpcDoors)) return "no path";
            var last = path.corners.Count > 0 ? path.corners[path.corners.Count - 1].Value : from;
            return $"{path.status}/{path.corners.Count} corners, ends {Vector3.Distance(last, to):F1} m short";
        }

        // IN-1.2 — the gate probe as a permanent diagnostic: per-door nav-gate state and the
        // anchor→bed / anchor→storage path on the live mesh, doors allowed vs not.
        [ConsoleCommand("ff.base.doors")]
        private void CmdBaseDoors(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.base.doors <botId|name> — door nav-gate states + anchor→bed/storage paths on the live navmesh."); return; }
            var b = GetBase(rec);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no base."); return; }
            var nav = Rust.Ai.Gen2.Nav.RustNavigation.Instance;
            var sb = new StringBuilder();
            sb.AppendLine($"{rec.Identity.Name}: stage {b.Stage} base at {b.Origin.V:F1} — mesh {(ConVar.AI.useUnityNavmesh ? "Unity (-useOldNavmesh)" : "RustNav")}, built {(nav != null && nav.IsDefaultNavmeshBuilt())}, save in flight {(nav != null && nav.IsNavmeshSaveInFlight)}");
            _doorSweep.Clear();
            Vis.Entities(b.Origin.V, 40f, _doorSweep);
            var n = 0;
            foreach (var d in _doorSweep)
            {
                if (d == null || d.IsDestroyed || d.OwnerID != b.OwnerId) continue;
                n++;
                sb.AppendLine($"  {d.ShortPrefabName} at {d.transform.position:F1}: open={d.IsOpen()} locked={d.IsLocked()} canNpcOpen={d.canNpcOpen} npcOpenable={d.IsNpcOpenable} navGate={d.IsNavGate} swingLeaf={d.HasNavSwingLeaf}");
            }
            sb.AppendLine($"  {n} owned door(s)");
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : b.Origin.V;
            foreach (var purpose in new[] { "bed", "storage" })
            {
                AnchorDef a = null;
                if (b.Anchors != null) foreach (var x in b.Anchors) if (x.Purpose == purpose && x.Pos != null) { a = x; break; }
                if (a == null) { sb.AppendLine($"  anchor→{purpose}: no anchor recorded"); continue; }
                sb.AppendLine($"  anchor→{purpose} ({Vector3.Distance(home, a.Pos.V):F1} m): doors allowed {DescribePath(home, a.Pos.V, true)} | doors off {DescribePath(home, a.Pos.V, false)}");
            }
            arg.ReplyWith(sb.ToString().TrimEnd());
        }

        // ---- Interior navigation, Phase 2 (N-2, decision 0011): the walk primitive ----
        // StartInteriorWalk plans a doors-allowed path to an interior anchor; the generic
        // corner stepping in WanderTick moves him; InteriorWalkTick handles the door pass,
        // arrival, the dwell and the walk back out. Anything short of a complete plan, a
        // stall (< 1 m in 4 s) or a 25 s leg falls back to today's teleport hop — the
        // intent always completes, only the choreography changes. Counted in metrics.
        private const float WalkDoorOpenRange = 2.5f;   // swing the own door open within this
        private const float WalkDoorCloseRange = 3.0f;  // ...and shut it once past this (hysteresis)
        private const float WalkLegTimeout = 25f;
        private const float WalkStallSeconds = 4f;
        private const float WalkIndoorSpeed = 2.5f;     // nobody sprints through their own house
        // Walk kinds — what happens on arrival inside / after the walk out (Phase 3).
        private const int WalkTest = 0;     // harness: dwell, walk out
        private const int WalkDeposit = 1;  // stow the haul at the storage anchor, walk out, resume gathering
        private const int WalkShelter = 2;  // hole up at the bed (retreat owns the hold); walk out when clear
        private const int WalkSleep = 3;    // session end: lie down on the bag inside
        private const int WalkWake = 4;     // session start: walk out from the bag

        private bool StartInteriorWalk(BotRecord rec, BasePlayer bot, WanderState ws, Vector3 goal, int kind, float now)
        {
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null || ConVar.AI.useUnityNavmesh) { CountWalkFallback(b == null ? "noBase" : "unityMesh"); return false; }
            // IN-4.2 — a danger animal at the door: no choreography, the bear would walk
            // in behind him. Hop (the door never opens).
            if (NearestDangerAnimal(bot.transform.position, 6f) != null) { CountWalkFallback("beastAtDoor"); DLog($"[walk] {rec.Identity.Name}: something with teeth is at the door — hopping"); return false; }
            // IN-4.3 — upper-floor anchors keep the hop until the s2/s3 shells are probed.
            if (rec.State.HomePos != null && goal.y - GroundSnap(rec.State.HomePos.V).y > 2.5f) { CountWalkFallback("upperFloor"); return false; }
            if (!PlanPathTo(bot, ws, goal, true))
            {
                if (NavSaveInFlight())
                {
                    // IN-4.1 — door apertures can't be rewritten mid-save; try again in 3 s.
                    CacheWalkDoors(b, ws);
                    ws.Walk = 4; ws.WalkKind = kind; ws.WalkRetryGoal = goal; ws.WalkRetryOut = false; ws.WalkRetryAt = now + 3f;
                    ws.PassDoor = null;
                    DLog($"[walk] {rec.Identity.Name}: navmesh save in flight — retrying the walk in shortly");
                    return true;
                }
                CountWalkFallback("noPathIn");
                DLog($"[walk] {rec.Identity.Name}: no complete path inside ({FlatDistance(bot.transform.position, goal):0} m out) — hopping");
                return false;
            }
            CacheWalkDoors(b, ws);
            ws.Walk = 1; ws.WalkKind = kind; ws.WalkGoal = goal;
            ws.WalkStartAt = now; ws.WalkProgressPos = bot.transform.position; ws.WalkProgressAt = now;
            ws.PassDoor = null;
            _interiorWalks++;
            DLog($"[walk] {rec.Identity.Name} heading inside: {ws.Corners.Count} corners, {PathLength(bot.transform.position, ws):0.0} m, {ws.WalkDoors.Count} door(s)");
            return true;
        }

        private void CacheWalkDoors(BotBase b, WanderState ws)
        {
            if (ws.WalkDoors == null) ws.WalkDoors = new List<Door>();
            ws.WalkDoors.Clear();
            _doorSweep.Clear();
            Vis.Entities(b.Origin.V, 40f, _doorSweep);
            foreach (var d in _doorSweep)
                if (d != null && !d.IsDestroyed && d.OwnerID == b.OwnerId) ws.WalkDoors.Add(d);
        }

        // Walk OUT from inside to the exterior anchor (after a dwell, when the yard is
        // clear, or on waking). False = no complete path; the caller hops as before.
        private bool StartInteriorWalkOut(BotRecord rec, BasePlayer bot, WanderState ws, int kind, float now)
        {
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null || ConVar.AI.useUnityNavmesh || rec.State.HomePos == null) { CountWalkFallback("noPathOut"); return false; }
            var home = rec.State.HomePos.V;
            if (!PlanPathTo(bot, ws, home, true))
            {
                if (NavSaveInFlight())
                {
                    CacheWalkDoors(b, ws);
                    ws.Walk = 4; ws.WalkKind = kind; ws.WalkRetryGoal = home; ws.WalkRetryOut = true; ws.WalkRetryAt = now + 3f;
                    ws.PassDoor = null;
                    DLog($"[walk] {rec.Identity.Name}: navmesh save in flight — retrying the walk out shortly");
                    return true;
                }
                CountWalkFallback("noPathOut"); return false;
            }
            CacheWalkDoors(b, ws);
            ws.Walk = 3; ws.WalkKind = kind; ws.WalkGoal = home;
            ws.WalkStartAt = now; ws.WalkProgressPos = bot.transform.position; ws.WalkProgressAt = now;
            ws.PassDoor = null;
            DLog($"[walk] {rec.Identity.Name} heading back out: {ws.Corners.Count} corners");
            return true;
        }

        public static bool NavSaveInFlight()
        {
            var nav = Rust.Ai.Gen2.Nav.RustNavigation.Instance;
            return nav != null && nav.IsNavmeshSaveInFlight;
        }

        private void CountWalkFallback(string reason)
        {
            _interiorFallbacks++;
            int n; _interiorFallbackReasons.TryGetValue(reason, out n);
            _interiorFallbackReasons[reason] = n + 1;
        }

        // Returns true when the tick is consumed (arrival, dwell, abort); false when
        // corners remain and the generic stepping below should move him.
        private bool InteriorWalkTick(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            // A shelter walk IS the retreat and a sleep walk IS the session end — those
            // flags don't preempt their own walk. A fight always does.
            if (ws.FightTarget != null || (ws.GoingHome && ws.WalkKind != WalkSleep) || (ws.Retreating && ws.WalkKind != WalkShelter))
            {
                // Something that outranks choreography took over — drop the walk, shut
                // the door, let that state act on its own path.
                CloseWalkDoor(ws);
                CountWalkFallback("preempted");
                DLog($"[walk] {rec.Identity.Name}: walk preempted ({(ws.FightTarget != null ? "fight" : ws.Retreating ? "retreat" : "session end")})");
                ws.Walk = 0;
                return false; // the preempting state may already own Corners
            }
            if (ws.WalkKind == WalkShelter && (ws.Walk == 1 || ws.Walk == 4) && !ws.Retreating)
            {
                // All clear before he got inside (the threat tick stood him down) — carry on outside.
                CloseWalkDoor(ws);
                DLog($"[walk] {rec.Identity.Name}: all clear before he got inside — carrying on");
                FinishInteriorWalk(rec, bot, ws, false);
                return true;
            }
            if (ws.Walk == 4)
            {
                // IN-4.1 — the one retry after a navmesh save blocked the plan.
                if (now < ws.WalkRetryAt) return true;
                if (PlanPathTo(bot, ws, ws.WalkRetryGoal, true))
                {
                    ws.Walk = ws.WalkRetryOut ? 3 : 1; ws.WalkGoal = ws.WalkRetryGoal;
                    ws.WalkStartAt = now; ws.WalkProgressPos = bot.transform.position; ws.WalkProgressAt = now;
                    if (!ws.WalkRetryOut) _interiorWalks++;
                    DLog($"[walk] {rec.Identity.Name}: save done — {(ws.WalkRetryOut ? "heading back out" : "heading inside")}: {ws.Corners.Count} corners");
                    return true;
                }
                CountWalkFallback(ws.WalkRetryOut ? "noPathOut-afterSave" : "noPathIn-afterSave");
                if (ws.WalkRetryOut)
                {
                    DLog($"[walk] {rec.Identity.Name}: still no path out after the save — hopping to the anchor");
                    if (rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
                    FinishInteriorWalk(rec, bot, ws, false);
                }
                else
                {
                    DLog($"[walk] {rec.Identity.Name}: still no path inside after the save — hopping inside");
                    ws.WalkGoal = ws.WalkRetryGoal;
                    bot.Teleport(GetShelterPoint(rec, ws.WalkKind == WalkDeposit ? "storage" : "bed"));
                    OnInteriorArrived(rec, bot, ws, now);
                }
                return true;
            }
            if (ws.Walk == 2)
            {
                if (now < ws.WalkDwellUntil) return true;
                if (!StartInteriorWalkOut(rec, bot, ws, ws.WalkKind, now))
                {
                    DLog($"[walk] {rec.Identity.Name}: no path back out — hopping to the anchor");
                    CloseWalkDoor(ws);
                    if (rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
                    FinishInteriorWalk(rec, bot, ws, false);
                }
                return true;
            }
            // Walking (1 in / 3 out): door pass, valves, arrival.
            DoorPassTick(bot, ws);
            var pos = bot.transform.position;
            if (FlatDistance(pos, ws.WalkProgressPos) >= 1f) { ws.WalkProgressPos = pos; ws.WalkProgressAt = now; }
            else if (now - ws.WalkProgressAt > WalkStallSeconds) { AbortInteriorWalk(rec, bot, ws, "stall"); return true; }
            if (now - ws.WalkStartAt > WalkLegTimeout) { AbortInteriorWalk(rec, bot, ws, "timeout"); return true; }
            if (ws.CornerIndex < ws.Corners.Count) return false; // keep stepping
            // Arrived at the last corner.
            if (FlatDistance(pos, ws.WalkGoal) > 1.5f) { AbortInteriorWalk(rec, bot, ws, "shortArrival"); return true; }
            if (ws.Walk == 1)
            {
                CloseWalkDoor(ws); // he's in — the door shuts behind him (bears included)
                OnInteriorArrived(rec, bot, ws, now);
                return true;
            }
            CloseWalkDoor(ws);
            FinishInteriorWalk(rec, bot, ws, true);
            return true;
        }

        // Inside, at the anchor: what the walk was for (Phase 3 conversions by kind).
        private void OnInteriorArrived(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            var face = ws.WalkGoal - bot.transform.position; face.y = 0f;
            if (face.sqrMagnitude > 0.01f) bot.OverrideViewAngles(Quaternion.LookRotation(face.normalized).eulerAngles);
            var dist = FlatDistance(bot.transform.position, ws.WalkGoal);
            switch (ws.WalkKind)
            {
                case WalkDeposit:
                {
                    // IN-3.1 — stow the haul in the boxes, linger at the box, walk out.
                    BotRecord holder;
                    var b = GetGroupBase(rec.Identity.GroupId, out holder);
                    if (b != null) DepositInventoryToBase(rec, b);
                    WorkState wk;
                    if (_work.TryGetValue(rec.Identity.BotId, out wk)) wk.PhaseDeadline = now + 60f; // watchdog guard for the dwell + walk out
                    ws.Walk = 2;
                    ws.WalkDwellUntil = now + UnityEngine.Random.Range(5f, 10f);
                    DLog($"[walk] {rec.Identity.Name} stowed the haul inside ({dist:0.0} m from the box) — dwelling");
                    return;
                }
                case WalkShelter:
                    // IN-3.2 — holed up; the retreat logic owns the hold and the walk out.
                    ws.Sheltered = true;
                    ws.Walk = 0; ws.PassDoor = null;
                    ws.Corners.Clear(); ws.CornerIndex = 0;
                    _interiorWalksDone++;
                    if (ws.RetreatStory != null) { Story("retreat", ws.RetreatStory); ws.RetreatStory = null; } // WB-2 — now it's true
                    DLog($"[walk] {rec.Identity.Name} is inside and holed up ({dist:0.0} m from the bed)");
                    return;
                case WalkSleep:
                    // IN-3.3 — lie down on the bag; FinalizeSessionEnd does the rest (its teleport is a no-op here).
                    ws.Walk = 0; ws.PassDoor = null;
                    _interiorWalksDone++;
                    DLog($"[walk] {rec.Identity.Name} walked in to turn in ({dist:0.0} m from the bag)");
                    FinalizeSessionEnd(rec);
                    return;
                default:
                    ws.Walk = 2;
                    ws.WalkDwellUntil = now + UnityEngine.Random.Range(5f, 10f);
                    DLog($"[walk] {rec.Identity.Name} is inside ({dist:0.0} m from the anchor) — dwelling");
                    return;
            }
        }

        private void FinishInteriorWalk(BotRecord rec, BasePlayer bot, WanderState ws, bool walked)
        {
            var kind = ws.WalkKind;
            ws.Walk = 0; ws.PassDoor = null;
            ws.Corners.Clear(); ws.CornerIndex = 0; // whoever owns him next re-plans
            if (walked) { _interiorWalksDone++; DLog($"[walk] {rec.Identity.Name} is back out — walk complete"); }
            if (kind == WalkDeposit)
            {
                // IN-3.1 — back in the yard with empty pockets: next gather target.
                WorkState wk;
                if (_work.TryGetValue(rec.Identity.BotId, out wk) && wk.Phase == WorkPhase.Deposit)
                    StartNextWorkTarget(rec, bot, ws, wk, Time.realtimeSinceStartup);
            }
        }

        // The hop completes the intent the walk couldn't: inbound → straight to the
        // anchor and dwell; outbound → straight to the exterior anchor.
        private void AbortInteriorWalk(BotRecord rec, BasePlayer bot, WanderState ws, string reason)
        {
            CountWalkFallback(reason);
            CloseWalkDoor(ws);
            if (ws.Walk == 1)
            {
                DLog($"[walk] {rec.Identity.Name}: walk in aborted ({reason}, {FlatDistance(bot.transform.position, ws.WalkGoal):0.0} m short) — hopping inside");
                bot.Teleport(GetShelterPoint(rec, ws.WalkKind == WalkDeposit ? "storage" : "bed"));
                OnInteriorArrived(rec, bot, ws, Time.realtimeSinceStartup);
                return;
            }
            DLog($"[walk] {rec.Identity.Name}: walk out aborted ({reason}) — hopping to the anchor");
            if (rec.State.HomePos != null) bot.Teleport(GroundSnap(rec.State.HomePos.V));
            FinishInteriorWalk(rec, bot, ws, false);
        }

        // Own-door choreography: the nearest closed own door within 2.5 m swings open
        // (server-side SetOpen — the lock gates humans, not him) and shuts once he is
        // 3 m past it or has arrived. One door at a time; interior doors on the way
        // open in turn as he reaches them.
        private void DoorPassTick(BasePlayer bot, WanderState ws)
        {
            DriverDoorPass(bot.transform.position, ws.WalkDoors, ref ws.PassDoor);
        }

        // Shared with the driver-owned bodies (IN-3.4): one door at a time; the nearest
        // closed own door within 2.5 m opens, and shuts once he is 3 m past it.
        public static void DriverDoorPass(Vector3 pos, List<Door> doors, ref Door passDoor)
        {
            if (passDoor != null)
            {
                if (passDoor.IsDestroyed) { passDoor = null; }
                else if (FlatDistance(pos, passDoor.transform.position) > WalkDoorCloseRange) { passDoor.SetOpen(false, true); passDoor = null; }
                else return;
            }
            if (doors == null) return;
            Door best = null; var bestSq = WalkDoorOpenRange * WalkDoorOpenRange;
            foreach (var d in doors)
            {
                if (d == null || d.IsDestroyed || d.IsOpen()) continue;
                var dp = d.transform.position;
                var sq = (dp.x - pos.x) * (dp.x - pos.x) + (dp.z - pos.z) * (dp.z - pos.z);
                if (sq < bestSq) { bestSq = sq; best = d; }
            }
            if (best == null) return;
            best.SetOpen(true);
            passDoor = best;
        }

        public static void DriverCloseDoor(ref Door passDoor)
        {
            if (passDoor != null && !passDoor.IsDestroyed) passDoor.SetOpen(false, true);
            passDoor = null;
        }

        private static void CloseWalkDoor(WanderState ws)
        {
            DriverCloseDoor(ref ws.PassDoor);
        }

        // ---- IN-3.4: walk-in for driver-owned bodies (hostiles, runners) ----
        // Drivers move with BaseNavigator; the plugin supplies the interior goal (floor-
        // sampled, doors allowed), the base's doors and the counters. The driver flips
        // Nav.Agent.canOpenDoors on for the depot walk only, so BaseNavigator plans
        // through our NpcOpenable apertures (npcDoorsWillOpen) for that trip and never
        // routes a roam leg through the house.
        public Vector3 DriverInteriorGoal(int botId, Vector3 bodyPos, string purpose, out string why)
        {
            why = null;
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec) || rec.State.HomePos == null) { why = "noBase"; return Vector3.zero; }
            if (ConVar.AI.useUnityNavmesh) { why = "unityMesh"; return Vector3.zero; }
            BotRecord holder;
            if (GetGroupBase(rec.Identity.GroupId, out holder) == null) { why = "noBase"; return Vector3.zero; }
            if (NearestDangerAnimal(bodyPos, 6f) != null) { why = "beastAtDoor"; return Vector3.zero; } // IN-4.2
            var pt = GetShelterPoint(rec, purpose);
            var yard = GroundSnap(rec.State.HomePos.V);
            if (pt == Vector3.zero || pt == yard) { why = "noAnchor"; return Vector3.zero; } // no interior anchor to walk to
            if (pt.y - yard.y > 2.5f) { why = "upperFloor"; return Vector3.zero; }            // IN-4.3
            NavMeshHit hit;
            if (!NavSample(GroundSnap(pt), out hit, 2f, true)) { why = "offMesh"; return Vector3.zero; }
            return hit.position;
        }

        public List<Door> DriverBaseDoors(int botId)
        {
            BotRecord rec;
            if (!_bots.TryGetValue(botId, out rec)) return null;
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null || b.OwnerId == 0) return null;
            var list = new List<Door>();
            _doorSweep.Clear();
            Vis.Entities(b.Origin.V, 40f, _doorSweep);
            foreach (var d in _doorSweep)
                if (d != null && !d.IsDestroyed && d.OwnerID == b.OwnerId) list.Add(d);
            return list;
        }

        public void DriverWalkStarted(string who, float dist) { _interiorWalks++; DLog($"[walk] {who} heading inside to stash the take ({dist:0.0} m)"); }
        public void DriverWalkDone(string who) { _interiorWalksDone++; DLog($"[walk] {who} is back out — walk complete"); }
        public void DriverWalkFallback(string who, string reason) { CountWalkFallback(reason); DLog($"[walk] {who}: driver walk fell back ({reason}) — hopping"); }

        // IN-3.3 — waking on the bag: plan the walk out now; the wander state is created
        // here if the bot never ticked this load (fresh session after a reload).
        private bool TryWalkOutOnWake(BotRecord rec, BasePlayer bot)
        {
            if (rec.State.HomePos == null || ConVar.AI.useUnityNavmesh) return false;
            if (FlatDistance(bot.transform.position, rec.State.HomePos.V) > 15f) return false; // not actually at home — the hop re-anchors him
            WanderState ws;
            if (!_wander.TryGetValue(rec.Identity.BotId, out ws))
                _wander[rec.Identity.BotId] = ws = new WanderState { Speed = 5f + rec.Identity.Traits.SkillLevel };
            ws.Walk = 0; ws.PassDoor = null; ws.Retreating = false; ws.Sheltered = false; ws.GoingHome = false;
            return StartInteriorWalkOut(rec, bot, ws, WalkWake, Time.realtimeSinceStartup);
        }

        // Phase 2 harness: walk one bot in to its bed and back out. Marker file
        // (data/FakeFriends/probe_walkin.json) picks the first eligible bot within 60 m
        // of home and retries every 30 s for 10 min; the console command names one.
        private string ProbeWalkInMarker => System.IO.Path.Combine(Interface.Oxide.DataDirectory, "FakeFriends", "probe_walkin.json");
        private int _walkInProbeTries;

        private void ProbeWalkIn()
        {
            _walkInProbeTries++;
            foreach (var rec in _bots.Values)
            {
                var why = TryStartTestWalk(rec);
                if (why == null)
                {
                    DLog($"[walk] harness: test walk started for {rec.Identity.Name} (try {_walkInProbeTries})");
                    try { System.IO.File.Delete(ProbeWalkInMarker); } catch { }
                    return;
                }
            }
            if (_walkInProbeTries >= 20) { DLog("[walk] harness: no eligible bot near home in 10 min — giving up"); try { System.IO.File.Delete(ProbeWalkInMarker); } catch { } return; }
            timer.Once(30f, ProbeWalkIn);
        }

        // null = started; otherwise the reason it couldn't.
        private string TryStartTestWalk(BotRecord rec)
        {
            if (rec.State.Status != "Online" || rec.State.HomePos == null) return "not online";
            if (IsHostileP(rec) || _runnerActive.Contains(rec.Identity.BotId)) return "driver-owned body";
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null || bot.IsSleeping() || bot.IsDead()) return "no live body";
            WanderState ws;
            if (!_wander.TryGetValue(rec.Identity.BotId, out ws)) return "no wander state yet";
            if (ws.Walk != 0) return "already walking";
            if (ws.Retreating || ws.FightTarget != null || ws.GoingHome || ws.Sheltered) return "busy (retreat/fight/session end)";
            var b = GetBase(rec);
            if (b == null) return "no base";
            AnchorDef bed = null;
            foreach (var a in b.Anchors) if (a.Purpose == "bed" && a.Pos != null) { bed = a; break; }
            if (bed == null) return "no bed anchor";
            if (FlatDistance(bot.transform.position, rec.State.HomePos.V) > 60f) return "too far from home";
            ws.VisitPhase = 0;
            return StartInteriorWalk(rec, bot, ws, bed.Pos.V, 0, Time.realtimeSinceStartup) ? null : "no complete path (hop fallback not taken for the harness)";
        }

        [ConsoleCommand("ff.base.walkin")]
        private void CmdBaseWalkIn(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.base.walkin <botId|name> — walk the bot in to its bed and back out (Phase 2 harness)."); return; }
            var why = TryStartTestWalk(rec);
            arg.ReplyWith(why == null ? $"{rec.Identity.Name} is walking in — watch [walk] lines." : $"{rec.Identity.Name}: {why}. Stats: walks {_interiorWalks}, completed {_interiorWalksDone}, fallbacks {_interiorFallbacks}.");
        }

        [ConsoleCommand("ff.base.fill")]
        private void CmdBaseFill(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.base.fill <botId|name> — force-place all remaining interior anchors now."); return; }
            var b = GetBase(rec);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no base."); return; }
            var n = 0;
            foreach (var a in b.Anchors)
                if (!a.Filled && FillAnchor(rec, rec, a)) n++;
            SetBase(rec, b);
            MarkDirty(rec.Identity.BotId);
            arg.ReplyWith($"Placed {n} interior piece(s) for {rec.Identity.Name}.");
        }

        // Native-bot engine IDs change across respawns — refresh TC auth each spawn.
        private void ReauthBase(BotRecord rec)
        {
            var b = GetBase(rec);
            if (b == null || b.TcNetId == 0) return;
            var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
            if (tc == null || tc.IsDestroyed) return;
            AuthorizeGroupOnTc(tc, rec);
        }

        #endregion

        #region Economy — Worker core (Phase 3)

        // Source of truth is the REAL deployed containers at each base (decision 0005):
        // both embodied and abstract production drop real items into real boxes, so a
        // raid sets a bot back for free and a full box throttles that good. The only
        // virtual state is a per-GROUP career tally + scrap balance (a group shares one
        // base → shares its economy). Physical stock lives in the world, not here.

        // Cadence + catch-up cap are config (EconomyTickSeconds / EconomyCatchupCapMinutes) —
        // the cap stops a long downtime dumping a wipe's worth of production on load.
        private Oxide.Plugins.Timer _economyTimer;

        private class GroupEconomy
        {
            public int GroupId;
            public int Scrap;               // virtual currency from sales (decision 0005)
            public long CareerProduced;     // lifetime resource units this wipe — progression driver
            public bool HadFirstSale;       // story-log gate
            public int SpareKitTier = -1;   // tier of the spare kit currently banked in the locker (-1 = none)
            public float WageCarry;         // fractional ScrapPerHour remainder carried between ticks
            public float CareerCarry;       // fractional time-based career remainder carried between ticks (v0.16.0 retainer → v0.20.0 universal)
            public string LastTickAt = "";  // batch catch-up anchor
            public string NextEconomyAt = ""; // reserved: per-good pacing (Stage C)
        }

        private class EconomyStore
        {
            public int Schema = SchemaVersion;
            public Dictionary<string, GroupEconomy> Groups = new Dictionary<string, GroupEconomy>();
        }

        private EconomyStore _economy = new EconomyStore();
        private bool _economyDirty;

        private void LoadEconomy()
        {
            _economy = SafeRead<EconomyStore>($"{DataRoot}/economy") ?? new EconomyStore();
            if (_economy.Groups == null) _economy.Groups = new Dictionary<string, GroupEconomy>();
        }

        // Get-or-create the economy ledger for a group. GroupId is always > 0 for a
        // real roster member (FillRoster assigns one), so <= 0 is a guard, not a case.
        private GroupEconomy Econ(int groupId)
        {
            if (groupId <= 0) return null;
            var key = groupId.ToString();
            GroupEconomy ge;
            if (!_economy.Groups.TryGetValue(key, out ge) || ge == null)
            {
                ge = new GroupEconomy { GroupId = groupId };
                _economy.Groups[key] = ge;
                _economyDirty = true;
            }
            return ge;
        }

        // Drop ledgers for groups that no longer have any roster member (retired-out
        // solos, wiped groups that didn't clear). Cheap, runs once at load.
        private void PruneEconomy()
        {
            if (_economy.Groups.Count == 0) return;
            var live = new HashSet<int>();
            foreach (var rec in _bots.Values) live.Add(rec.Identity.GroupId);
            var drop = new List<string>();
            foreach (var kv in _economy.Groups)
                if (!live.Contains(kv.Value?.GroupId ?? -1)) drop.Add(kv.Key);
            if (drop.Count == 0) return;
            foreach (var k in drop) _economy.Groups.Remove(k);
            _economyDirty = true;
            DLog($"[econ] pruned {drop.Count} orphan group ledger(s).");
        }

        // The base is stored on whichever group member pasted it (Phase 2). Find it via
        // any member so the group's economy always locates its own containers.
        private BotBase GetGroupBase(int groupId, out BotRecord holder)
        {
            holder = null;
            foreach (var rec in _bots.Values)
            {
                if (rec.Identity.GroupId != groupId) continue;
                var b = GetBase(rec);
                if (b != null) { holder = rec; return b; }
            }
            return null;
        }

        // Any real human player within observation range of a point. Drives the
        // embodied-vs-abstract switch: watched bases act for real, unwatched ones
        // advance by accounting. Our own bots never count as observers (native-bot ids
        // aren't Steam ids; SteamId-mode bots are filtered by our live map).
        private bool IsObserved(Vector3 pos, float range = -1f)
        {
            if (range < 0f) range = _config.ObserveRange; // config default (can't be a const default param)
            var rangeSq = range * range;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (!IsRealPlayer(p)) continue;
                if ((p.transform.position - pos).sqrMagnitude <= rangeSq) return true;
            }
            return false;
        }

        // The single "is this a real human" filter (locked identity decision 0001):
        // native-bot ids aren't Steam ids; SteamId-mode bots are excluded by the live
        // map. Shared by the observe switch and grudge sensing so they can't disagree.
        private bool IsRealPlayer(BasePlayer p)
        {
            if (p == null || p.IsDestroyed) return false;
            var uid = (ulong)p.userID;
            return uid.IsSteamId() && !_byUserId.ContainsKey(uid);
        }

        // --- Persona economy profiles (data-driven — the Phase 3.5 seam) ------------
        // What a persona gathers, crafts/cooks, and sells. Trapper, Farmer, and Smelter
        // are authored in full (3.5). Free-roamers have no profile (their economy is
        // elsewhere). Item shortnames verified against the live Bundles/items dump.

        private class GatherRule
        {
            public string Kind;     // tree | ore | animal | meat (one species/tick) | crop (planter-gated, always abstract)
            public string Resource; // produced shortname (abstract accounting + deposit)
            public float RatePerMin; // base units/min at skill 0 (scaled 0.5x–1.5x by skill)
            public string StoreAs;  // deposit target purpose: storage | locker | food(→storage)
        }

        private class CraftRule
        {
            public string Output;   // item to craft/cook
            public int Amount;      // per batch (blueprint path multiplies by bp.amountToCreate)
            public string Station;  // required anchor purpose: workbench | furnace | mixing
            // Explicit PER-UNIT costs for outputs with no ItemBlueprint (mixing-table
            // teas). When set, this replaces the blueprint lookup; costs scale by
            // Amount and yield is Amount.
            public Dictionary<string, int> Ingredients;
        }

        private class SellRule
        {
            public string Item;     // shortname sold in the vending machine
            public int Amount;      // units per transaction
            public int Scrap;       // price
        }

        private class EconomyProfile
        {
            public List<GatherRule> Gather = new List<GatherRule>();
            public List<CraftRule> Craft = new List<CraftRule>();
            public List<SellRule> Sell = new List<SellRule>();
            public Dictionary<string, int> Upkeep = new Dictionary<string, int>(); // material → target amount kept in the TC
            public float ScrapPerHour;                 // wage credited to the ledger while a member is online (0 = none)
            // v0.20.0: career measures HOURS WORKED, not units hauled. Credited per online
            // minute at skill 1.0 (×0.5–1.5 by skill, ÷sqrt(members) for duos), whether the
            // group is producing abstractly, embodied, on a run or on a prowl. The day-9
            // review (docs/reports/2026-09-12) found unit-based career made Smelters 1.6×
            // Roleplayers per hour, Farmer crops credited career with nobody online, and a
            // two-Smelter duo hit stage 3 on day 3 (6.5× the pace line). Deposits are
            // unchanged — boxes stay the visible skin; the ledger is time on the clock.
            // 60/min = 3,600/h → one 3–6 h hyped session/day lands stage 2 ~day 5–8,
            // stage 3 ~day 12–19 across the skill spread. Replaces CareerPerHour (the
            // v0.16.0 Hostile-only retainer — an old key in the file is ignored).
            public float CareerPerMinute = 60f;
            public List<string> CloneGenes;            // gene string stamped on clone.* deposits, indexed by base stage (1-based)
            public List<int> CloneScrapByStage;        // clone sell price override, indexed by base stage (1-based)
        }

        // Per-CLASS economy catalogs. Authored here as defaults, then written to
        // data/FakeFriends/economy_profiles.json on first run so they can be tuned live
        // without recompiling. Individual bots differ only by traits (skill/caution),
        // which scale these shared numbers.
        private Dictionary<string, EconomyProfile> _economyProfiles = new Dictionary<string, EconomyProfile>(StringComparer.OrdinalIgnoreCase);

        // Biome-variant lookup: a "Trapper.Jungle" profile beats "Trapper" when the bot's
        // home landed in the jungle — species/goods swap by region, rates stay comparable.
        private EconomyProfile ProfileFor(BotRecord rec)
        {
            if (rec?.Identity?.Persona == null) return null;
            EconomyProfile p;
            var biome = rec.State?.HomeBiome;
            if (!string.IsNullOrEmpty(biome) &&
                _economyProfiles.TryGetValue(rec.Identity.Persona + "." + biome, out p) && p != null) return p;
            return _economyProfiles.TryGetValue(rec.Identity.Persona, out p) ? p : null;
        }

        private void LoadEconomyProfiles()
        {
            var loaded = SafeRead<Dictionary<string, EconomyProfile>>($"{DataRoot}/economy_profiles");
            if (loaded == null || loaded.Count == 0)
            {
                _economyProfiles = BuildDefaultProfiles();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/economy_profiles", _economyProfiles);
                DLog("Wrote default economy_profiles.json — edit it to tune each persona's economy.");
                return;
            }
            _economyProfiles = new Dictionary<string, EconomyProfile>(StringComparer.OrdinalIgnoreCase);
            // v0.20.0: the field initializer already gives a pre-0.20 profile the default
            // rate at runtime; the raw read is only to notice the KEY is absent so the
            // file gets rewritten with it (tunable live, like every other profile knob).
            var raw = SafeRead<Dictionary<string, Newtonsoft.Json.Linq.JObject>>($"{DataRoot}/economy_profiles");
            var migrated = false;
            foreach (var kv in loaded)
            {
                var p = kv.Value ?? new EconomyProfile();
                if (p.Gather == null) p.Gather = new List<GatherRule>();
                if (p.Craft == null) p.Craft = new List<CraftRule>();
                if (p.Sell == null) p.Sell = new List<SellRule>();
                if (p.Upkeep == null) p.Upkeep = new Dictionary<string, int>();
                // v0.20.0: a file written before the time-based career has no
                // CareerPerMinute key — inject the default and persist, same pattern
                // as the MonumentRunner injection.
                Newtonsoft.Json.Linq.JObject rawP;
                if (raw == null || !raw.TryGetValue(kv.Key, out rawP) || rawP == null || rawP["CareerPerMinute"] == null)
                {
                    p.CareerPerMinute = 60f;
                    migrated = true;
                }
                _economyProfiles[kv.Key] = p;
            }
            if (migrated)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/economy_profiles", _economyProfiles);
                DLog("economy_profiles.json: injected CareerPerMinute (v0.20.0 time-based career) into pre-0.20 profiles.");
            }
            // Phase 6: a server whose economy_profiles.json predates the MonumentRunner
            // profile gets it injected (and persisted) so runner groups turn economic
            // without hand-editing the data file.
            if (!_economyProfiles.ContainsKey("MonumentRunner"))
            {
                EconomyProfile mr;
                if (BuildDefaultProfiles().TryGetValue("MonumentRunner", out mr))
                {
                    _economyProfiles["MonumentRunner"] = mr;
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/economy_profiles", _economyProfiles);
                    DLog("economy_profiles.json: injected the Phase 6 MonumentRunner profile.");
                }
            }
        }

        // Per-CLASS economy catalogs, authored here and written to
        // data/FakeFriends/economy_profiles.json on first run so they can be tuned live
        // without recompiling. THE DATA FILE WINS once it exists — it is only ever
        // written when absent, so these defaults are what a fresh server (or a second
        // server) starts from. Regenerated from the live server's file 2026-07-30 after
        // it emerged that Hostile and Roleplayer were missing here entirely and every
        // ScrapPerHour but the runner's was unset: a fresh install would have run 7 of
        // 15 bots with no economy and no wage at all.
        private static Dictionary<string, EconomyProfile> BuildDefaultProfiles() =>
            new Dictionary<string, EconomyProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Trapper"] = new EconomyProfile
            {
                // Furs, meat and the woods. Meat rules are reservoir-sampled one per tick so
                // hauls arrive as single animals rather than a whole ark at once.
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 43.8f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 25f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 12.5f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                    new GatherRule { Kind = "animal", Resource = "leather", RatePerMin = 1.8f, StoreAs = "locker" },
                    new GatherRule { Kind = "animal", Resource = "bone.fragments", RatePerMin = 1.85f, StoreAs = "storage" },
                    new GatherRule { Kind = "animal", Resource = "cloth", RatePerMin = 0.95f, StoreAs = "storage" },
                    new GatherRule { Kind = "animal", Resource = "fat.animal", RatePerMin = 0.75f, StoreAs = "storage" },
                    new GatherRule { Kind = "meat", Resource = "deermeat.raw", RatePerMin = 0.15f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "wolfmeat.raw", RatePerMin = 0.15f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "chicken.raw", RatePerMin = 0.08f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "bearmeat", RatePerMin = 0.55f, StoreAs = "food" },
                },
                Craft = new List<CraftRule>
                {
                    new CraftRule { Output = "rug.bear", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["leather"] = 100, ["cloth"] = 50 } },
                },
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "deermeat.cooked", Amount = 5, Scrap = 6 },
                    new SellRule { Item = "bearmeat.cooked", Amount = 3, Scrap = 10 },
                    new SellRule { Item = "wolfmeat.cooked", Amount = 4, Scrap = 8 },
                    new SellRule { Item = "chicken.cooked", Amount = 5, Scrap = 4 },
                    new SellRule { Item = "fat.animal", Amount = 50, Scrap = 10 },
                    new SellRule { Item = "leather", Amount = 20, Scrap = 10 },
                    new SellRule { Item = "cloth", Amount = 20, Scrap = 8 },
                    new SellRule { Item = "rug.bear", Amount = 1, Scrap = 75 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
            ["Trapper.Jungle"] = new EconomyProfile
            {
                // Biome variant: a jungle Trapper works the same trade on different species.
                // "Trapper.Jungle" beats "Trapper" when the bot's home landed in the jungle.
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 43.8f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 25f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 12.5f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                    new GatherRule { Kind = "animal", Resource = "leather", RatePerMin = 1.83f, StoreAs = "locker" },
                    new GatherRule { Kind = "animal", Resource = "bone.fragments", RatePerMin = 2.73f, StoreAs = "storage" },
                    new GatherRule { Kind = "animal", Resource = "cloth", RatePerMin = 0.97f, StoreAs = "storage" },
                    new GatherRule { Kind = "animal", Resource = "fat.animal", RatePerMin = 1.93f, StoreAs = "storage" },
                    new GatherRule { Kind = "animal", Resource = "venom.snake", RatePerMin = 0.04f, StoreAs = "storage" },
                    new GatherRule { Kind = "meat", Resource = "chicken.raw", RatePerMin = 0.08f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "snakemeat", RatePerMin = 0.2f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "bigcatmeat", RatePerMin = 0.76f, StoreAs = "food" },
                    new GatherRule { Kind = "meat", Resource = "crocodilemeat", RatePerMin = 0.5f, StoreAs = "food" },
                },
                Craft = new List<CraftRule>(),
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "bigcatmeat.cooked", Amount = 3, Scrap = 10 },
                    new SellRule { Item = "crocodilemeat.cooked", Amount = 3, Scrap = 10 },
                    new SellRule { Item = "snakemeat.cooked", Amount = 5, Scrap = 4 },
                    new SellRule { Item = "chicken.cooked", Amount = 5, Scrap = 4 },
                    new SellRule { Item = "venom.snake", Amount = 4, Scrap = 10 },
                    new SellRule { Item = "fat.animal", Amount = 50, Scrap = 10 },
                    new SellRule { Item = "leather", Amount = 20, Scrap = 10 },
                    new SellRule { Item = "cloth", Amount = 20, Scrap = 8 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
            ["Farmer"] = new EconomyProfile
            {
                // Stage 1 forages the wild island — Kind "forage" is NOT planter-gated, so it
                // runs from day one, and the seed/cutting stock it banks is what the stage-2
                // planters will run on. Kind "crop" stays planter-gated: dormant until the
                // farm proper exists (stage-1 captures carry no planter, by design).
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 36f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 22f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 9f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                    new GatherRule { Kind = "forage", Resource = "cloth", RatePerMin = 6f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "mushroom", RatePerMin = 1.5f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "corn", RatePerMin = 0.8f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "potato", RatePerMin = 0.8f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "pumpkin", RatePerMin = 0.4f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "black.berry", RatePerMin = 0.6f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "green.berry", RatePerMin = 0.6f, StoreAs = "food" },
                    new GatherRule { Kind = "forage", Resource = "seed.hemp", RatePerMin = 0.5f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "seed.corn", RatePerMin = 0.25f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "seed.potato", RatePerMin = 0.25f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "seed.pumpkin", RatePerMin = 0.12f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "clone.hemp", RatePerMin = 0.05f, StoreAs = "storage" },
                    new GatherRule { Kind = "crop", Resource = "red.berry", RatePerMin = 5f, StoreAs = "food" },
                    new GatherRule { Kind = "crop", Resource = "blue.berry", RatePerMin = 4f, StoreAs = "food" },
                    new GatherRule { Kind = "crop", Resource = "yellow.berry", RatePerMin = 3f, StoreAs = "food" },
                    new GatherRule { Kind = "crop", Resource = "white.berry", RatePerMin = 3f, StoreAs = "food" },
                    new GatherRule { Kind = "crop", Resource = "clone.red.berry", RatePerMin = 0.6f, StoreAs = "storage" },
                },
                Craft = new List<CraftRule>
                {
                    new CraftRule { Output = "scraptea", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["yellow.berry"] = 2, ["white.berry"] = 2 } },
                    new CraftRule { Output = "oretea", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["yellow.berry"] = 2, ["blue.berry"] = 2 } },
                    new CraftRule { Output = "harvestingtea", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["yellow.berry"] = 1, ["green.berry"] = 3 } },
                    new CraftRule { Output = "craftingtea_quality", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["green.berry"] = 2, ["white.berry"] = 2 } },
                    new CraftRule { Output = "scraptea.advanced", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["scraptea"] = 4 } },
                    new CraftRule { Output = "oretea.advanced", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["oretea"] = 4 } },
                    new CraftRule { Output = "advanceharvestingtea", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["harvestingtea"] = 4 } },
                    new CraftRule { Output = "advancedcraftingtea_quality", Amount = 1, Station = "mixing",
                        Ingredients = new Dictionary<string, int> { ["craftingtea_quality"] = 4 } },
                },
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "scraptea.advanced", Amount = 1, Scrap = 60 },
                    new SellRule { Item = "oretea.advanced", Amount = 1, Scrap = 50 },
                    new SellRule { Item = "advanceharvestingtea", Amount = 1, Scrap = 45 },
                    new SellRule { Item = "advancedcraftingtea_quality", Amount = 1, Scrap = 45 },
                    new SellRule { Item = "clone.hemp", Amount = 1, Scrap = 10 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
            ["Smelter"] = new EconomyProfile
            {
                // Ore in, refined out. The furnace does the work unattended (SmeltOre).
                // v0.17.0: bulk ores trimmed ~20% (stones 25→20, metal 33.33→27,
                // sulfur 25→20) — wk-1 data 2026-08-23: smelter groups held the top
                // THREE career slots (684k/471k/379k vs best non-smelter 349k).
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 43.8f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 20f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.ore", RatePerMin = 27f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "hq.metal.ore", RatePerMin = 0.3333f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "sulfur.ore", RatePerMin = 20f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "crude.oil", RatePerMin = 0.1f, StoreAs = "storage" },
                },
                Craft = new List<CraftRule>
                {
                    new CraftRule { Output = "gunpowder", Amount = 1, Station = "workbench" }, // blueprint costs
                    new CraftRule { Output = "ammo.pistol", Amount = 1, Station = "workbench" }, // blueprint costs
                    new CraftRule { Output = "ammo.rifle", Amount = 1, Station = "workbench" }, // blueprint costs
                    new CraftRule { Output = "explosives", Amount = 1, Station = "workbench" }, // blueprint costs
                },
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "ammo.pistol", Amount = 20, Scrap = 15 },
                    new SellRule { Item = "ammo.rifle", Amount = 20, Scrap = 25 },
                    new SellRule { Item = "explosives", Amount = 1, Scrap = 50 },
                    new SellRule { Item = "gunpowder", Amount = 30, Scrap = 20 },
                    new SellRule { Item = "metal.refined", Amount = 5, Scrap = 20 },
                    new SellRule { Item = "sulfur", Amount = 100, Scrap = 15 },
                    new SellRule { Item = "charcoal", Amount = 200, Scrap = 5 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 1000, ["metal.fragments"] = 200, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
            ["MonumentRunner"] = new EconomyProfile
            {
                // Crates and barrels are his economy (handoff 3.2). These abstract rates stand
                // in for what an embodied run hauls; ShouldWork gates runners out of the
                // chop/mine loop because the monument run IS their work.
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 43.8f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 25f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 12.5f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "scrap", RatePerMin = 2f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "gears", RatePerMin = 0.05f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "metalspring", RatePerMin = 0.05f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "techparts", RatePerMin = 0.03333f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "basicblueprintfragment", RatePerMin = 0.08333f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "advancedblueprintfragment", RatePerMin = 0.01667f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "metalblade", RatePerMin = 0.06667f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "metalpipe", RatePerMin = 0.05f, StoreAs = "storage" },
                    new GatherRule { Kind = "run", Resource = "keycard_green", RatePerMin = 0.00833f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "keycard_blue", RatePerMin = 0.00417f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "keycard_red", RatePerMin = 0.00133f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "workbench.upgrade.efficiency", RatePerMin = 0.00417f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "workbench.upgrade.salvage", RatePerMin = 0.00417f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "workbench.upgrade.surplus", RatePerMin = 0.00417f, StoreAs = "locker" },
                    new GatherRule { Kind = "run", Resource = "workbench.upgrade.reinforced", RatePerMin = 0.00417f, StoreAs = "locker" },
                },
                Craft = new List<CraftRule>(),
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "gears", Amount = 1, Scrap = 10 },
                    new SellRule { Item = "metalspring", Amount = 1, Scrap = 10 },
                    new SellRule { Item = "techparts", Amount = 1, Scrap = 20 },
                    new SellRule { Item = "basicblueprintfragment", Amount = 5, Scrap = 10 },
                    new SellRule { Item = "advancedblueprintfragment", Amount = 1, Scrap = 15 },
                    new SellRule { Item = "metalblade", Amount = 2, Scrap = 5 },
                    new SellRule { Item = "metalpipe", Amount = 1, Scrap = 8 },
                    new SellRule { Item = "keycard_green", Amount = 1, Scrap = 20 },
                    new SellRule { Item = "keycard_blue", Amount = 1, Scrap = 60 },
                    new SellRule { Item = "keycard_red", Amount = 1, Scrap = 150 },
                    new SellRule { Item = "workbench.upgrade.efficiency", Amount = 1, Scrap = 40 },
                    new SellRule { Item = "workbench.upgrade.salvage", Amount = 1, Scrap = 40 },
                    new SellRule { Item = "workbench.upgrade.surplus", Amount = 1, Scrap = 40 },
                    new SellRule { Item = "workbench.upgrade.reinforced", Amount = 1, Scrap = 40 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
            ["Hostile"] = new EconomyProfile
            {
                // No craft, no sell — deliberately. A Cobalt raider's income is the wage and
                // the bounty board (HostileBountyScrap/Career), not a shop. Their camp is
                // worth raiding for what's in the boxes, and that's the whole trade.
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 25f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 15f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 4f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                },
                Craft = new List<CraftRule>(),
                Sell = new List<SellRule>(),
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 50f, // the Cobalt wage — soldiers don't chop wood for a living (career is time-based for everyone since v0.20.0)
            },
            ["Roleplayer"] = new EconomyProfile
            {
                // The island's curio dealer: forages cloth and oddments, then trades in
                // atmosphere — instruments, lanterns, costumes, picture frames. Craft and
                // sell need a workbench and a vending machine, so both wake up at stage 2.
                Gather = new List<GatherRule>
                {
                    new GatherRule { Kind = "tree", Resource = "wood", RatePerMin = 36f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "stones", RatePerMin = 20f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.fragments", RatePerMin = 10f, StoreAs = "storage" },
                    new GatherRule { Kind = "ore", Resource = "metal.refined", RatePerMin = 0.125f, StoreAs = "locker" },
                    new GatherRule { Kind = "forage", Resource = "cloth", RatePerMin = 8f, StoreAs = "storage" },
                    new GatherRule { Kind = "forage", Resource = "bone.fragments", RatePerMin = 5f, StoreAs = "storage" },
                },
                Craft = new List<CraftRule>
                {
                    new CraftRule { Output = "fun.guitar", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["wood"] = 200, ["cloth"] = 50, ["metal.fragments"] = 25 } },
                    new CraftRule { Output = "lantern", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["metal.fragments"] = 60, ["cloth"] = 10 } },
                    new CraftRule { Output = "hat.candle", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["cloth"] = 20, ["metal.fragments"] = 15 } },
                    new CraftRule { Output = "partyhat", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["cloth"] = 25 } },
                    new CraftRule { Output = "sign.pictureframe.portrait", Amount = 1, Station = "workbench",
                        Ingredients = new Dictionary<string, int> { ["wood"] = 150 } },
                },
                Sell = new List<SellRule> // vending machines cap at 7 orders
                {
                    new SellRule { Item = "fun.guitar", Amount = 1, Scrap = 60 },
                    new SellRule { Item = "lantern", Amount = 1, Scrap = 20 },
                    new SellRule { Item = "hat.candle", Amount = 1, Scrap = 15 },
                    new SellRule { Item = "partyhat", Amount = 1, Scrap = 12 },
                    new SellRule { Item = "sign.pictureframe.portrait", Amount = 1, Scrap = 18 },
                    new SellRule { Item = "cloth", Amount = 20, Scrap = 8 },
                    new SellRule { Item = "bone.fragments", Amount = 50, Scrap = 6 },
                },
                Upkeep = new Dictionary<string, int> { ["wood"] = 1500, ["stones"] = 800, ["metal.fragments"] = 100, ["metal.refined"] = 50 },
                ScrapPerHour = 100f,
            },
        };


        // --- The tick ---------------------------------------------------------------
        private readonly HashSet<int> _econGroupsSeen = new HashSet<int>();
        private readonly List<BaseEntity> _econScan = new List<BaseEntity>(); // reused

        private void EconomyTick()
        {
            if (_bots.Count == 0) return;
            _econGroupsSeen.Clear();
            foreach (var rec in _bots.Values)
            {
                var gid = rec.Identity.GroupId;
                if (gid <= 0 || !_econGroupsSeen.Add(gid)) continue;
                StepEconomyGroup(gid);
            }
        }

        // One group's economy step — extracted from the tick so ff.econ.tick (G1) can
        // force it on demand and both paths stay identical.
        private void StepEconomyGroup(int gid)
        {
            // Skip purely non-economic households (free-roamers) — no ledger, no
            // auto-acquire — so only worker groups do per-tick work. Groups are
            // single-persona, so any member's profile decides it. A fully
            // fallen/retired group stops cold: no production, no upkeep — the
            // abandoned cabin FallBot emptied must actually decay (decision 0006).
            if (!GroupIsEconomic(gid) || !GroupHasActiveMember(gid)) return;
            var ge = Econ(gid);
            UpdateKitTiers(gid, ge);              // F4 — advance each member's kit tier
            BotRecord holder;
            var b = GetGroupBase(gid, out holder);
            if (b == null) { TryAutoAcquireBase(gid); return; } // F1 — build the first base

            var now = DateTime.Now;
            var last = ParseTime(ge.LastTickAt);
            var elapsedMin = last == DateTime.MaxValue ? _config.EconomyTickSeconds / 60f : (float)(now - last).TotalMinutes;
            if (elapsedMin < 0f) elapsedMin = 0f;
            elapsedMin = Mathf.Min(elapsedMin, _config.EconomyCatchupCapMin);

            StepGroupEconomy(gid, holder, b, ge, elapsedMin, IsObserved(b.Origin.V));
            ge.LastTickAt = now.ToString("o");
            TryStageUpgrade(holder, b, ge);       // F2 — upgrade the base at career thresholds
            MarkEconomyDirty();
        }

        // F1 — a bot's first productive session auto-builds stage 1. Fires from the
        // economy tick for any group that has a settled home (an anchor is only assigned
        // on a real session start) but no base yet. Throttled so a transient failure
        // (footprint blocked, paste already running) doesn't spam the log every tick, and
        // silent when the persona simply has no captured stages to build from.
        private readonly Dictionary<int, DateTime> _autoAcquireNext = new Dictionary<int, DateTime>();
        private const float AutoAcquireRetryMinutes = 5f;

        private void TryAutoAcquireBase(int gid)
        {
            if (!_config.BaseAutoAcquire) return;
            // WB-4: a group whose base was DESTROYED isn't a fresh settler — the loss
            // valve holds a rebuild note with its own delay and scrap charge. Stand
            // aside or this hands out an instant free cabin the moment the loss is
            // detected (raced the first live pass, 2026-08-01 17:42).
            BotRecord noteHolder;
            if (GetGroupRebuild(gid, out noteHolder) != null) return;
            DateTime next;
            if (_autoAcquireNext.TryGetValue(gid, out next) && next > DateTime.Now) return;

            // Pick a group member that has settled a home to build for; prefer one that's
            // currently online so the cabin tends to appear during a live session.
            BotRecord holder = null;
            foreach (var rec in _bots.Values)
            {
                if (rec.Identity.GroupId != gid || rec.State.HomePos == null) continue;
                holder = rec;
                if (rec.State.Status == "Online") break;
            }
            if (holder == null) return; // no anchor yet — nothing to build until a session starts

            _autoAcquireNext[gid] = DateTime.Now.AddMinutes(AutoAcquireRetryMinutes);

            // Degrade quietly (throttled) when there's no captured stage-1 blueprint yet —
            // the content dependency (the owner builds the cabins) may not be met on this server.
            List<string> stages;
            if (!_baseCatalog.Stages.TryGetValue(holder.Identity.Persona, out stages) || stages.Count < 1 || string.IsNullOrEmpty(stages[0]))
            {
                DLog($"[base] auto-acquire deferred for {holder.Identity.Name} — no captured stage 1 for {holder.Identity.Persona} (ff.base.capture {holder.Identity.Persona} 1).");
                return;
            }

            var err = PlaceBase(holder, 1, float.NaN);
            if (err != null) DLog($"[base] auto-acquire for {holder.Identity.Name} deferred: {err}");
            else DLog($"[base] auto-acquiring stage 1 for {holder.Identity.Name} ({holder.Identity.Persona}).");
        }

        // F2 — cross a career threshold → paste the next stage (the existing upgrade path;
        // PlaceBase demolishes the old shell only after its own validation, so a blocked
        // upgrade never leaves the bot homeless). The fresh shell re-furnishes over time.
        private readonly Dictionary<int, DateTime> _stageUpgradeNext = new Dictionary<int, DateTime>();

        private void TryStageUpgrade(BotRecord holder, BotBase b, GroupEconomy ge)
        {
            if (!_config.BaseAutoUpgrade || holder == null || b == null) return;
            if (_pasteBuffers.ContainsKey(holder.Identity.BotId)) return; // a paste is already running
            var idx = b.Stage - 1; // stage 1 → threshold[0] gates stage 2
            var thresholds = _config.BaseUpgradeThresholds;
            if (thresholds == null || idx < 0 || idx >= thresholds.Count) return;
            if (ge.CareerProduced < thresholds[idx]) return;

            var gid = holder.Identity.GroupId;
            DateTime retry;
            if (_stageUpgradeNext.TryGetValue(gid, out retry) && retry > DateTime.Now) return; // backing off a deferred upgrade

            var next = b.Stage + 1;
            List<string> stages;
            if (!_baseCatalog.Stages.TryGetValue(holder.Identity.Persona, out stages) || stages.Count < next || string.IsNullOrEmpty(stages[next - 1]))
                return; // no blueprint for the next stage — stay put
            var err = PlaceBase(holder, next, float.NaN);
            if (err == null)
            {
                _stageUpgradeNext.Remove(gid);
                DLog($"[base] {holder.Identity.Name}: career {ge.CareerProduced} ≥ {thresholds[idx]} — upgrading base {b.Stage} → {next}.");
            }
            else
            {
                // Back off so a blocked upgrade (e.g. no room for the bigger footprint)
                // doesn't re-attempt + log every tick until an admin re-anchors.
                _stageUpgradeNext[gid] = DateTime.Now.AddMinutes(5);
                DLog($"[base] {holder.Identity.Name}: stage upgrade to {next} deferred (retry ~5m): {err}");
            }
        }

        private void MarkEconomyDirty() => _economyDirty = true;

        // A group is economic — worth ticking — if its (single) persona either produces
        // (has a gather profile) OR builds (has a captured stage-1 cabin). The latter lets
        // Smelter/Farmer build + maintain a base before their gather profile is authored
        // (Phase 3.5); their tick just no-ops production until then. Free-roamer households
        // (MonumentRunner/Hostile/Roleplayer — no profile, no captured cabin) are skipped.
        private bool GroupHasActiveMember(int gid)
        {
            foreach (var rec in _bots.Values)
                if (rec.Identity.GroupId == gid && !IsOut(rec)) return true;
            return false;
        }

        private bool GroupHasOnlineMember(int gid)
        {
            foreach (var rec in _bots.Values)
                if (rec.Identity.GroupId == gid && rec.State.Status == "Online") return true;
            return false;
        }

        // Members still on the roster (retired/fallen identities don't count).
        private int GroupMemberCount(int gid)
        {
            var n = 0;
            foreach (var rec in _bots.Values)
                if (rec.Identity.GroupId == gid && !IsOut(rec)) n++;
            return Mathf.Max(1, n);
        }

        private bool GroupIsEconomic(int gid)
        {
            foreach (var rec in _bots.Values)
            {
                if (rec.Identity.GroupId != gid) continue;
                var p = ProfileFor(rec);
                if (p != null && p.Gather.Count > 0) return true;
                List<string> stages;
                if (_baseCatalog.Stages.TryGetValue(rec.Identity.Persona, out stages) && stages != null && stages.Count > 0 && !string.IsNullOrEmpty(stages[0])) return true;
            }
            return false;
        }

        // Advance one group's economy: produce (abstract when unwatched, else the
        // embodied Stage B path handles it), then process — cook (D2), craft (D1), and
        // service the shop (E). Processing runs regardless of who's watching.
        private void StepGroupEconomy(int groupId, BotRecord holder, BotBase b, GroupEconomy ge, float elapsedMin, bool observed)
        {
            var profile = ProfileFor(holder);
            if (profile == null) return;
            var buckets = FindBaseContainers(b);
            if (buckets.Count == 0) return; // empty shell — no boxes placed yet (fill gates this)

            // Field gather and the wage only accrue while a member is actually online —
            // "per hour" means hours worked, not wall-clock. Crops keep growing (a planter
            // doesn't care who's logged in), and cooking/smelting below run unattended too.
            var online = GroupHasOnlineMember(groupId);
            if (online && profile.ScrapPerHour > 0f)
            {
                var wage = profile.ScrapPerHour * (elapsedMin / 60f) + ge.WageCarry;
                var whole = (int)wage;
                ge.WageCarry = wage - whole;
                if (whole > 0) ge.Scrap += whole;
            }
            // v0.20.0: career = time on the clock (see EconomyProfile.CareerPerMinute).
            // Same skill curve as the gather rules (0.5x–1.5x). A group is online for the
            // UNION of its members' sessions, so a duo already earns ~2× the hours; the
            // 1/sqrt(n) keeps that at ~1.4× — faster than a solo, not double (decided
            // 2026-09-12: the furnace brothers may lead, not lap the island).
            if (online && profile.CareerPerMinute > 0f)
            {
                var skillScale = 0.5f + (holder.Identity.Traits?.SkillLevel ?? 0.5f);
                var duoScale = 1f / Mathf.Sqrt(GroupMemberCount(groupId));
                var pay = profile.CareerPerMinute * skillScale * duoScale * elapsedMin + ge.CareerCarry;
                var wholePay = (long)pay;
                ge.CareerCarry = pay - wholePay;
                if (wholePay > 0) { ge.CareerProduced += wholePay; MarkEconomyDirty(); }
            }
            // 6A: while a runner is physically out on a run, his group's abstract
            // production stands down even if the base is unobserved — the haul he's
            // carrying IS this period's yield (no double-dipping).
            if (online && !observed && profile.Gather.Count > 0 && !GroupHasActiveRunner(groupId))
                AbstractProduce(holder, ge, profile, buckets, elapsedMin);
            ProduceCrops(holder, ge, profile, buckets, elapsedMin); // 3.5 — planter-gated, runs watched or not

            MaintainUpkeep(profile, b, buckets);           // keep the TC fed so the base doesn't decay
            SweepSpoiledFood(buckets);                     // food spoils (realism) — clear the waste so boxes don't clog
            CookMeat(buckets, elapsedMin);                 // D2
            SmeltOre(buckets, elapsedMin);                 // 3.5 — ore → fragments/sulfur/HQM, spare wood → charcoal
            CraftGoods(profile, buckets);                  // D1
            ServiceShop(holder, ge, profile, buckets);     // E1/E2/E3
            BankSpareKit(holder, ge, buckets);             // F4 — bank a spare kit when affordable
        }

        // Food in a bot's boxes spoils on the normal Rust timer (an unpowered fridge won't
        // stop it, and we don't run electricity). Let it spoil for realism, but sweep the
        // spoiled results (horsemeat.spoiled, spoiled.produce, …) so they don't clog the
        // boxes' slots. Raw stock is kept — CookMeat/ServiceShop keep it moving.
        private void SweepSpoiledFood(Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            foreach (var sc in Stashes(buckets))
            {
                var items = new List<Item>(sc.inventory.itemList);
                foreach (var it in items)
                    if (it?.info != null && it.info.shortname.Contains("spoiled")) it.Remove();
            }
        }

        // F4 — advance each group member's kit tier. Career (group-wide) earns the tier;
        // the bot's own skill caps how high it can reach, so a green worker never fields
        // top gear even in a rich group. Cheap: a few int compares per member per tick.
        private void UpdateKitTiers(int gid, GroupEconomy ge)
        {
            foreach (var rec in _bots.Values)
            {
                if (rec.Identity.GroupId != gid) continue;
                if (IsOut(rec)) continue; // the fallen don't earn posthumous tier-ups
                if (rec.Progression == null) rec.Progression = new BotProgression();
                var tier = ComputeKitTier(rec, ge);
                if (rec.Progression.KitTier != tier)
                {
                    var up = tier > rec.Progression.KitTier;
                    rec.Progression.KitTier = tier;
                    MarkDirty(rec.Identity.BotId);
                    if (up)
                    {
                        Story("kit", $"{rec.Identity.Name} upgraded to kit tier {tier}");
                        DLog($"[kit] {rec.Identity.Name} → kit tier {tier} (career {ge.CareerProduced}).");
                    }
                }

                // 4A — buy back up to the earned tier after a death knocked the physical
                // gear down: costs scrap (same schedule as banking a spare), re-gears live.
                // GearTier at/above the earned tier means nothing is owed — normalize the
                // sentinel so this bot doesn't pay for a free future tier-up.
                if (rec.Progression.GearTier >= rec.Progression.KitTier && rec.Progression.GearTier >= 0)
                {
                    rec.Progression.GearTier = -1;
                    MarkDirty(rec.Identity.BotId);
                }
                var gearTier = rec.Progression.GearTier;
                if (gearTier >= 0 && gearTier < rec.Progression.KitTier && ge.Scrap >= KitCost(rec.Progression.KitTier))
                {
                    var live = GetLive(rec.Identity.BotId);
                    if (live != null && !live.IsDead())
                    {
                        ge.Scrap -= KitCost(rec.Progression.KitTier);
                        rec.Progression.GearTier = -1;
                        live.inventory.Strip();
                        GiveKit(live, rec);
                        MarkDirty(rec.Identity.BotId);
                        MarkEconomyDirty();
                        Story("kit", $"{rec.Identity.Name} re-geared after a rough death");
                        DLog($"[kit] {rec.Identity.Name} bought back tier {rec.Progression.KitTier} gear (scrap now {ge.Scrap}).");
                    }
                }
            }
        }

        private int ComputeKitTier(BotRecord rec, GroupEconomy ge)
        {
            // Kits follow the home: base stage IS the kit tier (content pass decision).
            // No base yet → tier 0 flavor clothes. KitTierThresholds config is retired.
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            return Mathf.Clamp(b?.Stage ?? 0, 0, MaxKitTier);
        }

        private int KitCost(int tier)
        {
            var costs = _config.KitBankScrapCost;
            if (costs == null || costs.Count == 0) return 40 + tier * 40;
            return costs[Mathf.Clamp(tier, 0, costs.Count - 1)];
        }

        // The group can afford a spare kit it doesn't yet have banked → worth placing the
        // locker to hold it (F3 gates locker fill on this).
        private bool SpareKitReady(BotRecord holder, GroupEconomy ge)
        {
            var tier = Mathf.Clamp(holder?.Progression?.KitTier ?? 0, 0, MaxKitTier);
            return ge.SpareKitTier < tier && ge.Scrap >= KitCost(tier);
        }

        // F4 — once a locker exists and scrap allows, stock one spare re-gear bundle at the
        // current tier (the Phase 4 death loop re-equips from here). One per tier-up: the
        // SpareKitTier guard stops it topping the locker up every tick.
        private void BankSpareKit(BotRecord holder, GroupEconomy ge, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            var tier = Mathf.Clamp(holder?.Progression?.KitTier ?? 0, 0, MaxKitTier);
            if (ge.SpareKitTier >= tier) return;               // already banked this tier (or better)
            // Require a real locker — NOT PickContainer's storage fallback. Falling back to
            // storage would bank the kit into the goods box, spend the scrap, and set
            // SpareKitTier, which permanently closes the locker's need-gate (SpareKitReady)
            // so the locker is never placed. Wait for the actual locker instead.
            List<IItemContainerEntity> lockers;
            if (!buckets.TryGetValue("locker", out lockers) || lockers.Count == 0) return; // no locker yet — F3 places it when affordable
            var locker = lockers[0];
            var cost = KitCost(tier);
            if (ge.Scrap < cost) return;

            var ladder = ResolveKitLadder(holder.Identity.Persona);
            var kit = ladder[Mathf.Clamp(tier, 0, ladder.Count - 1)];
            // Don't pay for a kit that won't fit — bail if the locker lacks room for the
            // whole bundle (weapon + ammo + med + each wear piece), retry a later tick.
            var needSlots = (kit.Wear?.Length ?? 0) + 3;
            if (locker.inventory.capacity - locker.inventory.itemList.Count < needSlots) return;

            ForEachKitItem(kit, (shortname, amount) => DepositToContainer(locker.inventory, shortname, amount));
            ge.Scrap -= cost;
            ge.SpareKitTier = tier;
            MarkEconomyDirty();
            Story("kit", $"{holder.Identity.Name} banked a spare tier-{tier} kit in the locker");
            DLog($"[kit] {holder.Identity.Name} banked a tier {tier} spare kit for {cost} scrap (scrap now {ge.Scrap}).");
        }

        // 4A — the spare-kit bundle walk shared by banking, validation, and consumption.
        private static void ForEachKitItem(KitLoadout kit, Action<string, int> act)
        {
            if (kit.Wear != null) foreach (var w in kit.Wear) act(w, 1);
            if (!string.IsNullOrEmpty(kit.Weapon)) act(kit.Weapon, 1);
            if (!string.IsNullOrEmpty(kit.Ammo) && kit.AmmoCount > 0) act(kit.Ammo, kit.AmmoCount);
            if (!string.IsNullOrEmpty(kit.Med) && kit.Meds > 0) act(kit.Med, kit.Meds);
        }

        // 4A — peek/commit pair for the banked spare kit (decision 0006). Peek only
        // validates (never mutates the locker) so a failed spawn attempt can't destroy
        // the kit; commit removes the bundle after the bot actually spawned wearing it.
        // Physical-canonical: locker gone OR emptied by raiders → the spare went with
        // it — clear the flag and the bot respawns in burlap (no conjured gear).
        private bool TryPeekSpareKit(BotRecord rec, out int tier)
        {
            tier = 0;
            var gid = rec.Identity.GroupId;
            if (gid <= 0) return false;
            var ge = Econ(gid);
            if (ge == null || ge.SpareKitTier < 0) return false;
            var inv = FindSpareKitLocker(gid);
            if (inv == null) { ge.SpareKitTier = -1; MarkEconomyDirty(); return false; }
            var ladder = ResolveKitLadder(rec.Identity.Persona);
            var kit = ladder[Mathf.Clamp(ge.SpareKitTier, 0, ladder.Count - 1)];
            var present = false;
            ForEachKitItem(kit, (shortname, amount) => { if (!present && CountInContainer(inv, shortname) > 0) present = true; });
            if (!present)
            {
                ge.SpareKitTier = -1; // looted hollow — the raider has the gear now
                MarkEconomyDirty();
                DLog($"[kit] {rec.Identity.Name}'s spare kit was looted from the locker — respawning without it.");
                return false;
            }
            tier = ge.SpareKitTier;
            return true;
        }

        private void CommitSpareKit(BotRecord rec, int tier)
        {
            var ge = Econ(rec.Identity.GroupId);
            if (ge == null) return;
            var inv = FindSpareKitLocker(rec.Identity.GroupId);
            var ladder = ResolveKitLadder(rec.Identity.Persona);
            var kit = ladder[Mathf.Clamp(tier, 0, ladder.Count - 1)];
            if (inv != null) ForEachKitItem(kit, (shortname, amount) => TakeFromContainer(inv, shortname, amount));
            ge.SpareKitTier = -1;
            MarkEconomyDirty();
            DLog($"[kit] {rec.Identity.Name} re-geared from the locker's spare tier-{tier} kit.");
            Story("kit", $"{rec.Identity.Name} grabbed the spare kit from the locker");
        }

        private ItemContainer FindSpareKitLocker(int gid)
        {
            BotRecord holder;
            var b = GetGroupBase(gid, out holder);
            List<IItemContainerEntity> lockers;
            if (b == null || !FindBaseContainers(b).TryGetValue("locker", out lockers) || lockers.Count == 0) return null;
            return lockers[0].inventory;
        }

        private static void TakeFromContainer(ItemContainer c, string shortname, int amount)
        {
            if (c?.itemList == null || amount <= 0) return;
            var items = new List<Item>(c.itemList); // snapshot — UseItem mutates the list
            foreach (var it in items)
            {
                if (amount <= 0) return;
                if (it?.info == null || it.info.shortname != shortname) continue;
                var take = Mathf.Min(it.amount, amount);
                it.UseItem(take);
                amount -= take;
            }
        }

        // Top the TC up to the profile's upkeep targets from the resource boxes — the
        // "farm → deposit → maintain" loop. As long as production keeps wood/stone in the
        // boxes, the base never decays; raid the boxes and upkeep lapses (decision 0005).
        private void MaintainUpkeep(EconomyProfile profile, BotBase b, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            if (profile.Upkeep == null || profile.Upkeep.Count == 0 || b.TcNetId == 0) return;
            var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
            if (tc == null || tc.IsDestroyed || tc.inventory == null) return;
            foreach (var kv in profile.Upkeep)
            {
                var need = kv.Value - CountInContainer(tc.inventory, kv.Key);
                if (need > 0) MoveIntoContainer(buckets, tc.inventory, kv.Key, need);
            }
        }

        // C1 — deposit gathered goods straight into the real boxes at the persona/skill
        // rate, capacity-throttled. Same accounting as embodied gathering (decision 0005).
        private void AbstractProduce(BotRecord holder, GroupEconomy ge, EconomyProfile profile, Dictionary<string, List<IItemContainerEntity>> buckets, float elapsedMin)
        {
            // Pick ONE meat rule this tick (reservoir sample) so meat arrives as
            // occasional distinct hauls — one animal at a time — not every species at once.
            GatherRule meatPick = null;
            var seen = 0;
            foreach (var g in profile.Gather)
                if (g.Kind == "meat" && UnityEngine.Random.Range(0, ++seen) == 0) meatPick = g;
            RunGatherRules(holder, ge, profile, buckets, elapsedMin, crops: false, meatPick: meatPick);
        }

        // The ONE yield curve + deposit path for all abstract gather rules — crops
        // and field gathering must never drift apart (0.5x–1.5x skill scale, stochastic
        // rounding, capacity-throttled deposit). Career is NOT credited here since
        // v0.20.0 — it is time-based in StepGroupEconomy, so crops growing with nobody
        // online (the only offline career path, ~1/3 of Farmer career by day 9) and the
        // persona bulk spread no longer drive progression.
        private void RunGatherRules(BotRecord holder, GroupEconomy ge, EconomyProfile profile,
            Dictionary<string, List<IItemContainerEntity>> buckets, float elapsedMin, bool crops, GatherRule meatPick)
        {
            var skill = holder.Identity.Traits?.SkillLevel ?? 0.5f;
            var skillScale = 0.5f + skill; // 0.5x … 1.5x
            // Crop rates are authored PER PLANTER BOX — more boxes (bigger base stages)
            // mean a bigger farm. Non-crop rules ignore the multiplier.
            var planters = 1;
            List<IItemContainerEntity> pl;
            if (crops && buckets.TryGetValue("planter", out pl)) planters = Mathf.Max(1, pl.Count);
            foreach (var g in profile.Gather)
            {
                if ((g.Kind == "crop") != crops) continue;
                if (g.Kind == "meat" && g != meatPick) continue; // only the chosen meat this tick
                var mult = g.Kind == "crop" ? planters : 1;
                var units = UnitsFor(g.RatePerMin * skillScale * elapsedMin * mult);
                if (units <= 0) continue;
                // v0.16.0: deposit what fits; the overflow is hauled off in bulk (the
                // shop fiction already sells surplus). Crediting only deposited units
                // once froze every group as its stage-1 boxes filled — a deadlock, since
                // the storage that would unfreeze career comes from the stage the frozen
                // career can't reach. v0.20.0 moved the ledger to time entirely.
                var box = PickContainer(buckets, g.StoreAs);
                if (box != null)
                {
                    if (g.Resource.StartsWith("clone.", StringComparison.Ordinal))
                        DepositClone(holder, profile, box.inventory, g.Resource, units);
                    else
                        DepositToContainer(box.inventory, g.Resource, units);
                }
            }
        }

        // Clone cuttings carry genes: the profile's CloneGenes ladder (indexed by base
        // stage) stamps each deposited clone, so a stage-3 farm hands out GGGYYY while a
        // stage-1 patch sells scruffy cuttings. Falls back to plain deposit when the
        // profile has no gene ladder.
        private int DepositClone(BotRecord holder, EconomyProfile profile, ItemContainer container, string shortname, int amount)
        {
            var genes = CloneGenesFor(holder, profile);
            if (genes == null) return DepositToContainer(container, shortname, amount);
            var stored = 0;
            for (var i = 0; i < amount; i++) // clones don't stack once gene-stamped — create singly
            {
                var item = ItemManager.CreateByName(shortname, 1);
                if (item == null) { PrintWarning($"[econ] unknown clone shortname '{shortname}' — skipped."); return stored; }
                GrowableGeneEncoding.EncodeGenesToItem(GrowableGeneEncoding.EncodeGeneStringToInt(genes), item);
                if (item.MoveToContainer(container)) stored++;
                else { item.Remove(); return stored; } // box full — capacity throttle (decision 0005)
            }
            return stored;
        }

        private string CloneGenesFor(BotRecord holder, EconomyProfile profile)
        {
            if (profile?.CloneGenes == null || profile.CloneGenes.Count == 0) return null;
            BotRecord h;
            var b = GetGroupBase(holder.Identity.GroupId, out h);
            var idx = Mathf.Clamp((b?.Stage ?? 1) - 1, 0, profile.CloneGenes.Count - 1);
            var genes = profile.CloneGenes[idx];
            return string.IsNullOrEmpty(genes) ? null : genes;
        }

        // Expectation-preserving integer yield for a fractional amount: floor plus a
        // probabilistic extra unit, so low rates (hq ore) and short ticks don't floor
        // to zero forever — a 0.6/tick rate really averages 0.6/tick.
        private static int UnitsFor(float x)
        {
            var n = Mathf.FloorToInt(x);
            if (UnityEngine.Random.value < x - n) n++;
            return n;
        }

        // 3.5 — crop rules are stationary planter production, like cooking: they advance
        // every tick regardless of who's watching, but ONLY while a live planter stands
        // at the base. Raiders smashing the planter stop the farm with zero
        // reconciliation (decision 0005); the fill loop re-places it in time.
        private void ProduceCrops(BotRecord holder, GroupEconomy ge, EconomyProfile profile, Dictionary<string, List<IItemContainerEntity>> buckets, float elapsedMin)
        {
            if (!buckets.ContainsKey("planter")) return;
            RunGatherRules(holder, ge, profile, buckets, elapsedMin, crops: true, meatPick: null);
        }

        // D2 — a furnace/BBQ turns raw meat into cooked over time.
        private void CookMeat(Dictionary<string, List<IItemContainerEntity>> buckets, float elapsedMin)
        {
            if (!buckets.ContainsKey("furnace")) return; // need a furnace/BBQ placed
            var budget = Mathf.Max(1, Mathf.FloorToInt(_config.CookUnitsPerMin * elapsedMin));
            foreach (var sc in Stashes(buckets))
            {
                var items = new List<Item>(sc.inventory.itemList);
                foreach (var it in items)
                {
                    if (budget <= 0) return;
                    if (it?.info == null) continue;
                    var sn = it.info.shortname;
                    if (!(sn.Contains("meat") || sn.Contains("chicken"))) continue;
                    var cooked = CookedForm(sn);
                    if (cooked == null || ItemManager.FindItemDefinition(cooked) == null) continue;
                    var n = Mathf.Min(it.amount, budget);
                    it.UseItem(n);
                    var box = PickContainer(buckets, "storage") ?? sc;
                    DepositToContainer(box.inventory, cooked, n);
                    budget -= n;
                }
            }
        }

        // 3.5 — a placed furnace also smelts mined ore into product over time (the
        // Smelter's core loop, but generic — any persona whose boxes hold ore benefits).
        // Recipes live in config (SmeltRecipes) so they tune like the economy profiles.
        // Deposit-then-remove per conversion so a full output box throttles production
        // instead of destroying ore (decision 0005 capacity throttle). Spare wood chars
        // to charcoal at 2:1, but only above a reserve so smelting never starves TC
        // upkeep or crafting of fuel.
        private void SmeltOre(Dictionary<string, List<IItemContainerEntity>> buckets, float elapsedMin)
        {
            if (!buckets.ContainsKey("furnace")) return;
            if (_config.SmeltRecipes == null || _config.SmeltRecipes.Count == 0) return;
            var outBox = PickContainer(buckets, "storage");
            if (outBox == null) return;
            var budget = Mathf.Max(1, Mathf.FloorToInt(_config.SmeltUnitsPerMin * elapsedMin));
            foreach (var pair in _config.SmeltRecipes)
            {
                if (budget <= 0) return;
                // "ore@station" binds a recipe to a specific station bucket — e.g.
                // "crude.oil@refinery" only runs once the stage-3 refinery stands. The
                // base's physical equipment is the tech tree.
                var res = pair.Key;
                var at = res.IndexOf('@');
                if (at > 0)
                {
                    var station = res.Substring(at + 1);
                    res = res.Substring(0, at);
                    if (!buckets.ContainsKey(station)) continue;
                }
                var n = Mathf.Min(CountAcross(buckets, res), budget);
                if (n <= 0) continue;
                var stored = DepositToContainer(outBox.inventory, pair.Value, n);
                if (stored <= 0) continue; // output box full (or bad product name) — ore stays put
                RemoveAcross(buckets, res, stored);
                budget -= stored;
            }
            if (budget <= 0) return;
            var spareWood = CountAcross(buckets, "wood") - _config.CharcoalWoodReserve;
            if (spareWood < 2) return;
            var charcoal = Mathf.Min(spareWood / 2, budget);
            var made = DepositToContainer(outBox.inventory, "charcoal", charcoal);
            if (made > 0) RemoveAcross(buckets, "wood", made * 2);
        }

        private static string CookedForm(string raw)
        {
            if (raw.EndsWith(".cooked") || raw.EndsWith(".burned") || raw.EndsWith(".spoiled")) return null;
            if (raw.EndsWith(".raw")) return raw.Substring(0, raw.Length - 4) + ".cooked";
            if (raw == "bearmeat") return "bearmeat.cooked";
            if (raw == "meat.boar") return "meat.pork.cooked";
            // jungle species (5B content pass) — flat shortnames, no ".raw" suffix
            if (raw == "snakemeat" || raw == "bigcatmeat" || raw == "crocodilemeat") return raw + ".cooked";
            return null;
        }

        // D1 — craft profile goods from real ingredients, station-gated. One batch per
        // tick per rule; honest costs pulled from the boxes. Costs come from the item's
        // ItemBlueprint, or from the rule's explicit Ingredients for outputs that have
        // none (mixing-table teas use the engine's recipe system, not blueprints).
        private readonly Dictionary<string, int> _craftCosts = new Dictionary<string, int>(); // reused per rule
        // The materials the door/shell/upkeep wardens spend — crafting must not drain
        // these to zero or repairs starve silently (EchoRose, 2026-08-23). HQM is
        // deliberately absent: it accrues too slowly for a flat reserve to ever clear.
        private static readonly HashSet<string> RepairMats = new HashSet<string> { "wood", "stones", "metal.fragments" };
        private int ReserveFloor(string shortname) =>
            _config.CraftMaterialReserve > 0 && RepairMats.Contains(shortname) ? _config.CraftMaterialReserve : 0;

        private void CraftGoods(EconomyProfile profile, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            foreach (var rule in profile.Craft)
            {
                if (!string.IsNullOrEmpty(rule.Station) && !buckets.ContainsKey(rule.Station)) continue;
                _craftCosts.Clear();
                var yield = rule.Amount;
                if (rule.Ingredients != null && rule.Ingredients.Count > 0)
                {
                    // Data-tunable recipe: validate the output BEFORE consuming (a typo
                    // in economy_profiles.json must not silently eat the berries), and
                    // scale per-unit costs by Amount so cost stays proportional to yield.
                    if (ItemManager.FindItemDefinition(rule.Output) == null)
                    {
                        PrintWarning($"[econ] unknown craft output '{rule.Output}' in an economy profile — rule skipped.");
                        continue;
                    }
                    foreach (var kv in rule.Ingredients) _craftCosts[kv.Key] = kv.Value * Mathf.Max(1, rule.Amount);
                }
                else
                {
                    var def = ItemManager.FindItemDefinition(rule.Output);
                    var bp = def?.Blueprint;
                    if (bp == null || bp.ingredients == null || bp.ingredients.Count == 0) continue;
                    var bad = false;
                    foreach (var ing in bp.ingredients)
                    {
                        if (ing?.itemDef == null) { bad = true; break; }
                        _craftCosts[ing.itemDef.shortname] = Mathf.CeilToInt(ing.amount) * rule.Amount;
                    }
                    if (bad || _craftCosts.Count == 0) continue;
                    // One craft yields bp.amountToCreate (gunpowder = 10/batch, rug = 1).
                    yield = rule.Amount * Mathf.Max(1, bp.amountToCreate);
                }
                // v0.16.4: EchoRose's shop had never sold a thing, yet crafting had
                // turned every metal fragment into curios (8 guitars banked) and left
                // the boxes too poor to hang a heli-blown door. A rule stands down
                // while enough output sits unsold, and never dips into the repair
                // reserve the door/upkeep wardens draw from.
                if (_config.CraftStockCap > 0 && CountAcross(buckets, rule.Output) >= yield * _config.CraftStockCap) continue;
                var ok = true;
                foreach (var kv in _craftCosts)
                    if (CountAcross(buckets, kv.Key) < kv.Value + ReserveFloor(kv.Key)) { ok = false; break; }
                if (!ok) continue;
                foreach (var kv in _craftCosts) RemoveAcross(buckets, kv.Key, kv.Value);
                var outBox = PickContainer(buckets, "locker") ?? PickContainer(buckets, "storage");
                if (outBox != null) DepositToContainer(outBox.inventory, rule.Output, yield);
            }
        }

        // E — keep the vending machine configured, stocked from production, and swept of
        // its scrap income. Works while the bot is offline (native buy UI).
        private void ServiceShop(BotRecord holder, GroupEconomy ge, EconomyProfile profile, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            List<IItemContainerEntity> shops;
            if (!buckets.TryGetValue("shop", out shops) || shops.Count == 0) return;
            var vm = shops[0] as VendingMachine;
            if (vm == null || vm.IsDestroyed) return;
            // v0.16.3: visibility first, for EVERY bot shop — personas with no Sell rules
            // (Hostile) return below and never reached the old in-line enforcement, so
            // their pasted machine stayed on the map (Havoc_77, live 2026-08-21).
            ApplyShopBroadcast(vm);
            if (profile.Sell.Count == 0) return;

            // E1 — the storefront. Vanilla machines list at most 7 orders (server-side
            // AddSellOrder cap, confirmed live 2026-07-20: SmokeyAsh's 14-rule profile
            // showed 7). Choose up to 7 rules by ACTUAL stock (boxes or machine inv),
            // profile order as priority — the listing rotates as stock changes, so a
            // wide catalog sells everything eventually, 7 goods at a time. Rebuild
            // whenever the chosen set, amounts, or prices (clone stage-ups) drift.
            var chosen = new List<SellRule>();
            var rules = profile.Sell.Count;
            // Wide catalogs walk a 30-min rotating window so tail goods (red cards,
            // workbench modules) get shelf time even while the front stays stocked.
            var start = rules > 7 ? (int)(DateTime.Now.Ticks / TimeSpan.TicksPerMinute / 30 % rules) : 0;
            for (var k = 0; k < rules && chosen.Count < 7; k++)
            {
                var s = profile.Sell[(start + k) % rules];
                if (CountAcross(buckets, s.Item) > 0 || CountInContainer(vm.inventory, s.Item) > 0) chosen.Add(s);
            }
            if (chosen.Count == 0) // nothing stocked yet — show intent (first 7) so the shop isn't blank
                for (var i = 0; i < profile.Sell.Count && i < 7; i++) chosen.Add(profile.Sell[i]);

            var current = vm.sellOrders?.sellOrders?.Count ?? 0;
            var stale = current != chosen.Count;
            if (!stale && vm.sellOrders?.sellOrders != null)
                for (var i = 0; i < chosen.Count; i++)
                {
                    var def = ItemManager.FindItemDefinition(chosen[i].Item);
                    var o = vm.sellOrders.sellOrders[i];
                    if (def == null || o.itemToSellID != def.itemid || o.itemToSellAmount != chosen[i].Amount ||
                        o.currencyAmountPerItem != SellPriceFor(holder, profile, chosen[i]))
                    { stale = true; break; }
                }
            if (stale)
            {
                var scrap = ItemManager.FindItemDefinition("scrap");
                if (scrap != null)
                {
                    var firstOpen = current == 0;
                    if (vm.sellOrders?.sellOrders != null) vm.sellOrders.sellOrders.Clear();
                    foreach (var s in chosen)
                    {
                        var def = ItemManager.FindItemDefinition(s.Item);
                        if (def != null) vm.AddSellOrder(def.itemid, s.Amount, scrap.itemid, SellPriceFor(holder, profile, s), 0);
                    }
                    vm.shopName = $"{holder.Identity.Name}'s Goods";
                    if (firstOpen)
                    {
                        DLog($"[shop] {holder.Identity.Name}'s shop opened ({vm.sellOrders?.sellOrders?.Count ?? 0} orders)");
                        Story("shop", $"{holder.Identity.Name} opened a shop at home");
                    }
                    else DLog($"[shop] {holder.Identity.Name}'s sell orders updated to {vm.sellOrders?.sellOrders?.Count ?? 0}");
                }
            }

            // E2 — restock sell slots from stored goods, up to ~10 orders' worth.
            // The machine's canAcceptItem filter rejects items pushed in from a bot-owned
            // box server-side (it only allows corpse loot or an admin's own transfer), so
            // clear the filter around our restock, then restore it — player buying, which
            // relies on the filter, is untouched (this whole pass is synchronous).
            if (vm.inventory != null)
            {
                var savedFilter = vm.inventory.canAcceptItem;
                vm.inventory.canAcceptItem = null;
                foreach (var s in chosen) // stock only what's listed — the rest stays boxed until rotation
                {
                    var want = s.Amount * Mathf.Max(1, _config.ShopRestockOrders) - CountInContainer(vm.inventory, s.Item);
                    if (want > 0) MoveIntoContainer(buckets, vm.inventory, s.Item, want);
                }
                vm.inventory.canAcceptItem = savedFilter;
            }

            // E3 — sweep collected scrap (currency) into the group ledger.
            var swept = 0;
            var vmItems = new List<Item>(vm.inventory.itemList);
            foreach (var it in vmItems)
            {
                if (it?.info?.shortname != "scrap") continue; // scrap is never a Trapper good → income
                swept += it.amount;
                it.Remove();
            }
            if (swept > 0)
            {
                ge.Scrap += swept;
                MarkEconomyDirty();
                if (!ge.HadFirstSale) { ge.HadFirstSale = true; Story("sale", $"{holder.Identity.Name} made a first sale"); }
                DLog($"[shop] {holder.Identity.Name} collected {swept} scrap (total {ge.Scrap})");
            }

            vm.FullUpdate();
        }

        // Clone listings are priced by the farm's base stage (CloneScrapByStage ladder) —
        // better genes, better price. Everything else sells at the authored price.
        private int SellPriceFor(BotRecord holder, EconomyProfile profile, SellRule s)
        {
            if (profile.CloneScrapByStage == null || profile.CloneScrapByStage.Count == 0 ||
                !s.Item.StartsWith("clone.", StringComparison.Ordinal)) return s.Scrap;
            BotRecord h;
            var b = GetGroupBase(holder.Identity.GroupId, out h);
            var idx = Mathf.Clamp((b?.Stage ?? 1) - 1, 0, profile.CloneScrapByStage.Count - 1);
            return Mathf.Max(1, profile.CloneScrapByStage[idx]);
        }

        private static readonly string[] StashPurposes = { "storage", "food", "locker" };
        private IEnumerable<IItemContainerEntity> Stashes(Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            foreach (var p in StashPurposes)
            {
                List<IItemContainerEntity> list;
                if (buckets.TryGetValue(p, out list))
                    foreach (var sc in list) yield return sc;
            }
        }

        private int CountAcross(Dictionary<string, List<IItemContainerEntity>> buckets, string shortname)
        {
            var n = 0;
            foreach (var sc in Stashes(buckets))
                foreach (var it in sc.inventory.itemList)
                    if (it?.info != null && it.info.shortname == shortname) n += it.amount;
            return n;
        }

        private void RemoveAcross(Dictionary<string, List<IItemContainerEntity>> buckets, string shortname, int amount)
        {
            foreach (var sc in Stashes(buckets))
            {
                if (amount <= 0) return;
                var before = CountInContainer(sc.inventory, shortname);
                TakeFromContainer(sc.inventory, shortname, amount);
                amount -= Mathf.Min(before, amount);
            }
        }

        private static int CountInContainer(ItemContainer c, string shortname)
        {
            var n = 0;
            if (c?.itemList != null)
                foreach (var it in c.itemList)
                    if (it?.info != null && it.info.shortname == shortname) n += it.amount;
            return n;
        }

        private int MoveIntoContainer(Dictionary<string, List<IItemContainerEntity>> buckets, ItemContainer dest, string shortname, int amount)
        {
            var moved = 0;
            foreach (var sc in Stashes(buckets))
            {
                if (amount <= 0) break;
                var items = new List<Item>(sc.inventory.itemList);
                foreach (var it in items)
                {
                    if (amount <= 0) break;
                    if (it?.info == null || it.info.shortname != shortname) continue;
                    if (it.amount <= amount)
                    {
                        var a = it.amount;
                        if (it.MoveToContainer(dest)) { moved += a; amount -= a; }
                    }
                    else
                    {
                        var split = it.SplitItem(amount);
                        if (split == null) continue;
                        if (split.MoveToContainer(dest)) { moved += amount; amount = 0; }
                        else split.MoveToContainer(sc.inventory); // didn't fit — put it back
                    }
                }
            }
            return moved;
        }

        // Live owned storage at a base, bucketed by anchor purpose. TC (upkeep) and
        // unknown containers are excluded so raw production never lands in them.
        private Dictionary<string, List<IItemContainerEntity>> FindBaseContainers(BotBase b)
        {
            var buckets = new Dictionary<string, List<IItemContainerEntity>>();
            if (b == null || b.OwnerId == 0) return buckets;
            _econScan.Clear();
            Vis.Entities(b.Origin.V, 60f, _econScan);
            foreach (var ent in _econScan)
            {
                if (ent == null || ent.IsDestroyed || ent.OwnerID != b.OwnerId) continue;
                if (ent is BuildingPrivlidge) continue; // TC = upkeep, handled separately
                var sc = ent as IItemContainerEntity;
                if (sc == null || sc.inventory == null) continue;
                var purpose = AnchorPurposeFor(ent);
                if (purpose == null) continue;
                List<IItemContainerEntity> list;
                if (!buckets.TryGetValue(purpose, out list)) buckets[purpose] = list = new List<IItemContainerEntity>();
                list.Add(sc);
            }
            return buckets;
        }

        // Prefer a container of the requested purpose; fall back to general storage so
        // a good with no dedicated box (e.g. "food") still lands somewhere sane.
        private static IItemContainerEntity PickContainer(Dictionary<string, List<IItemContainerEntity>> buckets, string purpose)
        {
            List<IItemContainerEntity> list;
            if (buckets.TryGetValue(purpose, out list) && list.Count > 0) return list[0];
            if (purpose != "storage" && buckets.TryGetValue("storage", out list) && list.Count > 0) return list[0];
            return null;
        }

        // Create `amount` of a stackable and store as much as fits; returns the count
        // actually stored (0 if the shortname is unknown or the container is full).
        // A full container is the physical production throttle (decision 0005).
        private int DepositToContainer(ItemContainer container, string shortname, int amount)
        {
            if (container == null || amount <= 0) return 0;
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null)
            {
                PrintWarning($"[econ] unknown item shortname '{shortname}' in an economy profile — skipped.");
                return 0;
            }
            var before = item.amount;
            if (item.MoveToContainer(container)) return before; // fully stored
            var stored = before - item.amount;                  // partial — MoveToContainer leaves the remainder
            if (item.amount > 0) item.Remove();
            return stored;
        }

        [ConsoleCommand("ff.econ")]
        private void CmdEcon(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.econ <botId|name> — dump the bot's group economy."); return; }
            var gid = rec.Identity.GroupId;
            var ge = Econ(gid);
            BotRecord holder;
            var b = GetGroupBase(gid, out holder);
            var sb = new StringBuilder();
            sb.AppendLine($"=== economy: {rec.Identity.Name} (group {gid}, {rec.Identity.Persona}) ===");
            sb.AppendLine($"  career produced: {ge.CareerProduced}   scrap: {ge.Scrap}   kit tier: {rec.Progression?.KitTier ?? 0}   spare banked: {ge.SpareKitTier}");
            sb.AppendLine($"  last tick: {(string.IsNullOrEmpty(ge.LastTickAt) ? "never" : ge.LastTickAt)}");
            if (b == null)
            {
                sb.AppendLine($"  base: none — next: {DescribeNextAction(holder ?? rec, null, ge, null)}");
                arg.ReplyWith(sb.ToString());
                return;
            }
            var buckets = FindBaseContainers(b);
            sb.AppendLine($"  base: stage {b.Stage} at {GridRef(b.Origin.V)}  observed: {IsObserved(b.Origin.V)}");
            sb.AppendLine($"  next: {DescribeNextAction(holder ?? rec, b, ge, buckets)}");
            if (buckets.Count == 0) sb.AppendLine("  containers: none placed yet (empty shell)");
            foreach (var kv in buckets)
            {
                var used = 0; var cap = 0;
                foreach (var sc in kv.Value) { used += sc.inventory.itemList.Count; cap += sc.inventory.capacity; }
                sb.AppendLine($"  {kv.Key,-10} x{kv.Value.Count}  slots {used}/{cap}");
            }
            arg.ReplyWith(sb.ToString());
        }

        // A short human-readable "what will this group do next" line — shared by the
        // ff.econ dump and the admin detail UI (G1/G3).
        private string DescribeNextAction(BotRecord holder, BotBase b, GroupEconomy ge, Dictionary<string, List<IItemContainerEntity>> buckets)
        {
            if (holder == null) return "—";
            List<string> stages;
            var haveStages = _baseCatalog.Stages.TryGetValue(holder.Identity.Persona, out stages) && stages != null;
            if (b == null)
            {
                if (!haveStages || stages.Count < 1 || string.IsNullOrEmpty(stages[0])) return "waiting on a captured stage-1 blueprint";
                return holder.State.HomePos == null ? "waiting for a home (first session)" : "building stage 1";
            }
            // A pending stage upgrade takes priority.
            var idx = b.Stage - 1;
            var thr = _config.BaseUpgradeThresholds;
            var hasNext = haveStages && stages.Count >= b.Stage + 1 && !string.IsNullOrEmpty(stages[b.Stage]);
            if (hasNext && thr != null && idx >= 0 && idx < thr.Count)
            {
                var remain = thr[idx] - ge.CareerProduced;
                return remain > 0 ? $"{remain} career → stage {b.Stage + 1}" : $"upgrading to stage {b.Stage + 1}";
            }
            // Otherwise: the next interior piece it can justify placing.
            if (buckets == null) buckets = FindBaseContainers(b);
            AnchorDef nextFill = null;
            var bestPri = int.MaxValue;
            if (b.Anchors != null)
                foreach (var a in b.Anchors)
                {
                    if (a.Filled || !AnchorNeedMet(holder, ge, buckets, a.Purpose)) continue;
                    int pri;
                    if (!FillPriority.TryGetValue(a.Purpose, out pri)) pri = 9;
                    if (pri < bestPri) { bestPri = pri; nextFill = a; }
                }
            if (nextFill != null) return $"furnishing a {nextFill.Purpose}";
            return IsObserved(b.Origin.V) ? "working (watched)" : "producing + stocking shop";
        }

        [ConsoleCommand("ff.econ.tick")]
        private void CmdEconTick(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.econ.tick <botId|name> — force one economy tick for the bot's group."); return; }
            var ge = Econ(rec.Identity.GroupId);
            long careerBefore = ge.CareerProduced; var scrapBefore = ge.Scrap;
            StepEconomyGroup(rec.Identity.GroupId);
            arg.ReplyWith($"{rec.Identity.Name}: forced a tick on group {rec.Identity.GroupId} — career {careerBefore}→{ge.CareerProduced} (+{ge.CareerProduced - careerBefore}), scrap {scrapBefore}→{ge.Scrap}. ff.econ {rec.Identity.BotId} for the full dump.");
        }

        [ConsoleCommand("ff.craft")]
        private void CmdCraft(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var item = arg.GetString(1, "");
            var n = Mathf.Max(1, arg.GetInt(2, 1));
            if (rec == null || string.IsNullOrEmpty(item)) { arg.ReplyWith("Usage: ff.craft <botId|name> <itemShortname> [count] — craft from the base's boxes (ingredient-honest, station-agnostic)."); return; }
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no base to craft in."); return; }
            var def = ItemManager.FindItemDefinition(item);
            var bp = def?.Blueprint;
            if (bp == null || bp.ingredients == null || bp.ingredients.Count == 0) { arg.ReplyWith($"'{item}' is unknown or has no craftable blueprint."); return; }
            var buckets = FindBaseContainers(b);
            var outBox = PickContainer(buckets, "locker") ?? PickContainer(buckets, "storage");
            if (outBox == null) { arg.ReplyWith($"{rec.Identity.Name} has no storage/locker placed to hold the output."); return; }
            var made = 0;
            for (var k = 0; k < n; k++)
            {
                var ok = true;
                foreach (var ing in bp.ingredients)
                    if (ing?.itemDef == null || CountAcross(buckets, ing.itemDef.shortname) < Mathf.CeilToInt(ing.amount)) { ok = false; break; }
                if (!ok) break;
                foreach (var ing in bp.ingredients) RemoveAcross(buckets, ing.itemDef.shortname, Mathf.CeilToInt(ing.amount));
                made += DepositToContainer(outBox.inventory, item, Mathf.Max(1, bp.amountToCreate)); // one craft = amountToCreate units
            }
            arg.ReplyWith($"{rec.Identity.Name}: crafted {made} {item} ({n} batch(es) attempted) from base stock{(made <= 0 ? " — ran short on ingredients or space" : "")}.");
        }

        // Testing aid: force a chunk of abstract production regardless of who's watching,
        // then cook/craft/stock — so a shop can be filled on demand without walking out of
        // observe range. Normal play produces embodied (watched) or abstract (unwatched).
        [ConsoleCommand("ff.produce")]
        private void CmdProduce(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var minutes = Mathf.Clamp(arg.GetFloat(1, 5f), 0.1f, 60f);
            if (rec == null) { arg.ReplyWith("Usage: ff.produce <botId|name> [minutes] — force abstract production (ignores observation) + cook/craft/stock, for testing."); return; }
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no base to produce into."); return; }
            var profile = ProfileFor(holder);
            if (profile == null || profile.Gather.Count == 0)
            { arg.ReplyWith($"{holder.Identity.Persona} has no gather profile to produce from."); return; }
            var buckets = FindBaseContainers(b);
            if (buckets.Count == 0) { arg.ReplyWith($"{rec.Identity.Name}'s base has no containers placed yet."); return; }
            var ge = Econ(rec.Identity.GroupId);
            var careerBefore = ge.CareerProduced;
            AbstractProduce(holder, ge, profile, buckets, minutes);
            CookMeat(buckets, minutes);
            CraftGoods(profile, buckets);
            ServiceShop(holder, ge, profile, buckets);
            MarkEconomyDirty();
            arg.ReplyWith($"{rec.Identity.Name}: forced {minutes:0.#} min of production — career +{ge.CareerProduced - careerBefore} (now {ge.CareerProduced}). ff.shop {rec.Identity.BotId} to see the shelves.");
        }

        [ConsoleCommand("ff.shop")]
        private void CmdShop(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.shop <botId|name> — refresh + dump the bot's vending shop."); return; }
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) { arg.ReplyWith($"{rec.Identity.Name} has no base/shop."); return; }
            var buckets = FindBaseContainers(b);
            List<IItemContainerEntity> shops;
            if (!buckets.TryGetValue("shop", out shops) || shops.Count == 0) { arg.ReplyWith($"{rec.Identity.Name} has no vending machine placed yet."); return; }
            var vm = shops[0] as VendingMachine;
            var ge = Econ(rec.Identity.GroupId);
            var profile = ProfileFor(holder);
            if (profile != null)
                ServiceShop(holder, ge, profile, buckets); // reconcile orders + restock + sweep scrap
            vm?.RefreshSellOrderStockLevel(); // make the buyable counts current before we read them

            var sb = new StringBuilder();
            sb.AppendLine($"=== shop: {rec.Identity.Name} — \"{(vm?.shopName ?? "(unnamed)")}\" ({vm?.sellOrders?.sellOrders?.Count ?? 0} orders) ===");
            if (vm != null)
            {
                var invCount = vm.inventory?.itemList?.Count ?? 0;
                sb.AppendLine($"  broadcasting: {vm.IsBroadcasting()}   out-of-stock: {vm.HasFlag(BaseEntity.Flags.Reserved5)}   machine-inv items: {invCount}   pos: {GridRef(vm.transform.position)}");
            }
            var orders = vm?.sellOrders?.sellOrders;
            if (profile != null)
                foreach (var s in profile.Sell)
                {
                    var def = ItemManager.FindItemDefinition(s.Item);
                    var raw = vm?.inventory != null ? CountInContainer(vm.inventory, s.Item) : 0;
                    var buyable = 0;
                    if (orders != null && def != null)
                        foreach (var o in orders) if (o.itemToSellID == def.itemid) { buyable = o.inStock; break; }
                    // buyable = raw / Amount (engine floor-division): a good isn't purchasable
                    // until the machine holds a full order's worth.
                    sb.AppendLine($"  {s.Amount,3}x {s.Item,-18} → {s.Scrap,3} scrap   raw {raw,4}   buyable-orders {buyable}");
                }
            sb.AppendLine($"  scrap collected (group ledger): {ge.Scrap}");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("ff.kit")]
        private void CmdKit(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith($"Usage: ff.kit <botId|name> [tier 0-{MaxKitTier}] — show or override a bot's kit tier (re-gears if live)."); return; }
            if (rec.Progression == null) rec.Progression = new BotProgression();
            var ge = Econ(rec.Identity.GroupId);
            var skill = rec.Identity.Traits?.SkillLevel ?? 0.5f;
            var cap = Mathf.Clamp(Mathf.FloorToInt(skill * (MaxKitTier + 1)), 0, MaxKitTier);
            if (!arg.HasArgs(2))
            {
                arg.ReplyWith($"{rec.Identity.Name}: kit tier {rec.Progression.KitTier} (earned {ComputeKitTier(rec, ge)}, skill {skill:0.00} caps at {cap}; group spare banked: tier {ge.SpareKitTier}). ff.kit {rec.Identity.BotId} <0-{MaxKitTier}> to override.");
                return;
            }
            var tier = Mathf.Clamp(arg.GetInt(1, rec.Progression.KitTier), 0, MaxKitTier);
            rec.Progression.KitTier = tier;
            rec.Progression.GearTier = -1; // admin override wins over any post-death degraded gear
            MarkDirty(rec.Identity.BotId);
            var bot = GetLive(rec.Identity.BotId);
            var regeared = false;
            if (bot != null && !bot.IsDead()) { bot.inventory.Strip(); GiveKit(bot, rec); regeared = true; }
            arg.ReplyWith($"{rec.Identity.Name}: kit tier set to {tier}{(regeared ? " and re-geared live" : " (applies on next spawn)")}.");
        }

        #endregion

        #region Embodied work — gather & hunt (Phase 3 Stage B)

        // When a player is watching, the Worker does it for real: walk to a dispenser
        // and drive real gather hits. Proven in Phase 0 — ResourceDispenser.DoGather
        // delivers items to the bot's inventory with bot attribution and fires
        // OnDispenserGather, so gather-rate plugins see the bot as a player. B1 is the
        // primitive + ff.gather driver; B2/B3 layer the autonomous approach + losable
        // hunt on top; B4 deposits the haul into the base boxes.

        // One gather hit against a dispenser entity — the exact Phase 0 path. Returns
        // the item count delivered to the bot's main inventory.
        private int DriveGatherHit(BasePlayer bot, BaseEntity target, float damage = 25f)
        {
            if (bot == null || target == null || target.IsDestroyed) return 0;
            var dispenser = target.GetComponent<ResourceDispenser>();
            if (dispenser == null) return 0;
            var before = MainInventoryCount(bot);
            var info = new HitInfo(bot, target, DamageType.Generic, damage)
            {
                gatherScale = 1f,
                CanGather = true,
                PointStart = bot.transform.position,
                PointEnd = target.transform.position,
            };
            dispenser.DoGather(info);
            return MainInventoryCount(bot) - before;
        }

        private static int MainInventoryCount(BasePlayer bp)
        {
            var n = 0;
            var list = bp?.inventory?.containerMain?.itemList;
            if (list != null) foreach (var it in list) n += it.amount;
            return n;
        }

        // Nearest entity of type T carrying a ResourceDispenser (tree / ore node).
        // Typed Vis.Entities is the proven query (Phase 0) — the type is the filter.
        private T NearestDispenserOf<T>(Vector3 pos, float radius) where T : BaseEntity =>
            NearestDispenserOf<T>(pos, radius, null, 0f);

        // v0.21.3: with a WanderState, nodes inside the bot's fresh danger memory are
        // skipped — Soot's nearest ore sat beside a junkpile scientist, so every
        // all-clear walked him straight back into the alarm (43 cycles in 50 min).
        private T NearestDispenserOf<T>(Vector3 pos, float radius, WanderState ws, float now) where T : BaseEntity
        {
            var list = new List<T>();
            Vis.Entities(pos, radius, list);
            T best = null;
            var bestSq = float.MaxValue;
            foreach (var e in list)
            {
                if (e == null || e.IsDestroyed || e.GetComponent<ResourceDispenser>() == null) continue;
                if (ws != null && InDangerZone(ws, e.transform.position, now)) continue;
                var d = (e.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = e; }
            }
            return best;
        }

        [ConsoleCommand("ff.gather")]
        private void CmdGather(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.gather <botId|name> [tree|ore] — drive an embodied gather on the nearest node (B1 primitive test)."); return; }
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null) { arg.ReplyWith($"{rec.Identity.Name} is not live — ff.session start {rec.Identity.BotId} first."); return; }
            var kind = (arg.GetString(1, "tree") ?? "tree").ToLowerInvariant();
            BaseEntity target = kind == "ore"
                ? (BaseEntity)NearestDispenserOf<OreResourceEntity>(bot.transform.position, 80f)
                : NearestDispenserOf<TreeEntity>(bot.transform.position, 80f);
            if (target == null) { arg.ReplyWith($"No {kind} with a dispenser within 80m of {rec.Identity.Name}."); return; }
            var got = 0;
            for (var i = 0; i < 5; i++) got += DriveGatherHit(bot, target);
            var dist = Vector3.Distance(bot.transform.position, target.transform.position);
            DLog($"[gather] {rec.Identity.Name} drove 5 hits on {target.ShortPrefabName} at {dist:0.#}m — {got} items delivered to inventory");
            arg.ReplyWith($"{rec.Identity.Name}: 5 hits on {target.ShortPrefabName} ({dist:0.#}m) → {got} item(s) into inventory. (Autonomous approach + deposit come with B2/B4.)");
        }

        // --- Autonomous gather loop (B2) + deposit (B4) ------------------------------
        // Driven from WanderTick (10Hz) so it reuses the existing NavMesh movement: the
        // work logic sets a path goal, WanderTick walks the bot there, and control
        // returns here when the corners are exhausted (arrival). A watched economic bot
        // cycles: pick a node → walk → chop → haul home → deposit → repeat. Unwatched,
        // it drops back to idle wander and the abstract EconomyTick keeps the books.

        private enum WorkPhase { Idle, ToResource, Working, ToHome, Deposit, Hunting }

        private class WorkState
        {
            public WorkPhase Phase = WorkPhase.Idle;
            public BaseEntity Target;   // current resource node
            public float NextHitAt;     // realtime — gather cadence
            public int HitsLeft;        // per-node safety cap
            public float PhaseDeadline; // realtime — abort a stuck walk
        }

        private readonly Dictionary<int, WorkState> _work = new Dictionary<int, WorkState>();
        private const float WorkHitInterval = 0.7f;
        // Verified present in the live content bundle (2026-07-17) — the wood-chip +
        // chop-thunk the game plays when a tool strikes a tree. We drive DoGather
        // directly (no real strike raycast), so we run this ourselves per hit.
        private const string TreeHitEffect = "assets/bundled/prefabs/fx/entities/tree/tree-impact.prefab";
        // Bundle-verified 2026-07-18 — pickaxe-on-rock strike for embodied ore mining.
        private const string OreHitEffect = "assets/bundled/prefabs/fx/impacts/blunt/rock/slash_rock_01.prefab";
        private const float HuntHitInterval = 0.8f;
        // HuntMeleeRange / HuntHitDamage are config (deer/boar fall fast; a bear likely wins).

        private bool ShouldWork(BotRecord rec, BasePlayer bot, out BotBase b)
        {
            b = null;
            if (IsRunnerP(rec)) return false; // 6A: a runner's embodied work IS the monument run (RunnerDriver), never the chop loop
            var p = ProfileFor(rec);
            if (p == null || p.Gather.Count == 0) return false;
            BotRecord holder;
            b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) return false;
            return IsObserved(bot.transform.position);
        }

        // Decide each tick whether the bot is working, and keep a stuck walk from
        // hanging. Called at the top of the per-bot WanderTick body.
        private void UpdateWorkActivity(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            BotBase b;
            var work = !ws.GoingHome && ShouldWork(rec, bot, out b);
            WorkState wk;
            _work.TryGetValue(rec.Identity.BotId, out wk);
            if (!work)
            {
                // WA-2 (decision 0010): a defensive fight needs neither an audience nor
                // a gather profile — clearing an active hunt here made unwatched (and
                // non-economic) bots restart the fight every threat tick without ever
                // swinging (Fernwood vs the bear, 09:00 2026-08-01: 4 starts, 0 hits
                // landed). Let HuntStep resolve it; normal clearing resumes after.
                if (wk != null && wk.Phase == WorkPhase.Hunting) { ws.Working = true; return; }
                if (wk != null) _work.Remove(rec.Identity.BotId);
                ws.Working = false;
                return;
            }
            if (wk == null)
            {
                _work[rec.Identity.BotId] = wk = new WorkState();
                ws.Working = true;
                StartNextWorkTarget(rec, bot, ws, wk, now);
                return;
            }
            ws.Working = true;
            // Watchdog: a walk phase that overruns its deadline is retargeted so a bot
            // that can't reach a node/home never freezes mid-path.
            if ((wk.Phase == WorkPhase.ToResource || wk.Phase == WorkPhase.ToHome || wk.Phase == WorkPhase.Deposit) && now > wk.PhaseDeadline)
            {
                if (wk.Phase == WorkPhase.ToHome) DepositAndDuckInside(rec, bot, ws, wk, now);
                else if (wk.Phase == WorkPhase.Deposit) LeaveBaseAndResume(rec, bot, ws, wk, now);
                else StartNextWorkTarget(rec, bot, ws, wk, now);
            }
        }

        // Reached the current path goal (WanderTick corners exhausted) while working.
        private void WorkArrived(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            WorkState wk;
            if (!_work.TryGetValue(rec.Identity.BotId, out wk)) { ws.Working = false; return; }
            switch (wk.Phase)
            {
                case WorkPhase.ToResource:
                    if (wk.Target == null || wk.Target.IsDestroyed ||
                        FlatDistance(bot.transform.position, wk.Target.transform.position) > 5f)
                    {
                        StartNextWorkTarget(rec, bot, ws, wk, now); // node gone or unreachable
                        return;
                    }
                    wk.Phase = WorkPhase.Working;
                    wk.HitsLeft = 15;
                    wk.NextHitAt = now + WorkHitInterval;
                    FaceEntity(bot, wk.Target.transform.position);
                    return;

                case WorkPhase.Working:
                    if (wk.Target == null || wk.Target.IsDestroyed || wk.HitsLeft <= 0)
                    {
                        GoHomeToDeposit(rec, bot, ws, wk, now);
                        return;
                    }
                    if (now >= wk.NextHitAt)
                    {
                        FaceEntity(bot, wk.Target.transform.position);
                        bot.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty); // swing animation
                        DriveGatherHit(bot, wk.Target);
                        Effect.server.Run(wk.Target is OreResourceEntity ? OreHitEffect : TreeHitEffect,
                            wk.Target.transform.position + Vector3.up * 1.2f, Vector3.up); // strike sound + debris
                        wk.HitsLeft--;
                        wk.NextHitAt = now + WorkHitInterval;
                    }
                    return;

                case WorkPhase.ToHome:
                    DepositAndDuckInside(rec, bot, ws, wk, now);
                    return;

                case WorkPhase.Deposit:
                    return; // parked inside during the dwell; the watchdog steps him out

                default:
                    StartNextWorkTarget(rec, bot, ws, wk, now);
                    return;
            }
        }

        private void StartNextWorkTarget(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
            var radius = Mathf.Max(40f, rec.State.HomeRadius);

            // Hunt sometimes when healthy and prey is near — else chop wood. This mixes
            // the signature Trapper behavior in without hunting non-stop or while hurt.
            if (ProfileHunts(rec) && bot.health > 70f)
            {
                var prey = NearestAnimal(home, radius);
                if (prey != null && (NearestDispenserOf<TreeEntity>(home, radius, ws, now) == null || UnityEngine.Random.value < 0.5f))
                {
                    StartHunt(rec, bot, ws, wk, prey, now);
                    return;
                }
            }

            // 3.5 — the node class comes from the profile's gather kinds: a Smelter
            // mines ore, a Trapper chops wood, a persona listing both mixes 50/50.
            var wantsTree = false; var wantsOre = false;
            var prof = ProfileFor(rec);
            if (prof != null)
                foreach (var g in prof.Gather)
                {
                    if (g.Kind == "tree") wantsTree = true;
                    else if (g.Kind == "ore") wantsOre = true;
                }
            if (!wantsTree && !wantsOre) wantsTree = true; // hunt/crop-only profiles still chop between jobs

            BaseEntity target = null;
            var oreFirst = wantsOre && (!wantsTree || UnityEngine.Random.value < 0.5f);
            if (oreFirst) target = NearestDispenserOf<OreResourceEntity>(home, radius, ws, now);
            if (target == null && wantsTree) target = NearestDispenserOf<TreeEntity>(home, radius, ws, now);
            if (target == null && wantsOre && !oreFirst) target = NearestDispenserOf<OreResourceEntity>(home, radius, ws, now);
            if (target == null)
            {
                // Nothing to gather in range — fall back to idle wander (re-checked at
                // the ~1Hz work eval, so no 10Hz rescan).
                _work.Remove(rec.Identity.BotId);
                ws.Working = false;
                return;
            }
            wk.Target = target;
            wk.Phase = WorkPhase.ToResource;
            wk.PhaseDeadline = now + 30f;
            var from = bot.transform.position;
            var approach = target.transform.position + (from - target.transform.position).normalized * 1.0f;
            if (!PlanPathTo(bot, ws, approach) && !PlanPathTo(bot, ws, target.transform.position))
            {
                _work.Remove(rec.Identity.BotId); // can't path there — idle-wander instead
                ws.Working = false;
                return;
            }
            ws.Working = true;
            EnsureTool(bot, target is OreResourceEntity ? "ore" : "tree"); // walk over with the right tool out
        }

        // Make sure the bot is holding the right gathering tool (visual — the gather
        // itself is driven by DoGather). Creates one in the belt if missing; a bad
        // shortname just leaves the bot tool-less (gather still works).
        private void EnsureTool(BasePlayer bot, string kind)
        {
            if (bot?.inventory?.containerBelt == null) return;
            var want = kind == "ore" ? "pickaxe" : "stonehatchet";
            var active = bot.GetActiveItem();
            if (active?.info != null && active.info.shortname == want) return;

            Item tool = null;
            var belt = bot.inventory.containerBelt.itemList;
            for (var i = 0; i < belt.Count; i++)
                if (belt[i]?.info != null && belt[i].info.shortname == want) { tool = belt[i]; break; }
            if (tool == null)
            {
                tool = ItemManager.CreateByName(want, 1);
                if (tool == null) { PrintWarning($"[work] unknown tool shortname '{want}' — bot works tool-less."); return; }
                if (!tool.MoveToContainer(bot.inventory.containerBelt)) { tool.Remove(); return; }
            }
            bot.UpdateActiveItem(tool.uid);
        }

        private void GoHomeToDeposit(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            wk.Target = null;
            wk.Phase = WorkPhase.ToHome;
            wk.PhaseDeadline = now + 40f;
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
            if (!PlanPathTo(bot, ws, home))
            {
                // No path home (rare) — deposit in place next tick via the deadline path.
                wk.PhaseDeadline = now; // trip the watchdog immediately
            }
        }

        // Arrived home with a haul: open the door, stow the loot in the boxes, then duck
        // inside for a few seconds (teleport the last hop — the interior floor is off the
        // navmesh) so the drop-off reads as "went in to stow the haul". The watchdog steps
        // him back out and resumes gathering when the dwell elapses.
        private void DepositAndDuckInside(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b == null) { StartNextWorkTarget(rec, bot, ws, wk, now); return; }
            // N-2 (IN-3.1): walk in through the door to the storage anchor and stow the
            // haul there; the hop below is the fallback when the mesh won't give a
            // complete path (or under -useOldNavmesh).
            if (ws.Walk == 0 && FlatDistance(bot.transform.position, rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position) <= 15f
                && StartInteriorWalk(rec, bot, ws, GetShelterPoint(rec, "storage"), WalkDeposit, now))
            {
                wk.Phase = WorkPhase.Deposit;
                wk.PhaseDeadline = now + 90f; // walk in (≤25 s) + dwell + walk out; the watchdog only fires if the walk dropped him
                return;
            }
            FlickBaseDoors(b, bot.transform.position); // door opens as he enters
            DepositInventoryToBase(rec, b);
            bot.Teleport(GetShelterPoint(rec)); // step inside
            wk.Phase = WorkPhase.Deposit;
            wk.PhaseDeadline = now + UnityEngine.Random.Range(5f, 10f); // dwell inside
        }

        // Dwell elapsed — step back out to the exterior anchor (door flicks again on the
        // way out) and pick the next gather target.
        private void LeaveBaseAndResume(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
            bot.Teleport(GroundSnap(home));
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            if (b != null) FlickBaseDoors(b, bot.transform.position);
            StartNextWorkTarget(rec, bot, ws, wk, now);
        }

        // Kept on the bot at deposit time — tools, weapons, meds, ammo, food he might
        // eat. Everything else in the main inventory is gathered/harvested loot and
        // gets banked into the base boxes.
        private static readonly HashSet<string> KeepOnBot = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bandage", "largemedkit", "syringe.medical", "antiradpills",
            "arrow.wooden", "arrow.hv", "arrow.fire", "bow.hunting",
            "hatchet", "stonehatchet", "pickaxe", "stone.pickaxe", "torch", "rock",
            "spear.wooden", "spear.stone", "apple",
        };

        // Where a banked good lives: leather/hides in the locker, meat in the food box,
        // everything else in general storage. Routes ALL harvested goods, not just the
        // persona's listed gathers, so species-specific meats/loot deposit too (D3).
        private static string DepositRouteFor(string shortname)
        {
            if (shortname == "leather" || shortname.Contains("hide")) return "locker";
            if (shortname.Contains("meat") || shortname.Contains("chicken") || shortname == "fat.animal") return "food";
            return "storage";
        }

        // Move harvested loot from the bot's main inventory into the right base boxes,
        // capacity-throttled. Kit stays on the bot. (Career is time-based since v0.20.0 —
        // the online session already credited this work.)
        private void DepositInventoryToBase(BotRecord rec, BotBase b)
        {
            var bot = GetLive(rec.Identity.BotId);
            var main = bot?.inventory?.containerMain;
            if (main == null) return;
            var buckets = FindBaseContainers(b);
            if (buckets.Count == 0) return;

            long moved = 0;
            var items = new List<Item>(main.itemList); // snapshot — MoveToContainer mutates the list
            foreach (var it in items)
            {
                if (it?.info == null || KeepOnBot.Contains(it.info.shortname)) continue;
                var box = PickContainer(buckets, DepositRouteFor(it.info.shortname));
                if (box == null) continue;
                var amt = it.amount;
                if (it.MoveToContainer(box.inventory)) moved += amt;
                else moved += amt - it.amount; // partial (box filled)
            }
            if (moved > 0) DLog($"[work] {rec.Identity.Name} deposited {moved} gathered item(s) at home");
        }

        private static void FaceEntity(BasePlayer bot, Vector3 targetPos)
        {
            var dir = targetPos - bot.transform.position;
            if (dir.sqrMagnitude < 0.01f) return;
            var yaw = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z)).eulerAngles.y;
            bot.OverrideViewAngles(new Vector3(0f, yaw, 0f));
            bot.SendNetworkUpdate();
        }

        // --- The losable hunt (B3) --------------------------------------------------
        // The Trapper closes on an animal and trades real damage: our attributed hits
        // drop the animal, but the animal's native AI (Phase 0: animals hunt bots)
        // deals real damage back. Cautious bots break off early, reckless ones push
        // their luck — sometimes the bot wins and harvests, sometimes it flees or dies.
        // Decision 0005.

        private bool ProfileHunts(BotRecord rec)
        {
            var p = ProfileFor(rec);
            if (p == null) return false;
            foreach (var g in p.Gather) if (g.Kind == "animal") return true;
            return false;
        }

        private BaseEntity NearestAnimal(Vector3 pos, float radius)
        {
            var list = new List<BaseAnimalNPC>();
            Vis.Entities(pos, radius, list);
            BaseAnimalNPC best = null;
            var bestSq = float.MaxValue;
            foreach (var a in list)
            {
                if (a == null || a.IsDestroyed || a.IsDead()) continue;
                var n = AnimalName(a);
                if (n == "bear" || n == "wolf") continue; // dangerous — flee these, don't hunt them
                var d = (a.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = a; }
            }
            return best;
        }

        private static string AnimalName(BaseEntity e)
        {
            var n = e?.ShortPrefabName ?? "animal";
            if (n.Contains("bear")) return "bear";
            if (n.Contains("wolf")) return "wolf";
            if (n.Contains("boar")) return "boar";
            if (n.Contains("stag") || n.Contains("deer")) return "deer";
            if (n.Contains("chicken")) return "chicken";
            if (n.Contains("horse")) return "horse";
            return n;
        }

        private void StartHunt(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, BaseEntity animal, float now)
        {
            wk.Target = animal;
            wk.Phase = WorkPhase.Hunting;
            wk.PhaseDeadline = now + 25f; // give up if it can't close/land hits
            wk.NextHitAt = now;
            ws.Working = true;
            EnsureTool(bot, "tree"); // hatchet doubles as the hunting weapon (spear/bow = later polish)
            DLog($"[hunt] {rec.Identity.Name} is hunting a {AnimalName(animal)}");
        }

        // Per-tick hunt driver (10Hz) — owns movement while hunting, so it bypasses the
        // idle-wander corner follow. Trees don't move; animals do, hence direct pursuit.
        private void HuntStep(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            var animal = wk.Target as BaseCombatEntity;
            if (animal == null || animal.IsDestroyed || animal.IsDead())
            {
                BeginHarvest(rec, bot, ws, wk, now); // win → harvest the corpse
                return;
            }
            // Flee valve: caution biases the break-off HP (cautious bail early).
            var caution = Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f);
            var fleeAt = Mathf.Lerp(30f, 60f, caution);
            if (bot.health <= fleeAt)
            {
                DLog($"[hunt] {rec.Identity.Name} broke off at {bot.health:0}hp — fleeing home");
                Story("hunt", $"{rec.Identity.Name} was driven off by a {AnimalName(animal)} and fled home");
                GoHomeToDeposit(rec, bot, ws, wk, now); // Phase → ToHome; normal walk resumes
                return;
            }
            if (now > wk.PhaseDeadline) { GoHomeToDeposit(rec, bot, ws, wk, now); return; } // can't close

            var apos = animal.transform.position;
            var dist = FlatDistance(bot.transform.position, apos);
            if (dist > _config.HuntMeleeRange)
            {
                // Direct pursuit across open ground.
                var toFlat = new Vector3(apos.x - bot.transform.position.x, 0f, apos.z - bot.transform.position.z);
                var d = toFlat.magnitude;
                if (d > 0.01f)
                {
                    var step = ws.Speed * WanderTickSeconds;
                    MoveBotStep(bot, bot.transform.position + toFlat * (step / d), toFlat);
                }
                return;
            }
            // In melee range: swing on cadence. Landing hits keeps the give-up watchdog back.
            FaceEntity(bot, apos);
            if (now >= wk.NextHitAt)
            {
                bot.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                animal.Hurt(new HitInfo(bot, animal, DamageType.Slash, _config.HuntHitDamage));
                wk.NextHitAt = now + HuntHitInterval;
                wk.PhaseDeadline = now + 20f;
            }
        }

        private void BeginHarvest(BotRecord rec, BasePlayer bot, WanderState ws, WorkState wk, float now)
        {
            var deathPos = wk.Target != null ? wk.Target.transform.position : bot.transform.position;
            var species = AnimalName(wk.Target);
            var corpse = NearestCorpse(deathPos, 6f);
            Story("hunt", $"{rec.Identity.Name} killed a {species} near home");
            if (species == "bear" || species == "wolf" || species == "boar")
                Story("trophy", $"{rec.Identity.Name} took a {species} trophy for the cabin"); // decor item: D3/decor
            if (corpse == null)
            {
                wk.Target = null; // corpse already gone/unreachable — resume work
                StartNextWorkTarget(rec, bot, ws, wk, now);
                return;
            }
            // Reuse the gather walk→work flow to harvest the corpse's dispenser.
            wk.Target = corpse;
            wk.Phase = WorkPhase.ToResource;
            wk.PhaseDeadline = now + 30f;
            if (!PlanPathTo(bot, ws, corpse.transform.position))
            {
                wk.Target = null;
                StartNextWorkTarget(rec, bot, ws, wk, now);
            }
        }

        private BaseEntity NearestCorpse(Vector3 pos, float radius)
        {
            _econScan.Clear();
            Vis.Entities(pos, radius, _econScan);
            BaseEntity best = null;
            var bestSq = float.MaxValue;
            foreach (var e in _econScan)
            {
                if (e == null || e.IsDestroyed) continue;
                var isCorpse = e is BaseCorpse ||
                    (e.ShortPrefabName != null && e.ShortPrefabName.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isCorpse || e.GetComponent<ResourceDispenser>() == null) continue;
                var d = (e.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = e; }
            }
            return best;
        }

        [ConsoleCommand("ff.hunt")]
        private void CmdHunt(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.hunt <botId|name> — force the bot to hunt the nearest animal (B3 test)."); return; }
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null) { arg.ReplyWith($"{rec.Identity.Name} is not live — ff.session start {rec.Identity.BotId} first."); return; }
            var animal = NearestAnimal(bot.transform.position, 100f);
            if (animal == null) { arg.ReplyWith($"No animal within 100m of {rec.Identity.Name}."); return; }
            WanderState ws;
            if (!_wander.TryGetValue(rec.Identity.BotId, out ws))
                _wander[rec.Identity.BotId] = ws = new WanderState { Speed = 2.2f + rec.Identity.Traits.SkillLevel };
            WorkState wk;
            if (!_work.TryGetValue(rec.Identity.BotId, out wk)) _work[rec.Identity.BotId] = wk = new WorkState();
            StartHunt(rec, bot, ws, wk, animal, Time.realtimeSinceStartup);
            var fleeAt = Mathf.Lerp(30f, 60f, Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f));
            var warn = bot.health <= fleeAt ? $" — WARNING: he's at {bot.health:0}hp (flees at {fleeAt:0}), he'll break off immediately; let him heal first" : "";
            arg.ReplyWith($"{rec.Identity.Name} ({bot.health:0}hp) is hunting a {AnimalName(animal)} at {Vector3.Distance(bot.transform.position, animal.transform.position):0}m.{warn}");
        }

        // 5A2-R test harness: show a resident's fight gate, or force a fight.
        [ConsoleCommand("ff.fight")]
        private void CmdFight(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) { arg.ReplyWith("Usage: ff.fight <botId|name> [go] — show the fight-or-flight gate; 'go' forces a fight vs the nearest animal (gate bypassed, weapon still required)."); return; }
            var bot = GetLive(rec.Identity.BotId);
            if (bot == null) { arg.ReplyWith($"{rec.Identity.Name} is not live — ff.session start {rec.Identity.BotId} first."); return; }
            var prog = rec.Progression;
            var tier = prog == null ? 0 : (prog.GearTier >= 0 ? prog.GearTier : prog.KitTier);
            var agg = rec.Identity.Traits?.Aggression ?? 0f;
            var gun = FightWeapon(bot);
            arg.ReplyWith($"{rec.Identity.Name}: {(CanFight(rec, bot) ? "FIGHTS" : "flees")} — worn tier {tier} (need {_config.FightMinKitTier}), aggression {agg:0.00} (need {_config.FightMinAggression:0.00}), weapon {(gun != null ? gun.GetItem()?.info?.shortname ?? "?" : "none")}, hp {bot.health:0}.");
            if (arg.GetString(1, "") != "go") return;
            if (gun == null) { arg.ReplyWith("No ranged weapon in his belt — nothing to fight with."); return; }
            var animal = (NearestDangerAnimal(bot.transform.position, 100f) ?? NearestAnimal(bot.transform.position, 100f)) as BaseCombatEntity;
            if (animal == null) { arg.ReplyWith($"No animal within 100m of {rec.Identity.Name}."); return; }
            WanderState ws;
            if (!_wander.TryGetValue(rec.Identity.BotId, out ws))
                _wander[rec.Identity.BotId] = ws = new WanderState { Speed = 2.2f + rec.Identity.Traits.SkillLevel };
            StartFight(rec, bot, ws, animal, Time.realtimeSinceStartup);
            arg.ReplyWith($"Forced: {rec.Identity.Name} is fighting a {AnimalName(animal)} at {Vector3.Distance(bot.transform.position, animal.transform.position):0}m.");
        }

        // --- Threat sense + retreat (Worker core: "flee per caution trait") ----------
        // A working/wandering bot senses real danger — patrol scientists, the heli,
        // Bradley, prowling bears/wolves — drops its job and falls back to its cabin to
        // hole up and heal until it's clear. Detection range scales with caution: a
        // cautious Trapper bolts early, a reckless one cuts it close (and may die).

        // Patrol-grade danger you can't melee your way out of — flee-to-cover threats.
        // (Bears/wolves are handled as fight-back threats, not flee threats — you can't
        // outrun a bear, and fleeing home just gets you mauled standing still.)
        private BaseEntity SensePatrolThreat(BasePlayer bot, float caution) =>
            SensePatrolThreatAt(bot.transform.position, caution, 1f);

        // v0.21.2: `scale` is the hysteresis knob — the all-clear check scans a wider
        // circle than the alarm (PatrolClearScale), so a threat parked at the edge of
        // detection can't flip the bot in and out every hold (TrapperJoe 194×/6 days,
        // GrannyMags 378×, 1,556 "took cover" story beats one wipe — every cycle
        // dropped the gather job and walked the shell).
        private BaseEntity SensePatrolThreatAt(Vector3 pos, float caution, float scale)
        {
            // Ranges raised 2026-07-23 (live: bots strolled out mid-heli-patrol) —
            // players hear a heli from very far; bots should act like they do too.
            var r = Mathf.Lerp(50f, 90f, caution) * scale;
            var t = NearestScientist(pos, r);
            if (t != null) return t;
            t = NearestThreatOf<PatrolHelicopter>(pos, Mathf.Max(r, 180f * scale)); // loud + deadly from far
            if (t != null) return t;
            return NearestThreatOf<BradleyAPC>(pos, Mathf.Max(r, 130f * scale));
        }
        private const float PatrolClearScale = 1.3f;
        private const float DangerMemorySeconds = 900f; // 15 min — outlives a hold, not a session
        private const float DangerRepeatHoldSeconds = 300f;

        private static bool InDangerZone(WanderState ws, Vector3 p, float now) =>
            now < ws.DangerUntil && FlatDistance(p, ws.DangerPos) <= ws.DangerRadius;

        // Both scientist generations in one scan — a typed List<ScientistNPC> sweep
        // can't see ScientistNPC2 (separate hierarchy), same trap as the old
        // BaseAnimalNPC wildlife scans. BaseCombatEntity scan + predicate, per the
        // NearestDangerAnimal precedent below.
        private BaseEntity NearestScientist(Vector3 pos, float radius)
        {
            var list = new List<BaseCombatEntity>();
            Vis.Entities(pos, radius, list);
            BaseCombatEntity best = null;
            var bestSq = float.MaxValue;
            foreach (var e in list)
            {
                if (e == null || e.IsDestroyed || e.IsDead() || !IsScientist(e)) continue;
                // v0.21.2: not every scientist silhouette is a threat to flee. Our own
                // legacy-chassis hostiles derive from ScientistNPC — residents must not
                // see through the Cobalt disguise on sight (v0.10 doctrine) — and
                // Outpost/Bandit peacekeepers never leave their zone; an anchor 30 m
                // past the zone edge sat inside the 50–90 m alarm radius forever.
                if (e is HostileNpc) continue;
                if (InOrNearSafeZone(e.transform.position, 1f)) continue;
                var d = (e.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = e; }
            }
            return best;
        }

        private T NearestThreatOf<T>(Vector3 pos, float radius) where T : BaseEntity
        {
            var list = new List<T>();
            Vis.Entities(pos, radius, list);
            T best = null;
            var bestSq = float.MaxValue;
            foreach (var e in list)
            {
                if (e == null || e.IsDestroyed) continue;
                var combat = e as BaseCombatEntity;
                if (combat != null && combat.IsDead()) continue;
                var d = (e.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = e; }
            }
            return best;
        }

        // BaseCombatEntity scan + IsDangerWildlife, NOT a BaseAnimalNPC list with name
        // checks: Gen2 wolves aren't BaseAnimalNPCs (the old filter could only ever
        // return bears), and boars earned their spot on the danger list the hard way
        // (decision 0010).
        private BaseEntity NearestDangerAnimal(Vector3 pos, float radius)
        {
            var list = new List<BaseCombatEntity>();
            Vis.Entities(pos, radius, list);
            BaseCombatEntity best = null;
            var bestSq = float.MaxValue;
            foreach (var a in list)
            {
                if (a == null || a.IsDestroyed || a.IsDead() || !IsDangerWildlife(a)) continue;
                var d = (a.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = a; }
            }
            return best;
        }

        // WH-1: is this patrol heli actually HUNTING this bot (vs just flying nearby)?
        // Verified against the live assembly 2026-08-01: PatrolHelicopter.myAI
        // (public), PatrolHelicopterAI._targetList (public List<targetinfo>),
        // targetinfo.ply (public BasePlayer).
        private static bool PatrolIsHuntingMe(BaseEntity patrol, BasePlayer bot)
        {
            var heli = patrol as PatrolHelicopter;
            var ai = heli != null ? heli.myAI : null;
            if (ai == null || ai._targetList == null) return false;
            foreach (var t in ai._targetList)
                if (t != null && t.ply == bot) return true;
            return false;
        }

        // One flee leg away from the hunter AND away from home (a real player doesn't
        // lead the heli back to a wood shell it can level). Falls back to a beeline
        // step off the navmesh, same as the cornered-retreat path.
        private void PlanAwayLeg(BasePlayer bot, WanderState ws, Vector3 threatPos, Vector3 home)
        {
            var away = bot.transform.position - threatPos;
            away.y = 0f;
            var dir = away.sqrMagnitude > 0.01f ? away.normalized : bot.transform.forward;
            var fromHome = bot.transform.position - home;
            fromHome.y = 0f;
            if (fromHome.sqrMagnitude > 0.01f) dir = (dir + fromHome.normalized * 0.5f).normalized;
            var goal = bot.transform.position + dir * 60f;
            if (!PlanPathTo(bot, ws, goal))
                MoveBotStep(bot, bot.transform.position + dir * (ws.Speed * WanderTickSeconds), dir);
        }

        private static string ThreatName(BaseEntity e)
        {
            if (e is PatrolHelicopter) return "patrol helicopter";
            if (e is BradleyAPC) return "Bradley";
            if (IsScientist(e)) return "scientist"; // old-gen or Gen2 ("patrol" read as the heli — v0.21.2)
            return AnimalName(e); // bear / wolf
        }

        // Console-only forensic tag: WHAT the threat is and how far — so a looping
        // retreat line names its cause (peacekeeper? junkpile scientist? a plugin NPC?).
        private static string ThreatTag(BaseEntity e, BasePlayer bot)
        {
            if (e == null) return "?";
            var dist = Vector3.Distance(e.transform.position, bot.transform.position);
            return $"{ThreatName(e)} ({e.ShortPrefabName}, {dist:0}m)";
        }

        // The interior spot a bot ducks into to heal/hide — its bed (inside, where it
        // rests), else the TC, else the base origin, else the outdoor anchor. The base
        // sits on foundations the navmesh doesn't reach, so we walk to the anchor and
        // teleport the last hop inside (RetreatArrived).
        private Vector3 GetShelterPoint(BotRecord rec, string purpose = "bed")
        {
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : Vector3.zero;
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            var pt = home;
            if (b != null)
            {
                if (b.Anchors != null)
                    foreach (var a in b.Anchors)
                        if (a.Purpose == purpose && a.Pos != null) { pt = a.Pos.V; break; }
                if (pt == home && b.TcNetId != 0)
                {
                    var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(b.TcNetId)) as BuildingPrivlidge;
                    if (tc != null && !tc.IsDestroyed) pt = tc.transform.position;
                }
                if (pt == home && b.Origin != null) pt = b.Origin.V;
            }
            // Reject a point floating well above (stale/relocated anchor) or buried below
            // the ground — teleporting there is a lethal fall / clip. Fall back to safe
            // ground at the anchor. A real interior floor sits ~1–2m up and is kept.
            if (home != Vector3.zero)
            {
                var ground = TerrainMeta.HeightMap.GetHeight(pt);
                if (pt.y - ground > 4f || pt.y < ground - 1f) return GroundSnap(home);
            }
            return pt;
        }

        // Immersion: swing the base's entrance door open as the bot comes/goes (to
        // deposit, hole up, sleep, or head out), then close it behind him. Only the door
        // nearest where he's standing — the one he'd actually walk through — not every
        // interior door. `fromPos` is his exterior approach position.
        private readonly List<BaseEntity> _doorScan = new List<BaseEntity>(); // reused
        private void FlickBaseDoors(BotBase b, Vector3 fromPos)
        {
            if (b == null || b.OwnerId == 0) return;
            _doorScan.Clear();
            Vis.Entities(b.Origin.V, 20f, _doorScan);
            Door best = null;
            var bestSq = float.MaxValue;
            foreach (var e in _doorScan)
            {
                var d = e as Door;
                if (d == null || d.IsDestroyed || d.OwnerID != b.OwnerId) continue;
                var sq = (d.transform.position - fromPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = d; }
            }
            if (best == null) return;
            best.SetOpen(true);
            var door = best;
            timer.Once(Mathf.Max(0.5f, _config.DoorFlickSeconds), () => { if (door != null && !door.IsDestroyed) door.SetOpen(false, true); });
        }

        // Throttled (~1.5s) danger/health decision. Patrols OR low HP → fall back to
        // base and hole up inside to heal. Bears/wolves that catch a healthy, watched
        // bot → turn and fight (the hunt's own flee-valve takes over if it's losing).
        private void UpdateThreatResponse(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            var caution = Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f);
            var maxHp = bot.MaxHealth();
            BotRecord holder;
            var hasBase = GetGroupBase(rec.Identity.GroupId, out holder) != null;
            // 5A2-R — fight or flight. A fresh hit, a firsthand-grudged player, a
            // Cobalt hostile, or a predator: armed (kit tier) + aggressive residents
            // stand and fight; everyone else keeps the Phase 4 flee-and-hole-up.
            var attacker = RecentAttacker(bot);
            // Entry needs only a weapon — the tier/aggression gates are per-foe inside
            // FightEligible now (a tier-0 bot fights the wolf but still flees a player).
            // WG-2: plus a health floor above the flee valve and the post-break-off
            // suppression window — RustyBill (2026-08-01 10:06) broke off at 4hp and
            // re-engaged the same bear three times inside a minute, because animals
            // have no userID for the per-target cooldown and entry never asked if he
            // was fit to fight. Don't start a fight the valve would immediately end.
            if (ws.FightTarget == null && !ws.Retreating && !ws.Sheltered &&
                now >= ws.FightSuppressUntil &&
                bot.health > Mathf.Lerp(30f, 60f, caution) + 5f &&
                FightArmed(bot)) // WD-3: an empty gun takes the retreat path, not the pose
            {
                var foe = PickFightTarget(rec, bot, ws, attacker, caution, now);
                if (foe != null)
                {
                    StartFight(rec, bot, ws, foe, now);
                    return;
                }
            }
            // Being shot by someone we won't (or can't) fight is patrol-grade danger.
            var shotAt = attacker as BasePlayer;
            var patrol = SensePatrolThreat(bot, caution);
            // 4E — a remembered killer walking up counts as danger too (defensive only
            // until Phase 5): fall back and hole up; chatty bots call out a FIRSTHAND
            // killer (no callout on hearsay — the bot only "heard something").
            var firsthand = false;
            var grudged = patrol != null ? null : SenseGrudgedPlayer(rec, bot, caution, out firsthand);
            if (grudged != null && firsthand) MutterAtGrudged(rec, ws, grudged, now);
            // Only hole up to heal if there's actually a base to hide in. A baseless bot
            // standing still just dies to whatever hazard hurt it — better it keeps
            // moving (and healing) and wanders clear.
            var hurt = bot.health < maxHp * 0.5f && hasBase;

            if (patrol != null || grudged != null || hurt || shotAt != null)
            {
                var repeatHold = false;
                if (!ws.Retreating)
                {
                    if (patrol != null)
                    {
                        // v0.21.3 danger memory (see WanderState): same spot again while
                        // the memory is fresh → the job/wander avoidance didn't keep him
                        // away (or the threat moved with him) — hold indoors for minutes.
                        var tpos = patrol.transform.position;
                        var sameSpot = now < ws.DangerUntil && FlatDistance(tpos, ws.DangerPos) <= ws.DangerRadius;
                        ws.DangerHits = sameSpot ? ws.DangerHits + 1 : 1;
                        ws.DangerPos = tpos;
                        ws.DangerRadius = Mathf.Lerp(50f, 90f, caution) * PatrolClearScale;
                        ws.DangerUntil = now + DangerMemorySeconds;
                        repeatHold = ws.DangerHits >= 2;
                    }
                    ws.Retreating = true;
                    ws.Sheltered = false;
                    ws.LeadAway = false;
                    ws.RetreatStory = null; // never carry a stale "holed up" beat into a new retreat
                    _work.Remove(rec.Identity.BotId); // drop the job
                    ws.Working = false;
                    ws.Corners.Clear();
                    ws.CornerIndex = 0;
                    ws.RetreatGoal = GetShelterPoint(rec);
                    var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
                    // WB-1 (decision 0010): retreat used to walk STRAIGHT at the anchor
                    // the animal was camping — Slagg died in his own yard ~20 s after
                    // every "holed up to heal" line, 15 times. If wildlife holds the
                    // yard and this bot won't fight it: close to home → duck straight
                    // inside (the last hop was a teleport anyway); far from home → put
                    // distance between them first, and the next threat tick re-plans
                    // home once the yard clears.
                    var camper = NearestDangerAnimal(home, 35f);
                    BotRecord campHolder;
                    var campBase = camper != null ? GetGroupBase(rec.Identity.GroupId, out campHolder) : null;
                    // WH-1 (2026-08-01): a heli that has this bot ON ITS TARGET LIST
                    // must never be led home — that's how Slagg's base died (rocketed
                    // 22 s after "took cover from a patrol helicopter"). A real player
                    // ditches into open ground or eats the death; the cabin survives.
                    // Merely-nearby helis still send the bot indoors like before.
                    if (patrol != null && PatrolIsHuntingMe(patrol, bot))
                    {
                        ws.LeadAway = true;
                        ws.RetreatGoal = Vector3.zero; // no ducking indoors — the point is to be elsewhere
                        PlanAwayLeg(bot, ws, patrol.transform.position, home);
                    }
                    else if (camper != null && !CanFight(rec, bot, camper) && campBase != null &&
                        FlatDistance(bot.transform.position, home) <= 60f)
                    {
                        FlickBaseDoors(campBase, bot.transform.position);
                        bot.Teleport(ws.RetreatGoal != Vector3.zero ? ws.RetreatGoal : home);
                        ws.Sheltered = true;
                        DLog($"[retreat] {rec.Identity.Name}'s yard is camped by a {AnimalName(camper)} — ducked straight inside");
                    }
                    else if (camper != null && !CanFight(rec, bot, camper))
                    {
                        var awayDir = bot.transform.position - camper.transform.position;
                        awayDir.y = 0f;
                        if (awayDir.sqrMagnitude < 0.01f) awayDir = bot.transform.forward;
                        PlanPathTo(bot, ws, bot.transform.position + awayDir.normalized * 45f);
                        DLog($"[retreat] {rec.Identity.Name}'s yard is camped by a {AnimalName(camper)} — putting distance first");
                    }
                    else PlanPathTo(bot, ws, home); // walk to the anchor; duck inside on arrival
                    if (patrol != null && ws.LeadAway)
                    {
                        DLog($"[retreat] {rec.Identity.Name} is target-locked by a {ThreatTag(patrol, bot)} — leading it AWAY from home");
                        Story("retreat", $"{rec.Identity.Name} broke for open ground, leading the {ThreatName(patrol)} away from home");
                    }
                    else if (patrol != null)
                    {
                        DLog($"[retreat] {rec.Identity.Name} spotted a {ThreatTag(patrol, bot)} — falling back to base" +
                            (repeatHold ? $" (same spot, {ws.DangerHits}× — holding {DangerRepeatHoldSeconds / 60f:0} min)" : ""));
                        // v0.21.2: one story beat per 10 min per bot — a parked threat used
                        // to write "took cover" every cycle (1,556 beats in one wipe).
                        if (now - ws.PatrolStoryAt >= 600f)
                        {
                            ws.PatrolStoryAt = now;
                            Story("retreat", $"{rec.Identity.Name} took cover from a {ThreatName(patrol)}");
                        }
                    }
                    else if (grudged != null)
                    {
                        DLog($"[retreat] {rec.Identity.Name} spotted {grudged.displayName} (grudged) — keeping distance");
                        Story("grudge", $"{rec.Identity.Name} saw {grudged.displayName} coming and made for cover");
                    }
                    else if (shotAt != null && !hurt)
                    {
                        DLog($"[retreat] {rec.Identity.Name} is under fire from {shotAt.displayName} — falling back");
                        Story("retreat", $"{rec.Identity.Name} came under fire from {shotAt.displayName} and ran for cover");
                    }
                    else
                    {
                        DLog($"[retreat] {rec.Identity.Name} is hurt ({bot.health:0}hp) — holing up to heal");
                        // WB-2: don't log "holed up" while he's still crossing the yard
                        // (Slagg's log claimed cover 20 s before each yard death) —
                        // emit on real arrival. Ducked straight inside above = arrived.
                        if (ws.Sheltered) Story("retreat", $"{rec.Identity.Name} holed up at home to heal");
                        else ws.RetreatStory = $"{rec.Identity.Name} holed up at home to heal";
                    }
                }
                // Max: the per-tick re-arm must not shorten a repeat-alarm hold (v0.21.3).
                ws.HoldUntil = Mathf.Max(ws.HoldUntil, now + (hurt && patrol == null && grudged == null ? 5f : (repeatHold ? DangerRepeatHoldSeconds : 20f)));
                return;
            }
            if (ws.Retreating)
            {
                // Leave cover once it's clear AND healed back up. "Clear" means LOOK
                // before stepping out: a bot he can't fight prowling the yard keeps
                // him indoors (WildSadie67 flip-flopped out of cover into the same
                // wolf pack five deaths running, 2026-07-22 — hold expiry alone isn't
                // clearance). Re-arm a short hold so the recheck stays cheap.
                if (now > ws.HoldUntil && bot.health >= maxHp * 0.75f)
                {
                    // WD-4: restock BEFORE the yard-clear check — CanFight now requires
                    // live rounds (WD-3), so a dry bot indoors would otherwise fail the
                    // beast check forever and never step out to break the camp.
                    if (ws.Sheltered) ResupplyKitAmmo(bot, rec);
                    var yard = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
                    // Heli/Bradley keep EVERYONE indoors — they're never-fight targets
                    // (6C doctrine), so CanFight must not excuse stepping out under one
                    // (live 2026-07-23: geared bots wandered out mid-heli-patrol).
                    // v0.21.2 hysteresis: scan from the YARD (where he steps out to) over
                    // PatrolClearScale× the alarm radius — sheltered at the bed he sat a
                    // few metres further from an edge-of-range threat than the yard did.
                    var yardThreat = SensePatrolThreatAt(yard, caution, PatrolClearScale);
                    var armorPatrol = yardThreat is PatrolHelicopter || yardThreat is BradleyAPC;
                    var yardBeast = NearestDangerAnimal(yard, 35f);
                    // Scientists/heli/Bradley are never resident fight targets (PickFightTarget
                    // lists hostiles, grudges and beasts only), so CanFight can't excuse
                    // stepping out under one — it only ever re-armed the loop (v0.21.2).
                    if (armorPatrol || yardThreat != null ||
                        (yardBeast != null && !CanFight(rec, bot, yardBeast))) // armed bots step out into a camped yard — and fight it
                    {
                        ws.HoldUntil = now + 15f;
                        return; // yard's not actually clear — keep the door shut
                    }
                    if (ws.Sheltered && rec.State.HomePos != null && !StartInteriorWalkOut(rec, bot, ws, WalkShelter, now))
                        bot.Teleport(GroundSnap(rec.State.HomePos.V)); // step back outside to resume (hop fallback)
                    ws.Retreating = false;
                    ws.Sheltered = false;
                    ws.LeadAway = false;
                    ws.RetreatStory = null; // healed on the move — the "holed up" beat never came true
                    DLog($"[retreat] {rec.Identity.Name} is back out (clear + healed to {bot.health:0}hp)");
                }
                return; // still holed up
            }

            // Fight off a predator that's on us — healthy and not already in a fight.
            // WA-2 (decision 0010): no observed gate here — wildlife kills bots
            // unwatched, so unwatched bots fight back too; the abstract world only
            // resolves what the real one doesn't force. Gun bots never reach this
            // (PickFightTarget takes the beast); this is the melee trade.
            WorkState wk;
            if (_work.TryGetValue(rec.Identity.BotId, out wk) && wk.Phase == WorkPhase.Hunting) return;
            var fleeAt = Mathf.Lerp(30f, 60f, caution);
            if (bot.health <= fleeAt + 15f) return;
            var beast = NearestDangerAnimal(bot.transform.position, 15f);
            if (beast != null)
            {
                WorkState w2;
                if (!_work.TryGetValue(rec.Identity.BotId, out w2)) _work[rec.Identity.BotId] = w2 = new WorkState();
                StartHunt(rec, bot, ws, w2, beast, now);
                Story("defend", $"{rec.Identity.Name} was set upon by a {AnimalName(beast)} and fought back");
                TrackWildlifeHarassment(rec, bot.transform.position); // WC-2 — churn counts toward the camp
            }
        }

        // 4E — nearest player this bot has reason to fear, within its caution-scaled
        // detection range. Firsthand grudges always register; secondhand warnings
        // (wary) only spook the cautious half (caution ≥ 0.5) — hearsay is enough for
        // the careful, not the bold. Phase 5.5 gave bot-vs-bot grudges their teeth:
        // live bots with `bot:` ledger entries register too, which is how residents
        // see through a native-body hostile's disguise — but only AFTER Cobalt has
        // shown its hand (the scientist chassis used to give it away on sight).
        private BasePlayer SenseGrudgedPlayer(BotRecord rec, BasePlayer bot, float caution, out bool firsthand)
        {
            firsthand = false;
            var soc = rec.Social;
            var hasGrudges = soc?.Grudges != null && soc.Grudges.Count > 0;
            var heedsWarnings = caution >= 0.5f && soc?.Reputation != null && soc.Reputation.Count > 0;
            if (!hasGrudges && !heedsWarnings) return null;
            var r = Mathf.Lerp(35f, 75f, caution);
            var rSq = r * r;
            var pos = bot.transform.position;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (!IsRealPlayer(p) || p.IsDead()) continue;
                if ((p.transform.position - pos).sqrMagnitude > rSq) continue;
                var key = SteamKey((ulong)p.userID);
                if (hasGrudges && soc.Grudges.ContainsKey(key)) { firsthand = true; return p; }
                if (heedsWarnings && soc.Reputation.ContainsKey(key)) return p;
            }
            foreach (var kv in _live)
            {
                if (kv.Key == rec.Identity.BotId) continue;
                var p = kv.Value;
                if (p == null || p.IsDestroyed || p.IsDead() || p.IsSleeping()) continue;
                if ((p.transform.position - pos).sqrMagnitude > rSq) continue;
                var key = BotKey(kv.Key);
                if (hasGrudges && soc.Grudges.ContainsKey(key)) { firsthand = true; return p; }
                if (heedsWarnings && soc.Reputation.ContainsKey(key)) return p;
            }
            return null;
        }

        // Chatty bots call their killer out in global chat when he shows his face —
        // cooldown lives on the per-live-bot WanderState so it's cleaned up with it.
        private void MutterAtGrudged(BotRecord rec, WanderState ws, BasePlayer grudged, float now)
        {
            if ((rec.Identity.Traits?.Chattiness ?? 0f) < 0.5f) return;
            if (now < ws.NextMutterAt) return;
            ws.NextMutterAt = now + 120f;
            PrintToChat(L("Presence.GrudgeWarn", rec.Identity.Name, grudged.displayName));
        }

        // Corners exhausted while retreating: close on the anchor, duck inside, hold.
        private void RetreatArrived(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            // WH-1: leg done while heli-hunted — still locked? Keep making distance.
            // Lock broken (shot down, lost interest, flew off) → fall back to the
            // normal close-on-home path next tick.
            if (ws.LeadAway)
            {
                var caution = Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f);
                var hunter = SensePatrolThreat(bot, caution);
                if (hunter != null && PatrolIsHuntingMe(hunter, bot))
                {
                    PlanAwayLeg(bot, ws, hunter.transform.position,
                        rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position);
                    return;
                }
                ws.LeadAway = false;
            }
            if (ws.Sheltered)
            {
                // Inside cover — hold; glance around like he's watching the door.
                if (UnityEngine.Random.value < 0.02f)
                {
                    bot.OverrideViewAngles(new Vector3(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                    bot.SendNetworkUpdate();
                }
                return;
            }
            var home = rec.State.HomePos != null ? rec.State.HomePos.V : bot.transform.position;
            if (FlatDistance(bot.transform.position, home) > 6f)
            {
                if (!PlanPathTo(bot, ws, home))
                {
                    // no navmesh path — beeline so a cornered bot still bolts for home
                    var toFlat = new Vector3(home.x - bot.transform.position.x, 0f, home.z - bot.transform.position.z);
                    var d = toFlat.magnitude;
                    if (d > 0.01f) MoveBotStep(bot, bot.transform.position + toFlat * (ws.Speed * WanderTickSeconds / d), toFlat);
                }
                return;
            }
            // Reached the anchor — duck inside to the sheltered spot (teleport the last
            // hop; the interior floor is off the navmesh) and hole up there to heal.
            BotRecord holder;
            var b = GetGroupBase(rec.Identity.GroupId, out holder);
            var goal = ws.RetreatGoal != Vector3.zero ? ws.RetreatGoal : home;
            // N-2 (IN-3.2): walk in through the door to the bed; Sheltered is set on
            // arrival (OnInteriorArrived). The hop below is the fallback.
            if (ws.Walk == 0 && goal != home && StartInteriorWalk(rec, bot, ws, goal, WalkShelter, now)) return;
            if (b != null) FlickBaseDoors(b, bot.transform.position);
            bot.Teleport(goal);
            ws.Sheltered = true;
            ws.Corners.Clear();
            ws.CornerIndex = 0;
            if (ws.RetreatStory != null) { Story("retreat", ws.RetreatStory); ws.RetreatStory = null; } // WB-2 — now it's true
        }

        // --- 5A2-R: stand-and-fight (the fight half of fight-or-flight) --------------
        // Gate: worn kit tier ≥ FightMinKitTier AND aggression ≥ FightMinAggression AND
        // a ranged weapon in the belt — else the flee/hole-up path above handles it.
        // Combat uses the probe-proven native-fire recipe (2026-07-21 report): ServerUse
        // at damageModifier 0 is pure theater (muzzle flash, tracer, gunshot, ammo
        // drain); the damage is our own hit roll + raycast + HitInfo through Hurt so
        // the full pipeline (grudges, kill credit, bounties, death feed) fires.
        // Engagement caps borrowed from the HostileBrain doctrine (5D): a resident
        // defends himself, he doesn't lay siege.
        private const float FightEngageTimeout = 90f;
        private const float FightNoLosTimeout = 20f;
        private const float FightTargetCooldown = 300f;

        // A hit that just landed, polled off the engine's lastAttacker at the 1.5s
        // threat cadence — no OnEntityTakeDamage subscription (hot-hook rule).
        private BaseCombatEntity RecentAttacker(BasePlayer bot)
        {
            var last = bot.lastAttacker as BaseCombatEntity;
            if (last == null || last == bot || last.IsDestroyed || last.IsDead()) return null;
            if (UnityEngine.Time.time - bot.lastAttackedTime > 6f) return null;
            return last;
        }

        // 0→1 over SeasoningDays of wipe-time since the bot's first session — the
        // longer a bot has lived on the server this wipe, the more seasoned it fights
        // (+25% of the skill scale to aim/cadence, up to +30% damage at full season).
        private float Seasoning(BotRecord rec)
        {
            if (_config.SeasoningDays <= 0f) return 0f;
            var s = rec?.State?.WipeFirstOnlineAt;
            if (string.IsNullOrEmpty(s)) return 0f;
            var t = ParseTime(s);
            if (t == DateTime.MinValue) return 0f;
            return Mathf.Clamp01((float)(DateTime.Now - t).TotalDays / _config.SeasoningDays);
        }

        private bool CanFight(BotRecord rec, BasePlayer bot) => CanFight(rec, bot, null);

        private bool CanFight(BotRecord rec, BasePlayer bot, BaseEntity foe)
        {
            if (rec.Identity.BotId == _probeFightBotId) return FightWeapon(bot) != null; // probe bypasses the gates
            var prog = rec.Progression;
            var tier = prog == null ? 0 : (prog.GearTier >= 0 ? prog.GearTier : prog.KitTier); // the loadout actually worn
            // WA-1 (decision 0010): an animal is not a firefight. Any armed bot stands
            // up to wildlife — no tier ladder, no aggression floor; a real player with
            // a bow doesn't run from a boar. The old gates made death loops
            // self-sustaining: a respawn wears tier 0, and tier 0 wasn't allowed to
            // fight the thing that killed it. PvP keeps the full 5A2-R gates.
            if (foe != null && IsWildlife(foe))
                return tier >= _config.WildlifeFightMinKitTier && FightArmed(bot); // WD-3: dry guns don't stand ground
            if (tier < _config.FightMinKitTier) return false;
            if ((rec.Identity.Traits?.Aggression ?? 0f) < _config.FightMinAggression) return false;
            return FightArmed(bot);
        }

        private BaseProjectile FightWeapon(BasePlayer bot)
        {
            if (bot?.inventory?.containerBelt == null) return null;
            foreach (var item in bot.inventory.containerBelt.itemList)
            {
                var gun = item?.GetHeldEntity() as BaseProjectile;
                if (gun != null) return gun;
            }
            return null;
        }

        // WD-3 (v0.15.2): "armed" means the gun can actually FIRE — a round in the
        // mag or in the pockets. Entry on an empty gun was one-second theater: 121 of
        // 152 logged engagements (8/12–8/15) opened "stood his ground" and closed
        // "ran out of ammo" a median 1 s later, clearing retreat state each time
        // while the animal kept the yard.
        private bool FightArmed(BasePlayer bot)
        {
            var gun = FightWeapon(bot);
            if (gun == null) return false;
            if (gun.primaryMagazine.contents > 0) return true;
            var ammoDef = gun.primaryMagazine.ammoType;
            if (ammoDef == null || bot.inventory?.containerMain == null) return false;
            foreach (var item in bot.inventory.containerMain.itemList)
                if (item != null && item.info.itemid == ammoDef.itemid && item.amount > 0) return true;
            return false;
        }

        // Priority: whoever just hit us → a firsthand-grudged player in sight → a
        // Cobalt hostile prowling close → a predator on us. Heli/Bradley/turrets are
        // never fight targets (rifles don't argue with those — the flee path handles
        // them), and neither are group-mates or targets we recently gave up on.
        private BaseCombatEntity PickFightTarget(BotRecord rec, BasePlayer bot, WanderState ws, BaseCombatEntity attacker, float caution, float now)
        {
            if (FightEligible(rec, bot, ws, attacker, now)) return attacker;
            bool firsthand;
            var grudged = SenseGrudgedPlayer(rec, bot, caution, out firsthand);
            if (firsthand && FightEligible(rec, bot, ws, grudged, now)) return grudged;
            var hostile = NearestThreatOf<HostileNpc>(bot.transform.position, Mathf.Lerp(35f, 75f, caution));
            if (FightEligible(rec, bot, ws, hostile, now)) return hostile;
            var beast = NearestDangerAnimal(bot.transform.position, 15f) as BaseCombatEntity;
            if (FightEligible(rec, bot, ws, beast, now)) return beast;
            return null;
        }

        private bool FightEligible(BotRecord rec, BasePlayer bot, WanderState ws, BaseCombatEntity t, float now)
        {
            if (t == null || t.IsDestroyed || t.IsDead()) return false;
            if (!CanFight(rec, bot, t)) return false; // per-foe gates: wildlife fights on any tier, PvP keeps 5A2-R
            // Stand-and-fight has no chase: a threat beyond earshot of the firing
            // range would just mean 90s of posing — flee instead.
            if (Vector3.Distance(bot.transform.position, t.transform.position) > _config.FightRange * 1.5f) return false;
            var p = t as BasePlayer; // players, residents, hostiles, old-gen scientists (Gen2 scientists are NOT BasePlayers — final return)
            if (p != null)
            {
                if (p == bot || p.IsDead() || p.IsSleeping()) return false;
                int tid;
                if (_byUserId.TryGetValue((ulong)p.userID, out tid))
                {
                    if (tid == rec.Identity.BotId) return false;
                    BotRecord trec;
                    if (_bots.TryGetValue(tid, out trec) && rec.Identity.GroupId > 0 &&
                        trec.Identity.GroupId == rec.Identity.GroupId) return false; // no friendly-fire feuds
                }
                float until;
                if (ws.FightCooldown != null && ws.FightCooldown.TryGetValue((ulong)p.userID, out until) && now < until) return false;
                return true;
            }
            // Animals (incl. Gen2 wolves) get shot; a Gen2 scientist that opened fire
            // gets the same stand-and-fight an old-gen one did via the BasePlayer path
            // (CanFight still applies the full 5A2-R PvP gates — a firefight is a
            // firefight); heli/Bradley/turrets never.
            return t is BaseAnimalNPC || IsDangerWildlife(t) || t is Rust.Ai.Gen2.ScientistNPC2;
        }

        private static string FightTargetName(BaseCombatEntity t)
        {
            var p = t as BasePlayer;
            if (p != null) return p.displayName;
            if (IsScientist(t)) return "a scientist"; // Gen2 — old-gen took the displayName path above
            return "a " + AnimalName(t);
        }

        private void StartFight(BotRecord rec, BasePlayer bot, WanderState ws, BaseCombatEntity target, float now)
        {
            var gun = FightWeapon(bot);
            if (gun == null) return;
            _work.Remove(rec.Identity.BotId); // the job can wait
            ws.Working = false;
            ws.Retreating = false;
            ws.Sheltered = false;
            ws.Corners.Clear();
            ws.CornerIndex = 0;
            ws.FightTarget = target;
            ws.FightDesc = FightTargetName(target);
            ws.FightWasPlayer = target is BasePlayer;
            ws.FightLastPos = target.transform.position;
            ws.FightStartAt = now;
            ws.FightLastLos = now;
            ws.NextShotAt = now + UnityEngine.Random.Range(0.5f, 1.1f); // draw + shoulder
            var item = gun.GetItem();
            if (item != null) bot.UpdateActiveItem(item.uid);
            var tname = ws.FightDesc;
            DLog($"[fight] {rec.Identity.Name} is standing his ground against {tname}");
            Story("fight", $"{rec.Identity.Name} stood his ground against {tname}");
            if (rec.Identity.BotId == _probeFightBotId) PLog($"[probe:fight] {rec.Identity.Name} engaged {tname}");
        }

        private void EndFight(BotRecord rec, BasePlayer bot, WanderState ws, float now, bool cooldownTarget)
        {
            var tp = ws.FightTarget as BasePlayer;
            if (cooldownTarget && tp != null)
            {
                if (ws.FightCooldown == null) ws.FightCooldown = new Dictionary<ulong, float>();
                ws.FightCooldown[(ulong)tp.userID] = now + FightTargetCooldown; // peeking out doesn't re-aggro
            }
            else if (cooldownTarget)
            {
                // WG-2: an animal target has no userID for the per-target map — after
                // ANY give-up (flee valve, timeout, lost LOS) suppress all re-engagement
                // briefly so the retreat/hole-up path owns the next move instead of the
                // fight entry winning the race back to the same bear.
                ws.FightSuppressUntil = now + 60f;
            }
            ws.FightTarget = null;
            if (bot != null && !bot.IsDestroyed) bot.UpdateActiveItem(default(ItemId)); // holster
            ws.RetreatCheckAt = 0f; // decide flee/hole-up on the very next tick
        }

        // Per-tick fight driver (10Hz) — stands ground, tracks the target, fires on a
        // skill-scaled cadence. No pursuit: the engagement caps end fights the target
        // walks away from.
        private void FightStep(BotRecord rec, BasePlayer bot, WanderState ws, float now)
        {
            var target = ws.FightTarget;
            var probe = rec.Identity.BotId == _probeFightBotId;
            var tp = target as BasePlayer;
            if (ReferenceEquals(target, null)) { EndFight(rec, bot, ws, now, false); return; } // stale call — nothing to fight
            // Unity fake-null: a killed bot/animal's entity is destroyed and compares
            // == null — read outcomes from the cached FightDesc/FightLastPos, never
            // from the (possibly destroyed) entity.
            if (target == null || target.IsDestroyed || target.IsDead() || (tp != null && tp.IsSleeping()))
            {
                // Won (or the target vanished). Kill credit, grudges, and bounties
                // already flowed through the Hurt→death pipeline — just stand down.
                if (probe) PLog($"[probe:fight] WIN — {ws.FightDesc} down after {now - ws.FightStartAt:0.0}s");
                if (!ws.FightWasPlayer)
                {
                    Story("fight", $"{rec.Identity.Name} shot {ws.FightDesc} dead");
                    if (ProfileHunts(rec)) // a hunter walks over and harvests his kill
                    {
                        var corpse = NearestCorpse(ws.FightLastPos, 8f);
                        if (corpse != null)
                        {
                            WorkState wk;
                            if (!_work.TryGetValue(rec.Identity.BotId, out wk)) _work[rec.Identity.BotId] = wk = new WorkState();
                            wk.Target = corpse;
                            wk.Phase = WorkPhase.ToResource;
                            wk.PhaseDeadline = now + 30f;
                            EndFight(rec, bot, ws, now, false);
                            if (PlanPathTo(bot, ws, corpse.transform.position)) { ws.Working = true; return; }
                            wk.Target = null;
                        }
                    }
                }
                else DLog($"[fight] {rec.Identity.Name} won the fight against {ws.FightDesc}");
                EndFight(rec, bot, ws, now, false);
                return;
            }
            ws.FightLastPos = target.transform.position;
            // Flee valve — same caution curve as the hunt's break-off.
            var caution = Mathf.Clamp01(rec.Identity.Traits?.Caution ?? 0.5f);
            if (bot.health <= Mathf.Lerp(30f, 60f, caution))
            {
                DLog($"[fight] {rec.Identity.Name} broke off at {bot.health:0}hp — running for home");
                Story("fight", $"{rec.Identity.Name} was shot up and ran for cover");
                if (probe) PLog($"[probe:fight] flee valve at {bot.health:0}hp");
                EndFight(rec, bot, ws, now, true);
                return;
            }
            if (now - ws.FightStartAt > FightEngageTimeout || now - ws.FightLastLos > FightNoLosTimeout)
            {
                DLog($"[fight] {rec.Identity.Name} broke off against {FightTargetName(target)} — {(now - ws.FightLastLos > FightNoLosTimeout ? "lost sight" : "fight dragged on")}");
                if (probe) PLog("[probe:fight] break-off (timeout/lost LOS)");
                EndFight(rec, bot, ws, now, true);
                return;
            }
            // Wounded targets are prone — head-height aim just combs the grass; finish
            // at center mass.
            var aimPoint = tp != null && tp.eyes != null && !tp.IsWounded()
                ? tp.eyes.position - Vector3.up * 0.15f : target.CenterPoint();
            var dist = Vector3.Distance(bot.transform.position, target.transform.position);
            var visible = dist <= _config.FightRange * 1.5f && bot.IsVisible(aimPoint);
            if (visible) ws.FightLastLos = now;
            var dir = (aimPoint - bot.eyes.position).normalized;
            if (now >= ws.NextFaceAt) // track with the muzzle; sync throttled
            {
                ws.NextFaceAt = now + 0.25f;
                bot.OverrideViewAngles(Quaternion.LookRotation(dir).eulerAngles);
                bot.SendNetworkUpdate();
            }
            if (!visible || dist > _config.FightRange || now < ws.NextShotAt) return;

            var gun = FightWeapon(bot);
            if (gun == null) { EndFight(rec, bot, ws, now, true); return; }
            if (gun.primaryMagazine.contents <= 0)
            {
                if (!FightReload(bot, gun))
                {
                    DLog($"[fight] {rec.Identity.Name} ran dry — breaking off");
                    Story("fight", $"{rec.Identity.Name} ran out of ammo and fell back");
                    if (probe) PLog("[probe:fight] out of ammo — break-off");
                    EndFight(rec, bot, ws, now, true);
                    return;
                }
                ws.NextShotAt = now + 2.5f; // reload pause
                if (probe) PLog($"[probe:fight] reloaded ({gun.primaryMagazine.contents} rds)");
                return;
            }
            float dmg, interval;
            FightWeaponProfile(gun, out dmg, out interval);
            // Seasoned residents fight harder: wipe-days sharpen aim/cadence and
            // hitting power on top of the innate skill trait.
            var season = Seasoning(rec);
            var skill = Mathf.Clamp01((rec.Identity.Traits?.SkillLevel ?? 0.5f) + 0.25f * season);
            var landed = NativeShot(bot, gun, target, aimPoint, dist, skill, dmg * (1f + 0.3f * season));
            ws.NextShotAt = now + interval * Mathf.Lerp(1.35f, 0.75f, skill) * UnityEngine.Random.Range(0.85f, 1.25f);
            if (probe) PLog($"[probe:fight] shot at {dist:0.0}m: {(landed ? "HIT" : "miss")}, target hp {(target.IsDestroyed ? -1f : target.health):0.#}, mag {gun.primaryMagazine.contents}");
        }

        // The native-fire shot, shared by resident fights and native-body hostiles
        // (probe recipe 2026-07-21): ServerUse at damageModifier 0 is pure theater
        // (muzzle flash, tracer, gunshot, ammo drain); damage is a skill-scaled hit
        // roll + a clean trace — cover works, and a miss never wings a bystander
        // (no damage unless the ray lands on the intended target). Returns landed.
        //
        // Armor is REAL (world-feel pass 2026-07-22): the shot rolls a hit area
        // (head/chest/stomach/limbs) and applies the victim's WORN clothing via the
        // same ItemModWearable protection data the engine uses. We can't ride the
        // engine's own clothing path — HitInfo.boneArea is computed from a skeleton
        // bone we can't fake — so UseProtection is turned OFF and this math replaces
        // it wholesale. Bots in laddered kits genuinely tank more, exactly as they
        // already do against real players' bullets.
        private bool NativeShot(BasePlayer shooter, BaseProjectile gun, BaseCombatEntity target, Vector3 aimPoint, float dist, float skill, float dmg)
        {
            var dir = (aimPoint - shooter.eyes.position).normalized;
            gun.ServerUse(new HeldEntityServerUseParams(0f, 1f,
                Matrix4x4.TRS(shooter.eyes.position, Quaternion.LookRotation(dir), Vector3.one), true, false));
            var hitChance = Mathf.Clamp01(Mathf.Lerp(0.35f, 0.8f, skill) * (dist < 10f ? 1.15f : dist > 25f ? 0.8f : 1f));
            if (UnityEngine.Random.value >= hitChance) return false;
            RaycastHit tr;
            if (!Physics.Raycast(shooter.eyes.position, dir, out tr, Mathf.Max(dist + 10f, _config.FightRange * 1.5f), 1220225793)) return false;
            var ent = tr.GetEntity();
            var victim = ent as BaseCombatEntity ?? (ent != null ? ent.GetParentEntity() as BaseCombatEntity : null);
            if (victim != target) return false;
            // Where the round lands: headshots hurt, limb grazes don't.
            HitArea area;
            float areaScale;
            var roll = UnityEngine.Random.value;
            if (roll < 0.12f) { area = HitArea.Head; areaScale = 1.75f; }
            else if (roll < 0.60f) { area = HitArea.Chest; areaScale = 1f; }
            else if (roll < 0.78f) { area = HitArea.Stomach; areaScale = 1f; }
            else { area = UnityEngine.Random.value < 0.5f ? HitArea.Arm : HitArea.Leg; areaScale = 0.75f; }
            var armor = 0f;
            var vp = target as BasePlayer;
            if (vp?.inventory?.containerWear?.itemList != null)
            {
                foreach (var it in vp.inventory.containerWear.itemList)
                {
                    var w = it?.info != null ? it.info.GetComponent<ItemModWearable>() : null;
                    if (w == null || w.protectionProperties == null || !w.ProtectsArea(area)) continue;
                    armor += w.protectionProperties.Get(DamageType.Bullet);
                }
            }
            var final = dmg * Mathf.Max(0f, _config.FightDamageScale) * areaScale * Mathf.Clamp(1f - armor, 0.15f, 1f);
            var hi = new HitInfo(shooter, target, DamageType.Bullet, final);
            hi.UseProtection = false; // armor handled above — no double dip through baseProtection
            hi.PointStart = shooter.eyes.position;
            hi.HitPositionWorld = tr.point;
            target.Hurt(hi);
            return true;
        }

        // Top the magazine back up from pocketed ammo; false = pockets are empty.
        private bool FightReload(BasePlayer bot, BaseProjectile gun)
        {
            var ammoDef = gun.primaryMagazine.ammoType;
            if (ammoDef == null || bot.inventory?.containerMain == null) return false;
            var need = gun.primaryMagazine.capacity - gun.primaryMagazine.contents;
            if (need <= 0) return true;
            foreach (var item in bot.inventory.containerMain.itemList)
            {
                if (item == null || item.info.itemid != ammoDef.itemid || item.amount <= 0) continue;
                var take = Mathf.Min(need, item.amount);
                item.UseItem(take);
                gun.SetAmmoCount(gun.primaryMagazine.contents + take);
                gun.SendNetworkUpdateImmediate();
                return true;
            }
            return false;
        }

        // Per-weapon feel: damage per landed shot + base seconds between shots.
        // (Skill then scales cadence 1.35x→0.75x; kit ladders are data-driven, so
        // unknown weapons take the fallback line.)
        private static void FightWeaponProfile(BaseProjectile gun, out float dmg, out float interval)
        {
            var sn = gun.GetItem()?.info?.shortname ?? "";
            switch (sn)
            {
                case "bow.hunting": dmg = 25f; interval = 2.0f; break;
                case "crossbow": dmg = 35f; interval = 2.6f; break;
                case "bow.compound": dmg = 40f; interval = 2.4f; break;
                case "pistol.revolver": dmg = 17f; interval = 0.7f; break;
                case "pistol.semiauto": dmg = 14f; interval = 0.55f; break;
                case "smg.thompson": case "smg.2": case "smg.mp5": dmg = 13f; interval = 0.35f; break;
                case "rifle.semiauto": dmg = 16f; interval = 0.75f; break;
                case "rifle.ak": case "rifle.ak.ice": case "m16a2": dmg = 18f; interval = 0.4f; break;
                case "rifle.lr300": dmg = 17f; interval = 0.45f; break;
                case "rifle.m39": dmg = 25f; interval = 0.9f; break;
                case "rifle.bolt": dmg = 55f; interval = 2.8f; break;
                case "pistol.python": dmg = 30f; interval = 0.8f; break;
                case "pistol.prototype17": dmg = 14f; interval = 0.5f; break;
                case "shotgun.m4": case "shotgun.spas12": dmg = 45f; interval = 1.2f; break;
                case "shotgun.pump": dmg = 50f; interval = 1.6f; break;
                case "shotgun.double": dmg = 50f; interval = 1.8f; break;
                case "shotgun.waterpipe": case "blunderbuss": dmg = 40f; interval = 2.5f; break;
                default: dmg = 15f; interval = 1.0f; break;
            }
        }

        #endregion

        #region Admin roster CUI (D2)

        private const string UiPanel = "FakeFriends.AdminUI";

        private bool IsAdminPlayer(BasePlayer player)
        {
            if (player == null) return false;
            if (player.net?.connection != null && player.net.connection.authLevel >= 2) return true;
            return permission.UserHasPermission(player.UserIDString, _config.AdminPermission);
        }

        [ChatCommand("ffadmin")]
        private void ChatAdminUi(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminPlayer(player))
            {
                SendReply(player, L("Admin.NoPermission"));
                return;
            }
            ShowAdminUi(player);
        }

        [ConsoleCommand("ff.ui")]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("Run in-game."); return; }
            if (!Allowed(arg)) return;
            ShowAdminUi(player, arg.GetString(0, "") == "all");
        }

        [ConsoleCommand("ff.ui.close")]
        private void CmdUiClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, UiPanel);
        }

        [ConsoleCommand("ff.ui.session")]
        private void CmdUiSession(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var player = arg.Player();
            if (rec != null)
            {
                if (rec.State.Status == "Online") EndSession(rec);
                else if (rec.State.Status == "Sleeping" || rec.State.Status == "Unspawned") StartSession(rec);
                else if (rec.State.Status == "Dead") RespawnBot(rec);
            }
            if (player != null) ShowAdminUi(player);
        }

        [ConsoleCommand("ff.ui.retire")]
        private void CmdUiRetire(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var player = arg.Player();
            if (rec != null)
            {
                if (IsOut(rec)) ReviveBot(rec);
                else RetireBot(rec);
            }
            if (player != null) ShowAdminUi(player);
        }

        private static int StatusRank(BotRecord r)
        {
            switch (r.State.Status)
            {
                case "Online": return 0;
                case "Dead": return 1;
                case "Sleeping": return 2;
                case "Retired": return 4;
                case "Fallen": return 5;
                default: return 3;
            }
        }

        private void ShowAdminUi(BasePlayer player, bool showOut = false)
        {
            Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, UiPanel);
            var ui = new Oxide.Game.Rust.Cui.CuiElementContainer();
            ui.Add(new Oxide.Game.Rust.Cui.CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.96" },
                RectTransform = { AnchorMin = "0.18 0.12", AnchorMax = "0.82 0.9" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);

            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = $"FAKE FRIENDS — resident roster  ({_live.Count} awake, wipe day {WipeDay()})", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = "0.95 0.9 0.75 1" },
                RectTransform = { AnchorMin = "0.03 0.93", AnchorMax = "0.62 0.99" },
            }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui.story 0", Color = "0.32 0.3 0.42 1" },
                Text = { Text = "Story", FontSize = 11, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.63 0.94", AnchorMax = "0.73 0.99" },
            }, UiPanel);
            var outN = 0;
            foreach (var r in _bots.Values) if (IsOut(r)) outN++;
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = showOut ? "ff.ui" : "ff.ui all", Color = "0.3 0.3 0.3 1" },
                Text = { Text = showOut ? "Hide fallen" : $"Fallen ({outN})", FontSize = 11, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.74 0.94", AnchorMax = "0.92 0.99" },
            }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui.close", Color = "0.55 0.2 0.2 1" },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.94 0.94", AnchorMax = "0.985 0.99" },
            }, UiPanel);

            // Phase 7 header strip: SLM endpoint health at a glance
            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = SlmStripText(), FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.03 0.895", AnchorMax = "0.97 0.928" },
            }, UiPanel);

            var sorted = new List<BotRecord>();
            foreach (var r in _bots.Values) { if (!showOut && IsOut(r)) continue; sorted.Add(r); }
            sorted.Sort((a, b) =>
            {
                var ra = StatusRank(a); var rb = StatusRank(b);
                return ra != rb ? ra.CompareTo(rb) : a.Identity.BotId.CompareTo(b.Identity.BotId);
            });
            var rowH = 0.83f / Mathf.Max(15, sorted.Count);
            for (var i = 0; i < sorted.Count; i++)
            {
                var rec = sorted[i];
                var idn = rec.Identity;
                var yMax = 0.885f - i * rowH;
                var yMin = yMax - rowH * 0.9f;
                var live = GetLive(idn.BotId) != null;
                var status = rec.State.Status + (live ? " •" : "");
                var next = "";
                var t = ParseTime(rec.State.NextSessionAt ?? "");
                if (rec.State.Status == "Sleeping" && t != DateTime.MaxValue) next = $"  next {t:ddd HH:mm}";
                else if (rec.State.Status == "Dead")
                {
                    var r = ParseTime(rec.State.RespawnAt ?? "");
                    if (r != DateTime.MaxValue) next = $"  respawns {r:HH:mm}";
                }
                var stuck = rec.State.Status == "Online" && live && IsStuckBot(idn.BotId);
                var act = rec.State.Status == "Online" && live ? $" · {DescribeActivity(idn.BotId)}" : "";
                var statusColor = stuck ? "0.95 0.65 0.3 1"
                    : rec.State.Status == "Online" ? "0.5 0.9 0.5 1"
                    : rec.State.Status == "Dead" ? "0.9 0.45 0.45 1"
                    : rec.State.Status == "Fallen" ? "0.75 0.45 0.45 1"
                    : rec.State.Status == "Retired" ? "0.6 0.6 0.6 1" : "0.8 0.8 0.65 1";

                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = $"#{idn.BotId} {idn.Name}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.92 0.92 0.92 1" },
                    RectTransform = { AnchorMin = $"0.03 {yMin}", AnchorMax = $"0.24 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = $"{idn.Persona}  grp{idn.GroupId}", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 1" },
                    RectTransform = { AnchorMin = $"0.25 {yMin}", AnchorMax = $"0.42 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = $"{(stuck ? "STUCK · " : "")}{status}{act}{next}   d{rec.State.Deaths}/k{rec.State.Kills}  ♥{Mathf.Max(0, LivesFor(rec) - rec.State.WipeDeaths)}", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = statusColor },
                    RectTransform = { AnchorMin = $"0.43 {yMin}", AnchorMax = $"0.62 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.ui.detail {idn.BotId}", Color = "0.35 0.32 0.5 1" },
                    Text = { Text = "Eco", FontSize = 10, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = $"0.635 {yMin}", AnchorMax = $"0.705 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.tp {idn.BotId}", Color = "0.25 0.35 0.5 1" },
                    Text = { Text = "TP", FontSize = 10, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = $"0.71 {yMin}", AnchorMax = $"0.775 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.ui.session {idn.BotId}", Color = "0.3 0.45 0.3 1" },
                    Text = { Text = rec.State.Status == "Online" ? "End" : rec.State.Status == "Dead" ? "Rsp" : "Start", FontSize = 10, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = $"0.78 {yMin}", AnchorMax = $"0.855 {yMax}" },
                }, UiPanel);
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.ui.retire {idn.BotId}", Color = "0.45 0.3 0.3 1" },
                    Text = { Text = IsOut(rec) ? "Rev" : "Ret", FontSize = 10, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = $"0.86 {yMin}", AnchorMax = $"0.96 {yMax}" },
                }, UiPanel);
            }

            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = "Eco → per-bot economy + social (grudges, lives). ♥ = lives left this wipe. Console: ff.econ  ff.grudge  ff.kit  ff.story", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.55 0.55 0.55 1" },
                RectTransform = { AnchorMin = "0.03 0.015", AnchorMax = "0.97 0.05" },
            }, UiPanel);

            Oxide.Game.Rust.Cui.CuiHelper.AddUi(player, ui);
        }

        [ConsoleCommand("ff.ui.detail")]
        private void CmdUiDetail(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdminPlayer(player)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec != null) ShowBotDetail(player, rec);
        }

        [ConsoleCommand("ff.ui.tick")]
        private void CmdUiTick(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdminPlayer(player)) return;
            var rec = FindBot(arg.GetString(0, ""));
            if (rec == null) return;
            StepEconomyGroup(rec.Identity.GroupId); // force a tick, then re-render the page
            ShowBotDetail(player, rec);
        }

        // A eulogy that still carries assistant meta-chatter (Slagg's stone, 2026-07-24:
        // "Here's a short epitaph… --- Alternative versions…" + literal \" escapes).
        // Display paths fall back to the template for these; eulogize rewrites them.
        private static bool EulogyLooksBroken(string e) =>
            !string.IsNullOrEmpty(e) && (e.Contains("\\")
                || e.IndexOf("epitaph", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("alternative", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("here's a", StringComparison.OrdinalIgnoreCase) >= 0);

        // 7E backfill: graves placed before v0.13.3 predate the eulogy writer — this
        // writes the missing ones and REWRITES meta-polluted ones (staggered 3 s apart;
        // single-lane Ollama + budget).
        [ConsoleCommand("ff.grave.eulogize")]
        private void CmdGraveEulogize(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!SlmEnabled) { arg.ReplyWith("SLMEndpoint is empty — no writer available."); return; }
            var queue = new List<HeadstoneRec>();
            foreach (var h in _index.Headstones)
                if (h != null && !string.IsNullOrEmpty(h.Name) && (string.IsNullOrEmpty(h.Eulogy) || EulogyLooksBroken(h.Eulogy)))
                { h.Eulogy = ""; queue.Add(h); }
            if (queue.Count == 0) { arg.ReplyWith("Every grave already has its eulogy."); return; }
            arg.ReplyWith($"Writing eulogies for {queue.Count} grave(s), ~3 s apart — watch ff.slm; re-run to catch any failures.");
            for (var i = 0; i < queue.Count; i++)
            {
                var h = queue[i];
                timer.Once(1f + i * 3f, () =>
                {
                    if (h == null || !string.IsNullOrEmpty(h.Eulogy)) return;
                    string back = "", persona = "resident";
                    foreach (var rec in _bots.Values)
                        if (rec.Identity.Name == h.Name) { back = rec.Identity.Backstory ?? ""; persona = rec.Identity.Persona ?? "resident"; break; }
                    var killer = string.IsNullOrEmpty(h.Killer) || h.Killer == "the world" ? "the island" : h.Killer;
                    var life = GraveLife(h.Name).Replace("— their story —\n", "").Replace("\n", "; ");
                    var sys = $"You write graveside epitaphs for the residents of a Rust survival island. {h.Name} the {persona} has died for good, slain by {killer}. Their backstory: {back} Their life: {Trunc(life, 400)}";
                    SlmAsk(sys, null, "Write a solemn, slightly wry epitaph: 2 sentences, third person, under 40 words, plain text. Output ONLY the epitaph itself — no preamble, no alternatives, no quotes, no notes.", h.Name, eulogy =>
                    {
                        if (string.IsNullOrEmpty(eulogy)) return;
                        h.Eulogy = eulogy;
                        _indexDirty = true;
                    }, 80, 260);
                });
            }
        }

        [ConsoleCommand("ff.ui.forget")]
        private void CmdUiForget(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdminPlayer(player)) return;
            var rec = FindBot(arg.GetString(0, ""));
            var key = arg.GetString(1, "");
            if (rec?.Social?.Memories != null && rec.Social.Memories.Remove(key))
            {
                rec.Social.MemoryNames?.Remove(key);
                MarkDirty(rec.Identity.BotId);
            }
            if (rec != null) ShowBotDetail(player, rec);
        }

        [ConsoleCommand("ff.ui.story")]
        private void CmdUiStory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdminPlayer(player)) return;
            ShowStoryUi(player, Mathf.Max(0, arg.GetInt(0, 0)));
        }

        // The island-news tab: the story log, newest first, paged (CUI can't scroll).
        private void ShowStoryUi(BasePlayer player, int page)
        {
            const int per = 18;
            var total = _story.Count;
            var maxPage = Mathf.Max(0, (total - 1) / per);
            page = Mathf.Min(page, maxPage);

            Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, UiPanel);
            var ui = new Oxide.Game.Rust.Cui.CuiElementContainer();
            ui.Add(new Oxide.Game.Rust.Cui.CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.96" },
                RectTransform = { AnchorMin = "0.18 0.12", AnchorMax = "0.82 0.9" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = $"ISLAND STORY — page {page + 1}/{maxPage + 1}  ({total} events)", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = "0.95 0.9 0.75 1" },
                RectTransform = { AnchorMin = "0.03 0.93", AnchorMax = "0.6 0.99" },
            }, UiPanel);
            if (page > 0)
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.ui.story {page - 1}", Color = "0.3 0.35 0.45 1" },
                    Text = { Text = "‹ Newer", FontSize = 11, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = "0.6 0.94", AnchorMax = "0.7 0.99" },
                }, UiPanel);
            if (page < maxPage)
                ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                {
                    Button = { Command = $"ff.ui.story {page + 1}", Color = "0.3 0.35 0.45 1" },
                    Text = { Text = "Older ›", FontSize = 11, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = "0.71 0.94", AnchorMax = "0.81 0.99" },
                }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui", Color = "0.3 0.35 0.45 1" },
                Text = { Text = "Roster", FontSize = 11, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.82 0.94", AnchorMax = "0.92 0.99" },
            }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui.close", Color = "0.55 0.2 0.2 1" },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.94 0.94", AnchorMax = "0.985 0.99" },
            }, UiPanel);

            var start = total - 1 - page * per;
            for (var i = 0; i < per; i++)
            {
                var idx = start - i;
                if (idx < 0) break;
                var ev = _story[idx];
                if (ev == null) continue;
                var col = ev.Type == "permadeath" || ev.Type == "death" ? "0.85 0.5 0.5 1"
                    : ev.Type == "kill" ? "0.9 0.65 0.45 1"
                    : ev.Type == "arrival" || ev.Type == "return" ? "0.5 0.85 0.5 1"
                    : "0.78 0.78 0.72 1";
                var yMax = 0.9f - i * 0.048f;
                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = $"Day {ev.Day} · {ev.Text}", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = col },
                    RectTransform = { AnchorMin = $"0.03 {yMax - 0.045f}", AnchorMax = $"0.97 {yMax}" },
                }, UiPanel);
            }
            Oxide.Game.Rust.Cui.CuiHelper.AddUi(player, ui);
        }

        // G3 — per-bot economy detail: fills the roster's reserved economy section with
        // the group's live figures (stage, career, scrap, kit, shop) and its next action.
        private void ShowBotDetail(BasePlayer player, BotRecord rec)
        {
            var idn = rec.Identity;
            var gid = idn.GroupId;
            var ge = Econ(gid);
            BotRecord holder;
            var b = GetGroupBase(gid, out holder);
            var buckets = b != null ? FindBaseContainers(b) : null;

            Oxide.Game.Rust.Cui.CuiHelper.DestroyUi(player, UiPanel);
            var ui = new Oxide.Game.Rust.Cui.CuiElementContainer();
            ui.Add(new Oxide.Game.Rust.Cui.CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.96" },
                RectTransform = { AnchorMin = "0.24 0.16", AnchorMax = "0.76 0.86" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);

            ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
            {
                Text = { Text = $"#{idn.BotId} {idn.Name} — {idn.Persona}  (group {gid})", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = "0.95 0.9 0.75 1" },
                RectTransform = { AnchorMin = "0.04 0.9", AnchorMax = "0.8 0.97" },
            }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui", Color = "0.3 0.35 0.45 1" },
                Text = { Text = "‹ Roster", FontSize = 12, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.72 0.9", AnchorMax = "0.86 0.97" },
            }, UiPanel);
            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = "ff.ui.close", Color = "0.55 0.2 0.2 1" },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.88 0.9", AnchorMax = "0.96 0.97" },
            }, UiPanel);

            var skill = idn.Traits?.SkillLevel ?? 0.5f;
            var cap = Mathf.Clamp(Mathf.FloorToInt(skill * (MaxKitTier + 1)), 0, MaxKitTier);
            var lines = new List<string>
            {
                $"<color=#8fb5ff>Career produced</color>   {ge.CareerProduced}",
                $"<color=#8fb5ff>Scrap (currency)</color>   {ge.Scrap}",
                $"<color=#8fb5ff>Kit tier</color>   {rec.Progression?.KitTier ?? 0}   (earned {ComputeKitTier(rec, ge)}, skill {skill:0.00} caps at {cap})",
                $"<color=#8fb5ff>Spare kit banked</color>   {(ge.SpareKitTier < 0 ? "none" : $"tier {ge.SpareKitTier}")}",
            };
            if (b == null)
                lines.Add($"<color=#8fb5ff>Base</color>   none yet");
            else
            {
                lines.Add($"<color=#8fb5ff>Base</color>   stage {b.Stage} at {GridRef(b.Origin.V)}   {(IsObserved(b.Origin.V) ? "<color=#7fd67f>watched</color>" : "unwatched")}");
                var filled = 0; var total = b.Anchors?.Count ?? 0;
                if (b.Anchors != null) foreach (var a in b.Anchors) if (a.Filled) filled++;
                lines.Add($"<color=#8fb5ff>Interior</color>   {filled}/{total} anchors furnished");
                if (buckets != null && buckets.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in buckets)
                    {
                        var used = 0; var capc = 0;
                        foreach (var sc in kv.Value) { used += sc.inventory.itemList.Count; capc += sc.inventory.capacity; }
                        parts.Add($"{kv.Key} {used}/{capc}");
                    }
                    lines.Add($"<color=#8fb5ff>Containers</color>   {string.Join("   ", parts)}");
                }
                List<IItemContainerEntity> shops;
                if (buckets != null && buckets.TryGetValue("shop", out shops) && shops.Count > 0)
                {
                    var vm = shops[0] as VendingMachine;
                    lines.Add($"<color=#8fb5ff>Shop</color>   “{(vm?.shopName ?? "unnamed")}”   {(vm?.sellOrders?.sellOrders?.Count ?? 0)} orders");
                }
            }
            lines.Add($"<color=#ffd98f>Next action</color>   {DescribeNextAction(holder ?? rec, b, ge, buckets)}");

            // 4F — social section: lives + who this bot holds against whom.
            var livesLeft = Mathf.Max(0, LivesFor(rec) - rec.State.WipeDeaths);
            lines.Add($"<color=#ff9f8f>Lives</color>   {livesLeft} of {LivesFor(rec)} left this wipe{(rec.State.Status == "Fallen" ? "   <color=#d67f7f>FALLEN</color>" : "")}");
            var soc = rec.Social;
            if (soc != null && (soc.Grudges.Count > 0 || soc.Reputation.Count > 0))
            {
                var gparts = new List<string>();
                foreach (var kv in soc.Grudges) gparts.Add($"{kv.Value.Name} ×{kv.Value.Kills}");
                foreach (var kv in soc.Reputation) gparts.Add($"{kv.Value.Name} (wary)");
                lines.Add($"<color=#ff9f8f>Grudges</color>   {string.Join(",  ", gparts)}");
            }
            else lines.Add($"<color=#ff9f8f>Grudges</color>   none");

            // Group rollup: siblings at a glance (economy above is already group-level)
            var members = new List<string>();
            foreach (var m in _bots.Values)
                if (m.Identity.GroupId == gid && m.Identity.BotId != idn.BotId)
                    members.Add($"{m.Identity.Name} ({m.State.Status})");
            if (members.Count > 0) lines.Add($"<color=#8fb5ff>Group</color>   {string.Join(",  ", members)}");

            // Phase 7: what this bot remembers about players (with a prune button each)
            var mems = rec.Social?.Memories;
            var memShow = 0;
            if (mems != null) foreach (var kv in mems) { if (kv.Value != null && kv.Value.Count > 0) memShow++; if (memShow >= 3) break; }

            var slots = lines.Count + (memShow > 0 ? memShow + 1 : 0);
            var step = Mathf.Min(0.075f, 0.72f / Mathf.Max(1, slots));
            var y = 0.85f;
            foreach (var line in lines)
            {
                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = line, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.88 0.88 0.88 1" },
                    RectTransform = { AnchorMin = $"0.05 {y - step * 0.85f}", AnchorMax = $"0.97 {y}" },
                }, UiPanel);
                y -= step;
            }
            if (memShow > 0)
            {
                ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                {
                    Text = { Text = "<color=#8fd6c8>Memories</color>   (what they've concluded about players — forget prunes a bad one)", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = $"0.05 {y - step * 0.85f}", AnchorMax = $"0.97 {y}" },
                }, UiPanel);
                y -= step;
                var shown = 0;
                foreach (var kv in mems)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    if (shown++ >= 3) break;
                    ui.Add(new Oxide.Game.Rust.Cui.CuiLabel
                    {
                        Text = { Text = $"<color=#8fd6c8>{MemoryKeyName(rec, kv.Key)}</color>  {Trunc(string.Join(" · ", kv.Value), 170)}", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.82 0.82 0.82 1" },
                        RectTransform = { AnchorMin = $"0.05 {y - step * 0.85f}", AnchorMax = $"0.8 {y}" },
                    }, UiPanel);
                    ui.Add(new Oxide.Game.Rust.Cui.CuiButton
                    {
                        Button = { Command = $"ff.ui.forget {idn.BotId} {kv.Key}", Color = "0.42 0.3 0.3 1" },
                        Text = { Text = "forget", FontSize = 10, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = $"0.82 {y - step * 0.75f}", AnchorMax = $"0.94 {y - step * 0.1f}" },
                    }, UiPanel);
                    y -= step;
                }
            }

            ui.Add(new Oxide.Game.Rust.Cui.CuiButton
            {
                Button = { Command = $"ff.ui.tick {idn.BotId}", Color = "0.3 0.45 0.3 1" },
                Text = { Text = "Force economy tick", FontSize = 12, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.05 0.04", AnchorMax = "0.35 0.11" },
            }, UiPanel);

            Oxide.Game.Rust.Cui.CuiHelper.AddUi(player, ui);
        }

        #endregion
    }
}
