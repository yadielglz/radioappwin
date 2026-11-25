using System;

namespace RadioApp.Audio;

/// <summary>
/// Numerically stable stereo biquad filter (Transposed Direct Form II).
/// </summary>
public sealed class StereoBiquadFilter
{
	private double _b0 = 1.0, _b1, _b2, _a1, _a2;
	private double _z1L, _z2L;
	private double _z1R, _z2R;

	public void SetCoefficients(double b0, double b1, double b2, double a0, double a1, double a2)
	{
		if (Math.Abs(a0) < 1e-12) a0 = 1.0;
		_b0 = b0 / a0;
		_b1 = b1 / a0;
		_b2 = b2 / a0;
		_a1 = a1 / a0;
		_a2 = a2 / a0;
	}

	public void ConfigureLowPass(double sampleRate, double cutoffHz, double q = 0.7071067811865476)
	{
		double clampedHz = Math.Clamp(cutoffHz, 20.0, sampleRate * 0.45);
		double w0 = 2.0 * Math.PI * clampedHz / sampleRate;
		double cosW = Math.Cos(w0);
		double sinW = Math.Sin(w0);
		double alpha = sinW / (2.0 * q);
		double b0 = (1.0 - cosW) * 0.5;
		double b1 = 1.0 - cosW;
		double b2 = (1.0 - cosW) * 0.5;
		double a0 = 1.0 + alpha;
		double a1 = -2.0 * cosW;
		double a2 = 1.0 - alpha;
		SetCoefficients(b0, b1, b2, a0, a1, a2);
	}

	public void ConfigureHighPass(double sampleRate, double cutoffHz, double q = 0.7071067811865476)
	{
		double clampedHz = Math.Clamp(cutoffHz, 20.0, sampleRate * 0.45);
		double w0 = 2.0 * Math.PI * clampedHz / sampleRate;
		double cosW = Math.Cos(w0);
		double sinW = Math.Sin(w0);
		double alpha = sinW / (2.0 * q);
		double b0 = (1.0 + cosW) * 0.5;
		double b1 = -(1.0 + cosW);
		double b2 = (1.0 + cosW) * 0.5;
		double a0 = 1.0 + alpha;
		double a1 = -2.0 * cosW;
		double a2 = 1.0 - alpha;
		SetCoefficients(b0, b1, b2, a0, a1, a2);
	}

	public void ConfigureHighShelf(double sampleRate, double freqHz, double gainDb, double q = 0.7071067811865476)
	{
		double a = Math.Pow(10.0, gainDb / 40.0);
		double w0 = 2.0 * Math.PI * Math.Clamp(freqHz, 20.0, sampleRate * 0.45) / sampleRate;
		double cosW = Math.Cos(w0);
		double sinW = Math.Sin(w0);
		double alpha = sinW / (2.0 * q);
		double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

		double b0 = a * ((a + 1.0) + (a - 1.0) * cosW + twoSqrtAAlpha);
		double b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW);
		double b2 = a * ((a + 1.0) + (a - 1.0) * cosW - twoSqrtAAlpha);
		double a0 = (a + 1.0) - (a - 1.0) * cosW + twoSqrtAAlpha;
		double a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW);
		double a2 = (a + 1.0) - (a - 1.0) * cosW - twoSqrtAAlpha;
		SetCoefficients(b0, b1, b2, a0, a1, a2);
	}

	public void Reset()
	{
		_z1L = _z2L = _z1R = _z2R = 0.0;
	}

	public void ProcessFrame(double inL, double inR, out double outL, out double outR)
	{
		outL = _b0 * inL + _z1L;
		_z1L = _b1 * inL - _a1 * outL + _z2L;
		_z2L = _b2 * inL - _a2 * outL;

		outR = _b0 * inR + _z1R;
		_z1R = _b1 * inR - _a1 * outR + _z2R;
		_z2R = _b2 * inR - _a2 * outR;
	}
}

/// <summary>
/// ITU-R BS.1770-4 / EBU R128 K-weighting filter (Stage 1 pre-filter shelf + Stage 2 RLB high-pass).
/// </summary>
public sealed class EbuR128KWeightingFilter
{
	private readonly StereoBiquadFilter _stage1Shelf = new StereoBiquadFilter();
	private readonly StereoBiquadFilter _stage2HighPass = new StereoBiquadFilter();

	public EbuR128KWeightingFilter(int sampleRate = 44100)
	{
		// ITU-R BS.1770-4 analog prototype parameters via bilinear transform
		if (sampleRate == 48000)
		{
			_stage1Shelf.SetCoefficients(1.53512485958697, -2.69169618940638, 1.19839281085285, 1.0, -1.69065929318241, 0.73248077421585);
			_stage2HighPass.SetCoefficients(1.0, -2.0, 1.0, 1.0, -1.99004745483398, 0.99007225036621);
		}
		else
		{
			// Analytical bilinear transform for arbitrary sample rates (e.g. 44100 Hz)
			const double f0 = 1681.974450955533;
			const double gDb = 3.999843853973347;
			const double q = 0.7071752369554196;
			double k = Math.Tan(Math.PI * f0 / sampleRate);
			double vh = Math.Pow(10.0, gDb / 20.0);
			double vb = Math.Pow(vh, 0.4996667741545416);
			double a0 = 1.0 + k / q + k * k;
			double b0 = (vh + vb * k / q + k * k) / a0;
			double b1 = 2.0 * (k * k - vh) / a0;
			double b2 = (vh - vb * k / q + k * k) / a0;
			double a1 = 2.0 * (k * k - 1.0) / a0;
			double a2 = (1.0 - k / q + k * k) / a0;
			_stage1Shelf.SetCoefficients(b0, b1, b2, 1.0, a1, a2);

			const double fHp = 38.13547087602444;
			const double qHp = 0.5003270373238773;
			double k2 = Math.Tan(Math.PI * fHp / sampleRate);
			double a0Hp = 1.0 + k2 / qHp + k2 * k2;
			_stage2HighPass.SetCoefficients(
				1.0 / a0Hp,
				-2.0 / a0Hp,
				1.0 / a0Hp,
				1.0,
				2.0 * (k2 * k2 - 1.0) / a0Hp,
				(1.0 - k2 / qHp + k2 * k2) / a0Hp);
		}
	}

	public void Reset()
	{
		_stage1Shelf.Reset();
		_stage2HighPass.Reset();
	}

	public void ProcessFrame(float inL, float inR, out double outL, out double outR)
	{
		_stage1Shelf.ProcessFrame(inL, inR, out double midL, out double midR);
		_stage2HighPass.ProcessFrame(midL, midR, out outL, out outR);
	}
}

/// <summary>
/// 4th-order Linkwitz-Riley (LR24) stereo crossover.
/// Low + High bands are in-phase and sum to flat unity magnitude (0 dB ripple),
/// ideal for transparent bass-swap DJ/AutoMix transitions.
/// </summary>
public sealed class LinkwitzRileyCrossover
{
	private readonly StereoBiquadFilter _lp1 = new StereoBiquadFilter();
	private readonly StereoBiquadFilter _lp2 = new StereoBiquadFilter();
	private readonly StereoBiquadFilter _hp1 = new StereoBiquadFilter();
	private readonly StereoBiquadFilter _hp2 = new StereoBiquadFilter();

	public double CrossoverHz { get; private set; }

	public LinkwitzRileyCrossover(int sampleRate = 44100, double crossoverHz = 180.0)
	{
		Configure(sampleRate, crossoverHz);
	}

	public void Configure(int sampleRate, double crossoverHz)
	{
		CrossoverHz = Math.Clamp(crossoverHz, 60.0, 500.0);
		const double butterworthQ = 0.7071067811865476;
		_lp1.ConfigureLowPass(sampleRate, CrossoverHz, butterworthQ);
		_lp2.ConfigureLowPass(sampleRate, CrossoverHz, butterworthQ);
		_hp1.ConfigureHighPass(sampleRate, CrossoverHz, butterworthQ);
		_hp2.ConfigureHighPass(sampleRate, CrossoverHz, butterworthQ);
	}

	public void Reset()
	{
		_lp1.Reset();
		_lp2.Reset();
		_hp1.Reset();
		_hp2.Reset();
	}

	public void SplitFrame(float inL, float inR, out float lowL, out float lowR, out float highL, out float highR)
	{
		_lp1.ProcessFrame(inL, inR, out double l1, out double r1);
		_lp2.ProcessFrame(l1, r1, out double l2, out double r2);
		_hp1.ProcessFrame(inL, inR, out double h1, out double hr1);
		_hp2.ProcessFrame(h1, hr1, out double h2, out double hr2);
		lowL = (float)l2;
		lowR = (float)r2;
		highL = (float)h2;
		highR = (float)hr2;
	}
}
