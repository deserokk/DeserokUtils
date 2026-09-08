using System;

namespace DeserokUtils.Features.Fanfare.Notify;

internal static class Easing {

	internal static float Phase(float now, float start, float duration) {
		if (duration <= 0f)
			return now >= start ? 1f : 0f;
		return Math.Clamp((now - start) / duration, 0f, 1f);
	}

	internal static float Lerp(float a, float b, float t) => a + ((b - a) * t);

	internal static float EaseInOut(float t) => CubicBezier(0.42f, 0f, 0.58f, 1f, t);

	internal static float CubicBezier(float x1, float y1, float x2, float y2, float x) {
		if (x <= 0f)
			return 0f;
		if (x >= 1f)
			return 1f;

		var t = x;
		for (var i = 0; i < 8; i++) {
			var error = BezierAxis(t, x1, x2) - x;
			if (Math.Abs(error) < 1e-5f)
				return BezierAxis(t, y1, y2);

			var slope = BezierAxisDerivative(t, x1, x2);
			if (Math.Abs(slope) < 1e-6f)
				break;
			t -= error / slope;
		}

		float low = 0f, high = 1f;
		t = x;
		for (var i = 0; i < 24; i++) {
			var current = BezierAxis(t, x1, x2);
			if (Math.Abs(current - x) < 1e-5f)
				break;
			if (current < x)
				low = t;
			else
				high = t;
			t = (low + high) / 2f;
		}

		return BezierAxis(t, y1, y2);
	}

	private static float BezierAxis(float t, float a, float b) {
		var inv = 1f - t;
		return (3f * inv * inv * t * a) + (3f * inv * t * t * b) + (t * t * t);
	}

	private static float BezierAxisDerivative(float t, float a, float b) {
		var inv = 1f - t;
		return (3f * inv * inv * a) + (6f * inv * t * (b - a)) + (3f * t * t * (1f - b));
	}
}
