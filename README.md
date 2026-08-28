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
| **CastWatch** | a consistent way to announce spells | `/watch` `/ifwatch` |
| **Mouseover** | mouseover macros in one line, with a fallback | `/ifmo` |
| **FateWatch** | a timer for FATEs that run on a cycle | `/fatewatch` |
| **FcBuffs** | keeps the Free Company buffs topped up | `/fcbuffs` |
| **DrawSheathe** | a draw/sheathe key that incorporates the Gold Saucer emotes | `/drawsheathe` |
| **EmoteQuiet** | stops emote spam in your log | `/emotequiet` |
| **Interact** | an interact key that ignores open menus and advances dialogue | `/dsuinteract` |
| **EphemeralMarks** | private markers for the people you queued in with | `/dsumarks` |
| **DebuffMarks** | a private icon over anyone with a debuff you name | `/dsudebuffs` |
| **AchievementData** | makes achievement links in chat work | none |
| **Keybinds** | bind these to a key instead of a hotbar macro | none |

## Building

```
dotnet build DeserokUtils/DeserokUtils.csproj -c Release
```

No setup — `Dalamud.NET.Sdk` resolves its references from the local XIVLauncher install. Output lands
in `DeserokUtils/bin/Release/DeserokUtils/`, which is also where a dev-plugin location should point.

No package references, on purpose: `XivCommon 9.0.0` still wants the old `DalamudPluginInterface`, and
the only thing needed from it was a chat-line send, which is four lines of ClientStructs.

⭐ The reasoning behind each feature lives in the source comments, which are heavier than the code.
