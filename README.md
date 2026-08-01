# CastWatch

Gate a macro line on whether an action **actually went off**.

A vanilla FFXIV macro has no conditionals — `/ac` and `/p` are independent lines, so the callout
fires whether the spell cast, was out of range, was on cooldown, or never started. That's the macro
spam everyone complains about, and it matters most in exactly the case you want it least: declaring
a rez in two-healer content.

```
/watch Aurora
/ac Aurora <t>
/wait 1
/ifwatch
/echo Aurora on <t.name>
```

`/ifwatch` cancels the macro when the watched action didn't go off. Every line after it is an
ordinary vanilla macro line that **the game** sends.

## It never sends anything for you

This is subtractive by design. The plugin cannot add chat — it can only stop a line you already
wrote. The one thing it transmits is `/macrocancel`, which is client-side and produces no chat.

## Commands

| command | |
|---|---|
| `/watch <action name>` | arm a watch. Put it above the `/ac`. Name it exactly as the hotbar does |
| `/watch` | report what's armed and whether it fired |
| `/watch off` | disarm |
| `/ifwatch` | continue if the action went off or is being cast; otherwise cancel the macro |

## Why a hook and not a cast-bar check

An instant cast — Swiftcast Raise, or any oGCD like Aurora — never produces an observable cast bar,
so `IsCasting` is false for exactly the case this exists to catch. `/ifwatch` passes on **either**
signal:

- the hook saw `ActionManager.UseAction` accept the watched action, **or**
- you're currently casting it (the hardcast case, where nothing has resolved yet)

⚠ The hook sees *the client accepted and sent the action*, not that the server resolved it. For a
callout that's arguably what you want — you're announcing at the moment you commit, not eight
seconds later when it lands.

## Design notes

**One watch slot, no named channels.** The game runs one macro at a time — starting another cancels
the first — so a second slot could never be reached.

**Arming replaces the previous arm.** That's what makes a double-press clean: every macro run starts
from a known state instead of inheriting the last one's result.

**`/ifwatch` is one-shot** — reading disarms — and an arm auto-expires after **10 seconds**. A macro
that arms and is then interrupted before its check can't leak a callout into some unrelated macro
later.

**Four outcomes, four distinct messages.** Nothing armed, arm expired, armed-but-didn't-fire, and
fired are all different things and none of them look alike. The first two report an authoring
problem and deliberately do **not** cancel the macro; only a genuine miss suppresses. If they all
silently failed the same way, deleting line 1 would look identical to the spell fizzling.

**A typo fails at `/watch`, not at `/ifwatch`.** An unknown action name refuses to arm and says so,
rather than arming a watch that can never fire.

**Idle cost is two comparisons** per `UseAction` call, which happens a few times a second at most.

## Building

```
dotnet build CastWatch/CastWatch.csproj -c Release
```

No setup — `Dalamud.NET.Sdk` resolves its references from the local XIVLauncher install. Output is
`CastWatch/bin/Release/CastWatch/`.

To test: Dalamud Settings → Experimental → **Dev Plugin Locations** → add that folder.

No package references, on purpose. `XivCommon 9.0.0` still wants the old `DalamudPluginInterface`
and the only thing needed from it was a chat-line send, which is four lines of ClientStructs.
