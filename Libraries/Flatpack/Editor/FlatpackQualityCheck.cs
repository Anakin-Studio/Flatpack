using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Measures how closely a bake matches its model: renders both in a private scene from a few angles at a range of
/// distances and counts the pixels that differ clearly. The same test as the iteration 3 to 5 benchmarks, cut down to
/// run in a second or two after every bake.
/// </summary>
/// <remarks>
/// Both are rendered through the Albedo debug view, so lighting can't hide or invent differences: what's left is shape
/// (missing thin parts, ghosting, holes) and colour, the failures that matter most for a card. Renders are made at the
/// pixel density of a 1080p screen at 60 degrees FOV, at twice the resolution and averaged down, like the benchmarks.
/// </remarks>
public static class FlatpackQualityCheck
{
	/// <summary>A pixel counts as clearly different past this much average channel difference (of 255).</summary>
	public const int BadPixelThreshold = 25;

	/// <summary>
	/// A bake holds up at a distance when its structural difference from the model (DSSIM, 0..1) is at most this much
	/// above its own floor: the difference that's left far away, where nothing more can be gained (lighting, texture
	/// noise). Calibrated by eye in iteration 5: the chair looked right from 6 radii, the tree from 6 to 8, the knight
	/// from 8. 0.10 reproduces all three; a fixed cut-off on any single score, or a ratio to the floor, didn't.
	/// </summary>
	public const float HoldsUpExcess = 0.10f;

	/// <summary>
	/// Whatever the structure score says, more than this share of clearly wrong pixels (a wire mesh filled in solid, a
	/// missing part) doesn't hold up.
	/// </summary>
	public const float HoldsUpBadShare = 0.25f;

	/// <summary>Distances checked, in bounding radii, nearest first.</summary>
	public static readonly float[] Distances = { 1.5f, 2f, 3f, 4f, 6f, 8f, 12f, 16f, 24f, 30f };

	/// <summary>Viewing directions averaged at each distance: four sides, a little above.</summary>
	static readonly Angles[] Views = { new( 12, 35, 0 ), new( 12, 125, 0 ), new( 12, 215, 0 ), new( 12, 305, 0 ) };

	public sealed class Report
	{
		/// <summary>Share of the object's pixels that differ clearly from the model, per entry of <see cref="Distances"/>.</summary>
		public float[] BadShare { get; init; }

		/// <summary>
		/// Structural difference from the model (1 - SSIM on luminance, 0..1) per entry of <see cref="Distances"/>. Unlike
		/// <see cref="BadShare"/> this sees blur and smearing, which is what makes a card look wrong up close.
		/// </summary>
		public float[] Difference { get; init; }

		/// <summary>The card's difference far away (median of 12 to 30 radii): the floor <see cref="HoldsUpFrom"/> is judged against.</summary>
		public float Floor { get; init; }

		/// <summary>
		/// Nearest distance (bounding radii) from which every checked distance out to 16 radii holds up (see
		/// <see cref="HoldsUpExcess"/> and <see cref="HoldsUpBadShare"/>), or <see cref="float.PositiveInfinity"/> when it
		/// never does.
		/// </summary>
		public float HoldsUpFrom { get; init; }

		/// <summary>
		/// The worst share of clearly different pixels from <paramref name="radii"/> outward (to the furthest checked
		/// distance): what a card swapped in at that distance shows at its worst.
		/// </summary>
		public float WorstFrom( float radii )
		{
			var worst = BadShareAt( radii );
			for ( var i = 0; i < Distances.Length; i++ )
			{
				if ( Distances[i] >= radii && !float.IsNaN( BadShare[i] ) )
					worst = MathF.Max( worst, BadShare[i] );
			}
			return worst;
		}

		/// <summary>One line for status bars and the inspector.</summary>
		public string Summary => float.IsPositiveInfinity( HoldsUpFrom )
			? "Doesn't match the model closely inside 16 radii: keep the swap distance generous."
			: $"Looks like the model from {HoldsUpFrom:0.#} bounding radii outward.";

		/// <summary>Share of clearly different pixels at <paramref name="radii"/>, interpolated between checked distances.</summary>
		public float BadShareAt( float radii )
		{
			if ( radii <= Distances[0] ) return BadShare[0];
			for ( var i = 1; i < Distances.Length; i++ )
			{
				if ( radii <= Distances[i] )
				{
					var t = (radii - Distances[i - 1]) / (Distances[i] - Distances[i - 1]);
					return BadShare[i - 1] + (BadShare[i] - BadShare[i - 1]) * t;
				}
			}
			return BadShare[^1];
		}
	}

	/// <summary>
	/// Compare <paramref name="impostor"/> against its source model. <paramref name="upTo"/> stops early, for a quick check
	/// at one distance (it still checks the distances below it).
	/// </summary>
	public static async Task<Report> MeasureAsync( FlatpackImpostor impostor, float upTo = float.MaxValue, CancellationToken cancel = default )
	{
		var scene = Scene.CreateEditorScene();
		scene.Name = "Flatpack Quality Check";
		var bad = new List<float>();
		var structure = new List<float>();

		try
		{
			ModelRenderer model, card;
			CameraComponent camera;

			using ( scene.Push() )
			{
				var a = new GameObject( true, "model" );
				model = a.Components.Create<ModelRenderer>();
				model.Model = impostor.SourceModel;
				model.LodOverride = 0;

				var b = new GameObject( true, "card" );
				card = b.Components.Create<ModelRenderer>();
				card.Model = impostor.CardModel;

				var eye = new GameObject( true, "camera" );
				camera = eye.Components.Create<CameraComponent>();
				camera.BackgroundColor = Color.Transparent;
				camera.DebugMode = SceneCameraDebugMode.Albedo;
			}

			var r = impostor.Radius;
			foreach ( var k in Distances )
			{
				if ( k > upTo && bad.Count > 0 )
					break;

				// Frame the bounding sphere with a little margin, at the pixel density a 1080p, 60 degree view would give.
				var halfAngle = MathF.Atan( 1.15f / k );
				var size = Math.Clamp( (int)(1080.0f * MathF.Tan( halfAngle ) / MathF.Tan( MathF.PI / 6.0f )), 24, 1024 );
				var target = new Bitmap( size * 2, size * 2, false );

				long badPixels = 0, objectPixels = 0;
				double dssimSum = 0;
				long dssimCount = 0;
				foreach ( var view in Views )
				{
					cancel.ThrowIfCancellationRequested();
					var dir = view.ToRotation().Backward;

					Color32[] m, c;
					using ( scene.Push() )
					{
						camera.FieldOfView = halfAngle * 2.0f * (180.0f / MathF.PI);
						camera.WorldPosition = impostor.Center + dir * r * k;
						camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );
						camera.ZNear = MathF.Max( 0.1f, r * (k - 1.2f) * 0.5f );
						camera.ZFar = r * (k + 2.0f);

						impostor.MarkAtlasesUsed();
						model.Enabled = true; card.Enabled = false;
						scene.EditorTick( 0.0f, 0.0f );
						camera.RenderToBitmap( target );
						m = target.GetPixels32();

						model.Enabled = false; card.Enabled = true;
						scene.EditorTick( 0.0f, 0.0f );
						camera.RenderToBitmap( target );
						c = target.GetPixels32();
					}

					Compare( m, c, size * 2, ref badPixels, ref objectPixels );
					Structure( m, c, size * 2, ref dssimSum, ref dssimCount );
					await Task.Delay( 1 );
				}

				bad.Add( objectPixels > 0 ? badPixels / (float)objectPixels : 0.0f );
				structure.Add( dssimCount > 0 ? (float)(dssimSum / dssimCount) : 0.0f );
			}
		}
		finally
		{
			scene.Destroy();
		}

		while ( bad.Count < Distances.Length )
		{
			bad.Add( float.NaN );
			structure.Add( float.NaN );
		}

		var far = new List<float>();
		for ( var i = 0; i < Distances.Length; i++ )
			if ( Distances[i] >= 12.0f && !float.IsNaN( structure[i] ) )
				far.Add( structure[i] );
		far.Sort();
		var floor = far.Count > 0 ? far[far.Count / 2] : float.NaN;

		var holds = float.PositiveInfinity;
		for ( var i = Distances.Length - 1; i >= 0; i-- )
		{
			if ( Distances[i] > 16.0f ) continue;
			if ( float.IsNaN( bad[i] ) || float.IsNaN( floor ) || bad[i] > HoldsUpBadShare || structure[i] > floor + HoldsUpExcess ) break;
			holds = Distances[i];
		}

		return new Report { BadShare = bad.ToArray(), Difference = structure.ToArray(), Floor = floor, HoldsUpFrom = holds };
	}

	/// <summary>
	/// Average 2 x 2 blocks of both renders (a stand-in for anti-aliasing), then count object pixels that differ clearly:
	/// <list type="bullet">
	/// <item>Shape: coverage blurred over 3 x 3 pixels differs by more than a quarter. Blurring forgives a thin wire
	/// drawn one pixel over, but not a part that's missing or a see-through mesh that filled in solid.</item>
	/// <item>Colour: both cover the pixel and the colours differ by more than <see cref="BadPixelThreshold"/>.</item>
	/// </list>
	/// A pixel belongs to the object when either render covers more than half of it.
	/// </summary>
	static void Compare( Color32[] m, Color32[] c, int size, ref long badPixels, ref long objectPixels )
	{
		var half = size / 2;
		var count = half * half;
		var mCover = new float[count];
		var cCover = new float[count];
		var mRgb = new int[count * 3];
		var cRgb = new int[count * 3];

		for ( var y = 0; y < half; y++ )
		{
			for ( var x = 0; x < half; x++ )
			{
				var o = y * half + x;
				for ( var dy = 0; dy < 2; dy++ )
				{
					for ( var dx = 0; dx < 2; dx++ )
					{
						var i = (y * 2 + dy) * size + x * 2 + dx;
						var pm = m[i]; var pc = c[i];
						mCover[o] += pm.a / (4.0f * 255.0f);
						cCover[o] += pc.a / (4.0f * 255.0f);
						mRgb[o * 3] += pm.r; mRgb[o * 3 + 1] += pm.g; mRgb[o * 3 + 2] += pm.b;
						cRgb[o * 3] += pc.r; cRgb[o * 3 + 1] += pc.g; cRgb[o * 3 + 2] += pc.b;
					}
				}
			}
		}

		for ( var y = 0; y < half; y++ )
		{
			for ( var x = 0; x < half; x++ )
			{
				var o = y * half + x;
				if ( mCover[o] <= 0.5f && cCover[o] <= 0.5f )
					continue;

				objectPixels++;

				float mBlur = 0, cBlur = 0, n = 0;
				for ( var dy = -1; dy <= 1; dy++ )
				{
					for ( var dx = -1; dx <= 1; dx++ )
					{
						int yy = y + dy, xx = x + dx;
						if ( yy < 0 || yy >= half || xx < 0 || xx >= half ) continue;
						mBlur += mCover[yy * half + xx];
						cBlur += cCover[yy * half + xx];
						n++;
					}
				}

				if ( MathF.Abs( mBlur - cBlur ) / n > 0.25f )
				{
					badPixels++;
					continue;
				}

				if ( mCover[o] > 0.5f && cCover[o] > 0.5f )
				{
					var diff = (Math.Abs( mRgb[o * 3] - cRgb[o * 3] ) + Math.Abs( mRgb[o * 3 + 1] - cRgb[o * 3 + 1] ) + Math.Abs( mRgb[o * 3 + 2] - cRgb[o * 3 + 2] )) / 12.0f;
					if ( diff > BadPixelThreshold )
						badPixels++;
				}
			}
		}
	}

	/// <summary>
	/// Structural difference (1 - SSIM, Wang et al. 2004) between the two renders' luminance, Gaussian window of 1.5
	/// pixels, at half resolution like <see cref="Compare"/>. Summed over pixels near the object (either render covers
	/// something within a few pixels); uncovered pixels count as the benchmarks' mid grey.
	/// </summary>
	static void Structure( Color32[] m, Color32[] c, int size, ref double sum, ref long count )
	{
		var half = size / 2;
		var n = half * half;
		var a = new float[n];
		var b = new float[n];
		var cover = new float[n];

		for ( var y = 0; y < half; y++ )
		{
			for ( var x = 0; x < half; x++ )
			{
				float la = 0, lb = 0, cv = 0;
				for ( var dy = 0; dy < 2; dy++ )
				{
					for ( var dx = 0; dx < 2; dx++ )
					{
						var i = (y * 2 + dy) * size + x * 2 + dx;
						la += Luma( m[i] );
						lb += Luma( c[i] );
						cv += Math.Max( m[i].a, c[i].a ) / 255.0f;
					}
				}

				var o = y * half + x;
				a[o] = la * 0.25f;
				b[o] = lb * 0.25f;
				cover[o] = cv * 0.25f;
			}
		}

		var muA = Blur( a, half );
		var muB = Blur( b, half );
		var aa = new float[n];
		var bb = new float[n];
		var ab = new float[n];
		for ( var i = 0; i < n; i++ )
		{
			aa[i] = a[i] * a[i];
			bb[i] = b[i] * b[i];
			ab[i] = a[i] * b[i];
		}

		var sAA = Blur( aa, half );
		var sBB = Blur( bb, half );
		var sAB = Blur( ab, half );
		var near = Blur( cover, half );

		const float c1 = (0.01f * 255.0f) * (0.01f * 255.0f);
		const float c2 = (0.03f * 255.0f) * (0.03f * 255.0f);
		for ( var i = 0; i < n; i++ )
		{
			if ( near[i] < 0.02f )
				continue;

			var va = sAA[i] - muA[i] * muA[i];
			var vb = sBB[i] - muB[i] * muB[i];
			var cov = sAB[i] - muA[i] * muB[i];
			var ssim = ((2 * muA[i] * muB[i] + c1) * (2 * cov + c2)) / ((muA[i] * muA[i] + muB[i] * muB[i] + c1) * (va + vb + c2));
			sum += 1.0 - ssim;
			count++;
		}
	}

	static float Luma( Color32 p ) => p.a > 127 ? p.r * 0.2126f + p.g * 0.7152f + p.b * 0.0722f : 128.0f;

	static readonly float[] Kernel = MakeKernel( 1.5f, 4 );

	static float[] MakeKernel( float sigma, int radius )
	{
		var k = new float[radius * 2 + 1];
		var total = 0.0f;
		for ( var i = -radius; i <= radius; i++ )
		{
			k[i + radius] = MathF.Exp( -i * i / (2 * sigma * sigma) );
			total += k[i + radius];
		}

		for ( var i = 0; i < k.Length; i++ )
			k[i] /= total;
		return k;
	}

	/// <summary>Separable Gaussian blur of a square image, clamped at the edges.</summary>
	static float[] Blur( float[] src, int w )
	{
		var r = Kernel.Length / 2;
		var tmp = new float[src.Length];
		var dst = new float[src.Length];
		for ( var y = 0; y < w; y++ )
		{
			for ( var x = 0; x < w; x++ )
			{
				float v = 0;
				for ( var k = -r; k <= r; k++ )
					v += src[y * w + Math.Clamp( x + k, 0, w - 1 )] * Kernel[k + r];
				tmp[y * w + x] = v;
			}
		}

		for ( var y = 0; y < w; y++ )
		{
			for ( var x = 0; x < w; x++ )
			{
				float v = 0;
				for ( var k = -r; k <= r; k++ )
					v += tmp[Math.Clamp( y + k, 0, w - 1 ) * w + x] * Kernel[k + r];
				dst[y * w + x] = v;
			}
		}

		return dst;
	}
}
