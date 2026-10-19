# Integrations with other plugins

## Presence API (any plugin)

Residents are native bot player bodies. The engine only puts a `BasePlayer` into
`BasePlayer.activePlayerList` from a real network connection (`PlayerInit`), so a bot
that is "online" is in **neither** engine list: it leaves `sleepingPlayerList` when it
wakes and never enters `activePlayerList`. Offline residents are ordinary sleepers and
already count in `BasePlayer.sleepingPlayerList`. Do **not** add bots to
`activePlayerList` yourself: that list feeds the Steam player count and slot math, the
anti-hack state arrays, and every plugin that assumes an active player has a connection.

FakeFriends exposes the missing half:

- `int FakeFriends.Call("GetOnlineBotCount")` — residents with status Online and a
  live, non-dead body. Returns a boxed `int`; `as int? ?? 0` is the safe read.
- Hook `OnFakeFriendsPresenceChanged(int onlineCount)` — fired to all plugins whenever
  that count changes (session start/end, death, respawn, and a 30 s scheduler
  catch-all). Fired with `0` on unload. Log-on/off also fire the engine's
  `OnPlayerSleepEnded` / `OnPlayerSleep` because the bot body really wakes and sleeps.

## Hud (ahigao) — Online count

`Hud.ff.patch` adds the resident count to the HUD's Online number (its Sleep number
already includes offline residents). Three hunks against Hud 3.5.3: a
`[PluginReference("FakeFriends")]` field, an `OnFakeFriendsPresenceChanged` handler
that queues the HUD's own count redraw, and `CountActivePlayers()` adding the bot count.

Hud is a paid third-party plugin and is not in this repo. To apply after a Hud update,
from the folder holding the new `Hud.cs`:

```bash
patch -p1 < /path/to/FakeFriends-RustPlugin/integrations/Hud.ff.patch
```

If a hunk fails, the three edits are small enough to redo by hand from the patch text.
The HUD runs unchanged without FakeFriends loaded (the reference is null, the count adds 0).
