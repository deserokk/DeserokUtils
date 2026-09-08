using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using NAudio.Wave;

namespace DeserokUtils.Features.Fanfare.Notify;

internal sealed class SoundPlayer: IDisposable {
	private readonly object gate = new();

	private readonly HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);

	private WaveOutEvent? output;
	private AudioFileReader? reader;
	private bool disposed;

	internal void Play(string path, float volume) {
		if (string.IsNullOrWhiteSpace(path) || this.disposed)
			return;

		if (!File.Exists(path)) {
			lock (this.gate) {
				if (!this.reported.Add(path))
					return;
			}

			Plugin.Chat.PrintError($"[Fanfare] sound file not found: {path}");
			return;
		}

		Task.Run(() => this.Start(path, Math.Clamp(volume, 0f, 1f)));
	}

	private void Start(string path, float volume) {
		try {
			lock (this.gate) {
				if (this.disposed)
					return;

				this.StopLocked();

				var newReader = new AudioFileReader(path) { Volume = volume };
				var newOutput = new WaveOutEvent();
				newOutput.Init(newReader);

				newOutput.PlaybackStopped += (_, _) => {
					lock (this.gate) {
						if (!ReferenceEquals(this.output, newOutput))
							return;
						this.output = null;
						this.reader = null;
					}

					newOutput.Dispose();
					newReader.Dispose();
				};

				this.output = newOutput;
				this.reader = newReader;
				newOutput.Play();
			}
		} catch (Exception ex) {

			lock (this.gate)
				this.reported.Add(path);

			Plugin.Log.Error(ex, $"sound: failed to play {path}");
		}
	}

	internal void Stop() {
		lock (this.gate)
			this.StopLocked();
	}

	internal void ForgetFailures() {
		lock (this.gate)
			this.reported.Clear();
	}

	private void StopLocked() {
		var oldOutput = this.output;
		var oldReader = this.reader;
		this.output = null;
		this.reader = null;

		try {
			oldOutput?.Stop();
		} catch (Exception ex) {
			Plugin.Log.Warning(ex, "sound: stop failed");
		}

		oldOutput?.Dispose();
		oldReader?.Dispose();
	}

	public void Dispose() {
		lock (this.gate) {
			this.disposed = true;
			this.StopLocked();
		}
	}
}
