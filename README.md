# DeserokUtils

Small FFXIV utilities, one feature per tab. `/dsu` or `/deserokutils` opens the window.

## Install

Add this to Dalamud's **Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/deserokk/DalamudPlugins/main/pluginmaster.json
```

⭐ That is the **shared feed** — one URL for every plugin, so nothing new ever needs adding. This
repo also carries `repo/pluginmaster.json` listing only DeserokUtils; it works, but the shared feed
is the one to use.

---

# CastWatch

Gate a macro line on whether an action **actually went off**.

A vanilla macro has no conditionals — `/ac` and `/p` are independent lines, so the callout fires
whether the spell cast, was out of range, was on cooldown, or never started. That is the macro spam
everyone complains about, and it matters most in the case you want it least: declaring a rez.

```
/watch Aurora --notself
/ac Aurora <mo>
/ac Aurora <2>
/ifwatch /p Aurora on {who}
```

## Commands

| command | |
|---|---|
| `/watch <action>` | Arm a watch. Put it above the `/ac`. Quotes optional; items work too |
| `/watch` | Report what is armed |
| `/watch off` | Disarm |
| `/ifwatch <command>` | Run that command **only** if the action went off |
| `/ifwatch` | No command: cancel the macro if it did not |
| `/dsu` · `/deserokutils` | Open the window. Add `debug` to toggle diagnostics |

### Target filters

`--self` · `--notself` · `--party` · `--any` (default)

Only **exact** tests: identity with you, and membership in the party roster. Friendly/hostile
inferred from `ObjectKind` would put pets, chocobos and friendly NPCs in a grey zone, and a filter
that is usually right is worse than one that is not offered.

⭐ `--notself` is what catches a **self-redirect** — abilities like Aurora quietly retarget to you
when the mouseover is invalid, which otherwise reads as a successful cast.

### The `{who}` token

Fills in whoever actually received the action. This is the thing a vanilla macro structurally
cannot do: `<mo>` on the callout line evaluates when *that line* runs, so a fallback macro that fell
through from `<mo>` to `<2>` announces your mouseover while someone else got the heal.

Braces, not `<who>` — angle brackets collide with the game's own placeholder syntax.

## It never sends anything for you

Subtractive by design. It cannot add chat; it runs or suppresses a line **you wrote**. The only
thing it transmits on its own is `/macrocancel`, which is client-side and produces no chat.

## Design notes

**Why a hook and not a cast-bar check.** An instant cast — Swiftcast Raise, any oGCD, a Phoenix
Down — never produces an observable cast bar, so `IsCasting` is false for exactly the case this
exists to catch. `/ifwatch` passes on either signal: the hook saw `UseAction` accept it, or you are
currently casting it.

⚠ The hook sees *the client accepted and sent* the action, not that the server resolved it. For a
callout that is arguably what you want — you are announcing when you commit, not eight seconds
later when it lands.

**One watch slot, no named channels.** The game runs one macro at a time — starting another cancels
the first — so a second slot could never be reached.

**Arming replaces the previous arm**, so a double-press starts from a known state. `/ifwatch` is
one-shot and an arm expires after 10s, so an interrupted macro cannot leak a callout into a later
one.

**Four outcomes, four distinct messages** — nothing armed, arm expired, armed-but-missed, fired.
The first two report an authoring mistake and deliberately do **not** cancel; only a genuine miss
suppresses. If they all failed identically, deleting line 1 would look the same as the spell
fizzling.

**Items are watchable** (Phoenix Down). Their ids live in a separate space from actions, so the
watcher tracks *which* it is watching — matching on id alone would let a watch on an action fire on
an unrelated item sharing its number. HQ items arrive at `id + 1,000,000` and are normalised.

⚠⚠ **The asymmetry in how commands are run is deliberate.** The cancel runs **synchronously**
(`RaptureShellModule.ExecuteCommandInner`) because it has to beat the next macro line — the queued
path loses that race, which cost a day of thinking the gate was broken when it was correct. Your
command runs **queued** (`ProcessChatBoxEntry`) because it has nothing to outrun, and that pipeline
is what expands `<t>` / `<mo>` / `<2>`.

---

# DrawSheathe

One key that draws or sheathes, whichever is currently correct.

```
/drawsheathe
```

The Gold Saucer **Draw Weapon** and **Sheathe Weapon** are *emotes* (Emote rows 238 and 237) — not
replacements for the default weapon toggle. Buying them adds two emotes rather than changing the
animations the default keybind plays, and an emote cannot be bound as a toggle. So they take two
hotbar slots and you have to know which one is correct at any moment. This restores the toggle.

## Commands

| command | |
|---|---|
| `/drawsheathe` | Draw or sheathe, whichever is right |
| `/drawsheathe state` | Report what it reads, without sending anything |
| `/drawsheathe conditions` | List every condition flag the game currently has set |
| `/drawsheathe sniff [seconds]` | Record what the client calls when you press the real keybind |

## Design notes

⚠⚠ **The emote does nothing while you are moving or jumping** — silently refused, no message. Those
cases fall back to the game's own toggle, so a single key covers everything.

⭐ The predicate is *"would the emote be refused"*, **not** *"am I moving"*. Moving was simply the
first case found; jumping refuses the emote too and reads `IsPlayerMoving == false`. A check named
after movement was answering a narrower question than the one being asked, and would have kept being
wrong one case at a time. The list is observed rather than a model of the game's rule — anything not
on it gets the emote, which fails harmlessly.

⚠⚠ **`SetUnsheathed`'s `isInstant` parameter means the opposite of its name.** Read the signature and
`false` is obviously "play the animation"; it teleports the weapon into the hand instead. The real
keybind passes `true` every time, standing and moving alike, and that is the call that animates.
Found by hooking the function and pressing the key — `/drawsheathe sniff` is that tool, kept.

⚠⚠ **A held key is collapsed to one toggle.** Auto-repeat arrives around 10 Hz, and without this a
held key cycles the weapon continuously. A cooldown alone does *not* fix it: gating on the game's
one-second sheathe cooldown only slows the cycle to one per second for as long as the key is down.
Only a quiet-gap test collapses it. The window is configurable in the tab because repeat rates are a
property of your hardware — this is an accessibility setting, not a tuning detail.

⭐ **The game's own `SheatheCooldown` is honoured too**, and it is a real countdown: measured at 1.0
on a state change, decaying at 1.0/second, 0 at rest. Both paths set it, the queued emote included,
so one gate covers both.

Both emote commands are editable in the tab, and the fallback can be switched off there. That
fallback is the only part which calls into the client directly, so the switch is what gets a working
key back if a patch ever breaks it — without waiting for a build.

---

# EmoteQuiet

Announce an emote the first time, then stay quiet about repeats of that same emote.

```
/emotequiet on         say it once, then be quiet for a minute
/emotequiet others     also hide OTHER people's repeats, in your log only
/emotequiet reset      forget the timers
```

The game gives you two settings and both are bad: log messages on means fifty *"X claps"* through a
performance, off means nobody ever knows you clapped at all. Most players pick off, so emotes stop
persisting anywhere — a `/dote` you looked away from is simply gone.

⭐ **The middle option was in the engine the whole time.** `DisableLogMessage` is a per-emote flag,
and `motion` is that flag exposed one command at a time. This sets it automatically on repeats, so
the first one talks and the rest don't.

⚠ **Off by default.** Everything else here affects only your own client; this changes what other
people see. It also needs the game's own "Display log message" ticked — that's the box this exists
to let you leave on.

## Design notes

⚠⚠ **Never assign `Flags` wholesale.** The emote window arrives carrying `0x88`; only the
`DisableLogMessage` property is touched, because it sets one bit and leaves the rest alone. Caught
only because the recording printed the raw byte — the friendly property read `False` in both cases
and looked identical.

⭐ Hooked at `EmoteManager`, not `AgentEmote`, and that was measured too: `AgentEmote.ExecuteEmote`
receives `option = NULL` for the emote window and hotbar, so a hook there would have had nothing to
modify on the paths that matter most.

⚠ The Cheer variants count as **one** emote. Fifteen rows begin `Cheer ` and all render the identical
line, so cycling colours to find the one you want announces once. Hand-listed, deliberately —
deriving families from shared log messages is a trap, since `ExtractText` drops the verb and Bow,
Clap, Wave, Yes, No and sixteen others all reduce to `"."`.

⭐ Other people's repeats are keyed **per sender**: ten people clapping is ten lines, one person
clapping thirty times is one. Unlike consecutive-duplicate collapsing, alternating emotes are the
case this handles best rather than the case that defeats it.

---

# Mouseover

```
/ifmo /ac Clemency {mo}                       mouseover, else ordinary targeting
/ifmo /ac Cover {mo|2}                        mouseover, then <2>, else ordinary targeting
/ifmo /ac "Heart of Corundum" {mo|2|noop}     mouseover, then <2>, else send NOTHING
```

Each candidate is checked against the action, and the first one it would **actually land on** is
used. Any placeholder the game understands works as a segment — `mo`, `2`, `t`, `f`, `me` — because
each goes through the game's own resolver rather than a list of names this plugin knows.

⚠ **The tail differs per action, so it's explicit.** Cover can't target you, so falling through to
ordinary targeting merely no-ops. Heart of Corundum *can*, so the same fallthrough quietly spends a
cooldown on yourself that you pressed for somebody else. `noop` is how you say "if none of these
work, do nothing".

⭐ Forgot the quotes on a multi-word name? It adds them. `/ac` rejects unquoted multi-word actions,
but this parser reads them fine — so without the fix you'd get a correct decision in the log and
nothing happening in game, which is a worse failure than not parsing at all.

Replaces the twelve-line mouseover macro with a bare fallback at the bottom. That pattern works and
has a race: press it while the GCD is rolling and every mouseover line fails on cooldown, then the
GCD expires onto the fallback — so it targets normally despite a perfectly good mouseover. The
fallback sits last, which is exactly where it's most likely to win. One line has nothing to race.

⚠⚠ The test is *"would this action work on that target"*, not *"is something under the cursor"*.
Pointing at an enemy while healing has to fall through, the way vanilla does; a presence check would
send `<mo>` anyway and simply fail. Uses `ActionManager.CanUseActionOnTarget` plus
`GetActionInRangeOrLoS`, whose codes are LogMessage row ids — `0` fine, `566` out of range, `562` no
line of sight. **Obeying the range code is required for parity, not a bonus:** ungated, an
out-of-range mouseover fails and nothing else happens, while the macro would have fallen through and
healed somebody.

---

# Interact

A key that operates the thing in front of you and **nothing else**.

```
/dsuinteract
```

Put it in a one-line macro, drag it to a hotbar slot, bind it.

FFXIV's Confirm key is console-shaped: it drives world objects *and* menus, and it takes two presses
for a lever — one to target, one to use. So you press it repeatedly. **With any menu open, those
presses land in the menu**, drop a cursor on it, and activate whatever is under it. If you keep
consumables at the top of your bags for quick access, a key-and-wheel dungeon can eat a raid potion.

A dedicated key can't do that, because it isn't Confirm — there's no menu path to be routed down.

⭐ It also **never changes your target**, so you aren't left pointing at a lever afterwards. That
wasn't designed in; calling the interact function directly simply doesn't touch targeting, which the
logs confirmed across a full dungeon before the target save/restore got written. The fix was to
measure and then not build anything.

## Design notes

⭐ **The game already has both behaviours as separate functions** — `InteractWithObject` and
`OpenObjectInteraction`. The engine distinguishes "do the thing" from "open the interaction UI"; only
the input layer collapses them. Recording showed Confirm's keyboard path passes
`checkLineOfSight: true` and calls only the first, while the mouse-click path passes `false` and
pairs it with the second. This uses the keyboard path.

⚠ Two guards, covering different things: it refuses while you're **casting** (interactions produce a
cast bar), and refuses within **1s** of the last interact — because aetherytes have no cast bar, so
the state check alone has a hole.

⭐ Target selection asks the game first — soft target, then current target — and only scans for the
nearest interactable as a last resort. That scan is the one place a list of object kinds appears, so
it **logs every nearby candidate it rejects**: a missing kind shows up as a named object in the log
rather than as "the key does nothing near that thing."

## Building

```
dotnet build DeserokUtils/DeserokUtils.csproj -c Release
```

No setup — `Dalamud.NET.Sdk` resolves its references from the local XIVLauncher install. Output
lands in `DeserokUtils/bin/Release/DeserokUtils/`, which is also where a dev-plugin location should
point.

No package references, on purpose. `XivCommon 9.0.0` still wants the old `DalamudPluginInterface`
and the only thing needed from it was a chat-line send, which is four lines of ClientStructs.
