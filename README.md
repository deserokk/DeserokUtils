# DeserokUtils

A bucket of small FFXIV utilities, one feature per tab. `/dsu` opens the window.

## Install

Add to Dalamud → Settings → Experimental → **Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/deserokk/DalamudPlugins/main/pluginmaster.json
```

One URL, several plugins. Then find DeserokUtils in the normal plugin installer.

## What's in it

| feature | what it does | commands |
|---|---|---|
| **CastWatch** | gate a macro line on whether an action actually went off | `/watch` `/ifwatch` |
| **FateWatch** | warn before a cyclic FATE spawns | `/fatewatch` |
| **FcBuffs** | keep the FC buffs up, from the FC's existing stock | `/fcbuffs` |
| **DrawSheathe** | one key that draws or sheathes, whichever is correct | `/drawsheathe` |
| **EmoteQuiet** | announce an emote once, then quiet its repeats | `/emotequiet` |
| **Mouseover** | one macro line that tries `<mo>` and falls back cleanly | `/ifmo` |
| **Interact** | operate the thing in front of you, without touching menus | `/dsuinteract` |
| **EphemeralMarks** | mark the people you queued into big content with | `/dsumarks` |

**On by default:** FateWatch, EphemeralMarks, the server-bar entry.
**Off until you ask:** FcBuffs, EmoteQuiet, diagnostics.
Everything else only happens when you press something.

---

## CastWatch

A vanilla macro has no conditionals, so a callout fires whether the spell cast, was out of range, or
never started. This gates it on what actually happened — including instant casts and oGCDs, which
have no cast bar to observe.

```
/watch Aurora --notself
/ac Aurora <mo>
/ac Aurora <2>
/ifwatch /p Aurora on {who}
```

`{who}` fills in whoever actually received it — a plain macro can't do that, since `<mo>` on the
callout line re-evaluates and announces the wrong person. Filters: `--self`, `--notself`, `--party`.
Bare `/ifwatch` cancels the macro instead of running something.

⭐ It never sends anything on your behalf. It only runs or suppresses a line you wrote.

## FateWatch

Warns before a cyclic FATE spawns, predicted from the last spawn it actually saw. Built for Occult
Crescent's pot FATEs, which run on a rotation the game surfaces nowhere. Countdown in the server bar,
plus optional toast and sound.

⭐ It says "not seen yet" rather than inventing a number.

`/fatewatch list` for the zone's FATEs, `/fatewatch anchor <name> <minsAgo>` to set the cycle by hand.

## FcBuffs

Checks whether your chosen FC buffs are still up and refreshes them from the FC's existing stock, in
cities and residential districts only.

⭐ **Spends no credits** — activating an action the FC already bought is free. It tells you when a buff
runs out somewhere it can't refresh, and when the stock is gone.

Off by default. `/fcbuffs now` to refresh immediately; there's a dry-run mode that rehearses every
step and logs what it *would* have done.

## DrawSheathe

The Gold Saucer draw and sheathe animations are separate emotes, not a replacement for the weapon
toggle — so they can't be bound as one key and cost two hotbar slots.

`/drawsheathe` reads whether your weapon is out and plays the right one. The emote is silently
refused while moving or jumping, so those fall back to the game's own toggle and one key covers every
case. Both emote commands are configurable, and holding the key collapses to a single toggle.

## EmoteQuiet

Announce an emote the first time, then stay quiet about repeats of that same emote for a minute.
Clapping through a performance says you clapped once, not fifty times.

The game only offers all-or-nothing, which is why most people switch emote messages off entirely and
then nobody ever knows they clapped at all.

Optionally hides other people's repeats from your log too — kept per sender, so ten people clapping
is ten lines and one person clapping thirty times is one. Off by default.

## Mouseover

`/ifmo` picks the first target the action would actually land on.

```
/ifmo /ac "Heart of Corundum" {mo|2|noop}
```

Mouseover, then party slot 2, then send nothing at all. One line replaces the twelve-line macro with
a fallback at the bottom — and removes the race where pressing early lands the fallback instead of
the mouseover, because it validates before firing rather than firing and failing down the list.

⚠ End with `|noop` for anything that can hit you, or a fallthrough self-casts a cooldown you pressed
for somebody else.

Works with `/ac`, `/pvpac` and `/blueaction`, and picks the PvP or non-PvP version of a shared name
based on where you are.

## Interact

`/dsuinteract` operates the thing in front of you and nothing else.

FFXIV's Confirm key is console-shaped: it drives world objects *and* menus, and takes two presses for
a lever, so you press it repeatedly. **With any menu open those presses land in the menu** and
activate whatever is under the cursor — a key-and-wheel dungeon can eat a raid potion.

⭐ A dedicated key can't do that, because it isn't Confirm. It also never changes your target.

## EphemeralMarks

Marks the people you queued in with, so you can find them in content full of identical-looking
allies. A star for the party leader, a diamond for everyone else.

⭐ Client-side only — nobody else sees it, and it never touches the shared party markers everyone
fights over.

The party is captured when the queue pops and frozen, so it survives the game reshuffling your
premade into an alliance. Shows in PvP, field operations and alliance raids; colour, size and height
are all configurable.

⭐ It switches itself off when you queued as the whole group, since marking everyone is the same as
marking nobody.

---

## Building

```
dotnet build DeserokUtils/DeserokUtils.csproj -c Release
```

No setup — `Dalamud.NET.Sdk` resolves its references from the local XIVLauncher install. Output lands
in `DeserokUtils/bin/Release/DeserokUtils/`, which is also where a dev-plugin location should point.

No package references, on purpose: `XivCommon 9.0.0` still wants the old `DalamudPluginInterface`, and
the only thing needed from it was a chat-line send, which is four lines of ClientStructs.

⭐ The reasoning behind each feature lives in the source comments, which are heavier than the code.
