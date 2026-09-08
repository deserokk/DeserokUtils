using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;

using Newtonsoft.Json;

namespace DeserokUtils;

public sealed class ChatColourOverride {
	public string Who { get; set; } = string.Empty;

	public string World { get; set; } = string.Empty;

	public ushort Colour { get; set; }
}

public sealed class MarkSnapshot {

	public ulong Owner { get; set; }

	public DateTime CapturedUtc { get; set; }

	public List<MarkSnapshotMember> Members { get; set; } = new();
}

public sealed class MarkSnapshotMember {
	public string Name { get; set; } = string.Empty;

	public uint World { get; set; }

	public bool Leader { get; set; }

	public int Slot { get; set; }
}

public sealed class DebuffMark {
	public bool Enabled { get; set; } = true;

	public string Status { get; set; } = string.Empty;

	public bool MineOnly { get; set; } = true;

	public Features.EphemeralMarks.MarkShape Shape { get; set; } = Features.EphemeralMarks.MarkShape.Icon;

	public int Glyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Skull;

	public System.Numerics.Vector4 Colour { get; set; } = new(1f, 0.35f, 0.2f, 1f);
}

public sealed class MarkOverride {

	public string Who { get; set; } = string.Empty;

	public Features.EphemeralMarks.MarkShape Shape { get; set; } = Features.EphemeralMarks.MarkShape.Icon;

	public int Glyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Heart;

	public System.Numerics.Vector4? Colour { get; set; }
}

[Serializable]
public sealed class FateRotation {

	public string Zone { get; set; } = string.Empty;

	public uint Territory { get; set; }

	public List<string> Members { get; set; } = new();

	public Dictionary<string, string> Labels { get; set; } = new();

	public double SlotMinutes { get; set; } = 30.0;
}

[Serializable]
public sealed class Configuration: IPluginConfiguration {

	public int Version { get; set; } = 1;

	public const int CurrentVersion = 6;

	private const ObjectCreationHandling ReplaceList = ObjectCreationHandling.Replace;

	public bool Verbose { get; set; } = false;

	public uint UiAccentRgb { get; set; } = 0x3D87EB;

	public int UiSize { get; set; } = 1;

	public float UiScale { get; set; } = 1f;

	public bool DresserOverlay { get; set; } = false;

	public bool DresserTooltip { get; set; } = false;

	public bool DresserArmoire { get; set; } = false;

	public bool FateWatchEnabled { get; set; } = false;

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<FateRotation> Rotations { get; set; } = new() {
		new FateRotation {
			Zone = "The Occult Crescent: South Horn",
			Territory = 1252,
			Members = new List<string> { "Persistent Pots", "Pleading Pots" },
			SlotMinutes = 30.0,
		},
		new FateRotation {
			Zone = "The Occult Crescent: North Horn",
			Territory = 1346,
			Members = new List<string> { "Daylight Pottery", "In a Pot of Bother" },
			Labels = new Dictionary<string, string> {
				["Daylight Pottery"] = "N",
				["In a Pot of Bother"] = "S",
			},
			SlotMinutes = 30.0,
		},
	};

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<double> AlertMinutes { get; set; } = new() { 10, 5 };

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, long> LastSeen { get; set; } = new();

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, List<double>> MeasuredIntervals { get; set; } = new();

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, uint> LastSeenTerritory { get; set; } = new();

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, uint> LastSeenInstance { get; set; } = new();

	public bool DtrEnabled { get; set; } = true;

	public bool FcBuffsEnabled { get; set; } = false;

	public bool FcBuffsDryRun { get; set; } = false;

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<string> FcBuffActions { get; set; } = new();

	public int FcBuffLowStockWarning { get; set; } = 2;

	public int FcBuffCheckSeconds { get; set; } = 60;

	public const string DefaultDrawCommand = "/draw motion";

	public const string DefaultSheatheCommand = "/sheathe motion";

	public bool MeldWindowKeepOpen { get; set; } = false;

	public bool MeldAutoAccept { get; set; } = false;

	public bool RepairAutoAccept { get; set; } = false;

	public bool MacroItemIcons { get; set; } = false;

	public bool MacroNameIcons { get; set; } = false;

	public bool EmoteQuietEnabled { get; set; } = false;

	public int EmoteQuietWindowSeconds { get; set; } = 60;

	public bool EmoteQuietIncomingEnabled { get; set; } = false;

	public bool MarksEnabled { get; set; } = false;

	public int MarksMaxGroupSize { get; set; } = 5;

	public MarkSnapshot? MarksLastParty { get; set; }

	public bool MarksInPvp { get; set; } = true;

	public bool MarksInFieldOps { get; set; } = true;

	public bool MarksInAllianceRaid { get; set; } = true;

	public bool MarksInDeepDungeon { get; set; } = true;

	public bool DebuffMarksEnabled { get; set; } = false;

	public float DebuffMarksScale { get; set; } = 1f;

	public float DebuffMarksHeight { get; set; } = 2.0f;

	public float DebuffMarksLift { get; set; } = 34f;

	public bool DebuffMarksPreview { get; set; }

	public bool MarksPreview { get; set; }

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<DebuffMark> DebuffMarks { get; set; } = new();

	public Features.EphemeralMarks.MarkShape MarksLeaderShape { get; set; } =
		Features.EphemeralMarks.MarkShape.Star;

	public Features.EphemeralMarks.MarkShape MarksMemberShape { get; set; } =
		Features.EphemeralMarks.MarkShape.Reticle;

	public int MarksLeaderGlyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Star;

	public int MarksMemberGlyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Heart;

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<MarkOverride> MarksOverrides { get; set; } = new();

	public bool MarksShowTag { get; set; } = false;

	public float MarksHeight { get; set; } = 2.0f;

	public float MarksLift { get; set; } = 34f;

	public float MarksScale { get; set; } = 2f;

	public System.Numerics.Vector4 MarksColour { get; set; } = new(1f, 0.32f, 0.85f, 1f);

	public bool AlertToast { get; set; } = true;
	public bool AlertChat { get; set; } = true;
	public bool AlertSound { get; set; } = true;

	public bool InteractAnswerGimmicks { get; set; } = true;

	public bool InteractAdvanceTalk { get; set; } = true;

	public bool InteractRidePillion { get; set; } = true;

	public Input.Keybind InteractKey { get; set; } = new();

	public Input.Keybind DrawSheatheKey { get; set; } = new();

	public Input.Keybind OpenWindowKey { get; set; } = new();

	public bool PartyJobsEnabled { get; set; } = false;

	public bool ChatColours { get; set; } = false;

	public bool ChatColoursOnlyKnown { get; set; }

	public bool ChatColourOwnName { get; set; } = false;

	public bool EarshotEnabled { get; set; }

	public bool FoodEnabled { get; set; }

	public bool FoodSayOnLapse { get; set; } = true;

	public string FoodMessage { get; set; } = "No food buff";

	public bool FoodBoopOnDuty { get; set; } = true;

	public float FoodVolume { get; set; } = 0.05f;

	public bool FoodBoopOverworld { get; set; }

	public string FoodSoundPath { get; set; } = string.Empty;

	public bool FoodSayPeriodically { get; set; }

	public int FoodNagMinutes { get; set; } = 5;

	public bool FoodAtMaxLevel { get; set; }

	public bool FoodShowIcon { get; set; }

	public float FoodIconScale { get; set; } = 1f;

	public float FoodIconAlpha { get; set; } = 0.75f;

	public bool FoodIconPreview { get; set; }

	public bool EarshotFirstName { get; set; } = true;

	public bool EarshotLastName { get; set; } = true;

	public bool EarshotFullName { get; set; } = true;

	public string EarshotCustom { get; set; } = string.Empty;

	public int EarshotSound { get; set; } = 1;

	public string EarshotSoundPath { get; set; } = Features.Earshot.EarshotFeature.BuiltInBell;

	public float EarshotVolume { get; set; } = 0.05f;

	public int EarshotCooldown { get; set; } = 15;

	public ushort EarshotHighlight { get; set; }

	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<ChatColourOverride> ChatColourOverrides { get; set; } = new();

	public bool PartyJobsPoll { get; set; } = false;

	public void Migrate() {

		foreach (var rot in this.Rotations) {
			int before = rot.Members.Count;
			rot.Members = rot.Members
				.Where(m => !string.IsNullOrWhiteSpace(m))
				.Select(m => m.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (rot.Members.Count != before)
				Plugin.Log.Warning(
					$"PotWatch: removed {before - rot.Members.Count} duplicate member(s) from the {rot.Zone} rotation");
		}

		if (this.Version < 4) {

			int alerts = this.AlertMinutes.Count;

			this.AlertMinutes = this.AlertMinutes.Distinct().OrderByDescending(m => m).ToList();

			this.FcBuffActions = this.FcBuffActions
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.Select(a => a.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (alerts != this.AlertMinutes.Count)
				Plugin.Log.Warning(
					$"config repair: alert thresholds {alerts} -> {this.AlertMinutes.Count} "
					+ "(Newtonsoft was appending defaults to the saved list on every load)");
		}

		if (this.Version >= CurrentVersion)
			return;

		if (this.Version < 6) {

			var retired = new Dictionary<int, Dalamud.Interface.FontAwesomeIcon> {
				[2] = Dalamud.Interface.FontAwesomeIcon.Heart,
				[3] = Dalamud.Interface.FontAwesomeIcon.Circle,
				[4] = Dalamud.Interface.FontAwesomeIcon.Play,
				[5] = Dalamud.Interface.FontAwesomeIcon.Square,
				[6] = Dalamud.Interface.FontAwesomeIcon.Times,
			};

			if (retired.TryGetValue((int)this.MarksLeaderShape, out var lead)) {
				this.MarksLeaderShape = Features.EphemeralMarks.MarkShape.Icon;
				this.MarksLeaderGlyph = (int)lead;
			}

			if (retired.TryGetValue((int)this.MarksMemberShape, out var member)) {
				this.MarksMemberShape = Features.EphemeralMarks.MarkShape.Icon;
				this.MarksMemberGlyph = (int)member;
			}

			foreach (var over in this.MarksOverrides) {
				if (!retired.TryGetValue((int)over.Shape, out var glyph))
					continue;
				over.Shape = Features.EphemeralMarks.MarkShape.Icon;
				over.Glyph = (int)glyph;
			}
		}

		if (this.Version < 3) {

			this.Verbose = false;
		}

		this.Version = CurrentVersion;
		this.Save();
		Plugin.Log.Information($"config migrated to v{CurrentVersion}: FATE rotations are now per-territory");
	}

	public bool DresserSkipDyed { get; set; } = false;

	public Features.Fanfare.FanfareSettings Fanfare { get; set; } = new();

	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
