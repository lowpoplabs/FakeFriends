# Changelog

## [0.21.2] — 2026-10-16 — retreat: honest "patrol" line, hysteresis, peacekeepers

Paul's brother read `[retreat] MaribelSook spotted a patrol — falling back to base`
with no heli up. "Patrol" was the label for any scientist-class NPC within the
50–90 m alarm radius (the heli says "patrol helicopter"), and the illmatic log showed
the same residents flipping in and out of cover every ~30 s for days — TrapperJoe
194 times and GrannyMags 378 times over six days, 1,556 "took cover from a patrol"
story beats this wipe. The alarm and the all-clear used the same radius, the all-clear
was scanned from the bed indoors while the alarm fired from the yard, and armed bots
were let back out under a scientist they can never fight (scientists are not
`PickFightTarget` foes), so a scientist parked at the edge of range re-armed the loop
every hold. Each cycle dropped the gather job and walked the shell.

- **The line names its cause.** `[retreat] X spotted a scientist (scientistnpc_peacekeeper, 64m)`
  — threat type, prefab short name and distance; the story beat keeps prose.
- **Hysteresis.** The all-clear scans from the yard anchor over 1.3× the alarm radius
  (`PatrolClearScale`): a threat must leave the wider circle before the bot steps out,
  and it must re-enter the narrower one to send him back in.
- **Non-threat scientists are ignored by the sense**: Outpost/Bandit peacekeepers
  (inside a `TriggerSafeZone`, which never leave it) and the plugin's own legacy-chassis
  `HostileNpc` (residents must not see through the Cobalt disguise on sight — the
  v0.10 doctrine). `IsScientist` itself is unchanged (monument-run scoring, kill names).
- **Door stays shut under any patrol-grade threat**: the step-out gate no longer lets
  `CanFight` excuse walking out under a scientist, heli or Bradley.
- **Story throttle**: one "took cover" beat per bot per 10 minutes.
- Watch: the next `spotted a scientist (…)` lines on both servers name what was actually
  parked near TrapperJoe/GrannyMags/Cinder/Soot/Slagg and MaribelSook.

## [0.21.1] — 2026-10-11 — zombies (scarecrows) respect the wildlife valves

A test server running Night Zombies reported its scarecrows hunting residents like
players. The `OnNpcTarget` guard was wildlife-only by construction: `IsWildlife`
covers `BaseNpc` and Gen2 `BaseNPC2` (minus scientists), and `ScarecrowNPC` is an
`NPCPlayer` — neither — so zombies fell through every valve, the camp breaker and
the fight gates. Verified on the live assembly (Oct 2026 build): scarecrows pick
targets only through `AIBrainSenses.GetNearest` (the `PlayerDetected` /
`TargetDetected` / `ThreatDetected` AI events all call it; `ScarecrowNPC.GetBestTarget`
returns null), where any non-null `OnNpcTarget` return drops the entity.

- **Scarecrows are wildlife now.** `IsWildlife` / `IsDangerWildlife` include
  `ScarecrowNPC`, so one change covers the lot: `AnimalsIgnoreSleepers` and
  `AnimalsIgnoreBaselessBots` exempt the logged-off body and the not-yet-built bot,
  `AnimalsIgnoreBots` is the master valve, `AnimalDamageToBotScale` applies, the
  wildlife camp breaker counts and clears a zombie parked on an anchor, anchor and
  respawn predator checks see them, and any armed bot stands and fights one on the
  wildlife rule (no 5A2-R PvP ladder for a melee humanoid). No new config key.
- Zombie kills land in the wildlife death bucket (`WildDeaths`, Grafana
  `wildlifeDeaths`) and the death line names the zombie by its display name.
- **Hook-conflict hygiene.** Night Zombies answers the same hook with `true` for its
  own exemptions; Oxide compares value-type hook returns with `Equals` and logs a
  "conflict between plugins" warning whenever two subscribers disagree — at this
  hook's ~1,300/s hunt cadence that would drown the log writer. The scarecrow branch
  therefore returns a shared boxed `true` (the wildlife branch keeps the boxed `0f`
  that the `BaseNpc` / Gen2 `IOnNpcTarget` sites need). Still allocation-free.
- Open: **Better NPC** has its own targeting code and likely needs an
  `OnCustomNpcTarget`-style exemption rather than `OnNpcTarget`; waiting on its
  source (PLAN.md §Third-party NPC compatibility).

## [0.21.0] — 2026-10-02 — interior navigation on RustNav (decision 0011)

Phase 1 shipped 2026-10-02 (door prep):

- **Bot shell doors are NPC-openable in the navmesh.** `MarkBotDoor` sets
  `canNpcOpen = true` and re-asserts the aperture (`RustNavDoorGates.OnDoorToggled`)
  on every door the base owns: at paste, when the door warden re-hangs one, on every
  5-minute warden pass (idempotent) and in a one-shot sweep at load. A closed bot door
  is now `NpcOpenable` rather than `Blocked` to a doors-allowed path query, which is
  what Phase 2's walk-in will use. Player doors are never touched; the engine's own
  door trigger still refuses locked doors, so nothing but FakeFriends opens them.
  No-op under `-useOldNavmesh`.
- `ff.base.doors <bot>`: per-door nav-gate state (open/locked/canNpcOpen/
  npcOpenable/navGate/swingLeaf) plus the anchor→bed and anchor→storage paths on the
  live mesh, doors allowed vs off. The load sweep logs the same path line for one base
  as proof the mesh agrees.
- Watch line (IN-1.3): the warden logs (1 h throttle per group) and closes any owned
  door found open with no group member within 8 m.

Phase 2 shipped 2026-10-02 (the walk primitive):

- **Interior walk.** `StartInteriorWalk` plans a doors-allowed path (`PlanPathTo(…, allowNpcDoors: true)`,
  goal snapped to the floor) to an interior anchor; while a walk is active it owns the
  bot's tick (chat, visits, work re-planning and pauses stand aside; fights, threat
  sense and session end still preempt it). The nearest closed own door swings open
  within 2.5 m and shuts once he is 3 m past it or has arrived; indoor pace caps at
  2.5 m/s. On arrival he faces the anchor and dwells 5–10 s, then walks back out to the
  exterior anchor and the door closes behind him.
- **Fallback is today's hop**, always: no complete plan, a stall (< 1 m in 4 s), a 25 s
  leg, a short arrival (> 1.5 m) or preemption → teleport to the anchor (inbound) or
  outside (outbound), door shut, reason counted. `metrics.json` gains `interior`
  (walks / completed / fallbacks / reasons) for Grafana.
- Harness only this release: `ff.base.walkin <bot>` and the one-shot marker file
  `data/FakeFriends/probe_walkin.json` walk a bot in to its bed and out. No production
  flow is converted yet (Phase 3). `GetShelterPoint` takes a purpose (bed/storage).

Phase 3 shipped 2026-10-02 (the production flows walk):

- **Deposit** (`DepositAndDuckInside`): home with a haul, the bot walks in to the
  storage anchor, stows the loot there, lingers 5–10 s and walks back out before the
  next gather target; the work watchdog deadline stretches to 90 s for the trip.
- **Hole-up** (`RetreatArrived`): reaching the anchor under threat now walks in to the
  bed; `Sheltered` is set on arrival (and the "holed up" story beat fires then). When
  the yard is clear he walks back out. "Ducked straight inside" (camped yard) keeps
  the instant hop — speed is the point there.
- **Session end / start**: at the anchor he walks in to the bag and turns in there
  (`FinalizeSessionEnd` on arrival); waking, he walks out through the door
  (`TryWalkOutOnWake`) instead of appearing in the yard.
- **Hostile and runner depots** (`HostileDriver` / `RunnerDriver`): at the anchor the
  driver flips `canOpenDoors` on, plans the aperture with `BaseNavigator`, the plugin
  opens/closes the door on proximity (`DriverDoorPass`), the take is stashed inside and
  he walks back out; `canOpenDoors` goes off again so roam legs never route through the
  house. A walked-out runner is not teleported by `FinishMonumentRun`.
- **Visitor doorstep**: a Roleplayer calling on a player base now walks to that base's
  nearest door, 2 m outside it (player doors stay Blocked, so he stops there); the old
  12 m ring is the fallback.
- Every conversion keeps the teleport hop as the fallback (counted by reason in
  `metrics.interior`); under `-useOldNavmesh` nothing changes.

Phase 4 shipped 2026-10-02 (hardening):

- **Save-in-flight retry.** RustNav defers door-aperture writes while the navmesh is
  being saved, so a plan can come back partial for a few seconds. Residents park in a
  pending walk state and retry once after 3 s before hopping; the drivers gate their
  depot walk the same way. Reasons `noPathIn-afterSave` / `noPathOut-afterSave` show
  when the retry didn't help.
- **Beast at the door.** A danger animal within 6 m when the walk would start means no
  choreography: he hops and the door never opens (`beastAtDoor`). The door already
  shuts the moment he is through. `metrics.combat` gains `wildlifeDeathsIndoors` and
  `wildlifeDeathsYard` (since load) for the follow-in soak.
- **Upper floors keep the hop** (`upperFloor`) until the stage-2/3 shells are read:
  every stage ≥ 2 paste now logs its own mesh fit 45 s later (anchor heights above the
  yard, doors-allowed path per anchor), so the first s2/s3 of the wipe self-reports.
- Review pass over Phases 1–4; decision 0008 carries its superseding note.


## [0.20.2] — 2026-10-02 — residents walk again on Facepunch's new navmesh (RustNav)

The October 1 Livestock update replaced Unity's navmesh with Facepunch's own
Recast-based **RustNav** and made it the default (`-useOldNavmesh` restores the
old bake; the live server runs stock args). `Bootstrap` no longer bakes the Unity
terrain or monument meshes under the default, so every `UnityEngine.AI.NavMesh`
query answered nothing map-wide after the 07:02 wipe restart. `FFNavProbe`
(throwaway, in repo) confirmed it live at 13:35: all 7 online residents sat on
spots where Unity sampling failed and RustNav returned a complete path, and the
residents' planner only asked Unity — wander, gather walks, visits and
close-on-home all failed the same way (retreats still beelined).

- **Fixed:** every static navmesh query now goes through
  `Rust.Ai.Gen2.RustNavMeshHelpers` (`NavSample` / `NavSampleTyped` shims +
  `PlanPathTo` on `RustNavMeshPath`), which dispatches to whichever mesh the
  server booted with — the plugin works unchanged under `-useOldNavmesh`.
- Agent-typed queries (the Animal/Humanoid bake split of decision 0008) only
  exist on the Unity mesh; under RustNav `TerrainAgentTypeId` short-circuits and
  the typed shim drops the filter — RustNav is one surface for terrain,
  monuments and player buildings, so the runner's crate reach check and stage
  point search work without the re-type dance.
- Grafted bodies (hostiles, runners) no longer enable the raw Unity
  `NavMeshAgent` under RustNav (Facepunch's own `TryEnableInternalUnityAgent`
  is a no-op there); `RustNavMeshAgent` binds to RustNav directly.
- Behaviour that is new by construction: RustNav bakes building blocks and
  open doorways as walkable, so a resident's path may now cross an open base
  floor. Closed doors stay solid (`allowNpcDoors` is off for residents).


## [0.20.1] — 2026-09-12 — reloads no longer kill a bot whose recorded engine id went stale

- **Fixed:** `ReconcileEntities` now runs two passes: bodies are matched by the
  recorded engine id first, then any roster record still unmatched reclaims a
  body **by name**, whether its recorded id was 0 or non-zero but stale. Before,
  only id-less records were name-eligible, so a stale id meant the real body was
  killed by `KillStrayBots` as a "duplicate by name" and the bot dropped to
  Unspawned until its next scheduled session (Soot and LootGoblinTV each lost
  ~4 h to the two v0.20.0 reloads; the September 7 restart took five bodies).
- Reconcile logs which path fired (`relinked … recorded id X was STALE, body is Y`,
  `not found and no body by name`), and the stray-kill pass names every body it
  removes with its id and sleep state, so the next reload shows how an id drifts.

## [0.20.0] — 2026-09-12 — career measures hours worked, not units hauled

Day-9 review of the September wipe (report):
every group reached stage 2 by day 6 and six were at stage 3 by day 8 against
targets of day 7 and day 14; the two-Smelter duo (Cinder + Soot) crossed 834k
career by day 9, 6.5× the pace line. Career counted every gathered unit, so bulk
personas earned 1.6× a Roleplayer's career per hour, Farmer crops credited career
with nobody online (~1/3 of Farmer career), and a duo's union of sessions doubled
the group's hours on the clock.

- **Time-based career.** New per-profile `CareerPerMinute` (default 60 → 3,600 an
  hour at skill 1.0, ×0.5–1.5 by the holder's skill) is credited in the economy
  tick whenever a member is online — abstract, embodied, mid-run or mid-prowl
  alike. Gather rules, embodied deposits and runner hauls no longer credit units;
  item deposits into the boxes are unchanged. Every persona now paces the same at
  equal hours and skill. Replaces the v0.16.0 Hostile-only `CareerPerHour`
  retainer (an old key in `economy_profiles.json` is ignored; pre-0.20 profiles
  get `CareerPerMinute` injected and persisted at load).
- **Crops no longer credit career offline.** Planters still grow and deposit
  around the clock; the ledger only moves while someone is on the clock.
- **Duo scaling.** The group's career rate is divided by √(members on the
  roster), so a duo earns ~1.4× a solo's career rather than 2×.
- Kept as bonuses on top: hostile kill bounty (`HostileBountyCareer` 1500) and
  the runner's 150 per real crate.
- Expected pace with week-1 WipeHype: stage 2 ~day 5–8, stage 3 ~day 12–19
  across the skill spread. Thresholds untouched (100k / 250k); revisit after a
  week of data.

## [0.19.1] — 2026-09-03 — September update compatibility

- **Fixed:** Compiles again on the September 3, 2026 Rust update (build 2633.288): the game removed `BaseEntity.SetFlag`, so entity flags are now set through the new flags-update scope with a network update, exactly what the old call did. No behaviour change. Three sites: switching a bot's boombox on, vending-machine broadcast toggles, and the lock on a bot base door.
- Requires the game's September 3 build; older servers will not compile this version.

## [0.19.0] — 2026-08-31 — bots visit players + hostile taunt prowls (interaction arc I-3)

The inversion of attend-on-approach: instead of waiting to be stumbled upon
on a 4700 map, the bots come to you.

- **Roleplayer visits** (`BotVisits`, default on): an online Roleplayer
  occasionally (`VisitCooldownMinutes`, avg 120 ±50%) walks to the nearest
  real player TC within `VisitRangeMeters` (600), waves at the door
  (gestures, a `Visit.Roleplayer` chat line if the owner is online), and
  leaves a pamphlet + one unit of something it actually sells, pulled from
  its own base stock (decision 0005 — real goods; no stock, no gift). The
  trip is ordinary wander-state movement (full-path or ~120 m legs with a
  beeline-home fallback), so retreat, fights, and session end preempt it
  like anything else. Owner offline is fine — finding the gift later IS the
  story. Our own owner-stamped TCs and RustQuests compounds are excluded.
- **Hostile taunt prowls** (`HostileTaunts`, default on): a prowling
  hostile's roam leg occasionally routes past an ONLINE player's TC (within
  1.5× its patrol ring; per-bot `HostileTauntCooldownMinutes`, avg 45) and
  drops one `Taunt.Hostile` line on arrival. The 5D privilege guard still
  keeps the body out of the base.
- Admin roster shows the new activity ("off visiting a player base" / "at a
  neighbor's door" / "walking home from a visit"); story log gets `visit`
  beats for departures, gifts, and cased bases.

## [0.18.0] — 2026-08-31 — corridor anchors, hamlets, persona re-mix (interaction arc I-0/I-1/I-2)

Wipe-week groundwork for the density problem: next wipe's anchors land where
players actually travel. Scope locked with the owner 2026-08-31 (rail-rider
dropped; RustQuests/Public-Works caveat resolved).

- **Corridor anchors** (`AnchorCorridorBiasMeters`, default 150): the anchor
  search prefers home spots within range of a road/rail spline (cached,
  subsampled `TerrainMeta.Path` Roads+Rails point cloud) and falls back to
  the first valid scattered spot only when 800 tries find no corridor-side
  ground. Custom maps without splines behave exactly as before.
- **Hamlets** (`HamletCount` 2, `HamletSlots` 3, ring 80–150 m): the first
  corridor-side settlers found up to two hamlets; later non-Hostile groups
  anchor on a ring around the founder (the 200 m keep-apart is waived for
  hamlet-mates; a 60 m floor, safezone/monument/footprint/predator checks
  all still apply). Membership lives on the index, clears at wipe, and slots
  free when a group retires or falls. Hamlet #1 is the **market**: gold map
  marker (grave-pin pattern: global broadcast, runtime-only, warden-kept)
  and its member shops broadcast real vending pins even while
  `ShopBroadcast` keeps everyone else off the map.
- **Persona re-mix** (owner call): default + live `PersonaWeights` now
  target 2 Smelter / 1 Farmer / 1 Trapper / 1 Runner / 4 Roleplayer /
  6 Hostile; `MaxConcurrentHostiles` 2 → 6 — the whole squad can prowl,
  retiring the slot lottery that starved Nils_44 (WH-4). Live roster
  reshape = the owner's retire/revive/fill console pass (see PLAN.md I-0).

## [0.17.0] — 2026-08-23 — rumor lines + smelter trim (big-map interaction, week-1 data)

the owner's 4700-size maps spread 15 bots too thin to stumble across:
`conversationsStarted` was **0** for the whole wipe week. Meanwhile smelter
groups held the top THREE career slots (684k/471k/379k vs best non-smelter
349k) — progression healthy, smelters a little fast.

- **Rumor lines** (`RumorLines`, default on; `RumorIntervalMinutes`, avg 50
  ±50%): every interval, while a real player is online, one online bot
  gossips a grid-ref rumor about ANOTHER bot into global chat — stocked
  shops, growing bases, lit smelters, buskers out playing, prowling
  hostiles. All generated from live state so rumors are never stale; a grid
  square (~146 m) invites a trip without giving the camp away (the
  secret-shop philosophy survives). Per-subject cooldown 3× interval; new
  `Rumor.*` pools ship via Lang like every other chat pool.
- **Smelter trim**: bulk ore gather −20% (stones 25→20, metal.ore
  33.33→27, sulfur 25→20/min). Career credit follows production, so this
  directly slows their stage pace; wood/HQM/crude untouched.
- Interaction arc planned in PLAN.md (corridor anchors, hamlets, bot
  visits, rail-rider probe) — pending the owner's cross-plugin re-think with
  RustQuests + Public Works.

## [0.16.4] — 2026-08-23 — crafting starved the repair economy

the owner found EchoRose's base with a missing front door and battle scars and
asked why the healing systems hadn't caught it. A live probe (FFBaseProbe)
showed the truth: the heli attacks of 8-22 destroyed her `door.hinged.metal`
and chewed the locker (243/500) and vending machine (938/1250); the shell
itself was fine (28/28 blocks, 500/500, TC fully fed — no actual decay).
The door warden had known about the door all along and skipped it **every
5 minutes for a day, silently**: rebuilding costs 150 metal.fragments from
the base boxes, and the boxes held 31 — while 237 frags sat in the TC it
wasn't allowed to touch. The famine was self-inflicted: her shop had never
sold a single item (`HadFirstSale: false`), yet `CraftGoods` kept converting
every fragment into curios — 8 guitars, 7 lanterns, 5 picture frames banked
in the locker — with no stock cap and no floor.

- **Craft stock cap** (`CraftStockCap`, default 4): a craft rule stands
  down while the base holds ≥ 4× its yield unsold (counted across locker,
  storage, and shop). Sales drain the stock, crafting resumes.
- **Repair-material reserve** (`CraftMaterialReserve`, default 350):
  crafting never dips wood/stones/metal.fragments below the reserve — the
  door warden's most expensive repair (350-wood double door / 300-frag
  garage door) is always affordable once production has banked it. HQM is
  exempt (accrues too slowly for a flat floor to ever clear).
- **Door warden draws TC surplus**: stock above the profile's upkeep
  target counts toward and pays for door rebuilds (boxes first, TC
  surplus for the shortfall). EchoRose's door re-hung on the first pass
  after deploy (31 boxes + 137 surplus ≥ 150).
- **The can't-afford skip is no longer silent**: `[door] group N: …
  still breached — can't afford X mat (boxes A, TC surplus B)`, throttled
  to once per 6h per group.

Not-a-bug findings recorded during triage: the "empty doorway" in every
s2/s3 blueprint is the vending-machine mount, not a lost door; block-level
"decay" was misread heli damage to deployables, which nothing heals yet
(queued in PLAN.md).

## [0.16.3] — 2026-08-21 — bot shops: broadcast policy for every machine

Havoc_77's vending machine was pinned on the map despite `ShopBroadcast =
false`. A freshly deployed vending machine **broadcasts by default** (prefab
flag), and the only steady-state enforcement lived inside `ServiceShop`
*after* its `profile.Sell.Count == 0` early return — so no-Sell personas
(Hostile) never had their machine corrected; only the one-shot load sweep
did, and Havoc's stage-2 shop was placed after the last plugin load.

- New `ApplyShopBroadcast(vm)` helper (idempotent SetFlag Reserved4 +
  `UpdateMapMarker`), used in three places: at shop placement in
  `FillAnchor` (fixed the moment the machine exists), in `ServiceShop`
  before the no-Sell early return (every bot shop, every economy pass), and
  in the load-time `EnforceShopBroadcast` sweep.
- Hot-reloading this version runs the sweep, which un-pins Havoc's machine
  immediately.

## [0.16.2] — 2026-08-19 — safezone anchors: the sleeper-kill death loop

EchoRose anchored half-inside the AA10 safezone and died **144 times** —
"killed by the world (Suicide 999)" every ~25 minutes, a die → respawn →
re-gear → sleep → die loop (plus constant "took cover from a patrol" spam
from the zone's scientists). Rust executes sleepers inside safezone
triggers, and an offline bot is a sleeper at its anchor. `FindAnchorSpot`
only rejected spots within 100m of a monument *centre*; safezone triggers
(Outpost, Bandit, fishing villages) reach well past that.

- **Anchor search rejects safezones**: new `InOrNearSafeZone` check against
  `TriggerSafeZone.allSafeZones` trigger-collider AABBs — a candidate spot
  must keep its whole footprint `AnchorSafeZoneMargin` (new config,
  default 30m) clear of every zone. AABB distance rather than
  `ClosestPoint` since trigger colliders aren't guaranteed convex; the
  slightly generous box errs in the only safe direction.
- **Self-heal for existing casualties**: `EnsureAnchor` now validates a
  bot's stored home on every session start/respawn — an anchor inside a
  zone gets its base swept and the whole group's anchor re-rolled, no
  admin action needed. EchoRose relocates on their next respawn.

## [0.16.1] — 2026-08-16 — default wipe cast: order is the roster

the owner's progression-test wipe surfaced the old 6-hostile roster again: the
live `cast.json` (20 entries, legacy hostiles GreyMarrow/Nils_44/Kestrel/
DeadAir still in the middle) persists across wipes, and `FillRoster`
consumes entries top-down until MaxBots (15) — so entry order IS the wipe
roster. The furnace-brothers duo sat last and got split/dropped by the cap.

- **DefaultCast reordered**: first 15 = 2 Trapper, 2 Farmer, 2 solo Smelter,
  the furnace-brothers duo (early, so the remaining-slots cap can never
  split it), 2 MonumentRunner, 3 Roleplayer (EchoRose, Strummer,
  PatchesQuinn), 2 Hostile (Vex, Havoc_77). Entries past 15 are the bench —
  permadeath replacements in order; Moth benches at 16.
- **Live cast.json reordered to match** (entries preserved verbatim, legacy
  hostiles moved to the bench tail; `.bak-20260816` beside it). Migration:
  `ff.retire` the four legacy hostiles, then `ff.roster.fill` pulls the duo
  + Strummer + PatchesQuinn into the opened slots.

## [0.16.0] — 2026-08-15 — the economy pass: credit work, wipe hype, the retainer

Day-9 production review (report):
8 of 15 active groups at stage 2 against a goal of all-by-week-1, zero near
stage 3, and every curve flattening the same way. Three causes, three fixes:

- **Career credits the work, not the storage**: `RunGatherRules` credited only
  units that physically fit in a base box — overflow was destroyed uncredited,
  so every group froze as its stage-1 boxes filled (RustyBill: three straight
  0-production days; Krupp: 26k/day → 2k/day, parked 300 career short of
  stage 3). It was also a deadlock — the storage that would unfreeze career
  comes from the stage the frozen career can't reach. Now the rolled units
  always credit; what fits is deposited, the surplus is hauled off in bulk.
- **Wipe-hype sessions**: real low-pop players no-life week 1 and taper.
  Session length now scales by wipe day — ×1.5 days 1–7, ×1.25 days 8–14,
  ×1 after — applied to residents and hostiles alike. The flat 2–4h casual
  pace couldn't carry most personas to 100k inside week 1 even loss-free.
- **The Cobalt retainer**: hostiles' career math never closed (lowest gather
  rate, 500/kill bounty ⇒ 200 kills for stage 2). New per-profile
  `CareerPerHour` (Hostile: 2000) pays salaried career while on the clock —
  the employer funds the compound — and `HostileBountyCareer` rises to 1500
  (code default + live config + live economy_profiles.json).

## [0.15.3] — 2026-08-15 — harassment camps trip the camp-breaker

Same-day follow-up: Strummer (flute, no gun) showed STUCK in the admin panel —
a boar owned his yard all morning, cycling him through 25-second melee-defend
hunts he could neither win nor escape (six in three minutes at one point). The
camp-breaker never fired because it counts *deaths*, and the boar wasn't
killing him — harassment camps were invisible.

- **WC-2**: defend-path triggers against danger wildlife near the anchor now
  count toward the camp in their own ring, at 4× the death threshold inside
  the same window — a passing animal that bites once never reaches it, a
  yard-owner does within the hour. Unobserved-only cleanup (players watching
  a boar chase a busker is content, not a problem); no respawn-hold branch
  since the bot is alive. Shared `RemoveCampingWildlife` tail with the death
  ring.

## [0.15.2] — 2026-08-15 — armed bots actually win animal fights

Week-2 story-log + Grafana verdict on the wildlife arc: the curve bent (223→~7
wildlife deaths/day) but wildlife was still 64% of all deaths, and "armed" was
a fiction — 121 of 152 gun engagements opened "stood his ground" and closed
"ran out of ammo" a median **1 second** later, while the three Hostile-profile
bots (Vex 15/15 deaths to animals, Havoc_77 12/16, Nils_44 7/7) died at their
own anchors with zero response of any kind.

- **Cobalt fights wildlife** (`BeastTick`): the HostileDriver's combat brain
  only knew humans — `lastAttacker as BasePlayer` casts a boar to null and the
  target scan covers only player lists. Hostiles now retaliate against and
  engage danger animals with the same stand-and-shoot doctrine residents use
  (no chase, no flee valve, depot resupply when dry, 60s re-engage suppression
  after a give-up). Human targets still preempt.
- **Ammo is a resource again — for everyone** (`ResupplyKitAmmo`): ammo only
  ever entered the world through `GiveKit`, so a bot that *won* its fights went
  permanently dry (GrannyMags: 25 engagements, 25 dry, 0 wins). The Cobalt
  retainer fiction now covers every persona: pockets top back up to the worn
  kit's AmmoCount at session start and while sheltered at home.
- **Dry guns don't pose** (`FightArmed`): fight entry and `CanFight` now
  require a live round (mag or pockets), so a dry bot takes the retreat path
  instead of one-second gun theater that cleared its retreat state while the
  animal kept the yard. The sheltered restock runs *before* the yard-clear
  check so a dry bot indoors can't deadlock.
- **Camp breaker catches slow camps**: `WildlifeCampWindowMinutes` default
  45 → 1440 (live config updated). The 45m window was tuned to Krupp's
  6-minute wolf metronome; a boar that kills once per session never tripped it
  (Havoc_77: 16 deaths at the same I12 anchor, breaker silent).

## [0.15.1] — 2026-08-08 — audible busking, readable (and frameable) pamphlets

Same-day fixes from the owner's first field test of v0.15.0:

- **Silent instruments**: the hardcoded pentatonic table sent octaves the
  instruments don't have, so `Client_PlayNote` played nothing. Notes now come
  from the instrument's own `KeyController.Bindings.BaseBindings` — the exact
  table the auto-turret path uses — so every note is valid for whatever the bot
  holds (flute, tambourine, trumpet, guitar). The melodic walk is unchanged,
  just over real bindings.
- **Pamphlet readability**: full redesign pass on all 10 zines — fonts up
  ~1.45× (plus the fractional sizes the first pass missed), rendered at 2×
  device scale (1708×960), photo-cache cap raised 256→512 KB. Every sheet
  visually verified against overflow/collision.
- **Frame-safe gutters**: the owner frames these (photo frames center-crop a 16:9
  photo to roughly 4:3, eating ~12% per side — his screenshots showed "EVERY"
  losing letters). All essential content now lives inside a 96px side gutter,
  and edge-pinned diagrams/stamps/footers moved inboard.

## [0.15.0] — 2026-08-08 — the engagement pass: catchable bots, busking roleplayers

Two-week soak verdict from the owner: bots outrun anyone trying to meet them, hostiles
outnumber the entertainment, and roleplayers don't *court* an audience. Three fixes:

- **Aware-speed** (`AwareSlowRadiusMeters`, default 40): the sprint-class cruise
  (owner call 2026-07-23) now only applies out of sight. With a real player inside
  the radius, wander/work/walk-home speed drops to a catchable, trait-shaped pace —
  `2.6 + 1.7·Caution − 0.9·Chattiness` m/s, clamped 1.8–4.4 — so wary bots stay
  brisk and social ones amble. Retreats, fights, hunts, and heli-flee keep full
  speed: fear is still fear.
- **Roster re-balance**: default `PersonaWeights` invert the hostile lean (Hostile
  0.2→0.1, Roleplayer 0.15→0.25). Two new authored Roleplayers join the cast ahead
  of the furnace brothers: **Strummer** (washed-up musician, four notes, no requests)
  and **PatchesQuinn** (pamphleteer; the seagulls are involved). Live rollout:
  retire 3 of the 6 hostiles and the refill seats Moth + both new characters.
- **Busking**: Roleplayers now attend-on-approach with or without the SLM, farther
  (`RoleplayerAttendRangeMeters`, default 15), longer (45 s), and with a shorter
  break (20 s) — and the attend is a *show*: they stop working, face you, throw
  gestures (candidate list validated at load against `GestureCollection`, 18
  candidates), hand a stamped pamphlet **into your inventory** within 5 m (drop
  beyond), and genuinely play their kit instrument — server-side
  `Client_PlayNote`/`Client_StopNote` RPCs, the same path auto-turrets use
  (verified against the live assembly 2026-08-08), walking an A-minor-pentatonic
  table on the flute/tambourine/trumpet the live kits already carry. Proximity
  antics also got eager: pamphlet band 6%→14% per 15 s tick, cooldown 30→15 min.

## [0.14.9] — 2026-08-01 — shell census: breached walls get an unwitnessed repaste

WG-4, the last gap in base self-repair. GreyMarrow's cabin sat missing walls for days:
doors rebuilt (0005 doctrine), interior anchors re-placed (0.14.6), but building blocks
had no census and no healer. Per-block wall rebuilding was rejected — sockets, building
IDs, and stability resettling are the moved-block-shear minefield — in favor of the
doctrine the economy already runs on: *fixed up while nobody watched*.

- Every paste now records the intact shell's building-block count (stage-1 blueprints:
  11 blocks; Slagg's stage-2: 28). Pre-census bases baseline whatever stands at the
  first warden pass, so an already-breached shell keeps its scars until its next paste.
- The 5-min warden counts standing owned blocks against the baseline. Short by
  `BaseRepairBlockDeficit` (default 3) **and** no real player within observe range ⇒
  same-stage repaste: stock carried over, rebuild fee from the group ledger, one repair
  per pass. Story beat: "patched the cabin back up after the attack".
- First census pass (18:11) baselined all 15 groups clean.

## [0.14.8] — 2026-08-01 — reload reconcile tucks sleepers into bed

Every hot reload was quietly stocking the island with wildlife bait. Reconcile's
settle-to-sleeper branch called `StartSleeping()` **wherever the entity stood**, so a
deploy day (~10 reloads on 2026-08-01) left mid-wander bots sleeping in their yards —
GrannyMags, Kestrel, and EchoRose were all found outdoors beside intact cabins, beds
fine (the 0.14.6 anchor check correctly found nothing missing). Outdoor sleepers are
free kills; this fed the death stats on every deploy day of the wipe.

- Reconcile now teleports a lapsed-session sleeper to its shelter point (bed → TC →
  base origin, same as a real logoff) before it starts sleeping.
- Self-healing by construction: deploying *is* a reload, so the fix's own deploy
  tucked in all 13 live bots.

## [0.14.7] — 2026-08-01 — never lead the heli home

Owner doctrine: a real player being hunted by the patrol helicopter hides in a monument
or eats the death — they don't sprint back to their wood shell and let the rockets
follow. Slagg did exactly that on 2026-07-29 ("took cover from a patrol helicopter" →
dead 22 s later under his own collapsing roof).

- A bot on a heli's actual target list (`PatrolHelicopter.myAI._targetList[].ply` —
  public API, verified against the live assembly) never retreats home. It flees in
  60 m legs biased away from **both** the heli and its base, re-planned per leg while
  the lock holds, with a beeline fallback off the navmesh.
- Lock broken (heli downed, distracted, or gone) ⇒ the normal walk-home-and-heal path
  resumes. A heli that's merely flying nearby still sends bots indoors, as before.
- Story beat: "broke for open ground, leading the patrol helicopter away from home".

## [0.14.6] — 2026-08-01 — base-integrity valve: lost bases get noticed, mourned, rebuilt

The soak week's death spiral wasn't wildlife aggression alone — a patrol heli had
**levelled several bot bases** and nothing in the plugin noticed. Slagg's base died
2026-07-29 20:46 (death #1: "the world", Blunt — the shell collapsed on him; group-5
career froze at 98,484 that exact minute), then a boar farmed the open anchor 40 times.
Krupp (313 deaths — 58% of the wipe's total), MaribelSook (96), and TrapperJoe shared
the fate: stale `TcNetId`s, `MaintainUpkeep`/`StepGroupEconomy` silently no-oping,
retreats "holing up" in phantom shelter.

- **5-min integrity warden** (rides the door-warden tick): a group's TC no longer
  resolving ⇒ surviving boxes salvaged into carryover, debris swept, base record
  dropped on the spot — so `hasBase`, the economy, and shelter checks all read honestly
  — plus a "home was destroyed" story beat and a persisted rebuild note
  (`BaseRebuildDelayMinutes` 45, `BaseRebuildScrapCostPerStage` 100 × stage, charges
  only what the ledger holds; retries blocked spots).
- **Interior anchors heal too:** TC alive but a Filled anchor's entity gone (heli
  splash, raid) ⇒ un-filled so the interior fill tick re-places it, bed and box first.
- **F1 auto-acquire composes:** a group with a pending rebuild note isn't a fresh
  settler — auto-acquire stands aside (the valve's first live pass raced it and handed
  groups 1/2/6 instant free cabins before the deferral shipped); a standing base clears
  any stale note.
- First live pass (17:42) detected all three dead-TC groups and re-housed them. Slagg,
  re-based, crossed 100k career at 17:47 — **the wipe's first organic stage-2 upgrade**.

## [0.14.5] — 2026-08-01 — PII scrub + rich-text sanitizer

Prep for sharing the plugin files outside the household, plus the one actionable
finding from the SLM chat security review:

- **Publishing alias.** Author in all three `[Info(...)]` attributes and the LICENSE
  is now **LowPopLabs**; personal usernames, the owner's first name, hostname, and
  LAN IP are gone from shippable code and README-linked docs. Git history rewritten
  to the alias (no remote existed). `.claude/` is now gitignored.
- **SLMEndpoint config key renamed** — the example IP in the `[JsonProperty]`
  description was a real LAN address; it's `localhost` now. Because the description
  *is* the config key, the live `FakeFriends.json` key was renamed in the same
  deploy (value untouched).
- **SLM output loses `<`/`>`.** `SlmSanitize` stripped markdown but not angle
  brackets, so a prompt-injected reply could smuggle Unity rich-text (`<color>`,
  `<size>`, fake `</color>SERVER:` prefixes) into global chat and CUI labels.
  Model output can't execute anything (verified: no path from SLM text to any
  command, spawn, file, or HTTP sink) — this closes the last cosmetic vector.
- New `/pii-scrub` session skill re-sweeps the repo before anything is shared.

## [0.14.4] — 2026-08-01 — a bot that fled a bear stays fled

WG-2 from the plan's soak-week queue, live-verified bug: RustyBill broke off a bear
fight at 4hp — "running for home" — then stood his ground against the same bear three
times inside a minute and died (death #4). Two holes, both closed:

- **Fight entry gets a health floor.** Entry required only a weapon; the flee valve
  fires at 30–60hp (caution-scaled) but nothing stopped a 4hp bot from *starting* a
  fight the valve would end on the next tick. Entry now requires health above the
  bot's own flee floor + 5.
- **Animal break-offs are sticky.** The per-target re-engage cooldown is keyed by
  `userID`, which animals don't have — so every give-up (flee, timeout, lost LOS)
  left the same bear immediately re-qualifiable via `RecentAttacker`. A non-player
  give-up now sets a 60 s `FightSuppressUntil` window so the retreat/hole-up path
  owns the next move. Also covers the full-hp posing loop against an unreachable
  animal after a timeout break-off.

## [0.14.3] — 2026-08-01 — the plugin learns Gen2 scientists exist too

The humanoid half of the decision-0010 Gen2 migration (this is the "0.14.1 parallel
audit" fix referenced below, renumbered on merge because 0.14.2 shipped first). A live
serverEntities census (probe:scicensus, 09:09) found the scientist population **mixed**:
198 old-gen `ScientistNPC` vs 104 `Rust.Ai.Gen2.ScientistNPC2` — Gen2 owns both oil
rigs plus 63 open-world roamers, old-gen holds the land monuments. Every typed
`ScientistNPC` check was silently blind to a third of the island's patrols, and since
`ScientistNPC2` is **not** a `BasePlayer` (old scientists are), `as BasePlayer` paths
never saw it either.

- **`IsScientist` predicate + `NearestScientist` scan** — the humanoid twin of
  `IsWildlife`, single source of truth; BaseCombatEntity sweep per the
  `NearestDangerAnimal` precedent.
- **Patrol threat sense** (`SensePatrolThreat`): workers/wanderers now flee Gen2
  patrols, not just old-gen ones.
- **Runner flee** (`Pursued`): a monument runner no longer "loses" a Gen2 pursuer the
  moment line-of-sight logic switches to the typed scan.
- **Monument risk census** (`PickRunMonument`): both generations counted — a
  Gen2-guarded monument no longer scores as undefended.
- **Fight-back parity** (`FightEligible`): a Gen2 scientist that opens fire gets the
  same 5A2-R stand-and-fight an old-gen one did via the BasePlayer path; full PvP
  gates still apply.
- **Naming** (`ThreatName`/`FightTargetName`): Gen2 reads "patrol"/"a scientist"
  instead of a prefab-name fallback.
- **scicensus probe** kept (one-shot, marker-gated, read-only) for post-update re-runs.

## [0.14.2] — 2026-08-01 — review pass: the defensive hunt survives its own bureaucracy

Fresh-eyes review of the 0.14.0 diff (report: docs/reports/2026-08-01-review.md), fixes
verified live. (0.14.1 is the Gen2-scientist parity fix from the parallel audit session —
merged separately.)

- **Defensive hunts no longer dismantled mid-fight.** `UpdateWorkActivity`'s 1 Hz
  observed/economic re-check cleared the work state — including an active `Hunting`
  phase — for unobserved bots and non-economic personas, so the new WA-2 fight-back
  restarted every threat tick without ever swinging (Fernwood vs the bear, 09:00: four
  starts, zero hits). An active hunt is now exempt; normal clearing resumes when
  `HuntStep` resolves it.
- **No stale story beats.** A deferred "holed up at home to heal" line set during an
  interrupted retreat could fire on a later, unrelated arrival — cleared at every
  retreat start.
- **Metrics canary.** The review chased a "dead metrics writer" for half an hour; the
  writer was fine — **SMB reads of metrics.json were 25+ minutes stale even through a
  lease-breaking open** (TimescaleDB, fed by Telegraf reading the file locally, showed
  an unbroken 60 s cadence and the auto-created `wildlifeDeaths` column). WriteMetrics
  now DLogs `first write ok` once per load so a genuinely dead writer is visible in the
  Oxide log; verification doctrine is "check the DB, not the share".

## [0.14.0] — 2026-08-01 — the plugin learns wolves exist, and armed bots stop running from boars

The TimescaleDB week-one dive (wipe eb67d3ec) found wildlife as the island's apex
economic force: 536 deaths, effectively all animal (368 wolf / 106 boar / 83 bear),
zero player or bot kills, and no group at the 100k stage-2 threshold seven days in —
progression near-perfectly inverse to wildlife pressure. Root cause was a class-hierarchy
migration: live wolves are `Rust.Ai.Gen2.Wolf2 : BaseNPC2`, **not** `BaseNpc`, so every
wildlife check in the plugin — the v0.13.17 sleeper/baseless exemptions, the anchor
predator veto, the respawn predator hold, the yard-clear check, fight-back targeting —
silently never saw a wolf. Krupp's wolf killed his *sleeper* every ~6 minutes for 43
straight minutes (career deaths: 311) with every protective layer no-opping.
Doctrine locked in decision 0010.

- **Gen2 blindness fixed everywhere.** New `IsWildlife`/`IsDangerWildlife` predicates
  (BaseNpc families + BaseNPC2 minus ScientistNPC2) are the single source of truth;
  every scan moved off `List<BaseAnimalNPC>`/`as BaseNpc`. Bonus finding: the old
  danger filter matched names "bear"/"wolf" over a list wolves could never appear in —
  it could only ever return bears. Boars (Slagg's E24 camper: 15 deaths, froze group 5
  at 98,484 career, 1,516 short of stage 2) are now danger wildlife too. The Gen2
  `SenseComponent.CanTarget` re-validates locked targets every tick, so the exemption
  now also breaks aggro already in progress.
- **Armed bots fight animals — the player-like answer** (WA, decision 0010). Against
  wildlife the fight gate drops to "holds a weapon + healthy" (`WildlifeFightMinKitTier`
  0); no aggression floor, no tier ladder — the old tier-2 gate made death loops
  self-sustaining because a respawn wears tier 0. PvP keeps the full 5A2-R gates.
  The defensive melee trade also lost its observed-only gate: wildlife kills bots
  unwatched, so unwatched bots fight back.
- **Retreat stops feeding the camper** (WB). If wildlife holds the yard and the bot
  won't fight it: near home he ducks straight inside (the last hop was a teleport
  anyway); far from home he puts distance first and re-plans home once the yard clears.
  The "holed up at home to heal" story beat now fires on *arrival* — Slagg's log
  claimed cover 20 seconds before each of his yard deaths.
- **Camp-breaker** (WC): 3 wildlife deaths inside 45 min within 60 m of the anchor
  declares a camp. Unobserved → the camping animals are quietly despawned (unwitnessed
  maulings aren't a story; neither is unwitnessed pest control). Observed → the victim's
  respawn holds 10 min instead: players may watch the wolf win. The respawn predator
  check also widened 30 → 60 m (`RespawnPredatorRadius`) — Gen2 senses outrange 30.
- **Valves** (WD): `AnimalsIgnoreBots` master switch (default OFF — visibly fake,
  emergencies only), `AnimalDamageToBotScale` dial (hook stays cold at 1.0), and
  `combat.wildlifeDeaths` in metrics.json so Grafana shows whether the curve bends
  during the soak week.

## [0.13.18] — 2026-07-30 — the Farmer forages, the Roleplayer deals in curios, and the defaults stop lying

Stage 1 is the gathering stage by design and stages 2–3 are where craft and sell come
online — but at stage 1 the Farmer's gather list was byte-identical to the Roleplayer's
(wood 43.8 / stones 25 / frags 12.5), so the persona had no identity in the stage bots
spend the most time in.

- **Farmers forage the wild island.** A new gather `Kind` — `"forage"` — is not
  planter-gated, so it works from day one: cloth off hemp, mushrooms, corn, potato,
  pumpkin, wild berries, and seed/cutting stock (`seed.hemp`, `seed.corn`,
  `seed.potato`, `seed.pumpkin`, `clone.hemp`). Shortnames verified against the live
  content bundle. The `"crop"` rules stay planter-gated and now carry real rates, so the
  farm proper wakes at stage 2 — and the seed stock stage 1 banks is what it runs on.
  Total holds at ~79 units/min against the old 81.4, deliberately, so the pacing
  baseline for the soak week doesn't move.
- **Roleplayers deal in atmosphere.** Forage cloth and bone, craft and sell
  instruments, lanterns, candle hats, party hats and picture frames. Both need a
  workbench and a vending machine, so they wake at stage 2 like every other trade.
- **Hostiles get no craft and no sell, deliberately.** A Cobalt raider's income is the
  wage and the bounty board. Their camp is worth raiding for what's in the boxes, and
  that's the whole trade.
- **`BuildDefaultProfiles()` regenerated from the live file, which is the source of
  truth.** It had drifted badly: no `Hostile` or `Roleplayer` entry at all and no
  `Trapper.Jungle`, and `ScrapPerHour` unset on everything but the runner. Because the
  data file is only ever written when absent, none of that showed on the live server — but a
  fresh install (a second server) would have run 7 of 15 bots with no economy and no
  wage, silently. Verified by deleting the live file, letting the plugin regenerate it,
  and diffing: the defaults now reproduce the live tuning exactly.

## [0.13.17] — 2026-07-30 — wildlife stops farming sleepers, and cabins stop landing in dens

Follow-on to 0.13.16. The death tax had a shape: not "bots die to animals" but "one
resident animal eats the same bot forever, and nobody is there to see it".

- **Animals ignore sleeping bots** (`AnimalsIgnoreSleepers`, default on). Sleeping is
  the logged-off state — Nils_44 logged six deaths and not one fight-or-flight tick,
  because a wolf was eating his sleeper at F16 about once a day for the whole wipe.
- **Animals ignore bots with no base yet** (`AnimalsIgnoreBaselessBots`, default on).
  The fight-or-flight path already refuses to hole up when there's nowhere to hole up
  in; this stops the hunt that the bot has no answer to. Group-aware, so a duo's
  non-holder isn't read as homeless.
  Both go through `OnNpcTarget`, verified against the live assembly: `AIBrainSenses.GetNearest`
  takes any non-null return as "skip this entity", `BaseNpc.GetWantsToAttack` takes a
  returned float as the attack desire, and a shared boxed `0f` satisfies both. The hook
  fires ~1,300x/sec in a hunt, so it's armed only while bots are live and the body is
  allocation-free — the "has a base?" question is precomputed on the 30s tick because
  `GetBase` deserializes a JToken per call. Players, bots and scientists are never
  exempt: deaths anyone could witness stay real.
- **Anchors avoid predator territory** (`AnchorPredatorRadius`, default 70m). Bears,
  wolves and boars hold ground, so an anchor drawn inside a den is a wipe-long sentence
  rather than one bad night — GrannyMags died to a wolf at Y5 ten times out of ten. The
  check runs last in the 800-try search, only for spots that already passed everything
  cheaper, and a final fallback tier drops it entirely: homeless is worse than hunted.

## [0.13.16] — 2026-07-30 — the death tax: cheaper respawns, a smarter rage-quit valve

Four days of wipe data (166 deaths across 15 bots, ~all wildlife) showed the roster
spending roughly a quarter of its entire productive budget lying dead or logged off
early. Career only accrues while a bot is Online, so every death is a direct tax on
progression — and the flat valve was compounding it.

- `RespawnDelayMinutes` 15 → 5. At 15m, 166 deaths cost ~41 bot-hours against a
  roster total of ~110 productive hours.
- The rage-quit valve is no longer a flat 2 deaths per sitting. It now ends the day on
  a hard cap (`RagequitDeathsPerSession`, default 3) **or** on a spiral — two deaths
  inside `RagequitSpiralMinutes` (default 30), the signature of something camping the
  anchor. Two deaths spread across a 4h session is bad luck and the bot plays on.
  GrannyMags logged 0.7 productive hours across four days under the flat rule: every
  one of her sessions ended inside half an hour to the same wolf at Y5.

Not changed, but found while looking (see the notes below the fold):
every stage-1 base capture contains only `storage`/`bed`/`furnace`, so crafting,
crops, shops, sales and spare kits have never been able to run on this server.

## [0.13.15] — 2026-07-26 — quest-shop check off the anchor hot path

- The quest-shop test now takes a cached snapshot of shop positions
  (`GetQuestShopPositions` + `GetQuestShopNoBuildRadius`, refreshed at most once a
  minute) and does the distance maths locally, instead of a cross-plugin `Call` per
  candidate. `SpotBlockReason` runs up to 800 times per anchor search and every bot
  re-anchors at once on the first boot after a wipe — the exact moment this fires —
  so the old shape boxed two arguments and allocated a params array on each try, in a
  path whose own comment promises to stay cheap. A one-minute-stale snapshot can't
  lag the world: shops place ~60 s after boot and the placer retries every 5 min.
  The cache is dropped, not stamped, while RustQuests is absent, so a later load is
  picked up immediately rather than after the TTL.

## [0.13.14] — 2026-07-26 — keep bot bases off the quest shops

- Residents no longer anchor or paste a base on top of a RustQuests trader shop. The
  shops are caboose *prefabs*, not building blocks, so neither `HasBlockingEntity` nor
  the World-layer sweep ever saw them. `SpotBlockReason` — the one check both anchor
  selection and per-stage pastes run through — now also asks RustQuests
  (`IsNearQuestShop`, soft `[PluginReference]`: not loaded, no restriction) using our own
  footprint plus a 15 m buffer, so a base lands clear of the zone rather than against it.
  Existing homes that predate a shop are handled by the standing relocation path.

## [0.13.7] — 2026-07-24 — Phase 7: the residents can talk (Roleplayer SLM + observability)

Endpoint stood up from bare metal (decision 0009:
Ollama on the live server's GTX 1060, `llama3.2:3b`, driver pinned to the 580 branch — 590+
dropped Pascal), gate probes P7-1/P7-2 PASS (report,
median round-trip 1.48 s from inside the plugin), then the whole arc v0.13.0→v0.13.7
across two live-feedback rounds. Review: 2026-07-24 report.

- **Conversations**: any resident (chattiness-gated; a grudge always stops to be cold)
  answers nearby players' chat in character — persona + backstory + traits + grudges in
  the prompt, the current conversation window as real context. The bot stops working,
  faces you, and attends; walking up to one does the same before a word is typed. Any
  damage breaks it off; when the window closes the bot summarizes ONE remembered
  sentence about you onto its record ("conclusions, not transcripts") and goes back to
  work for 5 minutes. Calling a bot's name reaches 2.5× earshot and beats distance.
  Bots never answer bots (structural), hostiles keep their cover, everything falls back
  to the template lines on ANY endpoint failure — `SLMEndpoint` empty = exactly the old
  behavior.
- **Grounding**: prompts pin true island facts, persona-specific closed lists (the five
  berry colors, the real teas, the real animals/ores), and the bot's ACTUAL shop
  inventory — GrannyMags no longer invents chamomile, Moth no longer warns about Skaven.
- **Backstories & eulogies**: replacement bots get an SLM-written biography at spawn
  (template first, upgraded async); every permadeath now writes the stone's eulogy once,
  cached forever — the grave whisper and press-E story window read it. `ff.grave.eulogize`
  backfilled the 14 pre-existing graves.
- **Admin panel**: SLM health strip (red BREAKER OPEN when the endpoint fails), roster
  sorted Online-first with fallen collapsed, live activity per row ("talking to X",
  "run: travel", STUCK flag), story-log tab, per-player memories with forget buttons.
- **Observability stack**: `metrics.json` (60 s) → dedicated `fakefriends-telegraf`
  container (own dedicated DB user + `fakefriends` schema, least privilege) →
  TimescaleDB → the "FakeFriends — Island" Grafana dashboard (population, economy
  pacing per group, combat, SLM ops). Hard-won: the container runs `--user 99:100`
  and the merge aggregator needs `grace=5m` for file-timestamped metrics.
- **Live-feedback fixes**: violet grave map markers; patrol awareness raised (heli
  180 m — bots now break off conversations and stay indoors, and CanFight no longer
  excuses strolling out under a heli); sprint-class movement everywhere; the door a
  patrol blows up gets REBUILT by its owner at real material cost (5-min warden,
  decision 0005 honest — can't afford = stays breached).
- Review findings fixed at close: attend-on-approach now respects `SLMEndpoint` empty
  (zero-regression contract); repaired doors respect `LockBotStorage`.

## [0.12.0] — 2026-07-22 — Phase 6: MonumentRunners run monuments for real

Gate probes first (6P1/6P2, both PASS — report),
then the embodiment, live-verified the same day (report):

- **Monument runs**: an online MonumentRunner (when observed, cooled down ~20 min)
  walks to a scored navmesh monument — filled crates vs distance vs scientists his
  kit can't argue with — strips up to 6 `crate_normal[_2]`s server-side (shells
  killed the vanilla way so respawn timers behave), and hauls the take home into
  the base boxes with career credit. Live: QuietFox brought 6 Airfield crates home.
- **Movement architecture** (decision 0008):
  ONE Animal-typed grafted agent covers terrain AND monument surfaces (the bake
  doesn't stop at the edge — probe-walked 162 m in and back out at Military
  Tunnel); the monument-local Humanoid bake is reachable by in-place re-typing,
  deferred until crate coverage demands it. Travel is leg-based (~120 m legs,
  stall jitter, give-up valve).
- **Fight-or-flee at the monument**: 5A2-R gate (worn tier + aggression) with the
  Phase 5 engagement caps; under-tiered runners flee correctly under scientist
  fire (verified live); heli/Bradley are never fight targets — QuietFox died to
  the patrol helicopter proving why. Fleeing always ends the visit; a mid-run
  death leaves the loot on the corpse at the monument (decision 0005 honest).
- **Runner economy**: MonumentRunner profile (components/frags/wood + the HQM
  trickle, component-stall sell orders, 10 scrap/hr pocket wage) auto-injected
  into live economy_profiles.json; abstract production keeps running unwatched
  and stands down while a member is physically mid-run (no double-dipping).
- **Observability**: `ff.run <bot> [monument]`, `ff.run.state`, story beats (set
  out / hauled / fled / gave up), marker-gated end-to-end probe harness.
- Fixed along the way: terrain-bake cache poisoning from monument-adjacent typing
  (broke stage-point sampling), unreachable-crate pathing oscillation (hangar
  crates off the Animal bake now skipped after 3 short legs).

## [0.11.5] — 2026-07-22 — rage-quit valve

- Two deaths in one session and the bot calls it a day (logs off to respawn on
  schedule) — ends the die-respawn-die treadmill a camped spawn produced.

## [0.11.0] — 2026-07-22 — world-feel pass: upkeep, secrecy, graves, pacing, seasoning

Five items from live observation, batched:

- **HQ door upkeep** (root cause corrected in 0.11.1): bots were already gathering
  metal.refined into lockers (live-tuned 0.125/min, Smelters via ore) — but the TC
  had NO metal.refined upkeep target, so the upkeep pass (which drains
  storage/food/locker buckets) never moved any of it. All seven profiles' upkeep
  targets +50 HQM; `TcInitialStock` +25 HQM as a bridge until gathering
  accumulates. 0.11.1: Hostile + Roleplayer live profiles gained the same
  0.125/min gather (they had no HQM income), and the code-default Trapper/Farmer
  profiles mirror the live-tuned rule for fresh installs
- **Shops are secret now** (`ShopBroadcast`, default off): no map pin, no lit
  broadcast — players stumble across bot shops instead of fast-traveling to them
- **Graves are points of interest** (`GraveMapMarkers`, default on): every
  headstone gets a slate-and-bone map marker inviting players to walk out and
  press E for the story; markers are runtime-only, warden-maintained, swept on
  unload
- **Progression pacing** (from 4 days of live data, ~15k units/day median):
  stage 2 ≈ end of week one (100k), stage 3 ≈ a week later (250k); kit tiers
  1/2/3 at 25k/75k/175k (≈ day 2 / day 5 / day 12 — the tier-2 fight gate now
  arrives mid-wipe, so early-wipe residents flee and veterans fight). Existing
  bots' EARNED tiers recompute downward; worn gear persists until death. Fixed
  the Newtonsoft list-append bug that had bloated the live config's threshold
  lists to 58–244 entries (`ObjectCreationHandling.Replace`)
- **Armor is real in bot fights + seasoning**: bot-fired shots now roll a hit
  area (12% head ×1.75, torso ×1, limbs ×0.75) and apply the victim's WORN
  clothing protection via the engine's own ItemModWearable data (the engine path
  needs a skeleton bone we can't fake — vs real players' bullets armor always
  worked). Laddered kits = genuinely tankier bots. And time on the server
  seasons them: over `SeasoningDays` (14) a bot gains up to +0.25 effective
  skill (aim + cadence) and +30% damage — early-wipe drifters are timid shots,
  two-week veterans are killers

## [0.10.2] — 2026-07-22 — hunt-observation fixes round 2

From live prey observation (spot → crouch → fire → kill worked; the rest didn't):

- **"Terrible shot and slow"**: the server's customized kits use weapons missing from
  the profile table (m16a2, m39, python, prototype17, shotguns) — all fired at the
  15 dmg / 1 s fallback. Full profile coverage added (m16a2 at AR cadence 0.4 s).
  Wounded targets are prone — both brains now execute at center mass instead of
  combing the grass at head height
- **"Did not loot"**: a killed BOT victim's entity is destroyed, and Unity fake-null
  made the kill-detect (`_target != null`) silently never fire — real-player victims
  persist after death, which is why looting them always worked. `ReferenceEquals`
  fix in the 5.5 driver, the legacy scientist brain, and the resident fight's win
  path (which now also caches the target's description/position for the win story
  and corpse-harvest handoff — the entity is gone by then)
- **"Still crouch-walking"**: the loot-seek give-up paths never stood back up out of
  the firing crouch; all give-up branches un-duck now, plus a never-patrol-crouched
  guard in both idle loops

## [0.10.1] — 2026-07-21 — 5.5 live-observation fixes

- **Hostile scan was blind to untracked bodies** (Vex walked past his prey): the 1 Hz
  target scan now sweeps the engine's `BasePlayer.bots` list — every native-bot body
  (prey, residents, third-party bots) — alongside real players and the registry
- **"Walking backwards"**: facing on an AI-driven player body is a trio —
  `eyes.rotation` + `viewAngles` + `ServerRotation` together (the
  `NPCPlayer.SetAimDirection` pattern); viewAngles alone left the networked model
  rotation at its spawn value while the body slid along the path
- `ff.hostile.test prey [x z]` — throwaway watchable hunt victim (added post-0.10.0)

## [0.10.0] — 2026-07-21 — Phase 5.5: the perfect disguise

Hostiles migrate off the scientist chassis onto the SAME native-bot player bodies
residents use (`HostileNativeBodies`, default on; the scientist path remains as a
config fallback). Gate: the navigator-graft probe
(report).

- **One body, end to end**: the offline sleeper at camp IS the prowling body —
  wake, graft, hunt, walk home, sleep. No despawn/respawn seam; player corpse,
  kill feed, and death screen by construction (the `displayName`/`AttackerInfo`
  patches and the death-screen "Scientist" leak retire with the chassis)
- **`HostileDriver`**: the HostileBrain state machine ported to a grafted
  driver — roam → chase → combat → loot → depot → return with the same doctrine
  (priv guard, engagement caps, wounded-lock, grudge preference 0.4×, far-side
  give-up, water policy); navigation via runtime-grafted
  NavMeshAgent + RustNavMeshAgent + BaseNavigator, pumped at 10 Hz with the
  ground-glide fix and client-smooth MovePosition networking
- **Chassis-forced ports**: senses → 1 Hz scan over real players + our bots
  (safe-zone and Cobalt-on-Cobalt excluded); retaliation → polled `lastAttacker`
- **Native hostiles burn real ammo**: combat is the shared 5A2-R native-fire
  recipe (skill-scaled hit roll + cadence, real magazine reloads); a dry mag
  sends them home, where the Cobalt resupply (depot) tops the pockets back up
- **The disguise now works on residents too**: they no longer flee hostiles on
  sight (the scientist silhouette used to give it away). Bot-keyed grudges got
  their Phase-5 teeth instead — residents recognize a hostile only AFTER Cobalt
  has shown its hand, then fight or flee per the 5A2-R gate
- `ff.hostile.state` (chassis/state/whereabouts per hostile); marker-gated
  end-to-end prowl probe (`probe_hostile55.json`)

## [0.9.0] — 2026-07-21 — 5A2-R: residents fight back

Gate probe first: the 2026-07-21 native-fire probe proved a native-bot BasePlayer can
fire a held gun (report) — FX via
`ServerUse` at damageModifier 0, damage via our own hit roll + raycast + `HitInfo`
through `Hurt`, so the full grudge/bounty/kill-feed pipeline fires unchanged.

### Resident fight-or-flight (5A2-R)
- **The decision**: attacked, or a firsthand-grudged player / Cobalt hostile /
  predator in sight → worn kit tier ≥ 2 AND aggression ≥ 0.4 (both config) →
  stand and fight with the kit weapon; anyone else keeps the Phase 4
  flee-and-hole-up. Being shot by someone you won't fight now also triggers
  retreat (it previously took a patrol sighting or half health).
- **The fight**: stands ground (no pursuit — a resident defends himself, he
  doesn't lay siege); tracks with the muzzle; skill scales hit chance
  (35→80%) and fire cadence (1.35×→0.75×); per-weapon damage/cadence profiles
  (bows to bolt rifle); real magazine + pocket-ammo reloads — running dry
  breaks off the fight
- **Engagement caps borrowed from the hostile doctrine**: 90 s engage timeout,
  20 s lost-LOS break-off, 5 min per-target cooldown; caution-scaled HP flee
  valve (same curve as the hunt); heli/Bradley/turrets are never fight targets
- **Boars get shot now**: animal attackers and close predators are fight
  targets; a hunter persona walks over and harvests the kill afterwards
- **Symmetry**: residents fighting hostiles triggers the hostile's
  retaliation path — real bot-vs-bot gunfights; deaths/kills land in the
  normal grudge, permadeath, and bounty ledgers
- `ff.fight <bot> [go]` admin harness; marker-gated end-to-end probe
  (`probe_fight.json`) with PLog shot-by-shot self-logging

## [0.8.0] — 2026-07-20 — Phases 4 & 5 closed: the island is dangerous, social, and remembered

Rolls up v0.6.13→v0.7.9 plus the closing polish. Hostile fiction: decisions/0007;
chat content: docs/content/chat-lines.md.

### Phase 5 — Hostile module (complete)
- **Cobalt fiction live**: 6 hostile identities with undercover backstories; kill →
  loot corpse → depot the take at the base stash → scrap bounty → base/kit stage
  upgrades (`HostileBountyCareer` credits kills as career); Cobalt retainer wage (50/hr)
  + resupply gather keeps camps standing; disguise kits ladder to full BDU/ballistic
- **Guardrails (5D)**: never paths into player building privilege (target + corpse,
  1 Hz, bot cabins exempt); never dormant (hunts run on an empty server);
  `MaxConcurrentHostiles` caps simultaneous prowls
- **Combat polish (5A/5A2)**: retaliates when shot from an unseen attacker (truce
  voided, charge bounded by the LOS timer); prefers grudged targets (0.4× distance
  weight); firing crouch at range; smooth grounding (no more terrain-bounce);
  deep water blocked
- **Grudge symmetry (5C)**: residents ledger hostiles under `bot:` keys, warn each
  other, and hostiles hunt their own killers back — the feud loop is closed

### Persona economy & content pass (v0.7.0)
- All six personas: tuned real-hour gather (upkeep-run model), craft chains, sell
  lists, 4-tier stage-following kits with workshop skins, `ScrapPerHour` wage
  (online-gated, like gather), biome-variant profiles (`Trapper.Jungle`), per-planter
  crop scaling, stage-graded clone genetics (→GGGYYY) with stage pricing,
  refinery-gated explosives (`crude.oil@refinery`)
- Proximity chat: persona greet/idle pools + 18 identity voices (Lang-backed,
  chattiness-gated); Roleplayer antics (gestures, boombox radio, instant-camera photo
  drops from `data/FakeFriends/photos`); template backstories for generated drifters
- Rotating 7-order storefront (vanilla vending cap); doors close when nobody's home

### Phase 4 close-out
- **Respawn on the sleeping bag** (inside the walls — ends the boar-camp respawn loop)
- **Murder-only permadeath**: animal/world deaths no longer burn lives
  (`WorldDeathsBurnLives`)
- **Graves are forever and speak**: headstones tracked + healed against decay
  (re-placed if destroyed, stacks deduped); approach whisper; **press E** for the story
  window — backstory plus a story-log-mined biography (milestones, feuds, kills,
  death tally); wipe clears the graveyard with the map
- Roster: reload no longer bypasses the 2–6 h post-permadeath refill delay; logs/UI
  show active/cap with fallen counted separately

*Phase 4 pending only the at-wipe verification (checklist §D wipe test). Phase 6
(MonumentRunner embodiment) and Phase 7 (SLM chat/eulogies/backstories) are next.*

## [0.6.0] — 2026-07-19 — Phase 4: death, permadeath & the social layer

Death has consequences now. Design: decisions/0006;
usage: docs/SOCIAL.md; in-game verification tracked in
docs/phase-3.5-4-test-checklist.md.

### Added
- **Honest re-gear**: the kit dies with the corpse; respawn consumes the group's banked
  spare kit from the locker (raided locker = no spare) or falls back to tier 0;
  `GearTier` tracks worn gear and the economy buys back up to the earned tier for scrap
- **Permadeath**: wipe-scoped lives (`PermadeathBaseLives` ±1 by caution) → status
  `Fallen` (revives on wipe; admin `Retired` still survives it); stone **gravestone** at
  the death spot + epitaph in the story log; last member fallen → base decays; roster
  refills with newcomers 2–6 h later (`ReplaceDeadBots`)
- **Grudges**: firsthand `hostile` entries (typed keys `steam:`/`bot:`) written to the
  whole group on a kill; secondhand `wary` reputation spreads between neighboring online
  bots every 5 min (`GrudgePropagationRangeMeters`); optional decay (`GrudgeDecayDays`,
  default permanent); carries over wipes unless `WipeGrudgeReset`
- **Defensive response**: bots retreat + hole up on sighting a grudged player; chatty
  bots call them out in chat (cooldown-gated). Armed retaliation lands in Phase 5.
- **Admin**: `ff.grudge`, `ff.grudge.reset`; lives in `ff.list`/roster CUI (♥, `Fallen`
  in red, Rev revives); Lives + Grudges section on the Eco detail page
- Config: `SmeltRecipes` (ore→product) externalized alongside the Phase 3.5 knobs

### Fixed (same-day 8-angle review, report)
- Fully-fallen groups' economies kept running (upkeep re-fed the emptied TC — the
  abandoned cabin never decayed); reload could resurrect a permadead bot via name-match
  adoption; a failed respawn destroyed the banked spare kit; a looted-but-standing
  locker still granted full spare gear; wrong-record `StopBaseUpkeep` when the base
  holder fell first; roster ratchet past `MaxBots` across wipes; tea-recipe typos
  destroyed ingredients; explicit craft costs didn't scale with `Amount`; auto-turret
  kills escaped the grudge system; `ff.kit` override lost to post-death gear state;
  wary reputation had no behavioral effect; plus assorted guards/cleanups
  (`RunGatherRules`, `IsRealPlayer`, `ForEachKitItem`, key helpers, timer teardown)

## [0.5.0] — 2026-07-18 — Phase 3.5: Farmer + Smelter specializations

Farmer and Smelter become authored data profiles over the Phase 3 Worker core, plus the
minimal code the two personas need. Deployed live; Smelter groups producing within minutes
(abstract ore income confirmed on groups 6/13). Farmer awaits its cabin captures (3.5-H).

### Added
- **Farmer profile** (the garden shop): red/blue/yellow/white berry crops + slow clone
  cuttings; brews healing/ore/wood teas at the mixing table; sells berries, clones, and
  teas (7 orders — the vending cap). Crops use the new `Kind: "crop"` — stationary
  planter production that advances watched or not, but **only while a live planter
  stands at the base** (raid the planter → the farm stops; decision 0005). Planters
  reclassified `decor` → `planter` anchor purpose. Teas have no `ItemBlueprint`
  (mixing-table recipes), so `CraftRule` gained explicit `Ingredients` — recipe costs
  live in the data file, tunable without recompiling.
- **Smelter profile**: stone/metal/sulfur/HQ-ore + fuel-wood gather; sells fragments,
  sulfur, charcoal, HQM, gunpowder; gunpowder craft rule (dormant until a captured stage
  includes a workbench). Shop arrives with the stage-3 build by design.
- **`SmeltOre` pass** (mirrors `CookMeat`): furnace-gated `metal.ore→fragments`,
  `sulfur.ore→sulfur`, `hq.metal.ore→refined`; deposit-then-remove so a full box
  throttles instead of destroying ore; spare wood → charcoal 2:1 above a reserve.
- **Embodied ore mining**: the work loop picks tree vs ore nodes from the profile's
  gather kinds — Smelters swing a pickaxe with a bundle-verified rock-strike FX.
- Config: `SmeltUnitsPerMinute`, `CharcoalWoodReserveUnits`.

### Fixed
- **Craft yields**: `CraftGoods`/`ff.craft` now deposit `Amount × blueprint
  amountToCreate` (gunpowder batch = 10; rug unchanged at 1).
- **Fractional yields**: stochastic rounding (`UnitsFor`) preserves expected output for
  low rates/short ticks — fixes the latent zero-yield for low-skill meat rates and
  enables the 0.8/min HQ-ore trickle.

### Groundwork (shipped unversioned pre-0.5.0)
- **Kit loadouts** → `data/FakeFriends/kits.json` (persona → 4-tier ladder + Default fallback)
- **Spoiled-food removal** — food spoils on the normal timer (unpowered fridge won't stop it);
  each economy tick sweeps `*.spoiled`/`spoiled.produce` from the boxes so slots don't clog
- **Config knobs added**: `PersonaHomeBiomes`, `ScheduleWindows`, `TcInitialStock`,
  `ObserveRange`, `EconomyTickSeconds`, `EconomyCatchupCapMinutes`, `CookUnitsPerMinute`,
  `ShopRestockOrders`, `HuntHitDamage`, `HuntMeleeRange`, `BotRegenPerTick`

### Open
- Farmer cabin captures (`ff.base.capture Farmer 1..3`, **planter + mixing table in
  every stage**); movement fidelity (run/jump/crouch) tracked for Phase 5

## [0.4.0] — 2026-07-18 — Phase 3: Worker economy (Trapper)

Persistent, self-running per-group economy: gather → cook/craft → stock shop → pay
upkeep → progress base & kit. Runs for real when watched, as abstract accounting when
not — the same real boxes either way (physical containers are canonical; see
decisions/0005). All stages A–H shipped;
validated end-to-end live on the live server (Trapper + Smelter), raid-drops-the-economy
confirmed. Re-reviewed before release (docs/reports/2026-07-18-review-2.md).

### Added
- **Economy core** (A/C): per-group career + scrap ledger (`economy.json`), 45 s
  `EconomyTick`, observed-vs-abstract production into the real base containers, data-driven
  per-persona `economy_profiles.json` (Trapper authored; Farmer/Smelter stubs)
- **Embodied gather + losable hunt** (B): real `DoGather` chops while watched; a two-sided
  animal hunt with a `Caution`-biased flee valve; deposits the haul into the base boxes
- **Cook / craft / vending shop** (D/E): furnace cooks raw→cooked, workbench crafts profile
  goods from honest ingredients, vending machine auto-configures/restocks/sweeps scrap
- **Progression wiring** (F): auto-acquire stage 1 on first productive session; stage
  upgrades at career thresholds; need-gated interior furnishing; kit tiering (career-earned,
  skill-capped) with tiered loadouts; spare-kit banking into the locker; code-locking of
  bot storage + shell doors so players must raid
- **Admin & observability** (G): `ff.econ`, `ff.econ.tick`, `ff.craft`, `ff.shop`, `ff.kit`
  console commands (permission-gated, RCON-mirrored); per-bot economy detail UI (roster
  **Eco** button) with a computed "next action"; economy events in the story feed
- Config: `BaseAutoAcquire`, `BaseAutoUpgrade`, `BaseUpgradeCareerThresholds`,
  `KitTierCareerThresholds`, `KitBankScrapCost`, `LockBotStorage`
- Docs: economy reference, H3 play-test checklist

### Hardening (H1/H2)
- Economy gated to economic groups only — free-roamer households do zero per-tick work
- Wipe now clears the stale base record so the new seed re-anchors + rebuilds a fresh cabin
- Bank-spare-kit won't charge scrap for a kit that doesn't fit the locker
- Confirmed: no LINQ / Reflection, single dynamically-subscribed hot hook, no vending hooks
  (scrap swept by direct read), bases/locks persist across reload, catch-up capped at 30 min

### Live-test hardening (H3, 2026-07-18 on the live server)
Bugs shaken out during the end-to-end play test and fixed live:
- **Vending machine never activated** — set the Broadcasting flag but never called
  `UpdateMapMarker` (the engine's own toggle does both); shop read as inactive
- **Shop never stocked** — `VendingMachine.CanAcceptItem` rejects items pushed in from a
  bot-owned box server-side; clear the filter around the restock, then restore it
- **Open shopfront** — the wall-mounted vending machine is now placed at base spawn (sealed),
  not left need-gated; `DoorFlickSeconds` config added; bots duck inside ~5–10s on deposit
- **Smelters never built** — the economic-group gate now also passes personas with captured
  cabins (Smelter/Farmer), not just those with a gather profile
- **Wrong-biome anchors** — tiered anchor search (prime → adjacent → any) + honest logging;
  the limiter was the large stage-N footprint in-biome, not the biome being absent
- **Sleeping outside** — a bot killed outside a session now respawns onto the bag inside
- **Stage upgrade stuck on clear ground** — `HasBlockingEntity` counted the bot's OWN standing
  base as an obstruction; now excludes owned entities. Blocked placements name the reason
- **Diagnostics** — world-death log names the damage type; deferred upgrades back off 5 min;
  `ff.produce` test command forces production to fill a shop on demand

## [0.3.0] — 2026-07-17 — Phase 2: base system

CopyPaste-backed persistent bases with bot ownership.

### Added
- Base capture (`ff.base.capture`) → catalog of persona stage blueprints
- Placement (`ff.base.paste`): footprint-fit vertical seating (aim-independent), bot-owned TC with group auth (re-authed on respawn) + upkeep stocking
- Interior anchor system: furniture harvested to purpose-tagged anchors; bots furnish empty shells over time (`ff.base.fill` forces it)
- Stage upgrades (`ff.base.stage`), decay-on-retire, `ff.base.remove`, `ff.anchor.reset`
- Footprint-aware placement: anchors sized for the persona's largest stage; per-stage relocation if a bigger stage would clip rocks/terrain
- Generic hazard avoidance for wandering bots (damage-trigger detection; decision 0004)

### Hardening (post-review, 6 of 7 findings fixed)
- Base replacement made atomic in PlaceBase (removes the old base only after validation passes) — no orphaned/duplicate bases, no homeless bot on a failed upgrade
- Trees dropped from the hard-obstruction check so wooded personas (Trapper) can anchor
- Footprint flatness now samples the interior, not just the outer ring
- Guard against concurrent pastes for the same bot
- Revive restores base upkeep (retire→revive fully reversible); revive logic de-duplicated

## [0.2.0] — 2026-07-17 — Phase 1: core framework

First playable release, live on the primary server.

### Added
- Persistent bot identities: authored cast (14 characters incl. one duo) + generated replacements, per-bot JSON data files, wipe detection with cast/grudge carry-over policy
- Native-bot spawner (`player.prefab`) with persona clothing kits, post-spawn naming, vitals init, network-silent spawn (prevents number-name flash)
- Session scheduler: trait-driven windows (earlybird/daytime/nightowl/random), sleeper bodies while offline, walk-home-to-log-off with teleport fallback
- Biome-anchored home regions (Trapper→jungle/woods, Farmer→temperate near water, Smelter→arctic/desert) with map-seed revalidation on wipe
- Death handling: real corpses, killer attribution (player/bot/world), quiet respawn after configurable delay
- Idle wander: 10 Hz ground-snapped navmesh movement with pauses and facing
- Presence layer: join/leave chat messages, kill-feed broadcasts, night campfires at anchors (orphan-swept on load)
- Admin: roster CUI (`/ffadmin`) with per-bot TP/session/retire buttons; `ff.*` console command set; story log (`ff.story` + data file + log render)

### Hardening (post-review, 18 findings fixed)
- Entity-vanish paths fully unlink wander/user-id state (no more insta-cancelled sessions)
- Corrupt data files can no longer leave the plugin inert; null-field records are repaired; phantom index IDs pruned
- Config parse errors no longer overwrite the user's config file
- Wipe-ID fallback upgrade no longer triggers a spurious wipe
- Retire/revive flow hardened (no resurrection via `ff.session start`, retirement survives wipes, no spurious death processing)
- Respawns outside sessions no longer broadcast fake leave messages
- Unload safe against hook re-entrancy; wounded bots not force-slept
- Perf: wander loop iterates live set only, single sqrt per step, NavMeshPath reuse

## [0.1.0] — 2026-07-17 — Phase 0: diagnostics

- `FakeFriendsProbe.cs` diagnostics plugin + live-server test runs
- Full findings in docs/DIAGNOSTICS.md; Phase 1 gate passed; native-bot identity decided (docs/decisions/0001)
