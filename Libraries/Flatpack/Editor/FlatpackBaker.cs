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
/// Bakes a model into a Flatpack impostor: albedo, normal + depth and roughness + AO atlases, a material using
/// <c>flatpack_impostor.shader</c>, and a <see cref="FlatpackImpostor"/> resource that ties them together.
/// </summary>
/// <remarks>
/// <para>
/// The model is rendered alone in a throwaway scene with an orthographic camera, once per frame of the
/// N x N grid. Each pass uses one of the engine's camera debug views, which write material data straight to
/// the target with no lighting, exposure or tonemapping:
/// </para>
/// <list type="bullet">
/// <item>Albedo: the colour texture as sRGB bytes, alpha = coverage.</item>
/// <item>NormalMap: the normal-mapped world normal packed as n * 0.5 + 0.5. The model sits at the origin with no
/// rotation, so world space is object space.</item>
/// <item>Roughness, AmbientOcclusion: linear values as bytes.</item>
/// <item>Depth: there is no depth view, so <c>flatpack_bake_depth.shader</c> goes on as a material override and
/// writes linear depth as albedo, read back through the Albedo view.</item>
/// </list>
/// </remarks>
public static class FlatpackBaker
{
	/// <summary>
	/// The runtime impostor shader the generated material uses.
	/// </summary>
	public const string ImpostorShader = "shaders/flatpack_impostor.shader";

	const string DepthShader = "shaders/flatpack_bake_depth.shader";

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
	}

	/// <summary>
	/// Bake one model. Runs on the main thread but yields between frames, so the editor keeps drawing; cancel
	/// with <paramref name="cancel"/>. <paramref name="progress"/> gets a 0..1 fraction and a status line.
	/// </summary>
	public static async Task<Result> BakeAsync( Model model, FlatpackBakeSettings settings, Action<float, string> progress = null, CancellationToken cancel = default )
	{
		if ( model is null || model.IsError )
			throw new ArgumentException( "Pick a valid model to bake." );

		var timer = Stopwatch.StartNew();
		var n = Math.Clamp( settings.GridSize, 2, 32 );
		// The shader addresses frame (x, y) at (x + u) / n of the atlas, so the atlas must be exactly n frames wide.
		// 2048 / 12 isn't whole; round the frame down (to keep the atlas a multiple of 4 for block compression) and
		// shrink the atlas to fit, rather than let the frames drift out of place across the atlas.
		var cell = (int)settings.AtlasSize / n;
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
		var (center, radius) = vertices is { Length: > 0 }
			? FlatpackMath.BoundingSphere( vertices.Select( v => v.Position ).ToArray() )
			: FlatpackMath.BoundingSphere( bounds );

		var (folder, assetsRoot) = ResolveOutputFolder( model, settings );
		var name = SafeName( model );

		var albedo = new Color32[atlas * atlas];
		var normalDepth = new Color32[atlas * atlas];
		var rao = settings.BakeRoughnessAo ? new Color32[atlas * atlas] : null;

		progress?.Invoke( 0.0f, $"Baking {name}" );
		await CaptureFramesAsync( model, layout, n, cell, atlas, center, radius, albedo, normalDepth, rao, settings.BakeSelfShadow, progress, cancel );

		cancel.ThrowIfCancellationRequested();
		progress?.Invoke( 0.85f, "Padding edges" );
		await Task.Delay( 1 );

		DilateIntoFrames( albedo, normalDepth, rao, atlas, cell, settings.EdgePadding );

		cancel.ThrowIfCancellationRequested();
		progress?.Invoke( 0.9f, "Writing files" );
		await Task.Delay( 1 );

		Directory.CreateDirectory( folder );
		var files = new List<string>();

		var albedoFile = WritePng( Path.Combine( folder, $"{name}_fp_albedo.png" ), albedo, atlas, files );
		var normalFile = WritePng( Path.Combine( folder, $"{name}_fp_normal.png" ), normalDepth, atlas, files );
		var raoFile = rao is null ? null : WritePng( Path.Combine( folder, $"{name}_fp_rao.png" ), rao, atlas, files );

		var zLocked = settings.ZLockedCard || layout == FlatpackLayout.Cylinder;
		var materialFile = Path.Combine( folder, $"{name}_fp.vmat" );
		File.WriteAllText( materialFile, BuildMaterial(
			Relative( assetsRoot, albedoFile ), Relative( assetsRoot, normalFile ), raoFile is null ? null : Relative( assetsRoot, raoFile ),
			n, layout, center, radius, zLocked ) );
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
		};
	}

	/// <summary>
	/// Render every frame and copy it into the CPU atlases.
	/// </summary>
	static async Task CaptureFramesAsync( Model model, FlatpackLayout layout, int n, int cell, int atlas, Vector3 center, float radius,
		Color32[] albedo, Color32[] normalDepth, Color32[] rao, bool selfShadow, Action<float, string> progress, CancellationToken cancel )
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

				// The frame covers the bounding sphere exactly: 2r tall (and wide, the frame is square), near plane on
				// the sphere's front, far plane on its back. Depth 0..1 then spans the whole object.
				camera.OrthographicHeight = radius * 2.0f;
				camera.ZNear = radius;
				camera.ZFar = radius * 3.0f;

				if ( selfShadow )
				{
					var light = new GameObject( true, "sky light" );
					sun = light.Components.Create<DirectionalLight>();
					sun.LightColor = Color.White;
					sun.Shadows = true;
				}
			}

			var frame = new Bitmap( cell, cell, false );
			var total = n * n;
			var sliceTimer = Stopwatch.StartNew();

			for ( var y = 0; y < n; y++ )
			{
				for ( var x = 0; x < n; x++ )
				{
					cancel.ThrowIfCancellationRequested();

					var dir = FlatpackMath.FrameDirection( x, y, n, layout );

					Color32[] colorPixels, normalPixels, depthPixels, roughPixels = null, aoPixels = null;
					byte[] visibility = null;

					using ( scene.Push() )
					{
						camera.WorldPosition = center + dir * radius * 2.0f;
						camera.WorldRotation = FlatpackMath.FrameRotation( dir );
						scene.EditorTick( 0.0f, 0.0f );

						colorPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.Albedo, null );
						normalPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.NormalMap, null );
						depthPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.Albedo, depthMaterial );

						if ( rao is not null )
						{
							roughPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.Roughness, null );
							aoPixels = Capture( camera, renderer, frame, SceneCameraDebugMode.AmbientOcclusion, null );
						}

						if ( selfShadow )
							visibility = CaptureSkyVisibility( scene, camera, renderer, sun, frame, normalPixels );
					}

					BlitFrame( x, y, cell, atlas, colorPixels, normalPixels, depthPixels, roughPixels, aoPixels, visibility, albedo, normalDepth, rao );

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

	static Color32[] Capture( CameraComponent camera, ModelRenderer renderer, Bitmap target, SceneCameraDebugMode mode, Material overrideMaterial )
	{
		camera.DebugMode = mode;
		renderer.MaterialOverride = overrideMaterial;
		camera.RenderToBitmap( target );
		return target.GetPixels32();
	}

	/// <summary>
	/// Copy one frame's passes into the atlases. Uncovered texels stay fully clear for the dilation pass.
	/// </summary>
	static void BlitFrame( int fx, int fy, int cell, int atlas, Color32[] color, Color32[] normal, Color32[] depth, Color32[] rough, Color32[] ao,
		byte[] visibility, Color32[] albedoAtlas, Color32[] normalAtlas, Color32[] raoAtlas )
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
					if ( raoAtlas is not null ) raoAtlas[dst] = default;
					continue;
				}

				albedoAtlas[dst] = c;

				// Depth rides in the normal atlas's alpha. Keep it at 1 or above: the bitmap drops the colour of a
				// texel whose alpha is 0, and that would wipe the normal.
				var n = normal[src];
				normalAtlas[dst] = new Color32( n.r, n.g, n.b, Math.Max( depth[src].r, (byte)1 ) );

				if ( raoAtlas is not null )
					raoAtlas[dst] = new Color32( rough[src].r, ao[src].r, visibility?[src] ?? 255, 255 );
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
	static void DilateIntoFrames( Color32[] albedo, Color32[] normalDepth, Color32[] rao, int atlas, int cell, int padding )
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

			// Grown texels keep the neighbour's normal but sit at the far plane (depth 255), so the shader's depth search
			// passes straight through the background instead of stopping on borrowed depth and drawing a ghost.
			var nd = normalDepth[s];
			normalDepth[to] = new Color32( nd.r, nd.g, nd.b, 255 );
			if ( rao is not null ) rao[to] = rao[s];
		}
	}

	static string WritePng( string path, Color32[] pixels, int size, List<string> files )
	{
		using var bitmap = new Bitmap( size, size, false );
		var colors = new Color[pixels.Length];
		for ( var i = 0; i < pixels.Length; i++ )
			colors[i] = pixels[i];

		bitmap.SetPixels( colors );
		File.WriteAllBytes( path, bitmap.ToPng() );
		files.Add( path );
		return path;
	}

	static string BuildMaterial( string albedo, string normal, string rao, int n, FlatpackLayout layout, Vector3 center, float radius, bool zLocked )
	{
		string F( float v ) => v.ToString( "0.000000", CultureInfo.InvariantCulture );

		var sb = new StringBuilder();
		sb.AppendLine( "// Flatpack impostor material. Generated by the Flatpack baker (Anakin Studio); re-baking overwrites it." );
		sb.AppendLine( "Layer0" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\tshader \"{ImpostorShader}\"" );
		sb.AppendLine( $"\tTextureAlbedo \"{albedo}\"" );
		sb.AppendLine( $"\tTextureNormalDepth \"{normal}\"" );
		if ( rao is not null )
			sb.AppendLine( $"\tTextureRoughnessAo \"{rao}\"" );
		sb.AppendLine( $"\tg_flFlatpackGrid \"{F( n )}\"" );
		sb.AppendLine( $"\tg_flFlatpackMode \"{F( (int)layout )}\"" );
		sb.AppendLine( $"\tg_vFlatpackCenter \"[{F( center.x )} {F( center.y )} {F( center.z )}]\"" );
		sb.AppendLine( $"\tg_flFlatpackRadius \"{F( radius )}\"" );
		sb.AppendLine( $"\tg_flFlatpackZLocked \"{F( zLocked ? 1 : 0 )}\"" );
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
