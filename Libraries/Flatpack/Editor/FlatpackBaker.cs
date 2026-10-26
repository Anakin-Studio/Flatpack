using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Bakes a model into a Flatpack impostor: albedo, normal, depth and roughness + AO atlases, a frame lookup texture, a
/// material using <c>flatpack_impostor.shader</c>, and a <see cref="FlatpackImpostor"/> resource that ties them together.
/// </summary>
/// <remarks>
/// <para>
/// The model is rendered alone in a throwaway scene with an orthographic camera, once per frame of the
/// N x N grid. Each camera is fitted to that view's outline and depth range (stored in the frame lookup texture), so the
/// object fills its frame. Each pass uses one of the engine's camera debug views, which write material data straight to
/// the target with no lighting, exposure or tonemapping:
/// </para>
/// <list type="bullet">
/// <item>Albedo: the colour texture as sRGB bytes, alpha = coverage.</item>
/// <item>NormalMap: the normal-mapped world normal packed as n * 0.5 + 0.5. The model sits at the origin with no
/// rotation, so world space is object space.</item>
/// <item>Roughness, AmbientOcclusion: linear values as bytes.</item>
/// <item>Depth: there is no depth view, so <c>flatpack_bake_depth.shader</c> goes on as a material override and
/// writes linear depth as albedo (16 bits split over red and green), read back through the Albedo view.</item>
/// </list>
/// </remarks>
public static class FlatpackBaker
{
	/// <summary>
	/// The runtime impostor shader the generated material uses.
	/// </summary>
	public const string ImpostorShader = "shaders/flatpack_impostor.shader";

	const string DepthShader = "shaders/flatpack_bake_depth.shader";

	/// <summary>Opacity at which a supersample counts as covered: the usual alpha-test cut of s&amp;box materials.</summary>
	const byte AlphaTestCut = 128;

	/// <summary>
	/// Bakes whose covered texels are more than this share partly covered are see-through detail. Measured on the test
	/// set: fence 0.75, weed tree 0.50, everything solid or leafy 0.06 to 0.24.
	/// </summary>
	public const float SeeThroughShare = 0.4f;

	/// <summary>
	/// Framed bakes whose covered texels are more than this share partly covered are fine wire mesh and get baked again
	/// unframed. Measured framed in iteration 8: fence 0.59, weed tree 0.42, solid and leafy models 0.10 or less.
	/// </summary>
	const float FineMeshShare = 0.5f;

	/// <summary>
	/// What a bake wrote.
	/// </summary>
	public sealed class Result
	{
		/// <summary>The <c>.fpack</c> resource, loaded.</summary>
		public FlatpackImpostor Impostor { get; init; }

		/// <summary>Asset-relative path of the <c>.fpack</c>.</summary>
		public string ImpostorPath { get; init; }

		/// <summary>Asset-relative path of the generated <c>.vmat</c>.</summary>
		public string MaterialPath { get; init; }

		/// <summary>Absolute paths of every file written.</summary>
		public IReadOnlyList<string> Files { get; init; }

		/// <summary>Wall-clock bake time.</summary>
		public TimeSpan Duration { get; init; }

		/// <summary>The quality level the bake ended up at (Auto picks one).</summary>
		public FlatpackQuality Level { get; init; }

		/// <summary>How closely the card matches the model by distance, when the bake was measured.</summary>
		public FlatpackQualityCheck.Report Measured { get; init; }
	}

	/// <summary>
	/// Bake one model at the settings' quality level, or for <see cref="FlatpackQuality.Auto"/> at the cheapest level that
	/// matches the model from <see cref="FlatpackBakeSettings.AutoTargetRadii"/> outward. Measures the result when
	/// <see cref="FlatpackBakeSettings.MeasureQuality"/> is on (always for Auto) and stores where it holds up.
	/// </summary>
	public static async Task<Result> BakeAsync( Model model, FlatpackBakeSettings settings, Action<float, string> progress = null, CancellationToken cancel = default )
	{
		if ( settings.Quality != FlatpackQuality.Auto )
		{
			var result = await BakeLevelAsync( model, settings, progress, cancel );
			return settings.MeasureQuality ? await MeasureAsync( result, progress, cancel ) : result;
		}

		// Auto: climb the levels until one holds up at the target distance. Each bake overwrites the last.
		var timer = Stopwatch.StartNew();
		var levels = new[] { FlatpackQuality.Low, FlatpackQuality.Medium, FlatpackQuality.High, FlatpackQuality.Ultra };
		var best = FlatpackQuality.Ultra;
		var bestScore = float.MaxValue;
		Result last = null;

		for ( var i = 0; i < levels.Length; i++ )
		{
			var level = levels[i];
			var step = i;
			var baked = await BakeLevelAsync( model, settings.WithQuality( level ),
				( f, text ) => progress?.Invoke( (step + f * 0.8f) / levels.Length, $"{level}: {text}" ), cancel );
			last = await MeasureAsync( baked, ( f, text ) => progress?.Invoke( (step + 0.8f + f * 0.2f) / levels.Length, $"{level}: {text}" ), cancel );

			// See-through detail goes straight to Ultra. At Medium a wire grid has about one texel per wire, so its frames
			// line up badly and the panel doubles when seen at an angle, even where the bake check passes it (iteration 7).
			if ( last.Impostor.SeeThrough > SeeThroughShare && level != FlatpackQuality.Ultra )
			{
				i = levels.Length - 2;
				continue;
			}

			// Holds up from the target (judged out to 16 radii; past that every level matches as well as it ever will).
			if ( last.Measured.HoldsUpFrom <= MathF.Min( settings.AutoTargetRadii, 16.0f ) )
				return Finish( last, timer );

			var score = last.Measured.HoldsUpFrom;

			if ( score < bestScore )
			{
				bestScore = score;
				best = level;
			}
		}

		// Nothing held up (fine wire mesh, for example). Keep whichever level held up from closest.
		if ( best != FlatpackQuality.Ultra )
		{
			var baked = await BakeLevelAsync( model, settings.WithQuality( best ), progress, cancel );
			last = await MeasureAsync( baked, progress, cancel );
		}

		return Finish( last, timer );
	}

	static Result Finish( Result r, Stopwatch timer ) => new()
	{
		Impostor = r.Impostor, ImpostorPath = r.ImpostorPath, MaterialPath = r.MaterialPath, Files = r.Files,
		Level = r.Level, Measured = r.Measured, Duration = timer.Elapsed,
	};

	/// <summary>
	/// Measure a finished bake and store where it holds up on its <c>.fpack</c>.
	/// </summary>
	static async Task<Result> MeasureAsync( Result baked, Action<float, string> progress, CancellationToken cancel )
	{
		if ( baked.Impostor.SeeThrough > SeeThroughShare )
		{
			progress?.Invoke( 0.0f, "Fitting see-through coverage to the model" );
			var (soft, growth) = await FlatpackQualityCheck.FitSoftCoverageAsync( baked.Impostor, cancel );
			WriteMaterialValue( baked.MaterialPath, "g_flFlatpackSoftCoverage", soft );
			WriteMaterialValue( baked.MaterialPath, "g_flFlatpackSoftGrowth", growth );
		}

		progress?.Invoke( 0.0f, "Measuring against the model" );
		var report = await FlatpackQualityCheck.MeasureAsync( baked.Impostor, cancel: cancel );

		var impostor = baked.Impostor;
		impostor.HoldsUpFrom = float.IsPositiveInfinity( report.HoldsUpFrom ) ? -1.0f : report.HoldsUpFrom;
		AssetSystem.FindByPath( baked.ImpostorPath )?.SaveToDisk( impostor );
		progress?.Invoke( 1.0f, report.Summary );

		return new Result
		{
			Impostor = impostor, ImpostorPath = baked.ImpostorPath, MaterialPath = baked.MaterialPath, Files = baked.Files,
			Duration = baked.Duration, Level = baked.Level, Measured = report,
		};
	}

	/// <summary>
	/// Share of covered texels (alpha above 2 of 255) that are only partly covered (below 250).
	/// </summary>
	static float PartialCoverageShare( Color32[] albedo )
	{
		long covered = 0, partial = 0;
		foreach ( var c in albedo )
		{
			if ( c.a <= 2 ) continue;
			covered++;
			if ( c.a < 250 ) partial++;
		}
		return covered > 0 ? partial / (float)covered : 0.0f;
	}

	/// <summary>
	/// Set one value in a generated <c>.vmat</c> (adding the line if it's missing) and recompile it.
	/// </summary>
	static void WriteMaterialValue( string materialPath, string key, float value )
	{
		var asset = AssetSystem.FindByPath( materialPath );
		if ( asset is null || !File.Exists( asset.AbsolutePath ) )
			return;

		var line = $"\t{key} \"{value.ToString( "0.000000", CultureInfo.InvariantCulture )}\"";
		var lines = File.ReadAllLines( asset.AbsolutePath ).ToList();
		var index = lines.FindIndex( l => l.TrimStart().StartsWith( key + " " ) );
		if ( index >= 0 )
			lines[index] = line;
		else
			lines.Insert( Math.Max( 0, lines.FindLastIndex( l => l.Trim() == "}" ) ), line );

		File.WriteAllLines( asset.AbsolutePath, lines );
		asset.Compile( true );
	}

	/// <summary>
	/// Bake one model at one quality level. Runs on the main thread but yields between frames, so the editor keeps
	/// drawing; cancel with <paramref name="cancel"/>. <paramref name="progress"/> gets a 0..1 fraction and a status line.
	/// </summary>
	static async Task<Result> BakeLevelAsync( Model model, FlatpackBakeSettings settings, Action<float, string> progress, CancellationToken cancel )
	{
		if ( model is null || model.IsError )
			throw new ArgumentException( "Pick a valid model to bake." );

		var timer = Stopwatch.StartNew();
		var (grid, atlasSize, searchSteps) = settings.Resolve();
		var n = Math.Clamp( grid, 2, 32 );
		// The shader addresses frame (x, y) at (x + u) / n of the atlas, so the atlas must be exactly n frames wide.
		// 2048 / 12 isn't whole; round the frame down (to keep the atlas a multiple of 4 for block compression) and
		// shrink the atlas to fit, rather than let the frames drift out of place across the atlas.
		var cell = (int)atlasSize / n;
		while ( (cell * n) % 4 != 0 )
			cell--;
		var atlas = cell * n;
		var layout = settings.Layout;

		var bounds = model.RenderBounds;
		if ( bounds.Size.Length < 0.001f )
			bounds = model.Bounds;

		// A sphere fitted to the vertices is usually much smaller than the box's half diagonal, so the object fills more
		// of every frame. Fall back to the box for models without readable vertices.
		var vertices = model.GetVertices();
		var points = vertices is { Length: > 0 } ? vertices.Select( v => v.Position ).ToArray() : null;
		var (center, radius) = points is not null
			? FlatpackMath.BoundingSphere( points )
			: FlatpackMath.BoundingSphere( bounds );

		var (folder, assetsRoot) = ResolveOutputFolder( model, settings );
		var name = SafeName( model );

		var fitted = settings.FitEachView && points is not null;
		var fits = FitFrames( points, center, radius, n, cell, layout, fitted );

		var albedo = new Color32[atlas * atlas];
		var normal = new Color32[atlas * atlas];
		var depth = new float[atlas * atlas];
		var rao = settings.BakeRoughnessAo ? new Color32[atlas * atlas] : null;
		var supersample = Math.Clamp( settings.Supersample, 1, 4 );

		progress?.Invoke( 0.0f, $"Baking {name}" );
		var bakedMetal = await CaptureFramesAsync( model, layout, n, cell, atlas, center, radius, fits, albedo, normal, depth, rao, settings.BakeSelfShadow, supersample, progress, cancel );
		var seeThrough = PartialCoverageShare( albedo );

		// Fine wire mesh does worse framed: a fence panel seen edge-on squeezes into a sliver of texels, and its card came
		// out sparser than the real fence from 16 radii (iteration 8: 64 to 68% wrong pixels against 45 unframed). Bake
		// those views sphere-sized again. The weed tree's twigs (0.42 framed) keep the framing, which helped them a lot.
		if ( fitted && seeThrough > FineMeshShare )
		{
			progress?.Invoke( 0.0f, $"Baking {name} again, unframed (fine mesh)" );
			fits = FitFrames( points, center, radius, n, cell, layout, false );
			bakedMetal = await CaptureFramesAsync( model, layout, n, cell, atlas, center, radius, fits, albedo, normal, depth, rao, settings.BakeSelfShadow, supersample, progress, cancel );
			seeThrough = PartialCoverageShare( albedo );
		}

		cancel.ThrowIfCancellationRequested();
		progress?.Invoke( 0.85f, "Padding edges" );
		await Task.Delay( 1 );

		DilateIntoFrames( albedo, normal, depth, rao, atlas, cell, settings.EdgePadding );
		var mipAlphaScales = MipAlphaScales( albedo, atlas );

		cancel.ThrowIfCancellationRequested();
		progress?.Invoke( 0.9f, "Writing files" );
		await Task.Delay( 1 );

		Directory.CreateDirectory( folder );
		var files = new List<string>();

		var depthBits = settings.DepthBits >= 12 ? 16 : 8;
		var albedoFile = WritePng( Path.Combine( folder, $"{name}_fp_albedo.png" ), albedo, atlas, files );
		var normalFile = WritePng( Path.Combine( folder, $"{name}_fp_normal.png" ), normal, atlas, files );
		var depthFile = depthBits == 16
			? WritePfm( Path.Combine( folder, $"{name}_fp_depth.pfm" ), depth, atlas, files )
			: WritePng( Path.Combine( folder, $"{name}_fp_depth.png" ), DepthBytes( depth ), atlas, files );
		var framesFile = WriteFrames( Path.Combine( folder, $"{name}_fp_frames.png" ), fits, n, files );

		// The coarse map must never read deeper than the depth texture it summarises, or the search would skip a block
		// it should have searched: BC4 rounds by a few steps, R16 by half of one.
		var coarse = CoarseDepth( depth, atlas, cell, n, depthBits == 16 ? 1.0f / 65535.0f : 4.0f / 255.0f, out var coarseSize );
		var coarseFile = Path.Combine( folder, $"{name}_fp_depthmin.pfm" );
		FlatpackPfm.WriteGray( coarseFile, coarse, coarseSize, coarseSize );
		files.Add( coarseFile );
		var raoFile = rao is null ? null : WritePng( Path.Combine( folder, $"{name}_fp_rao.png" ), rao, atlas, files );

		var zLocked = settings.ZLockedCard || layout == FlatpackLayout.Cylinder;
		var materialFile = Path.Combine( folder, $"{name}_fp.vmat" );
		File.WriteAllText( materialFile, BuildMaterial(
			Relative( assetsRoot, albedoFile ), Relative( assetsRoot, normalFile ), Relative( assetsRoot, depthFile ), depthBits,
			Relative( assetsRoot, framesFile ), Relative( assetsRoot, coarseFile ), raoFile is null ? null : Relative( assetsRoot, raoFile ),
			n, layout, center, radius, zLocked, mipAlphaScales, searchSteps, seeThrough > SeeThroughShare, bakedMetal ) );
		files.Add( materialFile );

		progress?.Invoke( 0.95f, "Compiling material" );

		// Textures first, so the material compile finds them already built.
		foreach ( var file in files )
		{
			var asset = AssetSystem.RegisterFile( file );
			if ( asset is not null )
				await asset.CompileIfNeededAsync( 120.0f );
		}

		var materialPath = Relative( assetsRoot, materialFile );
		var material = Material.Load( materialPath );

		// The resource: create it once, then overwrite its values on re-bakes so references to it survive.
		var impostorFile = Path.Combine( folder, $"{name}.fpack" );
		var impostorPath = Relative( assetsRoot, impostorFile );
		var impostorAsset = AssetSystem.FindByPath( impostorPath );
		if ( impostorAsset is null )
		{
			impostorAsset = AssetSystem.CreateResource( "fpack", impostorFile );
			await impostorAsset.CompileIfNeededAsync();
		}

		if ( !impostorAsset.TryLoadResource<FlatpackImpostor>( out var impostor ) )
			throw new InvalidOperationException( $"Couldn't create {impostorPath}." );

		impostor.SourceModel = model;
		impostor.Material = material;
		impostor.Layout = layout;
		impostor.GridSize = n;
		impostor.AtlasSize = atlas;
		impostor.Center = center;
		impostor.Radius = radius;
		impostor.Bounds = bounds;
		impostor.ThinDetail = mipAlphaScales[1];
		impostor.QualityLevel = settings.Quality.ToString();
		impostor.HoldsUpFrom = 0.0f;
		impostor.SeeThrough = seeThrough;
		impostorAsset.SaveToDisk( impostor );
		files.Add( impostorFile );

		progress?.Invoke( 1.0f, $"Baked {name}" );

		return new Result
		{
			Impostor = impostor,
			ImpostorPath = impostorPath,
			MaterialPath = materialPath,
			Files = files,
			Duration = timer.Elapsed,
			Level = settings.Quality,
		};
	}

	/// <summary>
	/// Each view's bounds: its outline and depth range from the vertices when <paramref name="fitted"/>, else the whole
	/// sphere. Padded by two texels so bilinear filtering at the outline stays inside the frame.
	/// </summary>
	static FlatpackMath.FrameFit[] FitFrames( Vector3[] points, Vector3 center, float radius, int n, int cell, FlatpackLayout layout, bool fitted )
	{
		var fits = new FlatpackMath.FrameFit[n * n];
		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				fits[y * n + x] = fitted
					? FlatpackMath.FitFrame( points, center, radius, FlatpackMath.FrameDirection( x, y, n, layout ), cell, 2 )
					: FlatpackMath.DecodeFrameFit( FlatpackMath.EncodeFrameFit( FlatpackMath.FrameFit.Sphere ) );
			}
		}
		return fits;
	}

	/// <summary>
	/// Render every frame and copy it into the CPU atlases. Returns whether metalness went into the roughness/AO atlas.
	/// </summary>
	static async Task<bool> CaptureFramesAsync( Model model, FlatpackLayout layout, int n, int cell, int atlas, Vector3 center, float radius, FlatpackMath.FrameFit[] fits,
		Color32[] albedo, Color32[] normal, float[] depth, Color32[] rao, bool selfShadow, int supersample, Action<float, string> progress, CancellationToken cancel )
	{
		var depthMaterial = Material.Create( "flatpack_bake_depth", DepthShader, true );
		selfShadow &= rao is not null;

		var scene = Scene.CreateEditorScene();
		scene.Name = "Flatpack Bake";

		try
		{
			ModelRenderer renderer;
			CameraComponent camera;
			DirectionalLight sun = null;
			MetalPass metal = null;

			using ( scene.Push() )
			{
				var source = new GameObject( true, "source" );
				renderer = source.Components.Create<ModelRenderer>();
				renderer.Model = model;

				// Bake from the most detailed LOD, whatever the frame's screen size would pick.
				renderer.LodOverride = 0;

				var eye = new GameObject( true, "camera" );
				camera = eye.Components.Create<CameraComponent>();
				camera.BackgroundColor = Color.Transparent;
				camera.Orthographic = true;

				if ( selfShadow )
				{
					var light = new GameObject( true, "sky light" );
					sun = light.Components.Create<DirectionalLight>();
					sun.LightColor = Color.White;
					sun.Shadows = true;
				}

				if ( rao is not null )
					metal = MetalPass.Create( renderer );
			}

			var frame = new Bitmap( cell * supersample, cell * supersample, false );
			var total = n * n;
			var sliceTimer = Stopwatch.StartNew();

			for ( var y = 0; y < n; y++ )
			{
				for ( var x = 0; x < n; x++ )
				{
					cancel.ThrowIfCancellationRequested();

					var dir = FlatpackMath.FrameDirection( x, y, n, layout );
					var fit = fits[y * n + x];

					Color32[] colorPixels, normalPixels, roughPixels = null, aoPixels = null;
					float[] depthPixels;
					byte[] visibility = null, metalness = null;

					using ( scene.Push() )
					{
						FrameCamera( camera, center, radius, dir, fit );
						scene.EditorTick( 0.0f, 0.0f );

						colorPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.Albedo, null );
						normalPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.NormalMap, null );
						depthPixels = DecodeDepth( Capture( camera, renderer, frame, SceneCameraDebugMode.Albedo, depthMaterial ) );

						if ( rao is not null )
						{
							roughPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.Roughness, null );
							aoPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.AmbientOcclusion, null );
						}

						if ( selfShadow )
							visibility = CaptureSkyVisibility( scene, camera, renderer, sun, frame, normalPixels );

						if ( metal is not null )
						{
							if ( sun is not null ) sun.Enabled = false;
							metalness = metal.Capture( scene, camera, renderer, frame );
							if ( sun is not null ) sun.Enabled = true;
						}
					}

					if ( supersample > 1 )
						Downsample( cell, supersample, ref colorPixels, ref normalPixels, ref depthPixels, ref roughPixels, ref aoPixels, ref visibility, ref metalness );

					BlitFrame( x, y, cell, atlas, colorPixels, normalPixels, depthPixels, roughPixels, aoPixels, visibility, metalness, albedo, normal, depth, rao );

					var done = y * n + x + 1;
					progress?.Invoke( done / (float)total * 0.85f, $"Frame {done} of {total}" );

					// Hand the main thread back now and then so the editor stays responsive.
					if ( sliceTimer.ElapsedMilliseconds > 30 )
					{
						await Task.Delay( 1 );
						sliceTimer.Restart();
					}
				}
			}

			return metal is not null;
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Directions the sky-visibility pass lights the model from: one overhead and two rings, spread over the upper
	/// hemisphere. Fixed in object space, so every frame sees the same shadows and blending between frames stays seamless.
	/// </summary>
	static readonly Angles[] SkyLights =
	{
		new( 85, 0, 0 ),
		new( 60, 0, 0 ), new( 60, 120, 0 ), new( 60, 240, 0 ),
		new( 30, 45, 0 ), new( 30, 135, 0 ), new( 30, 225, 0 ), new( 30, 315, 0 ),
	};

	/// <summary>
	/// How much of the sky each texel sees, 0..255: diffuse light with shadows over diffuse light without, both summed over
	/// <see cref="SkyLights"/>. This is the self-shadowing a dense model (a tree's inner branches) has and a card can't
	/// work out live; the shader uses it to darken ambient light and part of the albedo.
	/// </summary>
	/// <remarks>
	/// Eight lights average out to soft occlusion; with only a few, a surface facing one light gets that light's hard
	/// shadow and turns black. Ratios are taken in linear light.
	/// </remarks>
	static byte[] CaptureSkyVisibility( Scene scene, CameraComponent camera, ModelRenderer renderer, DirectionalLight sun, Bitmap target, Color32[] normals )
	{
		var count = target.Width * target.Height;
		var lit = new float[count];
		var open = new float[count];

		foreach ( var angles in SkyLights )
		{
			sun.WorldRotation = angles;

			foreach ( var shadowed in new[] { true, false } )
			{
				sun.Shadows = shadowed;
				scene.EditorTick( 0.0f, 0.0f );

				// Render twice: the first render after moving or toggling the light can still use the old shadow map.
				Capture( camera, renderer, target, SceneCameraDebugMode.Diffuse, null );
				var pixels = Capture( camera, renderer, target, SceneCameraDebugMode.Diffuse, null );

				var sum = shadowed ? lit : open;
				for ( var i = 0; i < count; i++ )
					sum[i] += Linear( pixels[i].r );
			}
		}

		// Shadow-map bias and terminator softness keep even fully open texels a little under 1, and that shifts from frame
		// to frame. Normalise: the well-lit texels of this frame (95th percentile) count as fully visible. Capped, so a
		// frame that really is mostly in shadow isn't brightened.
		var ratio = new float[count];
		var samples = new List<float>( count );
		var openMax = 0.0f;
		for ( var i = 0; i < count; i++ )
			openMax = MathF.Max( openMax, open[i] );

		for ( var i = 0; i < count; i++ )
		{
			ratio[i] = open[i] > 0.0f ? Math.Clamp( lit[i] / open[i], 0.0f, 1.0f ) : 1.0f;
			if ( normals[i].a > 0 && open[i] > openMax * 0.2f )
				samples.Add( ratio[i] );
		}

		samples.Sort();
		var reference = samples.Count > 16 ? samples[(int)(samples.Count * 0.95f)] : 1.0f;
		reference = Math.Clamp( reference, 0.75f, 1.0f );

		var visibility = new byte[count];
		for ( var i = 0; i < count; i++ )
		{
			// Texels the sky barely reaches (undersides) carry little information; fade them toward fully visible.
			var known = openMax > 0.0f ? Math.Clamp( open[i] / (openMax * 0.2f), 0.0f, 1.0f ) : 0.0f;
			var v = Math.Clamp( ratio[i] / reference, 0.0f, 1.0f );
			visibility[i] = (byte)Math.Clamp( (int)MathF.Round( (v * known + (1.0f - known)) * 255.0f ), 0, 255 );
		}

		return visibility;
	}

	/// <summary>
	/// The diffuse lighting view writes light through an sRGB target; ratios only make sense in linear light.
	/// </summary>
	static float Linear( byte value )
	{
		var c = value / 255.0f;
		return c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );
	}

	/// <summary>
	/// Point the bake camera at one frame: orthographic, framing exactly the frame's fitted image rectangle (wider or
	/// taller than square when the object is), with the near plane on its nearest surface.
	/// </summary>
	static void FrameCamera( CameraComponent camera, Vector3 center, float radius, Vector3 dir, FlatpackMath.FrameFit fit )
	{
		FlatpackMath.FrameBasis( dir, out var right, out var up );

		var size = (fit.Max - fit.Min) * (2.0f * radius);
		var mid = (fit.Min + fit.Max) * 0.5f - new Vector2( 0.5f, 0.5f );

		// Image v runs down, so the rectangle's centre is up by -v. The camera stays two radii out, outside the sphere.
		var distance = radius * 2.0f;
		camera.WorldPosition = center + right * (mid.x * 2.0f * radius) - up * (mid.y * 2.0f * radius) + dir * distance;
		camera.WorldRotation = FlatpackMath.FrameRotation( dir );

		// Depth 0..1 spans one bounding diameter back from the nearest surface, not just to the furthest one: the background
		// then sits at least a diameter behind the object, where the shader's search never reaches, even in mips that
		// average an outline with it. 16 bits over a diameter is still 256 times finer than the 8 bits it replaces.
		var zNear = distance - fit.Near * radius;
		var zFar = zNear + 2.0f * radius;
		camera.OrthographicHeight = size.y;
		camera.ZNear = zNear;
		camera.ZFar = zFar;

		// The engine's own orthographic camera is square for a square target; this is the same matrix (checked against
		// CameraComponent.ProjectionMatrix in the editor) with its own width, so a tall object fills the frame both ways.
		camera.CustomProjectionMatrix = new Matrix(
			2.0f / size.x, 0, 0, 0,
			0, 2.0f / size.y, 0, 0,
			0, 0, -1.0f / (zFar - zNear), -zNear / (zFar - zNear),
			0, 0, 0, 1 );
	}

	/// <summary>
	/// The depth bake's red and green bytes back to one 0..1 depth: red is whole 1/255 steps, green the fraction of a step.
	/// </summary>
	static float[] DecodeDepth( Color32[] pixels )
	{
		var depth = new float[pixels.Length];
		for ( var i = 0; i < pixels.Length; i++ )
			depth[i] = Math.Clamp( (pixels[i].r + pixels[i].g / 255.0f) / 255.0f, 0.0f, 1.0f );
		return depth;
	}

	static Color32[] Capture( CameraComponent camera, ModelRenderer renderer, Bitmap target, SceneCameraDebugMode mode, Material overrideMaterial )
	{
		camera.DebugMode = mode;
		renderer.MaterialOverride = overrideMaterial;
		camera.RenderToBitmap( target );
		return target.GetPixels32();
	}

	/// <summary>
	/// Bakes metalness. s&amp;box has no metalness debug view and materials store it in different ways (a texture, a
	/// constant, packed channels), so this solves for it instead of reading it: the Reflect view of the real materials
	/// against copies forced to metalness 0 and 1, all lit by one light from the camera. Per texel,
	/// metal = (real - dielectric) / (metal - dielectric).
	/// </summary>
	sealed class MetalPass
	{
		Material[] _original, _dielectric, _metal;
		DirectionalLight _light;

		/// <summary>A pass for <paramref name="renderer"/>'s model, or null when none of its materials can be metal.</summary>
		public static MetalPass Create( ModelRenderer renderer )
		{
			var count = renderer.Materials.Count;
			var pass = new MetalPass { _original = new Material[count], _dielectric = new Material[count], _metal = new Material[count] };
			var any = false;
			for ( var i = 0; i < count; i++ )
			{
				var source = renderer.Materials.GetOriginal( i );
				pass._original[i] = source;
				if ( source is null )
					continue;

				any |= source.GetFeature( "F_METALNESS_TEXTURE" ) != 0 || source.GetVector4( "g_flMetalness" ).x > 0.0f;
				pass._dielectric[i] = Forced( source, 0.0f, i );
				pass._metal[i] = Forced( source, 1.0f, i );
			}

			if ( !any )
				return null;

			pass._light = new GameObject( true, "metal light" ).Components.Create<DirectionalLight>();
			pass._light.LightColor = Color.White;
			pass._light.Shadows = false;
			pass._light.Enabled = false;
			return pass;
		}

		static Material Forced( Material source, float metalness, int slot )
		{
			var copy = source.CreateCopy( $"flatpack_metal_{metalness:0}_{slot}" );
			copy.SetFeature( "F_METALNESS_TEXTURE", 0 );
			copy.Set( "g_flMetalness", metalness );
			return copy;
		}

		/// <summary>Metalness per pixel of <paramref name="target"/>, 0..255. Call inside the bake scene.</summary>
		public byte[] Capture( Scene scene, CameraComponent camera, ModelRenderer renderer, Bitmap target )
		{
			_light.WorldRotation = camera.WorldRotation;
			_light.Enabled = true;
			var real = Render( scene, camera, renderer, target, _original );
			var low = Render( scene, camera, renderer, target, _dielectric );
			var high = Render( scene, camera, renderer, target, _metal );
			_light.Enabled = false;
			renderer.ClearMaterialOverrides();

			var w = target.Width;
			var h = target.Height;
			var count = real.Length;
			var value = new float[count];
			var known = new bool[count];
			for ( var i = 0; i < count; i++ )
			{
				if ( real[i].a < 128 )
					continue;

				float r = Brightness( real[i] ), d = Brightness( low[i] ), m = Brightness( high[i] );

				// Where metal and dielectric reflect alike (dark colours, grazing angles) the solve has nothing to go on;
				// metalness barely changes the look there anyway. Those texels borrow their neighbours' value below.
				if ( MathF.Abs( m - d ) < 6.0f )
					continue;

				value[i] = Math.Clamp( (r - d) / (m - d), 0.0f, 1.0f );
				known[i] = true;
			}

			// A few rounds of filling unknown texels from known neighbours; anything still unknown is non-metal.
			for ( var round = 0; round < 6; round++ )
			{
				var next = (bool[])known.Clone();
				for ( var i = 0; i < count; i++ )
				{
					if ( known[i] || real[i].a < 128 )
						continue;

					int x = i % w, y = i / w, n = 0;
					var sum = 0.0f;
					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							int nx = x + dx, ny = y + dy;
							if ( nx < 0 || ny < 0 || nx >= w || ny >= h ) continue;
							var j = ny * w + nx;
							if ( !known[j] ) continue;
							sum += value[j];
							n++;
						}
					}

					if ( n == 0 )
						continue;

					value[i] = sum / n;
					next[i] = true;
				}
				known = next;
			}

			var bytes = new byte[count];
			for ( var i = 0; i < count; i++ )
				bytes[i] = (byte)Math.Clamp( (int)MathF.Round( value[i] * 255.0f ), 0, 255 );
			return bytes;
		}

		static Color32[] Render( Scene scene, CameraComponent camera, ModelRenderer renderer, Bitmap target, Material[] materials )
		{
			for ( var i = 0; i < materials.Length; i++ )
			{
				if ( materials[i] is not null )
					renderer.Materials.SetOverride( i, materials[i] );
			}
			renderer.Materials.Apply();
			scene.EditorTick( 0.0f, 0.0f );
			return FlatpackBaker.Capture( camera, renderer, target, SceneCameraDebugMode.Reflect, null );
		}

		static float Brightness( Color32 c ) => (c.r + c.g + c.b) / 3.0f;
	}

	/// <summary>
	/// Average a supersampled frame down to <paramref name="cell"/> texels a side. Alpha becomes the share of covered
	/// samples; colour, normal, depth and the material channels average over the covered samples only, so the empty
	/// background doesn't darken thin parts.
	/// </summary>
	static void Downsample( int cell, int ss, ref Color32[] color, ref Color32[] normal, ref float[] depth, ref Color32[] rough, ref Color32[] ao, ref byte[] visibility, ref byte[] metal )
	{
		var size = cell * ss;
		var outColor = new Color32[cell * cell];
		var outNormal = new Color32[cell * cell];
		var outDepth = new float[cell * cell];
		var outRough = rough is null ? null : new Color32[cell * cell];
		var outAo = ao is null ? null : new Color32[cell * cell];
		var outVis = visibility is null ? null : new byte[cell * cell];
		var outMetal = metal is null ? null : new byte[cell * cell];

		for ( var y = 0; y < cell; y++ )
		{
			for ( var x = 0; x < cell; x++ )
			{
				float r = 0, g = 0, b = 0, a = 0, nx = 0, ny = 0, nz = 0, d = 0, ro = 0, oc = 0, vi = 0, me = 0;
				var covered = 0;

				for ( var sy = 0; sy < ss; sy++ )
				{
					for ( var sx = 0; sx < ss; sx++ )
					{
						var i = (y * ss + sy) * size + x * ss + sx;
						var c = color[i];

						// The albedo view writes the material's raw opacity. Alpha-tested textures keep their small mips
						// above the cut on purpose, so averaging that raw value overstates thin detail (a fence panel that's
						// 12% wire read as 44%). Cut each sample the way the game cuts the real model, then average.
						if ( c.a < AlphaTestCut )
							continue;

						a += 255;
						covered++;
						r += c.r; g += c.g; b += c.b;
						nx += normal[i].r / 127.5f - 1.0f; ny += normal[i].g / 127.5f - 1.0f; nz += normal[i].b / 127.5f - 1.0f;
						d += depth[i];
						if ( rough is not null ) ro += rough[i].r;
						if ( ao is not null ) oc += ao[i].r;
						if ( visibility is not null ) vi += visibility[i];
						if ( metal is not null ) me += metal[i];
					}
				}

				var o = y * cell + x;
				if ( covered == 0 )
					continue;

				byte B( float v ) => (byte)Math.Clamp( (int)MathF.Round( v ), 0, 255 );
				var alpha = B( a / (ss * ss) );
				outColor[o] = new Color32( B( r / covered ), B( g / covered ), B( b / covered ), Math.Max( alpha, (byte)1 ) );

				var len = MathF.Sqrt( nx * nx + ny * ny + nz * nz );
				if ( len < 1e-4f ) len = 1.0f;
				outNormal[o] = new Color32( B( (nx / len + 1.0f) * 127.5f ), B( (ny / len + 1.0f) * 127.5f ), B( (nz / len + 1.0f) * 127.5f ), 255 );

				outDepth[o] = d / covered;
				if ( outRough is not null ) { var v = B( ro / covered ); outRough[o] = new Color32( v, v, v, 255 ); }
				if ( outAo is not null ) { var v = B( oc / covered ); outAo[o] = new Color32( v, v, v, 255 ); }
				if ( outVis is not null ) outVis[o] = B( vi / covered );
				if ( outMetal is not null ) outMetal[o] = B( me / covered );
			}
		}

		color = outColor; normal = outNormal; depth = outDepth; rough = outRough; ao = outAo; visibility = outVis; metal = outMetal;
	}

	/// <summary>
	/// Copy one frame's passes into the atlases. Uncovered texels stay fully clear (depth at the far plane) for the
	/// dilation pass.
	/// </summary>
	static void BlitFrame( int fx, int fy, int cell, int atlas, Color32[] color, Color32[] normal, float[] depth, Color32[] rough, Color32[] ao,
		byte[] visibility, byte[] metal, Color32[] albedoAtlas, Color32[] normalAtlas, float[] depthAtlas, Color32[] raoAtlas )
	{
		for ( var py = 0; py < cell; py++ )
		{
			var row = (fy * cell + py) * atlas + fx * cell;

			for ( var px = 0; px < cell; px++ )
			{
				var src = py * cell + px;
				var dst = row + px;
				var c = color[src];

				if ( c.a == 0 )
				{
					albedoAtlas[dst] = default;
					normalAtlas[dst] = new Color32( 128, 128, 255, 255 );
					depthAtlas[dst] = 1.0f;
					if ( raoAtlas is not null ) raoAtlas[dst] = default;
					continue;
				}

				albedoAtlas[dst] = c;
				normalAtlas[dst] = new Color32( normal[src].r, normal[src].g, normal[src].b, 255 );
				depthAtlas[dst] = depth[src];

				// Metalness rides in alpha as 1 + metal x 254 (never 0, which would wipe the colour; see the shader).
				if ( raoAtlas is not null )
					raoAtlas[dst] = new Color32( rough[src].r, ao[src].r, visibility?[src] ?? 255, metal is null ? (byte)255 : (byte)(1 + metal[src] * 254 / 255) );
			}
		}
	}

	/// <summary>
	/// Grow every frame's colours outward from its silhouette, staying inside the frame, so bilinear filtering and
	/// mips near the edge read the object's own colour instead of the empty background.
	/// </summary>
	/// <remarks>
	/// One multi-source breadth-first fill over the whole atlas: each empty texel takes the values of the nearest
	/// covered texel (by steps) in its own frame. Albedo alpha on grown texels is 1/255, far below the alpha clip,
	/// but non-zero so the colour survives the PNG round trip.
	/// </remarks>
	static void DilateIntoFrames( Color32[] albedo, Color32[] normal, float[] depth, Color32[] rao, int atlas, int cell, int padding )
	{
		var maxSteps = padding <= 0 ? int.MaxValue : padding;
		var source = new int[albedo.Length];
		var steps = new int[albedo.Length];
		var queue = new Queue<int>( albedo.Length / 4 );

		for ( var i = 0; i < albedo.Length; i++ )
		{
			if ( albedo[i].a > 0 )
			{
				source[i] = i;
				queue.Enqueue( i );
			}
			else
			{
				source[i] = -1;
			}
		}

		while ( queue.Count > 0 )
		{
			var i = queue.Dequeue();
			if ( steps[i] >= maxSteps )
				continue;

			var x = i % atlas;
			var y = i / atlas;

			// Neighbours in the same frame only, never across a frame border.
			if ( x % cell > 0 ) Grow( i, i - 1 );
			if ( x % cell < cell - 1 ) Grow( i, i + 1 );
			if ( y % cell > 0 ) Grow( i, i - atlas );
			if ( y % cell < cell - 1 ) Grow( i, i + atlas );
		}

		void Grow( int from, int to )
		{
			if ( source[to] >= 0 )
				return;

			var s = source[from];
			source[to] = s;
			steps[to] = steps[from] + 1;
			queue.Enqueue( to );

			var c = albedo[s];
			albedo[to] = new Color32( c.r, c.g, c.b, 1 );

			// Grown texels keep the neighbour's normal but sit at the far plane, so the shader's depth search passes
			// straight through the background instead of stopping on borrowed depth and drawing a ghost.
			normal[to] = normal[s];
			depth[to] = 1.0f;
			if ( rao is not null ) rao[to] = rao[s];
		}
	}

	static string WritePng( string path, Color32[] pixels, int size, List<string> files ) => WritePng( path, pixels, size, size, files );

	static string WritePng( string path, Color32[] pixels, int width, int height, List<string> files )
	{
		using var bitmap = new Bitmap( width, height, false );
		var colors = new Color[pixels.Length];
		for ( var i = 0; i < pixels.Length; i++ )
			colors[i] = pixels[i];

		bitmap.SetPixels( colors );
		File.WriteAllBytes( path, bitmap.ToPng() );
		files.Add( path );
		return path;
	}

	/// <summary>The 16-bit depth atlas, as a float image the texture compiler turns into R16.</summary>
	static string WritePfm( string path, float[] depth, int size, List<string> files )
	{
		FlatpackPfm.WriteGray( path, depth, size, size );
		files.Add( path );
		return path;
	}

	/// <summary>Depth rounded to bytes, for the 8-bit (BC4) depth texture.</summary>
	static Color32[] DepthBytes( float[] depth )
	{
		var bytes = new Color32[depth.Length];
		for ( var i = 0; i < depth.Length; i++ )
		{
			var v = (byte)Math.Clamp( (int)MathF.Round( depth[i] * 255.0f ), 0, 255 );
			bytes[i] = new Color32( v, v, v, 255 );
		}
		return bytes;
	}

	/// <summary>Texels per side of one coarse depth block (written into the material as <c>g_flFlatpackCoarseBlock</c>).</summary>
	public const int CoarseBlock = 16;

	/// <summary>
	/// The coarse depth map: for each <see cref="CoarseBlock"/> x <see cref="CoarseBlock"/> texel block of each frame, the
	/// nearest depth in it (the smallest value) less <paramref name="margin"/>, laid out per frame like the atlas
	/// (frame x, y at blocks x, y of a <paramref name="size"/>-wide square). The shader's search skips every block whose
	/// nearest surface the ray never gets down to, and marches the rest a texel at a time.
	/// </summary>
	static float[] CoarseDepth( float[] depth, int atlas, int cell, int n, float margin, out int size )
	{
		var blocks = (cell + CoarseBlock - 1) / CoarseBlock;
		size = blocks * n;
		var coarse = new float[size * size];

		for ( var fy = 0; fy < n; fy++ )
		for ( var fx = 0; fx < n; fx++ )
		for ( var by = 0; by < blocks; by++ )
		for ( var bx = 0; bx < blocks; bx++ )
		{
			var nearest = 1.0f;
			for ( var y = by * CoarseBlock; y < Math.Min( (by + 1) * CoarseBlock, cell ); y++ )
			{
				var row = (fy * cell + y) * atlas + fx * cell;
				for ( var x = bx * CoarseBlock; x < Math.Min( (bx + 1) * CoarseBlock, cell ); x++ )
					nearest = MathF.Min( nearest, depth[row + x] );
			}

			coarse[(fy * blocks + by) * size + fx * blocks + bx] = MathF.Max( 0.0f, nearest - margin );
		}

		return coarse;
	}

	/// <summary>
	/// The frame lookup texture: two texels per frame (see <see cref="FlatpackMath.EncodeFrameFit"/>), image rectangles in
	/// the left half, depth ranges <see cref="FlatpackMath.FrameFitColumns"/> texels to the right.
	/// </summary>
	static string WriteFrames( string path, FlatpackMath.FrameFit[] fits, int n, List<string> files )
	{
		const int width = FlatpackMath.FrameFitColumns * 2;
		const int height = FlatpackMath.FrameFitColumns;
		var pixels = new Color32[width * height];
		for ( var i = 0; i < pixels.Length; i++ )
			pixels[i] = new Color32( 0, 0, 255, 255 );

		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				var (rect, range) = FlatpackMath.EncodeFrameFit( fits[y * n + x] );
				pixels[y * width + x] = rect;
				pixels[y * width + x + FlatpackMath.FrameFitColumns] = range;
			}
		}

		return WritePng( path, pixels, width, height, files );
	}

	/// <summary>
	/// Per-mip alpha multipliers that keep the share of covered texels the same as at full resolution.
	/// </summary>
	/// <remarks>
	/// Mipmaps average a thin part's alpha with the empty space around it. Past a mip or two, spindles, wires and leaf
	/// tips fall below the 0.5 clip and the card loses them (or they sparkle). For each mip level we find the alpha
	/// that the same share of texels exceeds as were above 0.5 at full size, and store the factor that lifts it back to
	/// 0.5. A fixed boost instead over-fills foliage and fences into solid blobs. Mips are approximated with a 2x2 box
	/// filter, as the texture compiler builds them.
	/// </remarks>
	static float[] MipAlphaScales( Color32[] albedo, int size )
	{
		const int levels = 8;
		var scales = new float[levels];
		var alpha = new float[albedo.Length];
		var covered = 0;
		for ( var i = 0; i < albedo.Length; i++ )
		{
			alpha[i] = albedo[i].a / 255.0f;
			if ( alpha[i] > 0.5f ) covered++;
		}

		var share = covered / (float)albedo.Length;
		var w = size;
		var h = size;

		for ( var level = 0; level < levels; level++ )
		{
			var nw = Math.Max( 1, w / 2 );
			var nh = Math.Max( 1, h / 2 );
			var next = new float[nw * nh];
			for ( var y = 0; y < nh; y++ )
			{
				for ( var x = 0; x < nw; x++ )
				{
					var x0 = Math.Min( x * 2, w - 1 ); var x1 = Math.Min( x * 2 + 1, w - 1 );
					var y0 = Math.Min( y * 2, h - 1 ); var y1 = Math.Min( y * 2 + 1, h - 1 );
					next[y * nw + x] = (alpha[y0 * w + x0] + alpha[y0 * w + x1] + alpha[y1 * w + x0] + alpha[y1 * w + x1]) * 0.25f;
				}
			}

			alpha = next; w = nw; h = nh;

			// The alpha value with `share` of this mip's texels above it.
			var sorted = (float[])alpha.Clone();
			Array.Sort( sorted );
			var index = Math.Clamp( (int)((1.0f - share) * sorted.Length), 0, sorted.Length - 1 );
			var threshold = sorted[index];
			scales[level] = threshold > 0.01f ? Math.Clamp( 0.5f / threshold, 1.0f, 4.0f ) : 1.0f;
		}

		return scales;
	}

	static string BuildMaterial( string albedo, string normal, string depth, int depthBits, string frames, string coarse, string rao, int n, FlatpackLayout layout, Vector3 center, float radius,
		bool zLocked, float[] mipAlphaScales, int searchSteps, bool seeThrough, bool bakedMetal )
	{
		string F( float v ) => v.ToString( "0.000000", CultureInfo.InvariantCulture );

		var sb = new StringBuilder();
		sb.AppendLine( "// Flatpack impostor material. Generated by the Flatpack baker (Anakin Studio); re-baking overwrites it." );
		sb.AppendLine( "Layer0" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\tshader \"{ImpostorShader}\"" );
		sb.AppendLine( $"\tTextureAlbedo \"{albedo}\"" );
		sb.AppendLine( $"\tTextureNormal \"{normal}\"" );
		sb.AppendLine( depthBits == 16 ? $"\tTextureDepth \"{depth}\"" : $"\tTextureDepthBC4 \"{depth}\"" );
		sb.AppendLine( $"\tTextureFrames \"{frames}\"" );
		sb.AppendLine( $"\tTextureDepthMin \"{coarse}\"" );
		sb.AppendLine( $"\tg_flFlatpackCoarse \"{F( 1 )}\"" );
		sb.AppendLine( $"\tg_flFlatpackCoarseBlock \"{F( CoarseBlock )}\"" );
		sb.AppendLine( $"\tg_flFlatpackFramed \"{F( 1 )}\"" );
		sb.AppendLine( $"\tg_flFlatpackDepthBits \"{F( depthBits )}\"" );
		if ( rao is not null )
			sb.AppendLine( $"\tTextureRoughnessAo \"{rao}\"" );
		sb.AppendLine( $"\tg_flFlatpackGrid \"{F( n )}\"" );
		sb.AppendLine( $"\tg_flFlatpackMode \"{F( (int)layout )}\"" );
		sb.AppendLine( $"\tg_vFlatpackCenter \"[{F( center.x )} {F( center.y )} {F( center.z )}]\"" );
		sb.AppendLine( $"\tg_flFlatpackRadius \"{F( radius )}\"" );
		sb.AppendLine( $"\tg_flFlatpackZLocked \"{F( zLocked ? 1 : 0 )}\"" );
		sb.AppendLine( $"\tg_vFlatpackMipAlpha0 \"[{F( mipAlphaScales[0] )} {F( mipAlphaScales[1] )} {F( mipAlphaScales[2] )} {F( mipAlphaScales[3] )}]\"" );
		sb.AppendLine( $"\tg_vFlatpackMipAlpha1 \"[{F( mipAlphaScales[4] )} {F( mipAlphaScales[5] )} {F( mipAlphaScales[6] )} {F( mipAlphaScales[7] )}]\"" );
		sb.AppendLine( $"\tg_flFlatpackSearchSteps \"{F( Math.Clamp( searchSteps, 0, 32 ) )}\"" );
		// See-through bakes start from a typical fit; the bake check fits both to the model after baking.
		sb.AppendLine( $"\tg_flFlatpackSoftCoverage \"{F( seeThrough ? 1.5f : 0.0f )}\"" );
		sb.AppendLine( $"\tg_flFlatpackSoftGrowth \"{F( seeThrough ? 0.5f : 0.0f )}\"" );
		sb.AppendLine( $"\tg_flFlatpackBakedMetal \"{F( bakedMetal ? 1.0f : 0.0f )}\"" );
		sb.AppendLine( "}" );
		return sb.ToString();
	}

	/// <summary>
	/// Where the outputs go, and the Assets folder that asset paths are relative to.
	/// </summary>
	static (string Folder, string AssetsRoot) ResolveOutputFolder( Model model, FlatpackBakeSettings settings )
	{
		var projectAssets = Project.Current.GetAssetsPath();

		if ( !string.IsNullOrWhiteSpace( settings.OutputFolder ) )
		{
			var sub = settings.OutputFolder.Trim().Trim( '/', '\\' );
			return (Path.GetFullPath( Path.Combine( projectAssets, sub ) ), projectAssets);
		}

		// Next to the source, but only when the source is a file in this project (or one of its libraries).
		// Engine, cloud and cached models are read-only to us.
		var source = AssetSystem.FindByPath( model.ResourcePath );
		var projectRoot = Path.GetFullPath( Project.Current.GetRootPath() );
		var absolute = source?.AbsolutePath;

		if ( !string.IsNullOrEmpty( absolute ) )
		{
			absolute = Path.GetFullPath( absolute );
			var inProject = absolute.StartsWith( projectRoot, StringComparison.OrdinalIgnoreCase )
				&& !absolute.Contains( $"{Path.DirectorySeparatorChar}.sbox{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase );

			var assetsRoot = inProject ? FindAssetsRoot( absolute ) : null;
			if ( assetsRoot is not null )
				return (Path.GetDirectoryName( absolute ), assetsRoot);
		}

		return (Path.Combine( projectAssets, "flatpack" ), projectAssets);
	}

	static string FindAssetsRoot( string file )
	{
		var dir = new DirectoryInfo( Path.GetDirectoryName( file ) );
		while ( dir is not null )
		{
			if ( dir.Name.Equals( "Assets", StringComparison.OrdinalIgnoreCase ) )
				return dir.FullName;

			dir = dir.Parent;
		}

		return null;
	}

	static string Relative( string assetsRoot, string file )
	{
		return Path.GetRelativePath( assetsRoot, file ).Replace( '\\', '/' ).ToLowerInvariant();
	}

	static string SafeName( Model model )
	{
		var name = Path.GetFileNameWithoutExtension( model.ResourcePath ?? model.Name ?? "model" );
		var invalid = Path.GetInvalidFileNameChars();
		return new string( name.Select( c => invalid.Contains( c ) || c == ' ' ? '_' : char.ToLowerInvariant( c ) ).ToArray() );
	}
}
