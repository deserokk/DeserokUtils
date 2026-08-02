# DeserokUtils

Small FFXIV utilities, one feature per tab. `/dsu` or `/deserokutils` opens the window.

## Install

Add this to Dalamud's **Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/deserokk/DeserokUtils/main/repo/pluginmaster.json
```

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

## Building

```
dotnet build DeserokUtils/DeserokUtils.csproj -c Release
```

No setup — `Dalamud.NET.Sdk` resolves its references from the local XIVLauncher install. Output
lands in `DeserokUtils/bin/Release/DeserokUtils/`, which is also where a dev-plugin location should
point.

No package references, on purpose. `XivCommon 9.0.0` still wants the old `DalamudPluginInterface`
and the only thing needed from it was a chat-line send, which is four lines of ClientStructs.
